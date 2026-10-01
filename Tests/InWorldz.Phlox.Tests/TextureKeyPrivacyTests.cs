using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Which asset keys a script may see:
/// - llGetInventoryKey and iwGetLinkInventoryKey: the asset key of an item that is copy, modify and transfer for its
///   owner, else NULL_KEY (llGetInventoryKey's rule; Halcyon's GetInventoryKey used it for both).
/// - llGetTexture, PRIM_TEXTURE, the PRIM_TYPE sculpt map, the projector reads: Halcyon's ConditionalTextureNameOrUUID -
///   the name when the texture is in the script's prim, else the key when the object is full-perm for its owner, else
///   NULL_KEY.
/// - llGetRenderMaterial and PRIM_RENDER_MATERIAL (Halcyon has none): SL's rule, "NULL_KEY is returned when the owner
///   does not have full permissions to the object and the Material is not in the prim's inventory"; the GLTF texture
///   reads take the same rule.
/// Each case runs with a full-perm, no-copy, no-mod and no-transfer object or item, from the owner's script and from a
/// script another avatar put in.
/// </summary>
// Test grouping: this class builds its own harness and touches no process-wide seam, so it runs in parallel.
public class TextureKeyPrivacyTests
{
    private readonly ITestOutputHelper _out;
    public TextureKeyPrivacyTests(ITestOutputHelper o) => _out = o;

    private const string NullKey = "00000000-0000-0000-0000-000000000000";
    private const uint Full = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer);
    private const uint NoCopy = (uint)(PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move);
    private const uint NoMod = (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Move);
    private const uint NoTrans = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Move);

    private static readonly UUID FaceTex = new("11111111-0000-0000-0000-000000000001");
    private static readonly UUID SculptMap = new("11111111-0000-0000-0000-000000000002");
    private static readonly UUID ProjTex = new("11111111-0000-0000-0000-000000000003");
    private static readonly UUID Material = new("11111111-0000-0000-0000-000000000004");
    private static readonly UUID GltfBase = new("11111111-0000-0000-0000-000000000005");
    private static readonly UUID GltfNormal = new("11111111-0000-0000-0000-000000000006");
    private static readonly UUID GltfMr = new("11111111-0000-0000-0000-000000000007");
    private static readonly UUID GltfEmissive = new("11111111-0000-0000-0000-000000000008");

    public static TheoryData<string, uint> Perms => new()
    {
        { "full", Full }, { "nocopy", NoCopy }, { "nomod", NoMod }, { "notrans", NoTrans },
    };

    // ── setup ──────────────────────────────────────────────────────────────────

    private static void AddItem(SceneObjectPart part, string name, UUID asset, AssetType type, uint perms)
    {
        var item = new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)type, InvType = (int)(type == AssetType.Notecard ? InventoryType.Notecard : InventoryType.Texture),
            BasePermissions = perms | (uint)PermissionMask.Move, CurrentPermissions = perms,
            NextPermissions = perms, EveryonePermissions = 0,
            OwnerID = part.OwnerID, CreatorID = part.OwnerID,
        };
        part.Inventory.AddInventoryItem(item, true);
    }

    /// <summary>Every texture-bearing field this work covers, on face 0 of the part.</summary>
    private static void Dress(SceneObjectPart part)
    {
        var te = new Primitive.TextureEntry(UUID.Zero);
        te.CreateFace(0).TextureID = FaceTex;
        part.Shape.Textures = te;

        part.Shape.SculptEntry = true;
        part.Shape.SculptTexture = SculptMap;
        part.Shape.SculptType = (byte)SculptType.Sphere;

        part.Shape.ProjectionEntry = true;
        part.Shape.ProjectionTextureUUID = ProjTex;
        part.Shape.ProjectionFOV = 1.5f;

        var gltf = new OSDMap
        {
            ["tex"] = OSD.FromString(GltfBase.ToString()),
            ["ntex"] = OSD.FromString(GltfNormal.ToString()),
            ["mrtex"] = OSD.FromString(GltfMr.ToString()),
            ["etex"] = OSD.FromString(GltfEmissive.ToString()),
        };
        part.Shape.RenderMaterials = new Primitive.RenderMaterials
        {
            entries = new[] { new Primitive.RenderMaterials.RenderMaterialEntry { te_index = 0, id = Material } },
            overrides = new[] { new Primitive.RenderMaterials.RenderMaterialOverrideEntry { te_index = 0, data = OSDParser.SerializeLLSDNotation(gltf) } },
        };
    }

    /// <summary>The harness prim as root "p1" and a child "p2" at link 2.</summary>
    private static SceneObjectPart TwoPrimLinkset(SchedulerHarness h)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        var child = h.Prim.ParentGroup.GetLinkNumPart(2);
        Assert.NotNull(child);
        return child;
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    /// <summary>
    /// Run a script in the root prim. <paramref name="scriptOwner"/> null: the object owner's script (the harness'
    /// own rez); otherwise a script item owned by that avatar, as when another avatar drops a script in.
    /// </summary>
    private void Run(SchedulerHarness h, string body, UUID? scriptOwner = null)
    {
        string source = "default { state_entry() { " + body + " llSay(0, \"done\"); } }";
        if (scriptOwner == null)
            h.RezScript(source);
        else
        {
            var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "visitor", source);
            item.OwnerID = scriptOwner.Value;
            var rez = h.Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(rez);
            rez!.Invoke(h.Engine, new object[] { h.Prim.LocalId, item.ItemID, source, 0, false, h.Engine.Name, 0 });
        }
        WaitFor(h, said => said.Contains("done"));
        _out.WriteLine(string.Join("\n", h.Said));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
    }

    private static string Line(SchedulerHarness h, string tag)
        => h.Said.Single(s => s.StartsWith(tag + "=")).Substring(tag.Length + 1);

    private static string Say(string tag, string expr) => "llSay(0, \"" + tag + "=\" + (string)(" + expr + ")); ";
    private static string SayList(string tag, string listExpr)
        => "llSay(0, \"" + tag + "=\" + llDumpList2String(" + listExpr + ", \"|\")); ";
    private static string OwnParams(string rules) => "llGetPrimitiveParams([" + rules + "])";
    private static string LinkParams(string rules) => "llGetLinkPrimitiveParams(2, [" + rules + "])";

    /// <summary>Every texture/material read, of the root prim (own reads) and of link 2 (llGetLinkPrimitiveParams).</summary>
    private static string ReadAll(Func<string, string> get, string tagPrefix) =>
        SayList(tagPrefix + "PRIM_TEXTURE", get("PRIM_TEXTURE, 0")) +
        SayList(tagPrefix + "PRIM_TYPE", get("PRIM_TYPE")) +
        SayList(tagPrefix + "PRIM_PROJECTOR", get("PRIM_PROJECTOR")) +
        SayList(tagPrefix + "IW_PRIM_PROJECTOR", get("IW_PRIM_PROJECTOR")) +
        SayList(tagPrefix + "IW_PRIM_PROJECTOR_TEXTURE", get("IW_PRIM_PROJECTOR_TEXTURE")) +
        SayList(tagPrefix + "PRIM_RENDER_MATERIAL", get("PRIM_RENDER_MATERIAL, 0")) +
        SayList(tagPrefix + "PRIM_GLTF_BASE_COLOR", get("PRIM_GLTF_BASE_COLOR, 0")) +
        SayList(tagPrefix + "PRIM_GLTF_NORMAL", get("PRIM_GLTF_NORMAL, 0")) +
        SayList(tagPrefix + "PRIM_GLTF_METALLIC_ROUGHNESS", get("PRIM_GLTF_METALLIC_ROUGHNESS, 0")) +
        SayList(tagPrefix + "PRIM_GLTF_EMISSIVE", get("PRIM_GLTF_EMISSIVE, 0"));

    private static string Own => Say("llGetTexture", "llGetTexture(0)") + Say("llGetRenderMaterial", "llGetRenderMaterial(0)") + ReadAll(OwnParams, "");

    /// <summary>The texture/material field of each read, by tag, given what each key should read as.</summary>
    private static void AssertReads(SchedulerHarness h, string prefix, Func<UUID, string, string> expect)
    {
        string F(string tag, int field) => Line(h, prefix + tag).Split('|')[field];
        if (prefix == "")
        {
            Assert.Equal(expect(FaceTex, "face"), Line(h, "llGetTexture"));
            Assert.Equal(expect(Material, "mat"), Line(h, "llGetRenderMaterial"));
        }
        Assert.Equal(expect(FaceTex, "face"), F("PRIM_TEXTURE", 0));
        Assert.Equal("7", F("PRIM_TYPE", 0));
        Assert.Equal(expect(SculptMap, "sculpt"), F("PRIM_TYPE", 1));
        Assert.Equal(expect(ProjTex, "proj"), F("PRIM_PROJECTOR", 0));
        Assert.Equal(expect(ProjTex, "proj"), F("IW_PRIM_PROJECTOR", 1));
        Assert.Equal(expect(ProjTex, "proj"), F("IW_PRIM_PROJECTOR_TEXTURE", 0));
        Assert.Equal(expect(Material, "mat"), F("PRIM_RENDER_MATERIAL", 0));
        Assert.Equal(expect(GltfBase, "gbase"), F("PRIM_GLTF_BASE_COLOR", 0));
        Assert.Equal(expect(GltfNormal, "gnormal"), F("PRIM_GLTF_NORMAL", 0));
        Assert.Equal(expect(GltfMr, "gmr"), F("PRIM_GLTF_METALLIC_ROUGHNESS", 0));
        Assert.Equal(expect(GltfEmissive, "gemissive"), F("PRIM_GLTF_EMISSIVE", 0));
    }

    private static readonly (UUID key, string name, AssetType type)[] AllMaps =
    {
        (FaceTex, "face", AssetType.Texture), (SculptMap, "sculpt", AssetType.Texture), (ProjTex, "proj", AssetType.Texture),
        (Material, "mat", AssetType.Material), (GltfBase, "gbase", AssetType.Texture), (GltfNormal, "gnormal", AssetType.Texture),
        (GltfMr, "gmr", AssetType.Texture), (GltfEmissive, "gemissive", AssetType.Texture),
    };

    // ── inventory keys ─────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Perms))]
    public void InventoryKeysAreGivenOnlyForFullPermItemsInEveryPrimOfTheLinkset(string label, uint perms)
    {
        using var h = new SchedulerHarness();
        var child = TwoPrimLinkset(h);
        var rootTex = UUID.Random(); var rootCard = UUID.Random(); var childTex = UUID.Random();
        AddItem(h.Prim, "tex", rootTex, AssetType.Texture, perms);
        AddItem(h.Prim, "card", rootCard, AssetType.Notecard, perms);
        AddItem(child, "ctex", childTex, AssetType.Texture, perms);

        Run(h,
            Say("ll", "llGetInventoryKey(\"tex\")") +
            Say("llcard", "llGetInventoryKey(\"card\")") +
            Say("iwthis", "iwGetLinkInventoryKey(LINK_THIS, \"tex\")") +
            Say("iwroot", "iwGetLinkInventoryKey(LINK_ROOT, \"card\")") +
            Say("iwchild", "iwGetLinkInventoryKey(2, \"ctex\")") +
            Say("iwmissing", "iwGetLinkInventoryKey(2, \"nothing\")"));

        bool full = label == "full";
        Assert.Equal(full ? rootTex.ToString() : NullKey, Line(h, "ll"));
        Assert.Equal(full ? rootCard.ToString() : NullKey, Line(h, "llcard"));
        Assert.Equal(full ? rootTex.ToString() : NullKey, Line(h, "iwthis"));
        Assert.Equal(full ? rootCard.ToString() : NullKey, Line(h, "iwroot"));
        Assert.Equal(full ? childTex.ToString() : NullKey, Line(h, "iwchild"));
        Assert.Equal(NullKey, Line(h, "iwmissing"));
    }

    [Theory]
    [MemberData(nameof(Perms))]
    public void AnotherAvatarsScriptGetsTheSameInventoryKeysAsTheOwners(string label, uint perms)
    {
        using var h = new SchedulerHarness();
        var child = TwoPrimLinkset(h);
        var rootTex = UUID.Random(); var childTex = UUID.Random();
        AddItem(h.Prim, "tex", rootTex, AssetType.Texture, perms);
        AddItem(child, "ctex", childTex, AssetType.Texture, perms);

        Run(h, Say("ll", "llGetInventoryKey(\"tex\")") + Say("iwchild", "iwGetLinkInventoryKey(2, \"ctex\")"),
            scriptOwner: UUID.Random());

        bool full = label == "full";
        Assert.Equal(full ? rootTex.ToString() : NullKey, Line(h, "ll"));
        Assert.Equal(full ? childTex.ToString() : NullKey, Line(h, "iwchild"));
    }

    // ── texture and material keys ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Perms))]
    public void TextureAndMaterialKeysOfAnObjectShowOnlyWhenItIsFullPerm(string label, uint perms)
    {
        using var h = new SchedulerHarness();
        h.Prim.OwnerMask = perms;
        Dress(h.Prim);

        Run(h, Own);

        bool full = label == "full";
        AssertReads(h, "", (key, _) => full ? key.ToString() : NullKey);
    }

    [Theory]
    [MemberData(nameof(Perms))]
    public void AnotherAvatarsScriptSeesTheObjectOwnersTextureRule(string label, uint perms)
    {
        using var h = new SchedulerHarness();
        h.Prim.OwnerMask = perms;
        Dress(h.Prim);

        Run(h, Own, scriptOwner: UUID.Random());

        bool full = label == "full";
        AssertReads(h, "", (key, _) => full ? key.ToString() : NullKey);
    }

    [Theory]
    [MemberData(nameof(Perms))]
    public void ATextureInThePrimReadsAsItsNameWhateverTheItemAndObjectPerms(string label, uint perms)
    {
        using var h = new SchedulerHarness();
        h.Prim.OwnerMask = perms;
        Dress(h.Prim);
        foreach (var (key, name, type) in AllMaps)
            AddItem(h.Prim, name, key, type, perms);

        Run(h, Own);

        _out.WriteLine(label);
        AssertReads(h, "", (_, name) => name);
    }

    [Fact]
    public void AnEmptyTextureStaysEmptyOrNullKey()
    {
        using var h = new SchedulerHarness();
        h.Prim.OwnerMask = NoMod;
        h.Prim.Shape.Textures = new Primitive.TextureEntry(UUID.Zero);

        Run(h, Say("llGetTexture", "llGetTexture(0)") +
               SayList("PRIM_TEXTURE", OwnParams("PRIM_TEXTURE, 0")) +
               SayList("PRIM_GLTF_BASE_COLOR", OwnParams("PRIM_GLTF_BASE_COLOR, 0")));

        Assert.Equal(NullKey, Line(h, "llGetTexture"));
        Assert.Equal(NullKey, Line(h, "PRIM_TEXTURE").Split('|')[0]);
        Assert.Equal("", Line(h, "PRIM_GLTF_BASE_COLOR").Split('|')[0]);
    }

    // ── another prim of the linkset ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Perms))]
    public void ALinkedPrimsKeysFollowThatPrimsOwnerPerms(string label, uint perms)
    {
        using var h = new SchedulerHarness();
        var child = TwoPrimLinkset(h);
        child.OwnerMask = perms;
        Dress(child);

        Run(h, ReadAll(LinkParams, "c."));

        bool full = label == "full";
        AssertReads(h, "c.", (key, _) => full ? key.ToString() : NullKey);
    }

    /// <summary>
    /// Where the name comes from when a script reads another prim. Halcyon's InventoryName searches the SCRIPT's prim
    /// (textures, sculpt map, projector); SL's material rule names "a material in the inventory of the target prim",
    /// and the GLTF reads follow that.
    /// </summary>
    [Fact]
    public void ALinkedPrimNamesTexturesFromTheScriptsPrimAndMaterialsFromItsOwn()
    {
        using var h = new SchedulerHarness();
        var child = TwoPrimLinkset(h);
        child.OwnerMask = NoMod;
        Dress(child);
        foreach (var (key, name, type) in AllMaps)
        {
            AddItem(h.Prim, "root." + name, key, type, Full);
            AddItem(child, "child." + name, key, type, Full);
        }

        Run(h, ReadAll(LinkParams, "c."));

        AssertReads(h, "c.", (key, name) =>
            key == Material || key == GltfBase || key == GltfNormal || key == GltfMr || key == GltfEmissive
                ? "child." + name : "root." + name);
    }

    [Fact]
    public void ALinkedPrimsTextureOnlyInItsOwnInventoryIsHiddenWhenItIsNotFullPerm()
    {
        using var h = new SchedulerHarness();
        var child = TwoPrimLinkset(h);
        child.OwnerMask = NoMod;
        Dress(child);
        AddItem(child, "child.face", FaceTex, AssetType.Texture, Full);

        Run(h, SayList("c.PRIM_TEXTURE", LinkParams("PRIM_TEXTURE, 0")));

        // Halcyon: InventoryName(assetID) looks in m_host only, so a texture in the child's inventory is not named.
        Assert.Equal(NullKey, Line(h, "c.PRIM_TEXTURE").Split('|')[0]);
    }
}
