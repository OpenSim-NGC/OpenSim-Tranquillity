/*
 * Copyright (c) Legion Builds
 * Copyright (c) 2026 Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The local address a region registers with the grid is one optional field of the region data. A region
/// that registers none, and data from a build that does not know the field, behave as before.
/// </summary>
public class ViewerLocalAddressRegistrationTests : OpenSimTestCase
{
    private const string Field = "viewerLocalAddress";

    private static GridRegion Region(string localAddress = null) => new()
    {
        RegionID = TestHelpers.ParseTail(0x1),
        RegionName = "example",
        ExternalHostName = "region.example.org",
        InternalEndPoint = new IPEndPoint(IPAddress.Any, 9000),
        ServerURI = "http://region.example.org:9000/",
        ViewerLocalAddress = localAddress,
    };

    private static Dictionary<string, object> AsReceived(Dictionary<string, object> kvp)
    {
        // The grid service receives every value as a string.
        Dictionary<string, object> received = new();
        foreach (KeyValuePair<string, object> pair in kvp)
            received[pair.Key] = pair.Value?.ToString();
        return received;
    }

    [Fact]
    public void A_region_without_a_local_address_adds_nothing_to_its_region_data()
    {
        TestHelpers.InMethod();
        Dictionary<string, object> kvp = Region().ToKeyValuePairs();
        Assert.False(kvp.ContainsKey(Field));

        Dictionary<string, object> emptyKvp = Region("").ToKeyValuePairs();
        Assert.False(emptyKvp.ContainsKey(Field));
    }

    [Fact]
    public void The_local_address_survives_the_region_data_round_trip()
    {
        TestHelpers.InMethod();
        Dictionary<string, object> kvp = Region("192.168.1.10").ToKeyValuePairs();
        Assert.Equal("192.168.1.10", kvp[Field]);

        GridRegion back = new(AsReceived(kvp));
        Assert.Equal("192.168.1.10", back.ViewerLocalAddress);
        Assert.Equal("region.example.org", back.ExternalHostName);
    }

    [Fact]
    public void Region_data_from_a_build_without_the_field_gives_no_local_address()
    {
        TestHelpers.InMethod();
        Dictionary<string, object> oldFormat = AsReceived(Region().ToKeyValuePairs());
        Assert.False(oldFormat.ContainsKey(Field));

        Assert.Null(new GridRegion(oldFormat).ViewerLocalAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_or_null_stored_value_is_no_local_address(string stored)
    {
        TestHelpers.InMethod();
        // A database row from before the column existed reads as null (MySQL) or an empty string (PostgreSQL).
        Dictionary<string, object> kvp = AsReceived(Region().ToKeyValuePairs());
        kvp[Field] = stored;

        Assert.Null(new GridRegion(kvp).ViewerLocalAddress);
    }

    [Fact]
    public void Copying_a_grid_region_keeps_its_local_address()
    {
        TestHelpers.InMethod();
        Assert.Equal("10.0.0.20", new GridRegion(Region("10.0.0.20")).ViewerLocalAddress);
        Assert.Null(new GridRegion(Region()).ViewerLocalAddress);
    }

    [Fact]
    public void The_grid_service_hands_back_the_local_address_a_region_registered_and_forgets_it_when_the_region_stops_registering_one()
    {
        TestHelpers.InMethod();
        SceneHelpers sh = new();
        TestScene scene = sh.SetupScene("example", TestHelpers.ParseTail(0x10), 1000, 1000);
        UUID scope = scene.RegionInfo.ScopeID;

        scene.RegionInfo.ViewerLocalAddress = "192.0.2.77";
        Assert.Equal(string.Empty, scene.GridService.RegisterRegion(scope, new GridRegion(scene.RegionInfo)));
        // GetRegionsByName asks the grid service itself; the single-region lookups answer from a cache.
        Assert.Equal("192.0.2.77", Assert.Single(scene.GridService.GetRegionsByName(scope, "example", 5)).ViewerLocalAddress);

        scene.RegionInfo.ViewerLocalAddress = null;
        Assert.Equal(string.Empty, scene.GridService.RegisterRegion(scope, new GridRegion(scene.RegionInfo)));
        Assert.Null(Assert.Single(scene.GridService.GetRegionsByName(scope, "example", 5)).ViewerLocalAddress);
    }

    [Fact]
    public void A_region_registers_the_local_address_it_was_given()
    {
        TestHelpers.InMethod();
        RegionInfo info = new()
        {
            RegionID = TestHelpers.ParseTail(0x2),
            RegionName = "example",
            ExternalHostName = "region.example.org",
            ViewerLocalAddress = "10.0.0.20",
        };

        Assert.Equal("10.0.0.20", new GridRegion(info).ViewerLocalAddress);
        Assert.Null(new GridRegion(new RegionInfo { RegionName = "other" }).ViewerLocalAddress);
    }
}
