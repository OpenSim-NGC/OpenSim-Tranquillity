using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Materials;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PRIM_TEXGEN, PRIM_NORMAL, PRIM_SPECULAR, PRIM_ALPHA_MODE, IW_PRIM_ALPHA and PRIM_CAST_SHADOWS set and read
/// back on one face and on ALL_SIDES, against the scene (the face's TexMapType, its colour, and the FaceMaterial the
/// region's materials module keeps for it), with SL's documented error cases (a face that does not exist, values of
/// the wrong type) and the ranges SL gives; the same material state read and written from a YEngine script's API;
/// IW_PRIM_ALPHA against llSetAlpha / llGetAlpha; and PRIM_OMEGA read back in SL's form.
/// </summary>
[Collection("phlox-yengine")]
public class PrimParamsSurfaceMaterialTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsSurfaceMaterialTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string NullKey = "00000000-0000-0000-0000-000000000000";
    private static readonly UUID NormalMap = new("11111111-2222-3333-4444-555555555555");
    private static readonly UUID SpecMap = new("66666666-7777-8888-9999-aaaaaaaaaaaa");

    /// <summary>The region's materials module, as a region with materials enabled (the default) has.</summary>
    private static MaterialsModule Materials(SchedulerHarness h)
    {
        var m = new MaterialsModule();
        SceneHelpers.SetupSceneModules(h.Scene, h.Config, m);
        Assert.Same(m, h.Scene.RequestModuleInterface<IMaterialsModule>());
        return m;
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private void Run(SchedulerHarness h, string body)
    {
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    /// <summary>llSay(0, "<tag>=" + the rules' read, "|"-joined).</summary>
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

    private static void Near(Vector3 expected, string lsl, float tolerance = 0.001f)
    {
        var n = Nums(lsl);
        var actual = new Vector3(n[0], n[1], n[2]);
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {lsl}");
    }

    private static void Near(float expected, string lsl, float tolerance = 0.001f)
        => Assert.True(Math.Abs(expected - Num(lsl)) < tolerance, $"expected {expected}, got {lsl}");

    private static void Near(float expected, float actual, float tolerance = 0.001f)
        => Assert.True(Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}");

    private static FaceMaterial Material(MaterialsModule m, SceneObjectPart part, int face)
    {
        UUID id = part.Shape.Textures.GetFace((uint)face).MaterialID;
        return id == UUID.Zero ? null : m.GetMaterial(id);
    }

    /// <summary>A texture in the prim's inventory, as a builder drops one in.</summary>
    private static UUID AddTexture(SchedulerHarness h, string name)
    {
        var asset = UUID.Random();
        const uint full = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Modify | OpenSim.Framework.PermissionMask.Transfer);
        h.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)AssetType.Texture, InvType = (int)InventoryType.Texture,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, OwnerID = h.Prim.OwnerID,
        }, true);
        return asset;
    }

    // ── PRIM_TEXGEN ───────────────────────────────────────────────────────────

    [Fact]
    public void PrimTexgenSetsOneFaceAndAllSidesAndReadsBack()
    {
        using var h = new SchedulerHarness();
        int sides = h.Prim.GetNumberOfSides();
        Assert.Equal(6, sides);

        Run(h,
            "llSetPrimitiveParams([PRIM_TEXGEN, 1, PRIM_TEXGEN_PLANAR]); " +
            Say("one", "PRIM_TEXGEN, 0, PRIM_TEXGEN, 1") +
            "llSetPrimitiveParams([PRIM_TEXGEN, ALL_SIDES, PRIM_TEXGEN_PLANAR]); " +
            Say("all", "PRIM_TEXGEN, ALL_SIDES") +
            "llSetPrimitiveParams([PRIM_TEXGEN, ALL_SIDES, PRIM_TEXGEN_DEFAULT, PRIM_TEXGEN, 4, PRIM_TEXGEN_PLANAR]); " +
            Say("mixed", "PRIM_TEXGEN, ALL_SIDES"));

        Assert.Equal(new[] { "0", "1" }, Fields(h, "one=", 2));
        Assert.All(Fields(h, "all=", sides), v => Assert.Equal("1", v));
        Assert.Equal(new[] { "0", "0", "0", "0", "1", "0" }, Fields(h, "mixed=", sides));
        Assert.Equal(MappingType.Planar, h.Prim.Shape.Textures.GetFace(4).TexMapType);
        Assert.Equal(MappingType.Default, h.Prim.Shape.Textures.GetFace(3).TexMapType);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void PrimTexgenOnAFaceThatDoesNotExistOrWithAWrongTypeChangesNothingAndTheWalkGoesOn()
    {
        using var h = new SchedulerHarness();

        // SL: PRIM_TEXGEN "silently fails if its face value indicates a face that does not exist". A value of the
        // wrong type skips the rule by its count; the rules after it apply.
        Run(h,
            "llSetPrimitiveParams([PRIM_TEXGEN, 9, PRIM_TEXGEN_PLANAR, PRIM_TEXGEN, \"1\", PRIM_TEXGEN_PLANAR, " +
            "PRIM_TEXGEN, 2, 1.0, PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_TEXGEN, 9, PRIM_TEXGEN, ALL_SIDES, PRIM_NAME"));

        Assert.Equal("after", h.Prim.Name);
        for (uint f = 0; f < 6; f++) Assert.Equal(MappingType.Default, h.Prim.Shape.Textures.GetFace(f).TexMapType);
        // Halcyon: a face that does not exist reads nothing; the rules after it still read.
        Assert.Equal(new[] { "0", "0", "0", "0", "0", "0", "after" }, Fields(h, "got=", 7));
        Assert.Empty(Errors(h));
    }

    // ── PRIM_NORMAL ───────────────────────────────────────────────────────────

    [Fact]
    public void PrimNormalSetsOneFacesMaterialAndReadsBack()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_NORMAL, 0, \"" + NormalMap + "\", <2, 3, 0>, <0.25, 0.5, 0>, 0.5]); " +
            Say("f0", "PRIM_NORMAL, 0") + Say("f1", "PRIM_NORMAL, 1"));

        var mat = Material(m, h.Prim, 0);
        Assert.NotNull(mat);
        Assert.Equal(NormalMap, mat.NormalMapID);
        Near(2f, mat.NormalRepeatX); Near(3f, mat.NormalRepeatY);
        Near(0.25f, mat.NormalOffsetX); Near(0.5f, mat.NormalOffsetY);
        Near(0.5f, mat.NormalRotation);
        Assert.Null(Material(m, h.Prim, 1));

        var f0 = Fields(h, "f0=", 4);
        Assert.Equal(NormalMap.ToString(), f0[0]);
        Near(new Vector3(2, 3, 0), f0[1]);
        Near(new Vector3(0.25f, 0.5f, 0), f0[2]);
        Near(0.5f, f0[3]);
        // SL: a face with no material reads [ NULL_KEY, <1,1,0>, ZERO_VECTOR, 0.0 ].
        var f1 = Fields(h, "f1=", 4);
        Assert.Equal(NullKey, f1[0]);
        Near(new Vector3(1, 1, 0), f1[1]);
        Near(Vector3.Zero, f1[2]);
        Near(0f, f1[3]);
    }

    [Fact]
    public void PrimNormalOnAllSidesGivesEveryFaceTheMapAndNullKeyClearsIt()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_NORMAL, ALL_SIDES, \"" + NormalMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0]); " +
            Say("all", "PRIM_NORMAL, ALL_SIDES") +
            "llSetPrimitiveParams([PRIM_NORMAL, 3, NULL_KEY, <1, 1, 0>, <0, 0, 0>, 0.0]); " +
            Say("cleared", "PRIM_NORMAL, 3, PRIM_NORMAL, 2"));

        var all = Fields(h, "all=", 6 * 4);
        for (int f = 0; f < 6; f++) Assert.Equal(NormalMap.ToString(), all[f * 4]);
        // SL: "To clear the normal map parameters from the face (and possibly remove the material), set texture to
        // NULL_KEY". With nothing else set the face has no material left.
        Assert.Null(Material(m, h.Prim, 3));
        Assert.Equal(NormalMap, Material(m, h.Prim, 2).NormalMapID);
        var cleared = Fields(h, "cleared=", 8);
        Assert.Equal(NullKey, cleared[0]);
        Assert.Equal(NormalMap.ToString(), cleared[4]);
    }

    [Fact]
    public void PrimNormalTakesAnInventoryTextureByNameAndHoldsSlsRanges()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);
        UUID bumps = AddTexture(h, "bumps");

        Run(h,
            "llSetPrimitiveParams([PRIM_NORMAL, 1, \"bumps\", <200, -300, 0>, <-1, 2, 0>, 1.0]); " +
            Say("got", "PRIM_NORMAL, 1"));

        var mat = Material(m, h.Prim, 1);
        Assert.Equal(bumps, mat.NormalMapID);
        // SL: repeats -100..100, offsets 0..1.
        Near(100f, mat.NormalRepeatX); Near(-100f, mat.NormalRepeatY);
        Near(0f, mat.NormalOffsetX); Near(1f, mat.NormalOffsetY);
        // Halcyon (and llGetTexture): a texture in the prim's inventory reads back by its name.
        var got = Fields(h, "got=", 4);
        Assert.Equal("bumps", got[0]);
        Near(new Vector3(100, -100, 0), got[1]);
        Near(new Vector3(0, 1, 0), got[2]);
    }

    [Fact]
    public void PrimNormalWithAnUnknownTextureOrAWrongTypeChangesNothingAndTheWalkGoesOn()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_NORMAL, 0, \"no such texture\", <1, 1, 0>, <0, 0, 0>, 0.0, " +
            "PRIM_NORMAL, 1, \"" + NormalMap + "\", \"<1, 1, 0>\", <0, 0, 0>, 0.0, " +
            "PRIM_NORMAL, 2, \"" + NormalMap + "\", <1, 1, 0>, <0, 0, 0>, \"0.0\", " +
            "PRIM_NORMAL, 7, \"" + NormalMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, " +
            "PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_NORMAL, 7, PRIM_NAME"));

        for (int f = 0; f < 6; f++) Assert.Null(Material(m, h.Prim, f));
        Assert.Equal("after", h.Prim.Name);
        Assert.Equal(new[] { "after" }, Fields(h, "got=", 1));
        Assert.Empty(Errors(h));
    }

    // ── PRIM_SPECULAR ─────────────────────────────────────────────────────────

    [Fact]
    public void PrimSpecularSetsAllSidesAndReadsBackPerFace()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_SPECULAR, ALL_SIDES, \"" + SpecMap + "\", <2, 2, 0>, <0.5, 0.25, 0>, 1.5, " +
            "<1, 0.5, 0>, 200, 30]); " +
            Say("all", "PRIM_SPECULAR, ALL_SIDES") + Say("f5", "PRIM_SPECULAR, 5"));

        for (int f = 0; f < 6; f++)
        {
            var mat = Material(m, h.Prim, f);
            Assert.Equal(SpecMap, mat.SpecularMapID);
            Assert.Equal(255, mat.SpecularLightColorR);
            Assert.Equal(128, mat.SpecularLightColorG);
            Assert.Equal(0, mat.SpecularLightColorB);
            Assert.Equal(200, mat.SpecularLightExponent);
            Assert.Equal(30, mat.EnvironmentIntensity);
        }
        Assert.Equal(6 * 7, Fields(h, "all=", 6 * 7).Length);
        var f5 = Fields(h, "f5=", 7);
        Assert.Equal(SpecMap.ToString(), f5[0]);
        Near(new Vector3(2, 2, 0), f5[1]);
        Near(new Vector3(0.5f, 0.25f, 0), f5[2]);
        Near(1.5f, f5[3]);
        Near(new Vector3(1, 128f / 255f, 0), f5[4]);
        Assert.Equal("200", f5[5]);
        Assert.Equal("30", f5[6]);
    }

    [Fact]
    public void PrimSpecularReadsSlsDefaultsWithNoMaterialAndHoldsGlossAndEnvironmentToAByte()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            Say("none", "PRIM_SPECULAR, 0") +
            "llSetPrimitiveParams([PRIM_SPECULAR, 1, \"" + SpecMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, <2, -1, 0.5>, 300, -5]); " +
            Say("held", "PRIM_SPECULAR, 1"));

        // SL: no material reads [ NULL_KEY, <1,1,0>, ZERO_VECTOR, 0.0, <1,1,1>, 51, 0 ].
        var none = Fields(h, "none=", 7);
        Assert.Equal(NullKey, none[0]);
        Near(new Vector3(1, 1, 0), none[1]);
        Near(Vector3.Zero, none[2]);
        Near(0f, none[3]);
        Near(Vector3.One, none[4]);
        Assert.Equal("51", none[5]);
        Assert.Equal("0", none[6]);

        var held = Fields(h, "held=", 7);
        Near(new Vector3(1, 0, 0.5f), held[4], 0.003f);
        Assert.Equal("255", held[5]);
        Assert.Equal("0", held[6]);
        Assert.Equal(255, Material(m, h.Prim, 1).SpecularLightExponent);
    }

    [Fact]
    public void PrimSpecularWithAWrongTypeOrABadFaceChangesNothing()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_SPECULAR, 0, \"" + SpecMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, <1, 1, 1>, 20.5, 0, " +
            "PRIM_SPECULAR, -2, \"" + SpecMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, <1, 1, 1>, 20, 0, " +
            "PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_SPECULAR, 6, PRIM_SPECULAR, -2, PRIM_NAME"));

        for (int f = 0; f < 6; f++) Assert.Null(Material(m, h.Prim, f));
        Assert.Equal(new[] { "after" }, Fields(h, "got=", 1));
    }

    // ── PRIM_ALPHA_MODE ───────────────────────────────────────────────────────

    [Fact]
    public void PrimAlphaModeSetsTheMaterialsAlphaModeAndCutoffNotTheMediaFlags()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_ALPHA_MODE, 2, PRIM_ALPHA_MODE_MASK, 128]); " +
            Say("f2", "PRIM_ALPHA_MODE, 2") + Say("f0", "PRIM_ALPHA_MODE, 0") +
            "llSetPrimitiveParams([PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_EMISSIVE, 0]); " +
            Say("all", "PRIM_ALPHA_MODE, ALL_SIDES"));

        Assert.Equal(new[] { "2", "128" }, Fields(h, "f2=", 2));
        // SL: a face with no material reads [ PRIM_ALPHA_MODE_BLEND, 0 ].
        Assert.Equal(new[] { "1", "0" }, Fields(h, "f0=", 2));
        var all = Fields(h, "all=", 12);
        for (int f = 0; f < 6; f++)
        {
            Assert.Equal("3", all[f * 2]);
            Assert.Equal("0", all[f * 2 + 1]);
            Assert.Equal(3, Material(m, h.Prim, f).DiffuseAlphaMode);
            Assert.False(h.Prim.Shape.Textures.GetFace((uint)f).MediaFlags);
        }
    }

    [Fact]
    public void PrimAlphaModeBlendOnAFaceWithNoMaterialLeavesNoneAndOutOfRangeValuesChangeNothing()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_ALPHA_MODE, 0, PRIM_ALPHA_MODE_BLEND, 0, " +
            "PRIM_ALPHA_MODE, 1, 4, 0, PRIM_ALPHA_MODE, 2, PRIM_ALPHA_MODE_MASK, 256, " +
            "PRIM_ALPHA_MODE, 3, 2.0, 10, PRIM_ALPHA_MODE, 12, PRIM_ALPHA_MODE_MASK, 10, PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_ALPHA_MODE, 12, PRIM_ALPHA_MODE, 1, PRIM_NAME"));

        for (int f = 0; f < 6; f++) Assert.Null(Material(m, h.Prim, f));
        Assert.Equal(new[] { "1", "0", "after" }, Fields(h, "got=", 3));
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void AlphaModeNormalAndSpecularShareOneFaceMaterial()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_NORMAL, 4, \"" + NormalMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, " +
            "PRIM_ALPHA_MODE, 4, PRIM_ALPHA_MODE_MASK, 77, " +
            "PRIM_SPECULAR, 4, \"" + SpecMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, <1, 1, 1>, 51, 0]); " +
            "llSetPrimitiveParams([PRIM_NORMAL, 4, NULL_KEY, <1, 1, 0>, <0, 0, 0>, 0.0]); " +
            Say("got", "PRIM_ALPHA_MODE, 4, PRIM_NORMAL, 4, PRIM_SPECULAR, 4"));

        var mat = Material(m, h.Prim, 4);
        Assert.Equal(2, mat.DiffuseAlphaMode);
        Assert.Equal(77, mat.AlphaMaskCutoff);
        Assert.Equal(UUID.Zero, mat.NormalMapID);
        Assert.Equal(SpecMap, mat.SpecularMapID);
        var got = Fields(h, "got=", 2 + 4 + 7);
        Assert.Equal(new[] { "2", "77", NullKey }, got.Take(3));
        Assert.Equal(SpecMap.ToString(), got[6]);
    }

    // ── one material state for both engines ───────────────────────────────────

    private static object[] YRead(LSL_Api y, params object[] rules) => y.llGetPrimitiveParams(new LSL_List(rules)).Data;

    [Fact]
    public void AYEngineScriptAndAPhloxScriptSeeTheSameMaterials()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var m = Materials(h);
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var yengine = new LSL_Api();
        yengine.Initialize(h.YEngine, h.Prim, item);

        // YEngine writes face 5; Phlox reads it.
        yengine.llSetPrimitiveParams(new LSL_List(
            36, 5, SpecMap.ToString(), new Vector3(3, 4, 0), new Vector3(0.5f, 0.5f, 0), 0.25f, new Vector3(0, 1, 0), 99, 12,
            38, 5, 2, 200,
            37, 5, NormalMap.ToString(), new Vector3(1, 2, 0), new Vector3(0.1f, 0.2f, 0), 0.75f));
        UUID yID = h.Prim.Shape.Textures.GetFace(5).MaterialID;
        Assert.NotEqual(UUID.Zero, yID);

        // Phlox writes face 1 and reads both faces.
        Run(h,
            "llSetPrimitiveParams([PRIM_SPECULAR, 1, \"" + SpecMap + "\", <3, 4, 0>, <0.5, 0.5, 0>, 0.25, <0, 1, 0>, 99, 12, " +
            "PRIM_ALPHA_MODE, 1, PRIM_ALPHA_MODE_MASK, 200, " +
            "PRIM_NORMAL, 1, \"" + NormalMap + "\", <1, 2, 0>, <0.1, 0.2, 0>, 0.75]); " +
            Say("p5", "PRIM_SPECULAR, 5, PRIM_ALPHA_MODE, 5, PRIM_NORMAL, 5") +
            Say("p1", "PRIM_SPECULAR, 1, PRIM_ALPHA_MODE, 1, PRIM_NORMAL, 1"));

        // The same edits give the same material, stored once and named by its content.
        Assert.Equal(yID, h.Prim.Shape.Textures.GetFace(1).MaterialID);
        Assert.Equal(Line(h, "p5="), Line(h, "p1="));

        var p = Fields(h, "p1=", 7 + 2 + 4);
        var y = YRead(yengine, 36, 1, 38, 1, 37, 1);
        Assert.Equal(13, y.Length);
        Assert.Equal(p[0], y[0].ToString());
        for (int i = 1; i < 13; i++)
        {
            if (i == 9) { Assert.Equal(p[9], y[9].ToString()); continue; } // the normal map key
            var pn = Nums(p[i]);
            var yn = Nums(y[i].ToString());
            Assert.Equal(pn.Length, yn.Length);
            for (int k = 0; k < pn.Length; k++) Near(pn[k], yn[k]);
        }
    }

    // ── IW_PRIM_ALPHA ─────────────────────────────────────────────────────────

    [Fact]
    public void IwPrimAlphaSetsAndReadsAsLlSetAlphaAndLlGetAlpha()
    {
        using var h = new SchedulerHarness();

        Run(h,
            "llSetPrimitiveParams([IW_PRIM_ALPHA, 1, 0.25]); " +
            Say("one", "IW_PRIM_ALPHA, 1, IW_PRIM_ALPHA, 0") +
            "llSay(0, \"ll1=\" + (string)llGetAlpha(1)); " +
            "llSetPrimitiveParams([IW_PRIM_ALPHA, ALL_SIDES, 0.5]); " +
            Say("all", "IW_PRIM_ALPHA, ALL_SIDES") +
            "llSay(0, \"llall=\" + (string)llGetAlpha(ALL_SIDES)); " +
            "llSetAlpha(0.75, 3); " +
            Say("three", "IW_PRIM_ALPHA, 3, IW_PRIM_ALPHA, 6"));

        var one = Fields(h, "one=", 2);
        // A face's colour is kept as bytes, so an alpha reads back to within 1/255.
        Near(0.25f, one[0], 0.005f);
        Near(1f, one[1], 0.005f);
        Near(0.25f, Line(h, "ll1="), 0.005f);
        // ALL_SIDES reads the faces' sum, as llGetAlpha(ALL_SIDES) does (Halcyon GetAlpha, SL llGetAlpha).
        Near(3f, Fields(h, "all=", 1)[0], 0.03f);
        Near(Num(Fields(h, "all=", 1)[0]), Line(h, "llall="));
        for (uint f = 0; f < 6; f++) Near(f == 3 ? 0.75f : 0.5f, h.Prim.Shape.Textures.GetFace(f).RGBA.A, 0.005f);
        var three = Fields(h, "three=", 2);
        Near(0.75f, three[0], 0.005f);
        Near(0f, three[1]); // a face that does not exist reads 0.0 (Halcyon GetAlpha)
    }

    [Fact]
    public void IwPrimAlphaWithAWrongTypeChangesNothing()
    {
        using var h = new SchedulerHarness();

        Run(h,
            "llSetPrimitiveParams([IW_PRIM_ALPHA, 0, \"0.1\", IW_PRIM_ALPHA, 1.0, 0.1, IW_PRIM_ALPHA, 2, 0, PRIM_NAME, \"after\"]); " +
            Say("got", "IW_PRIM_ALPHA, 0, IW_PRIM_ALPHA, 2, PRIM_NAME"));

        var got = Fields(h, "got=", 3);
        Near(1f, got[0], 0.005f);
        Near(0f, got[1], 0.005f); // an integer alpha is taken as a float
        Assert.Equal("after", got[2]);
    }

    // ── PRIM_CAST_SHADOWS ─────────────────────────────────────────────────────

    [Fact]
    public void PrimCastShadowsIsANoOpThatReadsZero()
    {
        using var h = new SchedulerHarness();
        byte[] before = h.Prim.Shape.TextureEntry;
        uint shadows = h.Prim.GetEffectiveObjectFlags() & (uint)PrimFlags.CastShadows;

        // SL: "It will always return 0 and setting it will have no effect."
        Run(h,
            "llSetPrimitiveParams([PRIM_CAST_SHADOWS, TRUE, PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_CAST_SHADOWS, PRIM_NAME"));

        Assert.Equal(new[] { "0", "after" }, Fields(h, "got=", 2));
        Assert.Equal(before, h.Prim.Shape.TextureEntry);
        // Only the shadow flag is compared: rezzing the script sets the object's scripted flag.
        Assert.Equal(shadows, h.Prim.GetEffectiveObjectFlags() & (uint)PrimFlags.CastShadows);
        Assert.Empty(Errors(h));
    }

    // ── PRIM_OMEGA read, SL's form ────────────────────────────────────────────

    [Fact]
    public void PrimOmegaReadsTheNormalisedAxisTheScaledSpinrateAndTheGain()
    {
        using var h = new SchedulerHarness();

        // SL: "the vector is normalized, and the spinrate is multiplied by the magnitude of the original vector".
        Run(h,
            "llSetPrimitiveParams([PRIM_OMEGA, <0, 2, 0>, 1.5, 0.5]); " +
            Say("rule", "PRIM_OMEGA") +
            "llTargetOmega(<3, 0, 4>, -2.0, 0.25); " +
            Say("ll", "PRIM_OMEGA") +
            "llTargetOmega(<1, 0, 0>, 1.0, 0.0); " +
            Say("stopped", "PRIM_OMEGA"));

        var rule = Fields(h, "rule=", 3);
        Near(new Vector3(0, 1, 0), rule[0]);
        Near(3f, rule[1]);
        Near(0.5f, rule[2]);
        var ll = Fields(h, "ll=", 3);
        Near(new Vector3(0.6f, 0, 0.8f), ll[0]);
        Near(-10f, ll[1]);
        Near(0.25f, ll[2]);
        // A gain of 0 stops the spin; the values read are still the ones set.
        var stopped = Fields(h, "stopped=", 3);
        Near(new Vector3(1, 0, 0), stopped[0]);
        Near(1f, stopped[1]);
        Near(0f, stopped[2]);
        Assert.Equal(Vector3.Zero, h.Prim.AngularVelocity);
    }

    [Fact]
    public void PrimOmegaReadsTheScenesSpinWhenSomethingElseChangedIt()
    {
        using var h = new SchedulerHarness();
        h.Prim.UpdateAngularVelocity(new Vector3(0, 0, 3)); // as a YEngine script's llTargetOmega leaves it

        Run(h, Say("other", "PRIM_OMEGA"));
        var other = Fields(h, "other=", 3);
        Near(new Vector3(0, 0, 1), other[0]);
        Near(3f, other[1]);
        Near(1f, other[2]);

        h.ClearSaid(UUID.Zero);
        h.Prim.UpdateAngularVelocity(Vector3.Zero);
        Run(h, Say("none", "PRIM_OMEGA"));
        var none = Fields(h, "none=", 3);
        Near(Vector3.Zero, none[0]);
        Near(0f, none[1]);
        Near(0f, none[2]);
    }

    // ── all the group's rules in one list ─────────────────────────────────────

    [Fact]
    public void OneListSetsAndReadsEveryRuleOfTheGroup()
    {
        using var h = new SchedulerHarness();
        var m = Materials(h);

        Run(h,
            "llSetPrimitiveParams([PRIM_TEXGEN, 0, PRIM_TEXGEN_PLANAR, " +
            "PRIM_NORMAL, 0, \"" + NormalMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, " +
            "PRIM_SPECULAR, 0, \"" + SpecMap + "\", <1, 1, 0>, <0, 0, 0>, 0.0, <1, 1, 1>, 51, 0, " +
            "PRIM_ALPHA_MODE, 0, PRIM_ALPHA_MODE_NONE, 0, IW_PRIM_ALPHA, 0, 0.5, PRIM_CAST_SHADOWS, FALSE, " +
            "PRIM_OMEGA, <0, 0, 1>, 1.0, 1.0, PRIM_NAME, \"all set\"]); " +
            Say("got", "PRIM_TEXGEN, 0, PRIM_NORMAL, 0, PRIM_SPECULAR, 0, PRIM_ALPHA_MODE, 0, IW_PRIM_ALPHA, 0, " +
                       "PRIM_CAST_SHADOWS, PRIM_OMEGA, PRIM_NAME"));

        var got = Fields(h, "got=", 1 + 4 + 7 + 2 + 1 + 1 + 3 + 1);
        Assert.Equal("1", got[0]);
        Assert.Equal(NormalMap.ToString(), got[1]);
        Assert.Equal(SpecMap.ToString(), got[5]);
        Assert.Equal(new[] { "0", "0" }, got.Skip(12).Take(2));
        Near(0.5f, got[14], 0.005f);
        Assert.Equal("0", got[15]);
        Near(new Vector3(0, 0, 1), got[16]);
        Assert.Equal("all set", got[19]);
        Assert.Equal(0, Material(m, h.Prim, 0).DiffuseAlphaMode);
        Assert.Empty(Errors(h));
    }
}
