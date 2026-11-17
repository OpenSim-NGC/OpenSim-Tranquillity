/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// Scripted glTF materials and overrides, as SL scripts use them (https://wiki.secondlife.com/wiki/GLTF_Overrides):
/// PRIM_RENDER_MATERIAL and PRIM_GLTF_BASE_COLOR / _NORMAL / _METALLIC_ROUGHNESS / _EMISSIVE in the prim-params calls,
/// llSetRenderMaterial, llSetLinkRenderMaterial and llSetLinkGLTFOverrides. What a script sets is stored in the compact
/// form viewers read (GltfMaterialOverride), "" clears one override, and an unset override reads back as "".
/// </summary>
// Test grouping: this class builds its own harness and touches no process-wide seam, so it runs in parallel.
public class GltfOverrideScriptTests
{
    private readonly ITestOutputHelper _out;
    public GltfOverrideScriptTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string Mat = "22222222-0000-0000-0000-000000000001";
    private static readonly UUID MatId = new(Mat);
    private static readonly UUID NamedMat = new("22222222-0000-0000-0000-000000000002");
    private static readonly UUID NamedTex = new("22222222-0000-0000-0000-000000000003");

    private static void AddItem(SceneObjectPart part, string name, UUID asset, AssetType type)
    {
        const uint full = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move);
        var item = new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(), Type = (int)type, InvType = (int)InventoryType.Texture,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full,
            OwnerID = part.OwnerID, CreatorID = part.OwnerID,
        };
        part.Inventory.AddInventoryItem(item, true);
    }

    private void Run(SchedulerHarness h, string body)
    {
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && !h.Said.Contains("done")) h.PumpOnce();
        _out.WriteLine(string.Join("\n", h.SaidOn.Select(m => m.Channel + ": " + m.Message)));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
    }

    private static string Line(SchedulerHarness h, string tag)
        => h.Said.Single(s => s.StartsWith(tag + "=")).Substring(tag.Length + 1);

    private static string SayList(string tag, string listExpr)
        => "llSay(0, \"" + tag + "=\" + llDumpList2String(" + listExpr + ", \"|\")); ";

    private static List<string> Errors(SchedulerHarness h)
        => h.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();

    private static GltfMaterialOverride Ovr(SceneObjectPart part, int face) => GltfMaterialOverride.Get(part.Shape, face);

    private static OSDMap StoredMap(SceneObjectPart part, int face)
    {
        var entry = part.Shape.RenderMaterials.overrides.Single(o => o.te_index == face);
        return (OSDMap)OSDParser.DeserializeLLSDNotation(entry.data);
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    // ── PRIM_GLTF_* set ────────────────────────────────────────────────────────

    [Fact]
    public void BaseColorIsStoredInTheFormViewersRead()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetPrimitiveParams([PRIM_GLTF_BASE_COLOR, 0, \"\", <2, 3, 0>, <0.25, 0.5, 0>, 1.0, <1, 0, 0>, 0.5, " +
               "PRIM_GLTF_ALPHA_MODE_BLEND, 0.3, TRUE]);");

        Assert.Empty(Errors(h));
        OSDMap map = StoredMap(h.Prim, 0);
        Assert.Equal(new[] { "ac", "am", "bc", "ds", "ti" }, map.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 1.0, 0.0, 0.0, 0.5 }, ((OSDArray)map["bc"]).Select(o => o.AsReal()));
        Assert.Equal(1, map["am"].AsInteger());
        Assert.True(map["ds"].AsBoolean());
        var ti = (OSDMap)((OSDArray)map["ti"])[0];
        Assert.Equal(new[] { 2.0, 3.0 }, ((OSDArray)ti["s"]).Select(o => o.AsReal()));
        Assert.Equal(new[] { 0.25, 0.5 }, ((OSDArray)ti["o"]).Select(o => o.AsReal()));
        Assert.Equal(1.0, ti["r"].AsReal());
    }

    [Fact]
    public void EachRuleKeepsTheOverridesTheOthersSet()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", ALL_SIDES); " +
               "llSetPrimitiveParams([PRIM_GLTF_METALLIC_ROUGHNESS, 1, \"\", \"\", \"\", \"\", 0.25, 0.75, " +
               "PRIM_GLTF_EMISSIVE, 1, \"\", \"\", \"\", \"\", <0, 1, 0>]); " +
               "llSetPrimitiveParams([PRIM_GLTF_NORMAL, 1, \"" + NamedTex + "\", \"\", \"\", 0.5]);");

        GltfMaterialOverride o = Ovr(h.Prim, 1);
        Assert.Equal(0.25f, o.MetallicFactor);
        Assert.Equal(0.75f, o.RoughnessFactor);
        Assert.Equal(new Vector3(0, 1, 0), o.EmissiveColor);
        Assert.Equal(NamedTex, o.GetTexture(GltfMaterialOverride.SLOT_NORMAL));
        Assert.Equal(0.5f, o.GetRotation(GltfMaterialOverride.SLOT_NORMAL));
        Assert.Null(o.BaseColor);
    }

    [Fact]
    public void EmptyStringsClearTheOverrides()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetPrimitiveParams([PRIM_GLTF_BASE_COLOR, 0, \"\", <2, 2, 0>, \"\", \"\", <1, 0, 0>, 0.5, 1, 0.5, 1]); " +
               "llSetPrimitiveParams([PRIM_GLTF_BASE_COLOR, 0, \"\", \"\", \"\", \"\", \"\", \"\", \"\", \"\", \"\"]);");

        Assert.True(Ovr(h.Prim, 0).IsEmpty);
        Assert.Null(h.Prim.Shape.RenderMaterials.overrides);
        Assert.Equal(MatId, GltfMaterialOverride.GetRenderMaterial(h.Prim.Shape, 0));
    }

    [Fact]
    public void OnAFaceWithNoMaterialOnlyTheTextureTransformIsKept()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetPrimitiveParams([PRIM_GLTF_BASE_COLOR, 0, \"\", <4, 4, 0>, \"\", \"\", <1, 0, 0>, 1.0, 0, 0.5, 0]);");

        OSDMap map = StoredMap(h.Prim, 0);
        Assert.Equal(new[] { "ti" }, map.Keys);
    }

    [Fact]
    public void ATextureIsNamedFromThePrimsInventory()
    {
        using var h = new SchedulerHarness();
        AddItem(h.Prim, "bumps", NamedTex, AssetType.Texture);

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetPrimitiveParams([PRIM_GLTF_NORMAL, 0, \"bumps\", <1, 1, 0>, <0, 0, 0>, 0.0]); " +
               SayList("normal", "llGetPrimitiveParams([PRIM_GLTF_NORMAL, 0])"));

        Assert.Equal(NamedTex, Ovr(h.Prim, 0).GetTexture(GltfMaterialOverride.SLOT_NORMAL));
        Assert.Equal("bumps", Line(h, "normal").Split('|')[0]);
    }

    [Fact]
    public void AMissingTextureShoutsAndChangesNothing()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetPrimitiveParams([PRIM_GLTF_EMISSIVE, 0, \"no such texture\", \"\", \"\", \"\", <1, 1, 1>, " +
               "PRIM_NAME, \"after\"]);");

        Assert.Contains(Errors(h), e => e.Contains("no such texture"));
        Assert.True(Ovr(h.Prim, 0).IsEmpty);
        Assert.Equal("after", h.Prim.Name);
    }

    [Fact]
    public void TheRulesAfterAGltfRuleStillApply()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetPrimitiveParams([PRIM_GLTF_BASE_COLOR, ALL_SIDES, \"\", \"\", \"\", \"\", \"\", \"\", \"\", \"\", \"\", " +
               "PRIM_NAME, \"after\"]);");

        Assert.Empty(Errors(h));
        Assert.Equal("after", h.Prim.Name);
    }

    // ── PRIM_GLTF_* get ────────────────────────────────────────────────────────

    [Fact]
    public void AnUnsetOverrideReadsAsTheEmptyString()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetPrimitiveParams([PRIM_GLTF_METALLIC_ROUGHNESS, 0, \"\", \"\", \"\", \"\", 0.25, \"\"]); " +
               SayList("mr", "llGetPrimitiveParams([PRIM_GLTF_METALLIC_ROUGHNESS, 0])") +
               SayList("bc", "llGetPrimitiveParams([PRIM_GLTF_BASE_COLOR, 0])"));

        string[] mr = Line(h, "mr").Split('|');
        Assert.Equal(6, mr.Length);
        Assert.Equal(new[] { "", "", "", "" }, mr.Take(4));
        Assert.Equal(0.25f, F(mr[4]));
        Assert.Equal("", mr[5]);
        Assert.Equal(new string('|', 8), Line(h, "bc"));
    }

    [Fact]
    public void AllSidesReadsEveryFaceAndAMissingFaceReadsNothing()
    {
        using var h = new SchedulerHarness();
        int sides = h.Prim.GetNumberOfSides();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", ALL_SIDES); " +
               SayList("all", "llGetPrimitiveParams([PRIM_RENDER_MATERIAL, ALL_SIDES])") +
               "llSay(0, \"missing=\" + (string)llGetListLength(llGetPrimitiveParams([PRIM_GLTF_NORMAL, 99])));");

        Assert.Equal(Enumerable.Repeat(Mat, sides), Line(h, "all").Split('|'));
        Assert.Equal("0", Line(h, "missing"));
    }

    // ── render materials ───────────────────────────────────────────────────────

    [Fact]
    public void ANewMaterialClearsTheOverridesButKeepsTheTransforms()
    {
        using var h = new SchedulerHarness();
        AddItem(h.Prim, "shiny", NamedMat, AssetType.Material);

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetPrimitiveParams([PRIM_GLTF_BASE_COLOR, 0, \"\", <3, 3, 0>, \"\", \"\", <1, 0, 0>, 1.0, 0, 0.5, 0]); " +
               "llSetPrimitiveParams([PRIM_RENDER_MATERIAL, 0, \"shiny\"]); " +
               "llSay(0, \"name=\" + llGetRenderMaterial(0));");

        Assert.Equal(NamedMat, GltfMaterialOverride.GetRenderMaterial(h.Prim.Shape, 0));
        GltfMaterialOverride o = Ovr(h.Prim, 0);
        Assert.Null(o.BaseColor);
        Assert.Equal(new Vector2(3, 3), o.GetScale(GltfMaterialOverride.SLOT_BASE_COLOR));
        Assert.Equal("shiny", Line(h, "name"));
    }

    [Fact]
    public void NullKeyRemovesTheMaterial()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", ALL_SIDES); " +
               "llSetPrimitiveParams([PRIM_RENDER_MATERIAL, 0, NULL_KEY]); " +
               "llSay(0, \"gltf=\" + (string)llIsLinkGLTFMaterial(LINK_THIS, 0) + \",\" + (string)llIsLinkGLTFMaterial(LINK_THIS, 1)); " +
               "llSay(0, \"get=\" + llGetRenderMaterial(0));");

        Assert.Equal(UUID.Zero, GltfMaterialOverride.GetRenderMaterial(h.Prim.Shape, 0));
        Assert.Equal(MatId, GltfMaterialOverride.GetRenderMaterial(h.Prim.Shape, 1));
        Assert.Equal("0,1", Line(h, "gltf"));
        Assert.Equal(UUID.Zero.ToString(), Line(h, "get"));
    }

    [Fact]
    public void AnUnknownMaterialShoutsAndChangesNothing()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"no such material\", 0);");

        Assert.Contains(Errors(h), e => e.Contains("no such material"));
        Assert.Equal(UUID.Zero, GltfMaterialOverride.GetRenderMaterial(h.Prim.Shape, 0));
    }

    [Fact]
    public void LinkRenderMaterialSetsTheChildsGltfMaterial()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        SceneObjectPart child = h.Prim.ParentGroup.GetLinkNumPart(2);

        Run(h, "llSetLinkRenderMaterial(2, \"" + Mat + "\", ALL_SIDES);");

        for (int f = 0; f < child.GetNumberOfSides(); f++)
            Assert.Equal(MatId, GltfMaterialOverride.GetRenderMaterial(child.Shape, f));
        Assert.Equal(UUID.Zero, GltfMaterialOverride.GetRenderMaterial(h.Prim.Shape, 0));
        // not the legacy (Blinn-Phong) material of the texture entry
        Assert.Equal(UUID.Zero, child.Shape.Textures.GetFace(0).MaterialID);
    }

    // ── llSetLinkGLTFOverrides ─────────────────────────────────────────────────

    [Fact]
    public void LinkGltfOverridesTakeSlsConstants()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetLinkGLTFOverrides(LINK_THIS, 0, [OVERRIDE_GLTF_BASE_ALPHA, 0.5, OVERRIDE_GLTF_BASE_ALPHA_MODE, " +
               "PRIM_GLTF_ALPHA_MODE_BLEND, OVERRIDE_GLTF_ROUGHNESS_FACTOR, 0.2, OVERRIDE_GLTF_EMISSIVE_FACTOR, <0, 0, 1>]);");

        GltfMaterialOverride o = Ovr(h.Prim, 0);
        Assert.Equal(0.5f, o.BaseAlpha);
        Assert.Equal(1, o.AlphaMode);
        Assert.Equal(0.2f, o.RoughnessFactor);
        Assert.Equal(new Vector3(0, 0, 1), o.EmissiveColor);
        // base color and alpha are coupled: an alpha alone stores white
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 0.5 }, ((OSDArray)StoredMap(h.Prim, 0)["bc"]).Select(x => x.AsReal()));
    }

    [Fact]
    public void LinkGltfOverridesClearWithTheEmptyString()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetRenderMaterial(\"" + Mat + "\", 0); " +
               "llSetLinkGLTFOverrides(LINK_THIS, 0, [OVERRIDE_GLTF_METALLIC_FACTOR, 0.5, OVERRIDE_GLTF_BASE_DOUBLE_SIDED, TRUE]); " +
               "llSetLinkGLTFOverrides(LINK_THIS, 0, [OVERRIDE_GLTF_METALLIC_FACTOR, \"\"]);");

        GltfMaterialOverride o = Ovr(h.Prim, 0);
        Assert.Null(o.MetallicFactor);
        Assert.True(o.DoubleSided);
    }
}
