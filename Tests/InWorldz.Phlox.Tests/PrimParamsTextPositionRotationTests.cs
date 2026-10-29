using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PRIM_TEXT sets and reads a prim's hover text as llSetText does (SL's 254-byte cut, color and alpha clamped);
/// PRIM_POS_LOCAL moves a root (the object) or a child (its offset from the root) within Halcyon's caps;
/// PRIM_ROTATION turns a root to the rotation given and a child to the root's rotation times it (SL's child-prim
/// quirk, as Halcyon and OpenSim do it); a seated avatar reads back its offset and rotation relative to the root, in
/// the root's frame; and the rule numbers OpenSim defines beyond SL's are skipped by OpenSim's count.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class PrimParamsTextPositionRotationTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsTextPositionRotationTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    /// <summary>The harness prim as the root "p1" plus a child "p2" at link 2.</summary>
    private static SceneObjectPart[] TwoPrimLinkset(SchedulerHarness h)
    {
        h.Prim.Name = "p1";
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        var group = h.Prim.ParentGroup;
        Assert.Equal(2, group.PrimCount);
        var parts = new[] { group.GetLinkNumPart(1), group.GetLinkNumPart(2) };
        Assert.Same(h.Prim, parts[0]);
        parts[1].Name = "p2";
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

    /// <summary>A viewer in the scene, so moved and turned prims queue their updates for it.</summary>
    private static void Viewer(SchedulerHarness h) => SceneHelpers.AddScenePresence(h.Scene, UUID.Random());

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private UUID Run(SchedulerHarness h, SceneObjectPart sender, string body)
    {
        var item = h.RezScriptInto(sender, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.Said));
        return item;
    }

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    private static IReadOnlyList<string> Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToList();

    private static float[] Nums(string lsl)
        => lsl.Trim('<', '>').Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static void Near(Vector3 expected, string lsl, float tolerance = 0.001f)
    {
        var n = Nums(lsl);
        Near(expected, new Vector3(n[0], n[1], n[2]), tolerance);
    }

    private static void Near(Quaternion expected, Quaternion actual)
        => Assert.True(Math.Abs(Quaternion.Dot(expected, actual)) > 0.9999f, $"expected {expected}, got {actual}");

    private static void Near(Quaternion expected, string lsl)
    {
        var n = Nums(lsl);
        Near(expected, new Quaternion(n[0], n[1], n[2], n[3]));
    }

    // ── PRIM_TEXT ─────────────────────────────────────────────────────────────

    [Fact]
    public void PrimTextSetsAndReadsBackOnTheRootAndAChildAlongsideOtherRules()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        // The texture entry and text color keep each channel as a byte: 0.5 reads back as 127/255.
        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_NAME, \"root\", PRIM_TEXT, \"root text\", <1, 0.5, 0>, 0.5, " +
            "PRIM_DESC, \"root desc\"]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_DESC, \"child desc\", PRIM_TEXT, \"child text\", <0, 0, 1>, 1.0, " +
            "PRIM_NAME, \"child\"]); " +
            "llSay(0, \"r=\" + llDumpList2String(llGetLinkPrimitiveParams(1, [PRIM_NAME, PRIM_TEXT, PRIM_DESC]), \"|\")); " +
            "llSay(0, \"c=\" + llDumpList2String(llGetLinkPrimitiveParams(2, [PRIM_NAME, PRIM_TEXT, PRIM_DESC]), \"|\")); " +
            "llSay(0, \"own=\" + llDumpList2String(llGetPrimitiveParams([PRIM_TEXT, PRIM_LINK_TARGET, 2, PRIM_TEXT]), \"|\"));");

        Assert.Equal("root", parts[0].Name);
        Assert.Equal("root desc", parts[0].Description);
        Assert.Equal("root text", parts[0].Text);
        Assert.Equal("child", parts[1].Name);
        Assert.Equal("child desc", parts[1].Description);
        Assert.Equal("child text", parts[1].Text);

        var r = Line(h, "r=").Split('|');
        Assert.Equal(new[] { "root", "root text" }, r.Take(2).ToArray());
        Near(new Vector3(1f, 0.5f, 0f), r[2], 0.01f);
        Assert.Equal(0.5f, float.Parse(r[3], CultureInfo.InvariantCulture), 2);
        Assert.Equal("root desc", r[4]);

        var c = Line(h, "c=").Split('|');
        Assert.Equal(new[] { "child", "child text" }, c.Take(2).ToArray());
        Near(new Vector3(0f, 0f, 1f), c[2], 0.01f);
        Assert.Equal(1f, float.Parse(c[3], CultureInfo.InvariantCulture), 3);
        Assert.Equal("child desc", c[4]);

        var own = Line(h, "own=").Split('|');
        Assert.Equal(6, own.Length);
        Assert.Equal("root text", own[0]);
        Assert.Equal("child text", own[3]);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimTextIsCutToSlsLimitAndItsColorAndAlphaAreClamped()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        // 300 ASCII bytes cut to 254; "a" and 200 two-byte characters cut to "a" and 126 of them (253 bytes): the
        // 127th would be split at byte 254, so it goes whole.
        Run(h, parts[0],
            "string ascii = \"\"; string wide = \"a\"; integer i; " +
            "for (i = 0; i < 300; ++i) ascii += \"x\"; " +
            "for (i = 0; i < 200; ++i) wide += llUnescapeURL(\"%C3%A9\"); " +
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_TEXT, ascii, <2, -1, 0.5>, 1.5]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_TEXT, wide, <0, 0, 0>, -0.5]); " +
            "llSay(0, \"r=\" + llDumpList2String(llList2List(llGetLinkPrimitiveParams(1, [PRIM_TEXT]), 1, 2), \"|\")); " +
            "llSay(0, \"c=\" + llDumpList2String(llList2List(llGetLinkPrimitiveParams(2, [PRIM_TEXT]), 1, 2), \"|\"));");

        Assert.Equal(new string('x', 254), parts[0].Text);
        Assert.Equal("a" + new string('é', 126), parts[1].Text);
        var r = Line(h, "r=").Split('|');
        Near(new Vector3(1f, 0f, 0.5f), r[0], 0.01f);
        Assert.Equal(1f, float.Parse(r[1], CultureInfo.InvariantCulture), 3);
        var c = Line(h, "c=").Split('|');
        Assert.Equal(0f, float.Parse(c[1], CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void LlSetTextTakesTheSameLimitAndClamp()
    {
        using var h = new SchedulerHarness();
        Run(h, h.Prim,
            "string ascii = \"\"; integer i; for (i = 0; i < 300; ++i) ascii += \"x\"; " +
            "llSetText(ascii, <2, -1, 0.5>, 1.0); " +
            "llSay(0, \"t=\" + llDumpList2String(llList2List(llGetPrimitiveParams([PRIM_TEXT]), 1, 1), \"|\"));");

        Assert.Equal(new string('x', 254), h.Prim.Text);
        Near(new Vector3(1f, 0f, 0.5f), Line(h, "t="), 0.01f);
    }

    // ── PRIM_POS_LOCAL ────────────────────────────────────────────────────────

    [Fact]
    public void PrimPosLocalOnTheRootMovesTheObjectAndOnAChildSetsItsOffset()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Viewer(h);
        var start = parts[0].AbsolutePosition;
        var childOffset = new Vector3(0.5f, -0.25f, 1f);
        // The root is turned, so a child offset that were taken in region axes would land elsewhere.
        parts[0].UpdateRotation(Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2));
        parts[0].UpdateFlag = PrimUpdateFlags.None;
        parts[1].UpdateFlag = PrimUpdateFlags.None;

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_POS_LOCAL, llGetPos() + <1, 2, 3>, PRIM_DESC, \"moved\"]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_POS_LOCAL, <0.5, -0.25, 1>, PRIM_NAME, \"offset\"]);");

        Near(start + new Vector3(1, 2, 3), parts[0].ParentGroup.AbsolutePosition);
        Near(start + new Vector3(1, 2, 3), parts[0].AbsolutePosition);
        Assert.Equal("moved", parts[0].Description);
        Near(childOffset, parts[1].OffsetPosition);
        Near(parts[0].AbsolutePosition + childOffset * parts[0].RotationOffset, parts[1].AbsolutePosition);
        Assert.Equal("offset", parts[1].Name);
        // Both changes went out through the scene's own update path.
        Assert.NotEqual(PrimUpdateFlags.None, parts[0].UpdateFlag);
        Assert.NotEqual(PrimUpdateFlags.None, parts[1].UpdateFlag);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimPosLocalOnAnUnattachedRootMovesAtMostTenMetres()
    {
        using var h = new SchedulerHarness();
        var start = h.Prim.AbsolutePosition;
        Run(h, h.Prim, "llSetPrimitiveParams([PRIM_POS_LOCAL, llGetPos() + <30, 0, 0>]);");
        Near(start + new Vector3(10, 0, 0), h.Prim.AbsolutePosition);
    }

    // ── PRIM_ROTATION ─────────────────────────────────────────────────────────

    [Fact]
    public void PrimRotationTurnsTheRootAndGivesAChildTheRootRotationTimesTheOneGiven()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Viewer(h);
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 4);
        var q = Quaternion.CreateFromEulers((float)Math.PI / 2, 0, 0);
        parts[0].UpdateFlag = PrimUpdateFlags.None;
        parts[1].UpdateFlag = PrimUpdateFlags.None;

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(LINK_ROOT, [PRIM_ROTATION, llEuler2Rot(<0, 0, PI / 4>), PRIM_DESC, \"turned\"]); " +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_ROTATION, llEuler2Rot(<PI_BY_TWO, 0, 0>), PRIM_NAME, \"child turned\"]);");

        Near(rootRot, parts[0].RotationOffset);
        Near(rootRot, parts[0].ParentGroup.GroupRotation);
        Assert.Equal("turned", parts[0].Description);
        // SL's child-prim quirk (SVC-93), as Halcyon and OpenSim reproduce it.
        Near(rootRot * q, parts[1].RotationOffset);
        Assert.Equal("child turned", parts[1].Name);
        Assert.NotEqual(PrimUpdateFlags.None, parts[0].UpdateFlag);
        Assert.NotEqual(PrimUpdateFlags.None, parts[1].UpdateFlag);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    // ── A seated avatar's PRIM_POS_LOCAL and PRIM_ROT_LOCAL ──────────────────

    [Fact]
    public void ASittersPosLocalAndRotLocalReadBackRelativeToATurnedRoot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var ann = SeatedOnChild(h, parts);
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        parts[0].UpdateRotation(rootRot);
        var offset = new Vector3(0.5f, -1.25f, 0.75f);
        var rot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 4);

        Run(h, parts[0],
            "llSetLinkPrimitiveParamsFast(3, [PRIM_POS_LOCAL, <0.5, -1.25, 0.75>, PRIM_ROT_LOCAL, llEuler2Rot(<0, 0, PI / 4>)]); " +
            "list got = llGetLinkPrimitiveParams(3, [PRIM_POS_LOCAL, PRIM_ROT_LOCAL, PRIM_NAME]); " +
            "llSay(0, \"n=\" + (string)llGetListLength(got)); " +
            "llSay(0, \"pos=\" + (string)llList2Vector(got, 0)); llSay(0, \"rot=\" + (string)llList2Rot(got, 1)); " +
            "llSay(0, \"name=\" + llList2String(got, 2));");

        Assert.Equal("3", Line(h, "n="));
        Near(offset, Line(h, "pos="));
        // Not the region offset, which the root's quarter turn swings round.
        var regionOffset = ann.AbsolutePosition - parts[0].AbsolutePosition;
        Assert.True(Vector3.Distance(regionOffset, offset) > 0.5f, $"the root's turn should move the region offset: {regionOffset}");
        Near(regionOffset * Quaternion.Inverse(rootRot), Line(h, "pos="));
        Near(rot, Line(h, "rot="));
        Assert.Equal("Ann Sitter", Line(h, "name="));
    }

    // ── OpenSim's own rules ──────────────────────────────────────────────────

    /// <summary>Every rule number OpenSim's LSL_Constants.cs defines and SL does not, with OpenSim's value count.</summary>
    public static TheoryData<string, int> OpenSimRules => new()
    {
        { "PRIM_PHYSICS_MATERIAL", 5 },
    };

    [Theory]
    [MemberData(nameof(OpenSimRules))]
    public void AnOpenSimRuleIsReadByOpenSimsCountWithoutAnErrorAndTheRulesAfterItApply(string name, int count)
    {
        using var h = new SchedulerHarness();
        var prim = h.Prim;
        var code = (int)typeof(ScriptBaseClass).GetField(name)!.GetRawConstantValue()!;
        Assert.Equal(count, PrimParamRules.SetValueCount(code));
        // Zero values: read as rules, 0 would be no rule at all.
        var values = string.Concat(Enumerable.Repeat(", 0", count));
        var item = Run(h, prim,
            $"llSetLinkPrimitiveParamsFast(LINK_THIS, [PRIM_DESC, \"before\", {code}{values}, PRIM_NAME, \"after\", " +
            $"{code}{values}, PRIM_SIZE, <0.25, 0.5, 0.75>]); " +
            $"llSetPrimitiveParams([{code}{values}, PRIM_TEXT, \"after too\", <1, 1, 1>, 1.0]); " +
            $"llSay(0, \"got=\" + llDumpList2String(llGetPrimitiveParams([PRIM_NAME, {code}, PRIM_DESC]), \"|\"));");

        Assert.Equal("before", prim.Description);
        Assert.Equal("after", prim.Name);
        Near(new Vector3(0.25f, 0.5f, 0.75f), prim.Scale);
        Assert.Equal("after too", prim.Text);
        Assert.Equal("after|before", Line(h, "got="));
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
        // Applied (PRIM_PHYSICS_MATERIAL as OpenSim's LSL_Api applies it), so never logged as not implemented.
        Assert.DoesNotContain(code, UnimplementedRulesLogged(h, item));
    }

    [Fact]
    public void EveryRuleNumberOpenSimDefinesIsKnownToTheCountTable()
    {
        // LSL_Constants.cs lines 428-476: the rule constants, not their value constants (PRIM_TYPE_BOX and so on).
        string[] rules =
        {
            "PRIM_MATERIAL", "PRIM_PHYSICS", "PRIM_TEMP_ON_REZ", "PRIM_PHANTOM", "PRIM_POSITION", "PRIM_SIZE",
            "PRIM_ROTATION", "PRIM_TYPE", "PRIM_TEXTURE", "PRIM_COLOR", "PRIM_BUMP_SHINY", "PRIM_FULLBRIGHT",
            "PRIM_FLEXIBLE", "PRIM_TEXGEN", "PRIM_POINT_LIGHT", "PRIM_CAST_SHADOWS", "PRIM_GLOW", "PRIM_TEXT",
            "PRIM_NAME", "PRIM_DESC", "PRIM_ROT_LOCAL", "PRIM_PHYSICS_SHAPE_TYPE", "PRIM_PHYSICS_MATERIAL",
            "PRIM_OMEGA", "PRIM_POS_LOCAL", "PRIM_LINK_TARGET", "PRIM_SLICE", "PRIM_SPECULAR", "PRIM_NORMAL",
            "PRIM_ALPHA_MODE", "PRIM_ALLOW_UNSIT", "PRIM_SCRIPTED_SIT_ONLY", "PRIM_SIT_TARGET", "PRIM_PROJECTOR",
            "PRIM_REFLECTION_PROBE", "PRIM_GLTF_NORMAL", "PRIM_GLTF_EMISSIVE", "PRIM_GLTF_METALLIC_ROUGHNESS",
            "PRIM_GLTF_BASE_COLOR", "PRIM_RENDER_MATERIAL", "PRIM_SIT_FLAGS", "PRIM_DAMAGE", "PRIM_HEALTH",
        };
        var unknown = rules
            .Select(n => (n, code: (int)typeof(ScriptBaseClass).GetField(n)!.GetRawConstantValue()!))
            .Where(r => PrimParamRules.SetValueCount(r.code) < 0)
            .Select(r => $"{r.n} ({r.code})")
            .ToList();
        Assert.True(unknown.Count == 0, "unknown to the count table: " + string.Join(", ", unknown));
    }

    /// <summary>The script's LSLSystemAPI record of the rules it has logged as not implemented yet.</summary>
    private static HashSet<int> UnimplementedRulesLogged(SchedulerHarness h, UUID item)
    {
        var interp = h.InterpreterFor(item);
        Assert.NotNull(interp);
        var shim = interp.GetType().GetField("_syscallShim", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(interp)!;
        var api = shim.GetType().GetProperty("SystemAPI")!.GetValue(shim)!;
        // Null until the script logs its first rule.
        return api.GetType().GetField("m_unimplementedPrimRulesLogged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(api) as HashSet<int> ?? new HashSet<int>();
    }
}
