/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Services.LLLoginService;
using Assert = Xunit.Assert;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The size of the region a viewer logs into is carried in the login reply in both forms: the XML-RPC reply
/// (region_size_x / region_size_y) and the LLSD reply. A viewer that reads the LLSD reply and finds no size
/// takes the region as 256 m until its first teleport or crossing.
/// </summary>
public class LoginRegionSizeTests
{
    private static LLLoginResponse Response(int sizeX, int sizeY)
    {
        GridRegion destination = new()
        {
            RegionName = "Example",
            ExternalHostName = "192.0.2.10",
            InternalEndPoint = new IPEndPoint(IPAddress.Any, 9000),
            ServerURI = "http://grid.example.org:9000/",
            RegionLocX = 256000,
            RegionLocY = 512000,
            RegionSizeX = sizeX,
            RegionSizeY = sizeY,
        };
        UserAccount account = new() { FirstName = "Test", LastName = "User", PrincipalID = UUID.Random() };
        AgentCircuitData circuit = new() { circuitcode = 1234, SessionID = UUID.Random(), SecureSessionID = UUID.Random(), CapsPath = "caps" };
        GridUserInfo grid = new() { HomePosition = new Vector3(128, 128, 30), HomeLookAt = Vector3.UnitX };

        LLLoginResponse response = new LLLoginResponse(account, circuit, grid, destination, new List<InventoryFolderBase>(),
            Array.Empty<FriendInfo>(), null!, "last", "last", new Vector3(128, 128, 30), Vector3.UnitX,
            new List<InventoryItemBase>(), "welcome", null!, new IPEndPoint(IPAddress.Parse("203.0.113.25"), 0),
            "", "", "", "", "", "none", "", "", UUID.Zero, "", 0,
            ViewerAddressSelector.Disabled, null!, true);
        // the login service sets this after construction
        response.AgentAppearanceServiceURL = string.Empty;
        // the library lists are only filled when a library service is given; the LLSD form needs them to exist
        foreach (System.Reflection.FieldInfo field in typeof(LLLoginResponse).GetFields(
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(ArrayList) && field.GetValue(response) is null)
                field.SetValue(response, new ArrayList());
        }
        return response;
    }

    [Fact]
    public void LargeRegion_LlsdReplyCarriesTheSize()
    {
        OSDMap map = (OSDMap)Response(1024, 1024).ToOSDMap();

        Assert.True(map.ContainsKey("region_size_x"), "LLSD reply has no region_size_x");
        Assert.True(map.ContainsKey("region_size_y"), "LLSD reply has no region_size_y");
        Assert.Equal(1024, map["region_size_x"].AsInteger());
        Assert.Equal(1024, map["region_size_y"].AsInteger());
    }

    [Fact]
    public void NonSquareRegion_LlsdReplyKeepsEachAxis()
    {
        OSDMap map = (OSDMap)Response(512, 1280).ToOSDMap();

        Assert.True(map.ContainsKey("region_size_x"), "LLSD reply has no region_size_x");
        Assert.Equal(512, map["region_size_x"].AsInteger());
        Assert.Equal(1280, map["region_size_y"].AsInteger());
    }

    [Fact]
    public void LargeRegion_XmlRpcReplyCarriesTheSize()
    {
        Hashtable table = Response(1024, 1024).ToHashtable();

        Assert.Equal(1024, Assert.IsType<int>(table["region_size_x"]));
        Assert.Equal(1024, Assert.IsType<int>(table["region_size_y"]));
    }

    [Fact]
    public void StandardRegion_BothRepliesGiveTheSame256()
    {
        LLLoginResponse response = Response(256, 256);
        OSDMap map = (OSDMap)response.ToOSDMap();
        Hashtable table = response.ToHashtable();

        Assert.Equal(256, Assert.IsType<int>(table["region_size_x"]));
        Assert.Equal(256, Assert.IsType<int>(table["region_size_y"]));
        Assert.True(map.ContainsKey("region_size_x"), "LLSD reply has no region_size_x");
        Assert.Equal((int)table["region_size_x"]!, map["region_size_x"].AsInteger());
        Assert.Equal((int)table["region_size_y"]!, map["region_size_y"].AsInteger());
    }

    [Fact]
    public void BothRepliesAgree_OnLocationAndSize()
    {
        LLLoginResponse response = Response(1024, 2048);
        OSDMap map = (OSDMap)response.ToOSDMap();
        Hashtable table = response.ToHashtable();

        foreach (string key in new[] { "region_x", "region_y", "region_size_x", "region_size_y" })
        {
            Assert.True(map.ContainsKey(key), "LLSD reply has no " + key);
            Assert.Equal((int)table[key]!, map[key].AsInteger());
        }
        Assert.Equal(256000, (int)table["region_x"]!);
        Assert.Equal(2048, (int)table["region_size_y"]!);
    }
}
