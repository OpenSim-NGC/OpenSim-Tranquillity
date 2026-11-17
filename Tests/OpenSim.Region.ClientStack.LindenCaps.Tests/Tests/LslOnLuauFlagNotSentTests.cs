/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Region.ClientStack.LindenUDP;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// The flag bit a region keeps on a script item to remember that the script is LSL on Lua is the region's own: no viewer
/// defines it, so every way the region tells a viewer an item's flags leaves it out. The values Second Life's viewer
/// does know (the script subtype in the low byte, the permission slam and overwrite bits) are sent as they were.
/// Each test builds its own scene, server and client and touches no process-wide state.
/// </summary>
public class LslOnLuauFlagNotSentTests
{
    private const uint Bit = ScriptCompileTargets.LslOnLuauFlag;
    private const uint LuaSubtype = 1;
    private const uint SlamPerm = (uint)InventoryItemFlags.ObjectSlamPerm;

    private static InventoryItemBase ScriptItem(uint flags) => new()
    {
        ID = UUID.Random(),
        Folder = UUID.Random(),
        Owner = UUID.Random(),
        CreatorId = UUID.Random().ToString(),
        Name = "script",
        Description = "a script",
        AssetID = UUID.Random(),
        AssetType = (int)AssetType.LSLText,
        InvType = (int)InventoryType.LSL,
        Flags = flags,
        BasePermissions = (uint)OpenSim.Framework.PermissionMask.All,
        CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        GroupPermissions = 0,
        EveryOnePermissions = 0,
        SalePrice = 0,
        SaleType = 0,
        CreationDate = 1700000000,
    };

    // ---- the one place that decides ---------------------------------------------------------------------------

    [Theory]
    [InlineData(Bit, 0u)]
    [InlineData(Bit | LuaSubtype, LuaSubtype)]
    [InlineData(LuaSubtype, LuaSubtype)]
    [InlineData(SlamPerm | LuaSubtype, SlamPerm | LuaSubtype)]
    [InlineData(Bit | 0x40000000u | 0x200000u | 0x21u, 0x40000000u | 0x200000u | 0x21u)]
    [InlineData(0u, 0u)]
    public void TheBitIsTheOnlyThingTakenOutOfTheFlagsAViewerIsTold(uint stored, uint told)
    {
        Assert.Equal(told, ScriptCompileTargets.ForViewer(stored));
    }

    // ---- FetchInventoryDescendents2, FetchLibDescendents2 and the library fetch (the item map) ----------------------

    private static int FlagsInItemMap(InventoryItemBase item, uint? mask = null)
    {
        osUTF8 sb = LLSDxmlEncode2.Start(4096);
        if (mask is null)
            item.ToLLSDxml(sb);
        else
            item.ToLLSDxml(sb, mask.Value);
        OSDMap map = (OSDMap)OSDParser.DeserializeLLSDXml(LLSDxmlEncode2.End(sb));
        return map["flags"].AsInteger();
    }

    [Fact]
    public void AnItemMapWithoutAMaskLeavesTheBitOut()
    {
        Assert.Equal((int)(LuaSubtype | SlamPerm), FlagsInItemMap(ScriptItem(Bit | LuaSubtype | SlamPerm)));
        Assert.Equal(0, FlagsInItemMap(ScriptItem(Bit)));
    }

    [Fact]
    public void AnItemMapWithAMaskIsStillCutByTheMask()
    {
        Assert.Equal((int)LuaSubtype, FlagsInItemMap(ScriptItem(Bit | LuaSubtype | SlamPerm), 0xff));
    }

    // ---- AIS v3 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AisItemsAndLinksLeaveTheBitOut()
    {
        UUID agent = UUID.Random();

        OSDMap item = AisEnvelope.Item(ScriptItem(Bit | LuaSubtype), agent);
        OSDMap link = AisEnvelope.Link(ScriptItem(Bit | LuaSubtype), agent);

        Assert.Equal((int)LuaSubtype, item["flags"].AsInteger());
        Assert.Equal((int)LuaSubtype, link["flags"].AsInteger());
    }

    // ---- UDP packets -----------------------------------------------------------------------------------------------

    private static (LLClientView client, TestLLUDPServer server, TestScene scene) NewClient()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udpServer = new(IPAddress.Loopback, 0, 0, new IniConfigSource(), scene.AuthenticateHandler);
        UUID agentId = UUID.Random();
        uint circuitCode = 123456;
        LLUDPClient udpClient = new(
            udpServer, udpServer.ThrottleRates, udpServer.Throttle, circuitCode, agentId,
            new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        AuthenticateResponse session = new() { Authorised = true, LoginInfo = new Login() };
        LLClientView client = new(scene, udpServer, udpClient, session, agentId, UUID.Random(), circuitCode);
        return (client, udpServer, scene);
    }

    [Fact]
    public void TheReplyToAnInventoryFetchLeavesTheBitOut()
    {
        var (client, server, _) = NewClient();

        // (one item to a packet: a reply for two to nine items is built with an empty slot and cannot be sent)
        client.SendInventoryItemDetails(new[] { ScriptItem(Bit | LuaSubtype | SlamPerm) });
        client.SendInventoryItemDetails(new[] { ScriptItem(LuaSubtype) });

        FetchInventoryReplyPacket[] replies = server.PacketsSent.OfType<FetchInventoryReplyPacket>().ToArray();
        Assert.Equal(2, replies.Length);
        Assert.Equal(LuaSubtype | SlamPerm, replies[0].InventoryData[0].Flags);
        Assert.Equal(LuaSubtype, replies[1].InventoryData[0].Flags);
    }

    [Fact]
    public void TheReplyToAnItemUpdateOrCreationLeavesTheBitOut()
    {
        var (client, server, _) = NewClient();

        client.SendInventoryItemCreateUpdate(ScriptItem(Bit | LuaSubtype), 1);

        UpdateCreateInventoryItemPacket reply = Assert.Single(server.PacketsSent.OfType<UpdateCreateInventoryItemPacket>());
        Assert.Equal(LuaSubtype, reply.InventoryData[0].Flags);
    }

    // ---- the bulk update that goes through the event queue ----------------------------------------------------------

    /// <summary>The event queue module, with what it is asked to send kept instead of sent.</summary>
    private sealed class CapturingQueue : EventQueueGetModule, IEventQueue
    {
        public readonly List<byte[]> Sent = new();

        public new bool Enqueue(byte[] evData, UUID avatarID)
        {
            Sent.Add(evData);
            return true;
        }
    }

    [Fact]
    public void TheBulkInventoryUpdateLeavesTheBitOut()
    {
        var (client, _, scene) = NewClient();
        CapturingQueue queue = new();
        scene.RegisterModuleInterface<IEventQueue>(queue);

        client.SendBulkUpdateInventory(Array.Empty<InventoryFolderBase>(), new[] { ScriptItem(Bit | LuaSubtype) });

        byte[] sent = Assert.Single(queue.Sent);
        string xml = "<llsd>" + Encoding.UTF8.GetString(sent) + "</llsd>";
        OSDMap body = (OSDMap)((OSDMap)OSDParser.DeserializeLLSDXml(xml))["body"];
        OSDMap item = (OSDMap)((OSDArray)body["ItemData"])[0];
        Assert.Equal((int)LuaSubtype, item["Flags"].AsInteger());
    }

    // ---- the task inventory file -----------------------------------------------------------------------------------

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
    public void TheTaskInventoryFileLeavesTheBitOut()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID owner = TestHelpers.ParseTail(1);
        SceneObjectGroup group = SceneHelpers.AddSceneObject(scene, 1, owner, "box", 0x10);
        TaskInventoryItem withBit = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart, "withbit", "default { }");
        TaskInventoryItem lua = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart, "lua", "default { }");
        withBit.Flags = Bit | LuaSubtype | SlamPerm;
        lua.Flags = LuaSubtype;
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(owner);
        TestClient client = new(acd, scene);
        CapturingXfer xfer = new();

        group.RootPart.Inventory.RequestInventoryFile(client, xfer);

        Assert.NotNull(xfer.Data);
        string file = Encoding.UTF8.GetString(xfer.Data);
        Assert.True(file.Contains("flags\t" + Utils.UIntToHexString(LuaSubtype | SlamPerm)), file.Replace("\t", "|"));
        Assert.Contains("flags\t" + Utils.UIntToHexString(LuaSubtype) + "\n", file);
        Assert.DoesNotContain(Utils.UIntToHexString(Bit), file);
        Assert.DoesNotContain(Utils.UIntToHexString(Bit | LuaSubtype), file);
    }
}
