using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Five SL events Phlox did not recognise: <c>path_update</c>, <c>on_damage</c>,
/// <c>final_damage</c>, <c>on_death</c>, <c>game_control</c>. A script declaring any of these handlers
/// failed to compile - the compiler rejected the handler name outright.
///
/// <para>
/// Signatures are the SL wiki's, checked page by page before anything was written. One of them
/// disagrees with the brief that scoped this work: the wiki gives <c>game_control</c> <b>three</b>
/// parameters - <c>(key id, integer button_levels, list axes)</c> - not four.
/// </para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SlEventRecognitionTests
{
    private readonly ITestOutputHelper _out;
    public SlEventRecognitionTests(ITestOutputHelper o) => _out = o;

    /// <summary>All five handlers in one script, each with the wiki's exact parameter list.</summary>
    private const string AllFive = @"
default
{
    state_entry() { llSay(0, ""up""); }
    path_update(integer type, list reserved) { llSay(0, ""path"" + (string)type); }
    on_damage(integer num_detected) { llSay(0, ""dmg""); }
    final_damage(integer num_detected) { llSay(0, ""final""); }
    on_death() { llSay(0, ""dead""); }
    game_control(key id, integer button_levels, list axes) { llSay(0, ""ctl""); }
}
";

    // ------------------------------------------------------------------ PART 1: compile + mask

    [Fact]
    public void AScriptDeclaringAllFiveHandlersCompiles()
    {
        var c = PhloxCompiler.Compile(AllFive);
        Assert.False(c.HasErrors(), c.Report);
    }

    [Theory]
    [InlineData("path_update(integer type, list reserved) {}")]
    [InlineData("on_damage(integer num_detected) {}")]
    [InlineData("final_damage(integer num_detected) {}")]
    [InlineData("on_death() {}")]
    [InlineData("game_control(key id, integer button_levels, list axes) {}")]
    public void EachHandlerCompilesOnItsOwn(string handler)
    {
        var c = PhloxCompiler.Compile("default { " + handler + " }");
        Assert.False(c.HasErrors(), handler + ": " + c.Report);
    }

    [Fact]
    public void ThePrimsMaskShowsTheDamageAndDeathHandlers()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default {
            on_damage(integer n) {}
            final_damage(integer n) {}
            on_death() {}
        }");
        h.PumpUntil(() => h.Prim.ScriptEvents.HasFlag(scriptEvents.on_damage) && h.Prim.ScriptEvents.HasFlag(scriptEvents.final_damage) && h.Prim.ScriptEvents.HasFlag(scriptEvents.on_death));
        _out.WriteLine($"ScriptEvents={h.Prim.ScriptEvents}");

        // These are the bits 'phlox status' prints.
        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.on_damage), $"on_damage missing: {h.Prim.ScriptEvents}");
        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.final_damage), $"final_damage missing: {h.Prim.ScriptEvents}");
        Assert.True(h.Prim.ScriptEvents.HasFlag(scriptEvents.on_death), $"on_death missing: {h.Prim.ScriptEvents}");
    }

    // ------------------------------------------------------------------ PART 2: delivery

    /// <summary>
    /// on_death is delivered from the region's one death hook, EventManager.OnAvatarKilled, to every
    /// script on every attachment the dead avatar wears. The harness prim is made an attachment of a
    /// presence and that presence is killed the way ScenePresence.PhysicsCollisionUpdate does it.
    /// </summary>
    [Fact]
    public void OnDeathReachesAScriptOnTheDeadAvatarsAttachment()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        Assert.NotNull(sp);

        var item = h.RezScript(@"default { state_entry() { llSay(0, ""up""); } on_death() { llSay(0, ""dead""); } }");
        h.PumpUntil(() => h.Said.Contains("up"));
        Assert.Contains("up", h.Said);

        // Wear the prim: what GetAttachments() enumerates is what on_death is posted to.
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sp.AddAttachment(sog);
        Assert.Contains(sog, sp.GetAttachments());

        h.Scene.EventManager.TriggerAvatarKill(0, sp);
        h.PumpUntil(() => h.Said.Contains("dead"));

        _out.WriteLine("said=[" + string.Join(",", h.Said) + "]");
        Assert.Contains("dead", h.Said);
        Assert.Equal(1, h.Said.Count(s => s == "dead"));
    }

    /// <summary>
    /// path_update arrives by the same door BotManager.FirePathEvent uses for bot_update:
    /// IScriptModule.PostScriptEvent(itemId, name, args). This drives that door with the exact
    /// argument shape the manager now posts, so the Phlox side of the delivery is exercised end to
    /// end without a bot manager in the test scene.
    /// </summary>
    [Fact]
    public void PathUpdateReachesTheHandlerWithThePuCode()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"default {
            state_entry() { llSay(0, ""up""); }
            path_update(integer type, list reserved) { llSay(0, ""path"" + (string)type); }
        }");
        h.PumpUntil(() => h.Said.Contains("up"));
        Assert.Contains("up", h.Said);

        // BOT_MOVE_COMPLETE (1) -> PU_GOAL_REACHED (1), reserved list empty - what FirePathEvent posts.
        bool posted = h.Engine.PostScriptEvent(item, "path_update", new object[] { 1, new object[0] });
        Assert.True(posted, "the engine did not recognise path_update");
        h.PumpUntil(() => h.Said.Contains("path1"));

        _out.WriteLine("said=[" + string.Join(",", h.Said) + "]");
        Assert.Contains("path1", h.Said);
    }

    [Fact]
    public void PathUpdateFailureMapsToUnreachable()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"default {
            path_update(integer type, list reserved) { llSay(0, ""path"" + (string)type); }
        }");
        h.Pump();
        h.PumpUntil(() => h.InterpreterFor(item) != null && h.RunStateOf(item) == "Waiting");
        // BOT_MOVE_FAILED (3, a navigation timeout) -> PU_FAILURE_UNREACHABLE (4).
        Assert.True(h.Engine.PostScriptEvent(item, "path_update", new object[] { 4, new object[0] }));
        h.PumpUntil(() => h.Said.Contains("path4"));
        Assert.Contains("path4", h.Said);
    }
}
