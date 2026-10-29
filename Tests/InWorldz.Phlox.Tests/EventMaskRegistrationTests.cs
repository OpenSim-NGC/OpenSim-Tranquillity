using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A fresh script instance runs its <c>state_entry</c> but never registers its event
/// mask, so the region does not know the prim is touchable: no touch cursor, and
/// <c>touch_start</c> never fires. Observed in world — llSetColor, llSetText,
/// llSay and llOwnerSay all ran, the Running box was ticked, and the prim could not be clicked.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class EventMaskRegistrationTests
{
    private readonly ITestOutputHelper _out;
    public EventMaskRegistrationTests(ITestOutputHelper o) => _out = o;

    private const string Touchable = @"
default
{
    state_entry()
    {
        llSay(0, ""ready"");
    }

    touch_start(integer n)
    {
        llSay(0, ""Touched."");
    }
}
";

    [Fact]
    public void AFreshCompileRegistersTouchOnThePart()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Touchable);
        h.PumpUntil(() => h.Prim.ScriptEvents.HasFlag(scriptEvents.touch_start) &&
                          (h.Prim.AggregatedScriptEvents & scriptEvents.anytouch) != 0);

        _out.WriteLine($"ScriptEvents={h.Prim.ScriptEvents} Aggregated={h.Prim.AggregatedScriptEvents} aggregated={h.Prim.AggregatedScriptEvents}");

        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.touch_start),
            $"the part was never told the script handles touch; mask={h.Prim.ScriptEvents}");
        // PrimFlags.Touch is DERIVED from this: aggregateScriptEvents sets it in m_localFlags when
        // anytouch is present (SceneObjectPart.cs:5254-5256, :5269) and the Flags setter strips it
        // (:1524), so the aggregate is the observable invariant - and it is what the region's own
        // touch dispatch tests (Scene.PacketHandlers.cs:334).
        Assert.True((h.Prim.AggregatedScriptEvents & scriptEvents.anytouch) != 0,
            $"no touch bit reached the part, so the viewer shows no touch cursor; aggregated={h.Prim.AggregatedScriptEvents}");
    }

    [Fact]
    public void ASharedScriptStartAlsoRegistersTouch()
    {
        using var h = new SchedulerHarness();
        var asset = OpenMetaverse.UUID.Random();

        h.RezScript(Touchable, asset);
        h.Pump();
        h.RezScript(Touchable, asset);   // second instance of the same asset: the shared path
        h.PumpUntil(() => h.Said.Count(s => s == "ready") >= 2 &&
                          h.Prim.ScriptEvents.HasFlag(scriptEvents.touch_start) &&
                          (h.Prim.AggregatedScriptEvents & scriptEvents.anytouch) != 0);

        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.touch_start),
            $"mask after a shared start={h.Prim.ScriptEvents}");
        Assert.True((h.Prim.AggregatedScriptEvents & scriptEvents.anytouch) != 0);
    }

    [Fact]
    public void ATouchThroughTheScenesOwnPathReachesTouchStart()
    {
        // Not a directly posted event: this is the region's real route, the one that was silent in
        // world - EventManager.TriggerObjectGrab into the engine's OnObjectGrab handler.
        using var h = new SchedulerHarness();
        var item = h.RezScript(Touchable);
        h.PumpUntil(() => h.SaidAnything(item));
        Assert.True(h.SaidAnything(item), "state_entry must have run before touch means anything");

        h.ClearSaid(item);
        h.TouchViaScene();
        h.PumpUntil(() => h.Said.Any(m => m.Contains("Touched")));

        Assert.Contains(h.Said, m => m.Contains("Touched"));
    }
}
