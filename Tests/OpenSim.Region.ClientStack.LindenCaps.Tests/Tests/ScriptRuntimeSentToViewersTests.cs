/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// A script item's runtime as Second Life's viewer reads it: the "runtime" value of a "script" map, at the top of an
/// item's LLSD map (LLInventoryItem::fromLLSD) or inside the "metadata" line of a task inventory file
/// (LLInventoryItem::importLegacyStream). The viewer's editor turns an LSL script whose runtime is "luau" into the
/// "LSL on Lua" target (llpreviewscript.cpp). The region keeps that target in a flag bit no viewer is told, so it sends
/// the runtime instead; a script with no Lua target and a script with only a saved engine are sent no runtime, as before.
/// Each test builds its own items and scene and touches no process-wide state.
/// </summary>
public class ScriptRuntimeSentToViewersTests
{
    private const uint LslOnLuau = ScriptCompileTargets.LslOnLuauFlag;
    private const uint LuaSubtype = 1;
    private static readonly uint PhloxChosen = ScriptEngineChoices.WithChoice(0, ScriptEngineChoices.Phlox);

    private static InventoryItemBase Item(uint flags, InventoryType invType = InventoryType.LSL) => new()
    {
        ID = UUID.Random(),
        Folder = UUID.Random(),
        Owner = UUID.Random(),
        CreatorId = UUID.Random().ToString(),
        Name = "script",
        Description = "a script",
        AssetID = UUID.Random(),
        AssetType = invType == InventoryType.LSL ? (int)AssetType.LSLText : (int)AssetType.Notecard,
        InvType = (int)invType,
        Flags = flags,
        BasePermissions = (uint)OpenSim.Framework.PermissionMask.All,
        CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        CreationDate = 1700000000,
    };

    private static OSDMap ItemMap(InventoryItemBase item)
    {
        osUTF8 sb = LLSDxmlEncode2.Start(4096);
        item.ToLLSDxml(sb);
        return (OSDMap)OSDParser.DeserializeLLSDXml(LLSDxmlEncode2.End(sb));
    }

    private static string RuntimeIn(OSDMap item)
    {
        Assert.True(item.ContainsKey("script"), "no script map in " + OSDParser.SerializeJsonString(item));
        return ((OSDMap)item["script"])["runtime"].AsString();
    }

    [Theory]
    [InlineData(LslOnLuau)]
    [InlineData(LuaSubtype)]
    public void AnItemMapCarriesTheLuauRuntime(uint flags)
    {
        OSDMap map = ItemMap(Item(flags));

        Assert.Equal("luau", RuntimeIn(map));
        Assert.Equal((int)(flags & 0xff), map["flags"].AsInteger());
    }

    [Fact]
    public void AnItemMapForAScriptWithNoLuaTargetIsAsBefore()
    {
        Assert.False(ItemMap(Item(0)).ContainsKey("script"));
        Assert.False(ItemMap(Item(PhloxChosen)).ContainsKey("script"));
        Assert.False(ItemMap(Item(LslOnLuau, InventoryType.Notecard)).ContainsKey("script"));
    }

    [Fact]
    public void AnAisItemCarriesTheLuauRuntimeAndOnlyForALuaTarget()
    {
        UUID agent = UUID.Random();

        Assert.Equal("luau", RuntimeIn(AisEnvelope.Item(Item(LslOnLuau), agent)));
        Assert.Equal("luau", RuntimeIn(AisEnvelope.Item(Item(LuaSubtype), agent)));
        Assert.False(AisEnvelope.Item(Item(0), agent).ContainsKey("script"));
        Assert.False(AisEnvelope.Item(Item(PhloxChosen), agent).ContainsKey("script"));
    }

    private sealed class CapturingXfer : IXfer
    {
        public byte[] Data;

        public bool AddNewFile(string fileName, byte[] data)
        {
            Data = data;
            return true;
        }
    }

    [Fact]
    public void TheTaskInventoryFileCarriesTheLuauRuntimeInTheMetadataLine()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID owner = TestHelpers.ParseTail(1);
        SceneObjectGroup group = SceneHelpers.AddSceneObject(scene, 1, owner, "box", 0x10);
        TaskInventoryItem onLuau = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart, "onluau", "default { }");
        TaskInventoryItem plain = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart, "plain", "default { }");
        TaskInventoryItem chosen = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart, "chosen", "default { }");
        onLuau.Flags = LslOnLuau;
        plain.Flags = 0;
        chosen.Flags = PhloxChosen;
        CapturingXfer xfer = new();

        group.RootPart.Inventory.RequestInventoryFile(new TestClient(SceneHelpers.GenerateAgentData(owner), scene), xfer);

        string file = Encoding.UTF8.GetString(xfer.Data);
        string[] lines = file.Split('\n');
        string[] metadata = lines.Where(l => l.TrimStart().StartsWith("metadata\t")).ToArray();
        string line = Assert.Single(metadata).TrimStart();
        string xml = line[(line.IndexOf('\t') + 1)..];
        Assert.StartsWith("<llsd>", xml);
        OSDMap meta = (OSDMap)OSDParser.DeserializeLLSDXml(xml);
        Assert.Equal("luau", RuntimeIn(meta));
        // the metadata line sits in the item that has the target, before that item's name
        int at = Array.FindIndex(lines, l => l.TrimStart() == line);
        string nameAfter = lines.Skip(at).First(l => l.TrimStart().StartsWith("name\t"));
        Assert.Contains("onluau|", nameAfter);
    }

    [Theory]
    [InlineData(LslOnLuau, "luau")]
    [InlineData(LuaSubtype, "luau")]
    [InlineData(LslOnLuau | LuaSubtype, "luau")]
    [InlineData(0u, null)]
    [InlineData(0x00000100u, null)]
    public void TheRuntimeComesFromTheLuaTargetAlone(uint flags, string expected)
    {
        Assert.Equal(expected, ScriptCompileTargets.ViewerRuntime(flags));
        Assert.Equal(expected, ScriptCompileTargets.ViewerRuntime(ScriptEngineChoices.WithChoice(flags, ScriptEngineChoices.YEngine)));
    }
}
