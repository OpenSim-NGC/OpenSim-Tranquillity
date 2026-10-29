using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Four functions that were no-ops or default returns in Phlox while upstream
/// <c>LSL_Api</c> implements them. Each is ported from the upstream body (line ranges cited on the
/// implementations) after checking that the SL wiki page agrees with upstream.
///
/// <para>Every test here is red on the tree before the port: the filter stored nothing, the animation
/// set stayed empty, the start string was always blank and the XOR was always <c>""</c>.</para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class UpstreamPortedStubTests
{
    private readonly ITestOutputHelper _out;
    public UpstreamPortedStubTests(ITestOutputHelper o) => _out = o;

    private static ColliderArgs CollisionFrom(string name, UUID id)
    {
        var args = new ColliderArgs();
        args.Colliders.Add(new DetectedObject { keyUUID = id, nameStr = name, ownerUUID = UUID.Random() });
        return args;
    }

    // ------------------------------------------------------------------ llCollisionFilter

    /// <summary>
    /// wiki: accept FALSE "excludes matches". A collision from the rejected name must NOT reach the
    /// script; one from any other name must. Triggered through EventManager directly, which is the
    /// door the region's own physics path does NOT pre-filter - so this exercises Phlox's own check.
    /// </summary>
    [Fact]
    public void ARejectedNameNeverReachesTheScriptAndAnotherNameDoes()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"default {
            state_entry() { llCollisionFilter(""Enemy"", NULL_KEY, FALSE); llSay(0, ""armed""); }
            collision_start(integer n) { llSay(0, ""hit:"" + llDetectedName(0)); }
        }");
        h.PumpUntil(() => h.Said.Contains("armed"));
        Assert.Contains("armed", h.Said);

        // The detected object is not in the scene, so DetectParams.Populate cannot name it and
        // llDetectedName(0) is blank; the COUNT of events is the assertion, and it is the stronger
        // one - a filtered collision must produce no event at all, not an event with no name.
        h.Scene.EventManager.TriggerScriptCollidingStart(h.Prim.LocalId, CollisionFrom("Enemy", UUID.Random()));
        h.Pump();
        Assert.Equal(0, h.Said.Count(s => s.StartsWith("hit:")));

        h.Scene.EventManager.TriggerScriptCollidingStart(h.Prim.LocalId, CollisionFrom("Friend", UUID.Random()));
        h.PumpUntil(() => h.Said.Count(s => s.StartsWith("hit:")) >= 1);
        _out.WriteLine("said=[" + string.Join(",", h.Said) + "]");
        Assert.Equal(1, h.Said.Count(s => s.StartsWith("hit:")));
    }

    /// <summary>wiki: accept TRUE "only accept collisions with objects name AND id".</summary>
    [Fact]
    public void AnAcceptFilterLetsOnlyThatNameThrough()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"default {
            state_entry() { llCollisionFilter(""Ball"", NULL_KEY, TRUE); }
            collision_start(integer n) { llSay(0, ""hit:"" + llDetectedName(0)); }
        }");
        h.Pump();
        h.PumpUntil(() => h.InterpreterFor(item) != null && h.RunStateOf(item) == "Waiting");

        h.Scene.EventManager.TriggerScriptCollidingStart(h.Prim.LocalId, CollisionFrom("Rock", UUID.Random()));
        h.Scene.EventManager.TriggerScriptCollidingStart(h.Prim.LocalId, CollisionFrom("Ball", UUID.Random()));
        h.Pump();
        h.PumpUntil(() => h.Said.Count(s => s.StartsWith("hit:")) >= 1);

        // Rock is filtered out, Ball is not: exactly one collision event of the two.
        Assert.Equal(1, h.Said.Count(s => s.StartsWith("hit:")));
    }

    // ------------------------------------------------------------------ object animations

    [Fact]
    public void ObjectAnimationsAreTrackedOnThePartAndListedByName()
    {
        using var h = new SchedulerHarness();
        // An animation item in the prim's inventory, so the name resolves the wiki's way.
        var anim = new TaskInventoryItem
        {
            Name = "wave", ItemID = UUID.Random(), AssetID = UUID.Random(),
            Type = (int)AssetType.Animation, InvType = (int)InventoryType.Animation,
        };
        h.Prim.Inventory.AddInventoryItem(anim, true);

        h.RezScript(@"default {
            state_entry() {
                llStartObjectAnimation(""wave"");
                llSay(0, ""n=" + @""" + (string)llGetListLength(llGetObjectAnimationNames()));
                llSay(0, ""names="" + llDumpList2String(llGetObjectAnimationNames(), "",""));
                llStopObjectAnimation(""wave"");
                llSay(0, ""after="" + (string)llGetListLength(llGetObjectAnimationNames()));
            }
        }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("after=")));

        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Contains("n=1", h.Said);
        Assert.Contains("names=wave", h.Said);
        Assert.Contains("after=0", h.Said);
        Assert.True(h.Prim.AnimationsNames == null || h.Prim.AnimationsNames.Count == 0);
    }

    // ------------------------------------------------------------------ llGetStartString

    /// <summary>
    /// The read half: the rezzed group carries RezStringParameter and the script reads it back.
    /// </summary>
    [Fact]
    public void TheStartStringComesBackFromTheGroup()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.RezStringParameter = "hello from the rezzer";
        h.RezScript(@"default { state_entry() { llSay(0, ""start="" + llGetStartString()); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("start=")));
        Assert.Contains("start=hello from the rezzer", h.Said);
    }

    /// <summary>
    /// The write half: llRezObjectWithParams with REZ_PARAM_STRING stores it on the object it rezzes.
    /// A real inventory object is rezzed in the test scene and its group inspected.
    /// </summary>
    /// <summary>Pump until a line starting with <paramref name="prefix"/> has been said, up to 30 s.</summary>
    private static void WaitFor(SchedulerHarness h, string prefix)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!h.Said.Any(s => s.StartsWith(prefix)) && DateTime.UtcNow < until)
            h.PumpFor(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void RezObjectWithParamsStoresTheStringOnTheRezzedObject()
    {
        using var h = new SchedulerHarness();
        var owner = h.Prim.OwnerID;
        TaskInventoryHelpers.AddSceneObject(h.Scene.AssetService, h.Prim, "child", UUID.Random(), owner);

        h.RezScript(@"default { state_entry() {
            llRezObjectWithParams(""child"", [REZ_PARAM_STRING, ""from-parent"", REZ_POS, llGetPos() + <0,0,1>, FALSE, FALSE]);
            llSay(0, ""rezzed"");
        } }");
        WaitFor(h, "rezzed");   // Wait for the result, not a fixed 1 s
        Assert.Contains("rezzed", h.Said);

        var child = h.Scene.GetSceneObjectGroups().FirstOrDefault(g => g.UUID != h.Prim.ParentGroup.UUID && g.OwnerID == owner);
        Assert.NotNull(child);
        _out.WriteLine($"rezzed group {child!.UUID} RezStringParameter='{child.RezStringParameter}'");
        Assert.Equal("from-parent", child.RezStringParameter);
    }

    // ------------------------------------------------------------------ llXorBase64Strings

    /// <summary>
    /// The upstream body's own identities: empty s1 gives "", empty s2 gives s1, and the XOR of
    /// two known strings gives what the (deliberately non-standard) algorithm produces.
    /// "QUJD" is "ABC"; "QQ==" is "A"; the padding comes from s1 only, per the ported comment.
    /// </summary>
    [Fact]
    public void XorBase64StringsIsTheUpstreamAlgorithmNotEmpty()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default { state_entry() {
            llSay(0, ""a="" + llXorBase64Strings("""", ""QQ==""));
            llSay(0, ""b="" + llXorBase64Strings(""QUJD"", """"));
            llSay(0, ""c="" + llXorBase64Strings(""QUJD"", ""QQ==""));
            llSay(0, ""d="" + llXorBase64Strings(""QUJD"", ""QUJD""));
        } }");
        WaitFor(h, "d=");   // four calls, 0.3 s sleep each. Wait for the last line, not a fixed 2 s (missed once)

        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Contains("a=", h.Said);
        Assert.Contains("b=QUJD", h.Said);
        // Equal inputs XOR to all-zero sextets, which the alphabet renders as 'A' - "AAAA".
        Assert.Contains("d=AAAA", h.Said);
        // Something, not the old "": the old stub made every call return "".
        Assert.Contains(h.Said, s => s.StartsWith("c=") && s.Length > 2);
    }
}
