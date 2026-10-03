using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A prim-params rule list is walked by SL's value count for every rule, so a rule Phlox does not act on yet is
/// skipped whole and the rules after it still apply; PRIM_ALPHA_MODE takes SL's 3 values; a rule number SL does not
/// define ends the walk with SL's script error; PRIM_LINK_TARGET retargets the getter; and the same lists move a
/// seated avatar.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class PrimParamsRuleWalkTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsRuleWalkTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    /// <summary>The harness prim as the root "p1" plus a child "p2" at link 2.</summary>
    private static SceneObjectPart[] TwoPrimLinkset(SchedulerHarness h)
    {
        h.Prim.Name = "p1";
        h.Prim.Description = "d1";
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        var group = h.Prim.ParentGroup;
        Assert.Equal(2, group.PrimCount);
        var parts = new[] { group.GetLinkNumPart(1), group.GetLinkNumPart(2) };
        Assert.Same(h.Prim, parts[0]);
        parts[1].Name = "p2";
        parts[1].Description = "d2";
        return parts;
    }

    /// <summary>"Ann Sitter", seated on the child through the scene's own sit request: link 3.</summary>
    private static ScenePresence SeatedOnChild(SchedulerHarness h, SceneObjectPart[] parts)
    {
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        acd.firstname = "Ann";
        acd.lastname = "Sitter";
        var sp = SceneHelpers.AddScenePresence(h.Scene, acd);
        sp.AbsolutePosition = parts[1].AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, parts[1].UUID, Vector3.Zero);
        Assert.True(sp.ParentID != 0, "Ann did not sit");
        return sp;
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private void Run(SchedulerHarness h, SceneObjectPart sender, string body)
    {
        h.RezScriptInto(sender, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    private static IReadOnlyList<string> Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToList();

    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 0.001f, $"expected {expected}, got {actual}");

    // Rules Phlox did not act on, each followed by rules it does, kept harmless for when they were added. Hover text,
    // the rotation the prim already has, zero spin, a prim physics shape, a sit target, no click action, planar texgen,
    // a blank specular map and a suppressed collision sound were among them; Phlox now applies them all.
    private const string LongList =
        "PRIM_NAME, \"renamed\", " +
        "PRIM_TEXT, \"hover\", <1, 0, 0>, 1.0, " +
        "PRIM_DESC, \"after text\", " +
        "PRIM_TEXGEN, ALL_SIDES, PRIM_TEXGEN_PLANAR, " +
        "PRIM_SPECULAR, ALL_SIDES, NULL_KEY, <1, 1, 0>, ZERO_VECTOR, 0.0, <1, 1, 1>, 51, 0, " +
        "PRIM_OMEGA, <0, 0, 1>, 0.0, 0.0, " +
        "PRIM_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_PRIM, " +
        "PRIM_ROTATION, ZERO_ROTATION, " +
        "PRIM_SIZE, <0.5, 0.6, 0.7>, " +
        "PRIM_SIT_TARGET, TRUE, <0, 0, 1>, ZERO_ROTATION, " +
        "PRIM_CLICK_ACTION, CLICK_ACTION_NONE, " +
        "PRIM_COLLISION_SOUND, \"\", 1.0, " +
        "PRIM_GLOW, ALL_SIDES, 0.5";

    [Fact]
    public void RulesNotImplementedYetAreSkippedAndTheRulesAfterThemApply()
    {
        using var h = new SchedulerHarness();
        var prim = h.Prim;
        Run(h, prim, "llSetLinkPrimitiveParamsFast(LINK_THIS, [" + LongList + "]);");

        Assert.Equal("renamed", prim.Name);
        Assert.Equal("after text", prim.Description);
        Near(new Vector3(0.5f, 0.6f, 0.7f), prim.Scale);
        // The texture entry keeps glow as a byte, so 0.5 reads back as 0.498.
        Assert.Equal(0.5f, prim.Shape.Textures.GetFace(0).Glow, 2);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimAlphaModeTakesThreeValuesAndTheNextRuleApplies()
    {
        using var h = new SchedulerHarness();
        var prim = h.Prim;
        // The mask cutoff, 128 and 7, would each be read as a rule if the walk took two values:
        // 7 is PRIM_SIZE, 128 is no rule at all.
        Run(h, prim, "llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_DESC, \"before\", " +
                     "PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_MASK, 128, PRIM_NAME, \"after mask\", " +
                     "PRIM_ALPHA_MODE, 0, PRIM_ALPHA_MODE_BLEND, 7, PRIM_SIZE, <0.25, 0.25, 0.25>]);");

        Assert.Equal("before", prim.Description);
        Assert.Equal("after mask", prim.Name);
        Near(new Vector3(0.25f, 0.25f, 0.25f), prim.Scale);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void AnUndefinedRuleNumberShoutsSlsErrorAndStopsTheWalk()
    {
        using var h = new SchedulerHarness();
        var prim = h.Prim;
        prim.Name = "unchanged";
        Run(h, prim, "llSetPrimitiveParams([PRIM_DESC, \"applied\", 99, PRIM_NAME, \"not reached\"]);");

        Assert.Equal("applied", prim.Description);
        Assert.Equal("unchanged", prim.Name);
        Assert.Contains(Errors(h), m => m.Contains("llSetPrimitiveParams error running rule #2: unknown rule."));
    }

    [Fact]
    public void ANonIntegerRuleShoutsSlsErrorAndStopsTheWalk()
    {
        using var h = new SchedulerHarness();
        var prim = h.Prim;
        prim.Name = "unchanged";
        // PRIM_DESC's value is followed by a second string where a rule should be.
        Run(h, prim, "llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_DESC, \"applied\", \"stray\", PRIM_NAME, \"not reached\"]);");

        Assert.Equal("applied", prim.Description);
        Assert.Equal("unchanged", prim.Name);
        Assert.Contains(Errors(h), m => m.Contains("llSetPrimitiveParams error running rule #2: non-integer rule."));
    }

    [Fact]
    public void AnUndefinedRuleAfterALinkTargetIsCountedAcrossTheListAndShoutedOnce()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(LINK_SET, [PRIM_NAME, \"both\", " +
                         "PRIM_LINK_TARGET, 2, PRIM_DESC, \"child\", 12, PRIM_DESC, \"not reached\"]);");

        Assert.Equal("both", parts[0].Name);
        Assert.Equal("both", parts[1].Name);
        Assert.Equal("d1", parts[0].Description);
        Assert.Equal("child", parts[1].Description);
        Assert.Single(Errors(h), m => m.Contains("error running rule"));
        Assert.Contains(Errors(h), m => m.Contains("llSetPrimitiveParams error running rule #4: unknown rule."));
    }

    [Fact]
    public void PrimLinkTargetInGetLinkPrimitiveParamsReadsFromTheNewLink()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Run(h, parts[0],
            "llSay(0, \"link=\" + llDumpList2String(llGetLinkPrimitiveParams(1, " +
            "[PRIM_NAME, PRIM_LINK_TARGET, 2, PRIM_NAME, PRIM_DESC, PRIM_LINK_TARGET, LINK_ROOT, PRIM_DESC]), \"|\")); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_NAME, PRIM_LINK_TARGET, 2, PRIM_NAME]), \"|\"));");

        Assert.Equal("p1|p2|d2|d1", Line(h, "link="));
        Assert.Equal("p1|p2", Line(h, "own="));
    }

    [Fact]
    public void AGetterRulePhloxDoesNotReadYetStillTakesItsFace()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        // Face 7 does not exist on a box, so even once PRIM_TEXGEN and PRIM_ALPHA_MODE are read they return
        // nothing here. Read as a rule, 7 would be PRIM_SIZE.
        Run(h, parts[0],
            "llSay(0, \"got=\" + llDumpList2String(llGetLinkPrimitiveParams(2, " +
            "[PRIM_TEXGEN, 7, PRIM_NAME, PRIM_ALPHA_MODE, 7, PRIM_DESC]), \"|\"));");

        Assert.Equal("p2|d2", Line(h, "got="));
    }

    [Fact]
    public void TheSameListsMoveASeatedAvatar()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var ann = SeatedOnChild(h, parts);
        var target = new Vector3(0.5f, -1.25f, 0.75f);
        var targetRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);

        // Rules an avatar ignores and rules Phlox does not act on yet come before and between the moves.
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(3, [PRIM_TEXT, \"hover\", <1, 1, 1>, 1.0, " +
                         "PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_MASK, 128, " +
                         "PRIM_POS_LOCAL, <0.5, -1.25, 0.75>, " +
                         "PRIM_OMEGA, <0, 0, 1>, 0.0, 0.0, " +
                         "PRIM_ROT_LOCAL, llEuler2Rot(<0, 0, PI_BY_TWO>)]);");

        Assert.Equal(target, ann.OffsetPosition);
        Assert.True(Math.Abs(Quaternion.Dot(targetRot, ann.Rotation)) > 0.9999f, $"expected {targetRot}, got {ann.Rotation}");
        Assert.NotEqual(0u, ann.ParentID);
    }

    [Fact]
    public void LinkSetListsReachThePrimsAndTheSitterPastRulesNotImplementedYet()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var ann = SeatedOnChild(h, parts);
        var target = new Vector3(0.25f, 0.5f, 0.75f);

        // PRIM_POS_LOCAL moves the sitter and the prims (a rule the prims once did not act on, where the walk used to
        // stop and lose PRIM_DESC). The undefined rule at the end stops the walk after everything has applied.
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(LINK_SET, [PRIM_TEXT, \"hover\", <1, 1, 1>, 1.0, " +
                         "PRIM_POS_LOCAL, <0.25, 0.5, 0.75>, PRIM_DESC, \"after\", 99]);");

        Assert.Equal(target, ann.OffsetPosition);
        Assert.Equal(new[] { "after", "after" }, parts.Select(p => p.Description).ToArray());
        Assert.Single(Errors(h), m => m.Contains("error running rule #4: unknown rule."));
    }
}
