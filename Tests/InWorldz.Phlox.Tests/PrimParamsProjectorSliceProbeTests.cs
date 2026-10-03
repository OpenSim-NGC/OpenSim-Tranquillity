using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PRIM_SLICE, PRIM_PROJECTOR, the IW_PRIM_PROJECTOR rules, PRIM_REFLECTION_PROBE and OpenSim's
/// PRIM_PHYSICS_MATERIAL set and read back against the scene state each one uses (the shape's PathBegin / PathEnd or
/// ProfileBegin / ProfileEnd, its Projection* fields, its ReflectionProbe, the part's Density / Friction / Restitution /
/// GravityModifier and the physics actor); their value counts in a long list; the error cases; what a YEngine script's
/// API writes read back by Phlox and the other way round; the saved form; llSetPhysicsMaterial sharing the rule's
/// helper (SL's mask bits); CLICK_ACTION_PAY reverting to CLICK_ACTION_NONE without a money event; and
/// llGetRootRotation on an attachment returning the wearer's rotation.
/// </summary>
[Collection("phlox-yengine")]
public class PrimParamsProjectorSliceProbeTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsProjectorSliceProbeTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string NullKey = "00000000-0000-0000-0000-000000000000";
    private static readonly UUID Gobo = new("bbbbbbbb-1111-2222-3333-444444444444");
    private static readonly UUID Other = new("cccccccc-1111-2222-3333-444444444444");

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

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private void Run(SchedulerHarness h, string body) => Run(h, h.Prim, body);

    private void Run(SchedulerHarness h, SceneObjectPart into, string body, string moreEvents = "")
    {
        h.RezScriptInto(into, "default { state_entry() { " + body + " llSay(0, \"done\"); } " + moreEvents + " }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.SaidOn.Select(s => $"[{s.Channel}] {s.Message}")));
    }

    /// <summary>llSay(0, "<tag>=" + the rules' read on <paramref name="link"/>, "|"-joined).</summary>
    private static string Say(string tag, string link, string rules)
        => "llSay(0, \"" + tag + "=\" + llDumpList2String(llGetLinkPrimitiveParams(" + link + ", [" + rules + "]), \"|\")); ";

    private static string Say(string tag, string rules)
        => "llSay(0, \"" + tag + "=\" + llDumpList2String(llGetPrimitiveParams([" + rules + "]), \"|\")); ";

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    private static string[] Fields(SchedulerHarness h, string prefix, int count)
    {
        var line = Line(h, prefix);
        var f = line.Length == 0 ? Array.Empty<string>() : line.Split('|');
        Assert.True(f.Length == count, $"{prefix} expected {count} values, got {f.Length}: {line}");
        return f;
    }

    private static IReadOnlyList<string> Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToList();

    private static float[] Nums(string lsl)
        => lsl.Trim('<', '>').Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();

    private static float Num(string lsl) => float.Parse(lsl.Trim(), CultureInfo.InvariantCulture);

    private static void Near(float expected, string lsl, float tolerance = 0.001f)
        => Assert.True(Math.Abs(expected - Num(lsl)) < tolerance, $"expected {expected}, got {lsl}");

    private static void Near(float expected, float actual, float tolerance = 0.001f)
        => Assert.True(Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}");

    private static void Near(Vector3 expected, string lsl, float tolerance = 0.001f)
    {
        var n = Nums(lsl);
        Assert.True(Vector3.Distance(expected, new Vector3(n[0], n[1], n[2])) < tolerance, $"expected {expected}, got {lsl}");
    }

    private static void Near(Quaternion expected, string lsl)
    {
        var n = Nums(lsl);
        var got = new Quaternion(n[0], n[1], n[2], n[3]);
        Assert.True(Math.Abs(Quaternion.Dot(expected, got)) > 0.9999f, $"expected {expected}, got {lsl}");
    }

    /// <summary>A texture in the prim, as a builder drops one in.</summary>
    private static UUID AddTexture(SceneObjectPart part, string name, UUID asset)
    {
        const uint full = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Modify
                                 | OpenSim.Framework.PermissionMask.Transfer);
        part.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)AssetType.Texture, InvType = (int)InventoryType.Texture,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, OwnerID = part.OwnerID,
        }, true);
        return asset;
    }

    /// <summary>A Phlox script's API on the harness prim for a YEngine script (OpenSim's LSL_Api).</summary>
    private static LSL_Api YEngineApi(SchedulerHarness h, SceneObjectPart part)
    {
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, part, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var api = new LSL_Api();
        api.Initialize(h.YEngine, part, item);
        return api;
    }

    private static LSL_Types.LSLFloat F(double v) => new(v);
    private static LSL_Types.Vector3 V(double x, double y, double z) => new(x, y, z);

    // The PRIM_TYPE lists that make a sphere and a torus with every cut at its full default.
    private const string Sphere = "PRIM_TYPE, PRIM_TYPE_SPHERE, PRIM_HOLE_DEFAULT, <0, 1, 0>, 0.0, <0, 0, 0>, <0, 1, 0>";
    private const string Torus = "PRIM_TYPE, PRIM_TYPE_TORUS, PRIM_HOLE_DEFAULT, <0, 1, 0>, 0.0, <0, 0, 0>, <1, 0.25, 0>, " +
                                 "<0, 0, 0>, <0, 1, 0>, <0, 0, 0>, 1.0, 0.0, 0.0";

    // ── PRIM_SLICE ────────────────────────────────────────────────────────────

    [Fact]
    public void PrimSliceSetsTheBoxSliceOnTheRootAndAChildAndReadsBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h,
            Say("default", "PRIM_SLICE") +
            "llSetPrimitiveParams([PRIM_SLICE, <0.25, 0.75, 0>]); " +
            Say("root", "PRIM_SLICE") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_SLICE, <0.1, 0.5, 0.9>]); " +
            Say("child", "2", "PRIM_SLICE"));

        // SL's full slice; z is ignored and reads 0.
        Near(new Vector3(0, 1, 0), Line(h, "default="));
        Near(new Vector3(0.25f, 0.75f, 0), Line(h, "root="));
        Near(new Vector3(0.1f, 0.5f, 0), Line(h, "child="));
        // The box's slice is its path cut, where PRIM_TYPE writes it.
        Near(0.25f, parts[0].Shape.PathBegin / 50000f);
        Near(0.75f, 1 - parts[0].Shape.PathEnd / 50000f);
        Near(0.5f, 1 - parts[1].Shape.PathEnd / 50000f);
        Assert.Equal(0, parts[0].Shape.ProfileBegin);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void PrimSliceKeepsBeginBelowEndAndInRange()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_SLICE, <0.5, 0.51, 0>]); " + Say("close", "PRIM_SLICE") +
            "llSetPrimitiveParams([PRIM_SLICE, <-1, 2, 0>]); " + Say("wide", "PRIM_SLICE") +
            "llSetPrimitiveParams([PRIM_SLICE, <0.8, 0.2, 0>]); " + Say("swapped", "PRIM_SLICE") +
            "llSetPrimitiveParams([PRIM_SLICE, <0, 0, 0>]); " + Say("none", "PRIM_SLICE"));

        // SL: "x must be at least 0.05 smaller than y" - "the difference can be as small as 0.02".
        Near(new Vector3(0.49f, 0.51f, 0), Line(h, "close="));
        Near(new Vector3(0, 1, 0), Line(h, "wide="));
        Near(new Vector3(0.2f, 0.8f, 0), Line(h, "swapped="));
        Near(new Vector3(0, 0.02f, 0), Line(h, "none="));
    }

    [Fact]
    public void PrimSliceIsASpheresDimpleAndATorussProfileCutAndPrimTypeReadsItBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h,
            "llSetLinkPrimitiveParamsFast(1, [" + Sphere + ", PRIM_SLICE, <0.2, 0.8, 0>]); " +
            "llSetLinkPrimitiveParamsFast(2, [" + Torus + ", PRIM_SLICE, <0.3, 0.6, 0>]); " +
            Say("sphere", "1", "PRIM_SLICE, PRIM_TYPE") + Say("torus", "2", "PRIM_SLICE, PRIM_TYPE") +
            // The other way round: PRIM_TYPE's dimple is read by PRIM_SLICE.
            "llSetLinkPrimitiveParamsFast(1, [PRIM_TYPE, PRIM_TYPE_SPHERE, PRIM_HOLE_DEFAULT, <0, 1, 0>, 0.0, <0, 0, 0>, <0.4, 0.9, 0>]); " +
            Say("dimple", "1", "PRIM_SLICE"));

        var sphere = Fields(h, "sphere=", 1 + 6);
        Near(new Vector3(0.2f, 0.8f, 0), sphere[0]);
        Assert.Equal("3", sphere[1]);
        Near(new Vector3(0.2f, 0.8f, 0), sphere[6]);       // [type, hole, cut, hollow, twist, dimple]
        var torus = Fields(h, "torus=", 1 + 12);
        Near(new Vector3(0.3f, 0.6f, 0), torus[0]);
        Assert.Equal("4", torus[1]);
        Near(new Vector3(0.3f, 0.6f, 0), torus[8]);        // [type, hole, cut, hollow, twist, size, shear, profilecut, ...]
        // The path cut is left alone.
        Near(new Vector3(0, 1, 0), torus[3]);
        Near(new Vector3(0.4f, 0.9f, 0), Line(h, "dimple="));
        Near(0.3f, parts[1].Shape.ProfileBegin / 50000f);
    }

    [Fact]
    public void PrimSliceLeavesABoxsPrimTypeAloneAndPrimTypeUnslicesIt()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, PRIM_HOLE_CIRCLE, <0.1, 0.9, 0>, 0.3, <0, 0, 0>, <1, 1, 0>, <0, 0, 0>]); " +
            Say("before", "PRIM_TYPE") +
            "llSetPrimitiveParams([PRIM_SLICE, <0.25, 0.75, 0>]); " +
            Say("after", "PRIM_TYPE") + Say("sliced", "PRIM_SLICE") +
            "llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_BOX, PRIM_HOLE_CIRCLE, <0.1, 0.9, 0>, 0.3, <0, 0, 0>, <1, 1, 0>, <0, 0, 0>]); " +
            Say("unsliced", "PRIM_SLICE"));

        Assert.Equal(Line(h, "before="), Line(h, "after="));
        Near(new Vector3(0.25f, 0.75f, 0), Line(h, "sliced="));
        // SL (llSetPrimitiveParams, PRIM_TYPE): "A sliced prim will become unsliced."
        Near(new Vector3(0, 1, 0), Line(h, "unsliced="));
    }

    [Fact]
    public void PrimSliceLeavesASculptAloneAndReadsTheFullSlice()
    {
        using var h = new SchedulerHarness();
        h.Prim.Shape.SculptEntry = true;
        h.Prim.Shape.SculptTexture = Gobo;
        h.Prim.Shape.SculptType = (byte)SculptType.Sphere;
        h.Prim.Shape.PathCurve = (byte)Extrusion.Curve1;

        Run(h, "llSetPrimitiveParams([PRIM_SLICE, <0.25, 0.75, 0>, PRIM_NAME, \"after\"]); " + Say("got", "PRIM_SLICE"));

        Near(new Vector3(0, 1, 0), Line(h, "got="));
        Assert.Equal(0, h.Prim.Shape.PathBegin);
        Assert.Equal(0, h.Prim.Shape.ProfileBegin);
        Assert.Equal("after", h.Prim.Name);
    }

    // ── PRIM_PROJECTOR ────────────────────────────────────────────────────────

    [Fact]
    public void PrimProjectorSetsTheProjectionAndReadsBackOnTheRootAndAChild()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        AddTexture(parts[0], "gobo", Gobo);

        Run(h,
            Say("default", "PRIM_PROJECTOR") +
            "llSetPrimitiveParams([PRIM_PROJECTOR, \"" + Other + "\", 1.5, 2.0, 0.5]); " +
            Say("root", "PRIM_PROJECTOR") +
            // A texture named in the script's prim.
            "llSetLinkPrimitiveParamsFast(2, [PRIM_PROJECTOR, \"gobo\", 0.75, -3.0, 0.25]); " +
            Say("child", "2", "PRIM_PROJECTOR"));

        // SL: "If the prim is not a projector the texture key will be NULL_KEY."
        var d = Fields(h, "default=", 4);
        Assert.Equal(NullKey, d[0]);
        Near(0f, d[1]); Near(0f, d[2]); Near(0f, d[3]);

        var r = Fields(h, "root=", 4);
        Assert.Equal(Other.ToString(), r[0]);
        Near(1.5f, r[1]); Near(2f, r[2]); Near(0.5f, r[3]);
        Assert.True(parts[0].Shape.ProjectionEntry);
        Assert.Equal(Other, parts[0].Shape.ProjectionTextureUUID);

        var c = Fields(h, "child=", 4);
        // Named, as Halcyon's texture reads (ConditionalTextureNameOrUUID) name a texture the script's prim holds.
        Assert.Equal("gobo", c[0]);
        Near(0.75f, c[1]); Near(-3f, c[2]); Near(0.25f, c[3]);
        Assert.Equal(Gobo, parts[1].Shape.ProjectionTextureUUID);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void PrimProjectorHoldsItsValuesToOpenSimsRangesAndNullKeyTurnsItOff()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_PROJECTOR, \"" + Gobo + "\", 5.0, -30.0, 2.0]); " +
            Say("held", "PRIM_PROJECTOR") +
            "llSetPrimitiveParams([PRIM_PROJECTOR, NULL_KEY, 1.0, 1.0, 1.0]); " +
            Say("off", "PRIM_PROJECTOR") +
            "llSetPrimitiveParams([PRIM_PROJECTOR, \"" + Gobo + "\", 1, 2, 0]); " +     // integers taken as floats
            "llSetPrimitiveParams([PRIM_PROJECTOR, \"\", 1.0, 1.0, 1.0]); " +
            Say("empty", "PRIM_PROJECTOR"));

        var held = Fields(h, "held=", 4);
        Near(3f, held[1]); Near(-20f, held[2]); Near(1f, held[3]);
        Assert.Equal(NullKey, Fields(h, "off=", 4)[0]);
        Assert.Equal(NullKey, Fields(h, "empty=", 4)[0]);
        Assert.False(h.Prim.Shape.ProjectionEntry);
    }

    [Fact]
    public void PrimProjectorWithATextureNotInThePrimShoutsAndChangesNothing()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_PROJECTOR, \"" + Gobo + "\", 1.0, 2.0, 0.5]); " +
            "llSetPrimitiveParams([PRIM_PROJECTOR, \"missing\", 0.1, 0.1, 0.1, PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_PROJECTOR"));

        Assert.Contains(Errors(h), m => m.Contains("Could not find texture 'missing'"));
        var got = Fields(h, "got=", 4);
        Assert.Equal(Gobo.ToString(), got[0]);
        Near(1f, got[1]);
        Assert.Equal("after", h.Prim.Name);
    }

    // ── IW_PRIM_PROJECTOR and its single-value rules ──────────────────────────

    [Fact]
    public void IwPrimProjectorSetsAllFiveAndEachSingleRuleReadsWhatAnotherSet()
    {
        using var h = new SchedulerHarness();
        AddTexture(h.Prim, "gobo", Gobo);

        Run(h,
            Say("default", "IW_PRIM_PROJECTOR") +
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR, 1, \"gobo\", 1.25, 4.0, 0.75]); " +
            Say("iw", "IW_PRIM_PROJECTOR") +
            Say("singles", "IW_PRIM_PROJECTOR_ENABLED, IW_PRIM_PROJECTOR_TEXTURE, IW_PRIM_PROJECTOR_FOV, IW_PRIM_PROJECTOR_FOCUS, IW_PRIM_PROJECTOR_AMBIENCE") +
            Say("sl", "PRIM_PROJECTOR") +
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR_FOV, 0.5, IW_PRIM_PROJECTOR_FOCUS, -2.0, IW_PRIM_PROJECTOR_AMBIENCE, 0.1, " +
            "IW_PRIM_PROJECTOR_TEXTURE, \"" + Other + "\"]); " +
            Say("changed", "PRIM_PROJECTOR") +
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR_ENABLED, 0]); " +
            Say("disabled", "IW_PRIM_PROJECTOR_ENABLED, PRIM_PROJECTOR"));

        var d = Fields(h, "default=", 5);
        Assert.Equal("0", d[0]);
        Assert.Equal(NullKey, d[1]);

        // Halcyon reads the texture's name when the prim holds it.
        var iw = Fields(h, "iw=", 5);
        Assert.Equal(new[] { "1", "gobo" }, iw.Take(2));
        Near(1.25f, iw[2]); Near(4f, iw[3]); Near(0.75f, iw[4]);
        Assert.Equal(iw, Fields(h, "singles=", 5));
        var sl = Fields(h, "sl=", 4);
        Assert.Equal("gobo", sl[0]);
        Near(1.25f, sl[1]);

        var changed = Fields(h, "changed=", 4);
        Assert.Equal(Other.ToString(), changed[0]);
        Near(0.5f, changed[1]); Near(-2f, changed[2]); Near(0.1f, changed[3]);

        var off = Fields(h, "disabled=", 1 + 4);
        Assert.Equal("0", off[0]);
        Assert.Equal(NullKey, off[1]);
        // The texture and values stay on the shape for the next enable.
        Assert.Equal(Other, h.Prim.Shape.ProjectionTextureUUID);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void IwPrimProjectorKeepsHalcyonsRulesForEnabledAndUnclampedValues()
    {
        using var h = new SchedulerHarness();
        Run(h,
            // Only 1 enables (Halcyon: "== 1"); the values are kept as given.
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR, 2, \"" + Gobo + "\", 5.0, 30.0, 2.0]); " +
            Say("two", "IW_PRIM_PROJECTOR") +
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR_ENABLED, 1]); " +
            Say("one", "IW_PRIM_PROJECTOR_ENABLED"));

        var two = Fields(h, "two=", 5);
        Assert.Equal("0", two[0]);
        Assert.Equal(Gobo.ToString(), two[1]);
        Near(5f, two[2]); Near(30f, two[3]); Near(2f, two[4]);
        Assert.Equal("1", Line(h, "one="));
    }

    [Fact]
    public void IwPrimProjectorRulesRefuseNullKeyWithHalcyonsErrorsAndTheRestApply()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR, 1, \"" + Gobo + "\", 1.0, 1.0, 1.0]); " +
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR, 1, NULL_KEY, 2.0, 2.0, 0.5, PRIM_NAME, \"one\"]); " +
            "llSetPrimitiveParams([IW_PRIM_PROJECTOR_TEXTURE, \"nothing here\", PRIM_DESC, \"two\"]); " +
            Say("got", "IW_PRIM_PROJECTOR"));

        var errors = Errors(h);
        Assert.Contains(errors, m => m.Contains("The second argument of IW_PRIM_PROJECTOR must not be NULL_KEY."));
        Assert.Contains(errors, m => m.Contains("The argument of IW_PRIM_PROJECTOR_TEXTURE must not be NULL_KEY."));
        var got = Fields(h, "got=", 5);
        Assert.Equal(new[] { "1", Gobo.ToString() }, got.Take(2));
        Near(1f, got[2]);
        Assert.Equal("one", h.Prim.Name);
        Assert.Equal("two", h.Prim.Description);
    }

    // ── PRIM_REFLECTION_PROBE ─────────────────────────────────────────────────

    [Fact]
    public void PrimReflectionProbeSetsAndReadsBackOnTheRootAndAChildAndFalseRemovesIt()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h,
            Say("default", "PRIM_REFLECTION_PROBE") +
            "llSetPrimitiveParams([PRIM_REFLECTION_PROBE, TRUE, 50.0, 10.0, PRIM_REFLECTION_PROBE_BOX | PRIM_REFLECTION_PROBE_MIRROR]); " +
            Say("root", "PRIM_REFLECTION_PROBE") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_REFLECTION_PROBE, 1, 0.5, 2, PRIM_REFLECTION_PROBE_DYNAMIC]); " +
            Say("child", "2", "PRIM_REFLECTION_PROBE") +
            "llSetPrimitiveParams([PRIM_REFLECTION_PROBE, FALSE, 1.0, 1.0, 0]); " +
            Say("off", "PRIM_REFLECTION_PROBE"));

        var d = Fields(h, "default=", 4);
        Assert.Equal("0", d[0]); Near(0f, d[1]); Near(0f, d[2]); Assert.Equal("0", d[3]);
        var r = Fields(h, "root=", 4);
        Assert.Equal("1", r[0]); Near(50f, r[1]); Near(10f, r[2]);
        Assert.Equal("5", r[3]);                 // SL's integer flags, not a float
        var c = Fields(h, "child=", 4);
        Assert.Equal("1", c[0]); Near(0.5f, c[1]); Near(2f, c[2]); Assert.Equal("2", c[3]);
        Assert.Equal("0", Fields(h, "off=", 4)[0]);
        Assert.Null(parts[0].Shape.ReflectionProbe);
        Assert.NotNull(parts[1].Shape.ReflectionProbe);
        Assert.Equal(2, parts[1].Shape.ReflectionProbe.Flags);
    }

    [Fact]
    public void PrimReflectionProbeHoldsAmbianceAndClipDistanceToSlsRanges()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_REFLECTION_PROBE, TRUE, 150.0, 2000.0, 0]); " + Say("high", "PRIM_REFLECTION_PROBE") +
            "llSetPrimitiveParams([PRIM_REFLECTION_PROBE, TRUE, -5.0, -1.0, 0]); " + Say("low", "PRIM_REFLECTION_PROBE"));

        // SL: ambiance "Ranges from 0.0 to 100.0", clip_distance "Ranges from 0.0 to 1024.0".
        var high = Fields(h, "high=", 4);
        Near(100f, high[1]); Near(1024f, high[2]);
        var low = Fields(h, "low=", 4);
        Near(0f, low[1]); Near(0f, low[2]);
    }

    // ── PRIM_PHYSICS_MATERIAL and llSetPhysicsMaterial ────────────────────────

    [Fact]
    public void PrimPhysicsMaterialReachesThePartAndAPhysicalPrimsActor()
    {
        using var h = new SchedulerHarness();
        var actor = new NullPhysicsActor();
        h.Prim.PhysActor = actor;
        h.Prim.AddFlag(PrimFlags.Physics);
        Assert.True((h.Prim.Flags & PrimFlags.Physics) != 0);

        Run(h, "llSetPrimitiveParams([PRIM_PHYSICS_MATERIAL, DENSITY | FRICTION | RESTITUTION | GRAVITY_MULTIPLIER, " +
               "500.0, 0.7, 0.4, 2.0, PRIM_NAME, \"after\"]);");

        Near(500f, h.Prim.Density);
        Near(0.7f, h.Prim.Friction);
        Near(0.4f, h.Prim.Restitution);
        Near(2f, h.Prim.GravityModifier);
        Assert.Same(actor, h.Prim.PhysActor);
        Near(500f, actor.Density);
        Near(0.7f, actor.Friction);
        Near(0.4f, actor.Restitution);
        Near(2f, actor.GravModifier);
        Assert.Equal("after", h.Prim.Name);
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    [Fact]
    public void PrimPhysicsMaterialChangesOnlyTheMaskedValuesOnAChildAndIgnoresOutOfRangeOnes()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        float gravity = parts[1].GravityModifier, restitution = parts[1].Restitution, density = parts[1].Density;

        Run(h, "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS_MATERIAL, FRICTION, 5000.0, 0.9, 0.9, 9.0]); " +
               // Density below SL's 1.0 changes nothing (the scene's setter); gravity in range does.
               "llSetLinkPrimitiveParamsFast(2, [PRIM_PHYSICS_MATERIAL, DENSITY | GRAVITY_MULTIPLIER, 0.5, 0, 0, 3]);");

        Near(0.9f, parts[1].Friction);
        Near(restitution, parts[1].Restitution);
        Near(density, parts[1].Density);
        Near(3f, parts[1].GravityModifier);
        Assert.NotEqual(gravity, parts[1].GravityModifier);
        // The root is untouched.
        Assert.NotEqual(0.9f, parts[0].Friction);
    }

    [Fact]
    public void LlSetPhysicsMaterialUsesSlsMaskBitsThroughTheSameHelper()
    {
        using var h = new SchedulerHarness();
        var actor = new NullPhysicsActor();
        h.Prim.PhysActor = actor;
        float gravity = h.Prim.GravityModifier, friction = h.Prim.Friction;

        // SL: DENSITY 1 "Indicates that density parameter is enabled"; the gravity multiplier is the first value.
        Run(h, "llSetPhysicsMaterial(DENSITY | RESTITUTION, 5.0, 0.25, 9.0, 700.0); " +
               "llSay(0, \"got=\" + llDumpList2String(llGetPhysicsMaterial(), \"|\"));");

        Near(700f, h.Prim.Density);
        Near(0.25f, h.Prim.Restitution);
        Near(gravity, h.Prim.GravityModifier);
        Near(friction, h.Prim.Friction);
        Near(700f, actor.Density);
        // [gravity_multiplier, restitution, friction, density]
        var got = Fields(h, "got=", 4);
        Near(gravity, got[0]); Near(0.25f, got[1]); Near(friction, got[2]); Near(700f, got[3]);
    }

    [Fact]
    public void LlSetPhysicsMaterialSilentlyFailsInAnAttachment()
    {
        using var h = new SchedulerHarness();
        Wear(h, Quaternion.Identity);
        float density = h.Prim.Density, gravity = h.Prim.GravityModifier;

        Run(h, "llSetPhysicsMaterial(DENSITY | GRAVITY_MULTIPLIER, 3.0, 0.5, 0.5, 900.0);");

        // SL: "llSetPhysicsMaterial silently fails if called from an attachment."
        Near(density, h.Prim.Density);
        Near(gravity, h.Prim.GravityModifier);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void PrimPhysicsMaterialIsNoLongerLoggedAsNotImplementedAndReadsNothing()
    {
        using var h = new SchedulerHarness();
        Run(h, "llSetPrimitiveParams([PRIM_PHYSICS_MATERIAL, 0, 0, 0, 0, 0]); " +
               Say("got", "PRIM_NAME, PRIM_PHYSICS_MATERIAL, PRIM_DESC"));

        // OpenSim's LSL_Api has no read for its own rule; neither has Phlox.
        Assert.Equal(h.Prim.Name + "|" + h.Prim.Description, Line(h, "got="));
        Assert.DoesNotContain(Errors(h), m => m.Contains("error running rule"));
    }

    // ── All of them in one list ───────────────────────────────────────────────

    [Fact]
    public void EveryNewRuleTakesItsCountInALongListAndTheRulesBetweenApply()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_NAME, \"a\", " +
            "PRIM_SLICE, <0.25, 0.75, 0>, PRIM_DESC, \"b\", " +
            "PRIM_PROJECTOR, \"" + Gobo + "\", 1.0, 2.0, 0.5, PRIM_SIZE, <0.5, 0.6, 0.7>, " +
            "IW_PRIM_PROJECTOR, 1, \"" + Other + "\", 0.5, 1.5, 0.25, PRIM_TEXT, \"hover\", <1, 1, 1>, 1.0, " +
            "IW_PRIM_PROJECTOR_ENABLED, 1, IW_PRIM_PROJECTOR_TEXTURE, \"" + Gobo + "\", IW_PRIM_PROJECTOR_FOV, 0.75, " +
            "IW_PRIM_PROJECTOR_FOCUS, 3.0, IW_PRIM_PROJECTOR_AMBIENCE, 0.5, PRIM_CLICK_ACTION, CLICK_ACTION_SIT, " +
            "PRIM_REFLECTION_PROBE, TRUE, 20.0, 5.0, 0, PRIM_GLOW, ALL_SIDES, 0.5, " +
            "PRIM_PHYSICS_MATERIAL, FRICTION, 1.0, 0.8, 0.5, 1.0, PRIM_NAME, \"z\"]); " +
            Say("got", "PRIM_SLICE, PRIM_PROJECTOR, IW_PRIM_PROJECTOR, IW_PRIM_PROJECTOR_ENABLED, IW_PRIM_PROJECTOR_TEXTURE, " +
                       "IW_PRIM_PROJECTOR_FOV, IW_PRIM_PROJECTOR_FOCUS, IW_PRIM_PROJECTOR_AMBIENCE, PRIM_REFLECTION_PROBE, " +
                       "PRIM_PHYSICS_MATERIAL, PRIM_NAME, PRIM_DESC"));

        Assert.Equal("z", h.Prim.Name);
        Assert.Equal("b", h.Prim.Description);
        Assert.Equal("hover", h.Prim.Text);
        Assert.Equal(1, h.Prim.ClickAction);
        Near(0.8f, h.Prim.Friction);
        var got = Fields(h, "got=", 1 + 4 + 5 + 1 + 1 + 1 + 1 + 1 + 4 + 0 + 1 + 1);
        Near(new Vector3(0.25f, 0.75f, 0), got[0]);
        Assert.Equal(Gobo.ToString(), got[1]);      // the last texture set, by any of the rules
        Near(0.75f, got[2]); Near(3f, got[3]); Near(0.5f, got[4]);
        Assert.Equal("1", got[5]);
        Assert.Equal("1", got[15]);
        Near(20f, got[16]);
        Assert.Equal(new[] { "z", "b" }, got.Skip(19));
        Assert.Empty(Errors(h));
    }

    // ── One state for both engines ────────────────────────────────────────────

    [Fact]
    public void WhatAYEngineScriptSetsAPhloxScriptReads()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var parts = TwoPrimLinkset(h);
        var yengine = YEngineApi(h, parts[0]);

        // YEngine (LSL_Api.SetPrimParams): a box slice on the root, a sphere slice on the child, a projector, a probe
        // and a physics material.
        yengine.llSetPrimitiveParams(new LSL_List(35, V(0.2, 0.7, 0), 42, Gobo.ToString(), F(1.0), F(2.0), F(0.5),
            44, 1, F(40.0), F(8.0), 1, 31, 2, F(1.0), F(0.6), F(0.5), F(1.0)));
        yengine.llSetLinkPrimitiveParamsFast(2, new LSL_List(9, 3, 0, V(0, 1, 0), F(0.0), V(0, 0, 0), V(0, 1, 0),
            35, V(0.3, 0.9, 0)));

        Run(h, Say("root", "PRIM_SLICE, PRIM_PROJECTOR, PRIM_REFLECTION_PROBE") + Say("child", "2", "PRIM_SLICE"));

        var root = Fields(h, "root=", 1 + 4 + 4);
        Near(new Vector3(0.2f, 0.7f, 0), root[0]);
        Assert.Equal(Gobo.ToString(), root[1]);
        Near(1f, root[2]); Near(2f, root[3]); Near(0.5f, root[4]);
        Assert.Equal("1", root[5]); Near(40f, root[6]); Near(8f, root[7]); Assert.Equal("1", root[8]);
        Near(new Vector3(0.3f, 0.9f, 0), Line(h, "child="));
        Near(0.6f, parts[0].Friction);
    }

    [Fact]
    public void WhatAPhloxScriptSetsAYEngineScriptReads()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var parts = TwoPrimLinkset(h);
        var yengine = YEngineApi(h, parts[0]);

        Run(h,
            "llSetPrimitiveParams([PRIM_SLICE, <0.15, 0.85, 0>, PRIM_PROJECTOR, \"" + Other + "\", 0.5, -1.0, 0.25, " +
            "PRIM_REFLECTION_PROBE, TRUE, 30.0, 3.0, PRIM_REFLECTION_PROBE_DYNAMIC]); " +
            "llSetLinkPrimitiveParamsFast(2, [" + Torus + ", PRIM_SLICE, <0.4, 0.8, 0>]);");

        var got = yengine.llGetPrimitiveParams(new LSL_List(35, 42, 44));
        var slice = (LSL_Types.Vector3)got.Data[0];
        Near(0.15f, (float)slice.x); Near(0.85f, (float)slice.y);
        Assert.Equal(Other.ToString(), got.Data[1].ToString());
        Near(0.5f, (float)((LSL_Types.LSLFloat)got.Data[2]).value);
        Near(-1f, (float)((LSL_Types.LSLFloat)got.Data[3]).value);
        Assert.Equal(1, ((LSL_Types.LSLInteger)got.Data[5]).value);
        Near(30f, (float)((LSL_Types.LSLFloat)got.Data[6]).value);

        var child = yengine.llGetLinkPrimitiveParams(2, new LSL_List(35));
        var childSlice = (LSL_Types.Vector3)child.Data[0];
        Near(0.4f, (float)childSlice.x); Near(0.8f, (float)childSlice.y);
    }

    [Fact]
    public void TheNewRulesSurviveTheSavedForm()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_SLICE, <0.25, 0.75, 0>, PRIM_PROJECTOR, \"" + Gobo + "\", 1.0, 2.0, 0.5, " +
            // An ambiance within 0..1: the scene's loader clamps a saved one to that (see the skipped test below).
            "PRIM_REFLECTION_PROBE, TRUE, 0.75, 10.0, PRIM_REFLECTION_PROBE_BOX, " +
            "PRIM_PHYSICS_MATERIAL, DENSITY | FRICTION, 800.0, 0.3, 0.0, 0.0]);");

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
        var copy = SceneObjectSerializer.FromOriginalXmlFormat(xml).RootPart;

        Assert.Equal(h.Prim.Shape.PathBegin, copy.Shape.PathBegin);
        Assert.Equal(h.Prim.Shape.PathEnd, copy.Shape.PathEnd);
        Assert.True(copy.Shape.ProjectionEntry);
        Assert.Equal(Gobo, copy.Shape.ProjectionTextureUUID);
        Near(2f, copy.Shape.ProjectionFocus);
        Assert.NotNull(copy.Shape.ReflectionProbe);
        Near(0.75f, copy.Shape.ReflectionProbe.Ambiance);
        Near(10f, copy.Shape.ReflectionProbe.ClipDistance);
        Assert.Equal(1, copy.Shape.ReflectionProbe.Flags);
        Near(800f, copy.Density);
        Near(0.3f, copy.Friction);
    }

    [Fact]
    public void AReflectionProbeAmbianceAboveOneSurvivesTheSavedForm()
    {
        using var h = new SchedulerHarness();
        Run(h, "llSetPrimitiveParams([PRIM_REFLECTION_PROBE, TRUE, 50.0, 10.0, 0]);");

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
        var copy = SceneObjectSerializer.FromOriginalXmlFormat(xml).RootPart;

        // SL: ambiance "Ranges from 0.0 to 100.0".
        Near(50f, copy.Shape.ReflectionProbe.Ambiance);
    }

    // ── CLICK_ACTION_PAY ──────────────────────────────────────────────────────

    private const string MoneyEvent = "money(key id, integer amount) { }";

    [Fact]
    public void ClickActionPayWithoutAMoneyEventRevertsToNone()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetClickAction(CLICK_ACTION_PAY); " + Say("ll", "PRIM_CLICK_ACTION") +
            "llSetPrimitiveParams([PRIM_CLICK_ACTION, CLICK_ACTION_PAY]); " + Say("rule", "PRIM_CLICK_ACTION") +
            "llSetClickAction(CLICK_ACTION_BUY); " + Say("buy", "PRIM_CLICK_ACTION"));

        // SL: "If llSetClickAction is CLICK_ACTION_PAY then you must have a money event, or it will revert to
        // CLICK_ACTION_NONE."
        Assert.Equal("0", Line(h, "ll="));
        Assert.Equal("0", Line(h, "rule="));
        Assert.Equal("2", Line(h, "buy="));   // other actions are untouched
    }

    [Fact]
    public void ClickActionPayWithAMoneyEventStays()
    {
        using var h = new SchedulerHarness();
        Run(h, h.Prim,
            "llSetClickAction(CLICK_ACTION_PAY); " + Say("ll", "PRIM_CLICK_ACTION") +
            "llSetClickAction(CLICK_ACTION_TOUCH); " +
            "llSetPrimitiveParams([PRIM_CLICK_ACTION, CLICK_ACTION_PAY]); " + Say("rule", "PRIM_CLICK_ACTION"),
            MoneyEvent);

        Assert.Equal("3", Line(h, "ll="));
        Assert.Equal("3", Line(h, "rule="));
        Assert.Equal(3, h.Prim.ClickAction);
    }

    [Fact]
    public void ClickActionPayOnAChildCountsTheRootsMoneyEventWhichThePaymentReaches()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        // No money event anywhere: the child reverts.
        Run(h, parts[1], "llSetClickAction(CLICK_ACTION_PAY);");
        Assert.Equal(0, parts[1].ClickAction);

        // A money event in the root: a payment on the child reaches it, so PAY stays.
        h.ClearSaid(UUID.Zero);
        Run(h, parts[0], "llSetLinkPrimitiveParamsFast(2, [PRIM_CLICK_ACTION, CLICK_ACTION_PAY]);", MoneyEvent);
        Assert.Equal(3, parts[1].ClickAction);
    }

    // ── llGetRootRotation ─────────────────────────────────────────────────────

    /// <summary>The harness object worn on a new avatar's chest, the wearer turned by <paramref name="wearerRot"/>.</summary>
    private static ScenePresence Wear(SchedulerHarness h, Quaternion wearerRot)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = (uint)AttachmentPoint.Chest;
        sp.AddAttachment(sog);
        sog.AbsolutePosition = new Vector3(0, 0, 0.2f);
        sog.RootPart.AttachedPos = new Vector3(0, 0, 0.2f);
        sog.RootPart.UpdateRotation(Quaternion.CreateFromEulers((float)Math.PI / 4, 0, 0));
        sp.Rotation = wearerRot;
        return sp;
    }

    [Fact]
    public void LlGetRootRotationOnARezzedObjectIsTheRootsRotationFromAChildToo()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var rootRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 3);
        parts[0].UpdateRotation(rootRot);
        parts[1].UpdateRotation(Quaternion.CreateFromEulers((float)Math.PI / 2, 0, 0));

        Run(h, parts[1], "llSay(0, \"root=\" + (string)llGetRootRotation());");

        Near(rootRot, Line(h, "root="));
    }

    [Fact]
    public void LlGetRootRotationOnAnAttachmentIsTheWearersRotationFromTheRootAndAChild()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var wearerRot = Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2);
        Wear(h, wearerRot);

        Run(h, parts[0], "llSay(0, \"root=\" + (string)llGetRootRotation() + \"|\" + (string)llGetRot());");
        Run(h, parts[1], "llSay(0, \"child=\" + (string)llGetRootRotation());");

        // SL: "In an attached object, returns region rotation of avatar NOT of the object's root prim."
        var root = Line(h, "root=").Split('|');
        Near(wearerRot, root[0]);
        Near(wearerRot, root[1]);           // the same read as llGetRot on the root
        Near(wearerRot, Line(h, "child="));
    }

    [Fact]
    public void LlGetRootRotationOnASeatedWearersAttachmentIsItsRegionRotation()
    {
        using var h = new SchedulerHarness();
        var sp = Wear(h, Quaternion.Identity);
        var seat = SceneHelpers.AddSceneObject(h.Scene, "seat", h.Prim.OwnerID).RootPart;
        seat.ParentGroup.UpdateGroupRotationR(Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2));
        sp.AbsolutePosition = seat.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.UUID, Vector3.Zero);
        Assert.NotEqual(0u, sp.ParentID);
        var world = sp.GetWorldRotation();
        Assert.True(Math.Abs(Quaternion.Dot(world, sp.Rotation)) < 0.9999f, "the seat's turn should change the wearer's region rotation");

        Run(h, "llSay(0, \"root=\" + (string)llGetRootRotation());");

        // SL: "Returns an accurate facing for Avatars seated"; not the rotation relative to the seat.
        Near(world, Line(h, "root="));
    }
}
