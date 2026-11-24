/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Data;
using Xunit;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// <see cref="UserAgentService.LocateUser"/> answers which grid a resident is on, from their
/// travel sessions. The instant message service forwards a message to that grid when the
/// resident is not online here, so a session on this grid must never be named as elsewhere.
/// </summary>
public class LocateUserTests
{
    // UserAgentService lower-cases its own gatekeeper URI and ends it with a slash.
    private const string ThisGrid = "http://grid.example:8002/";

    private static HGTravelingData Session(string gridExternalName) => new()
    {
        SessionID = UUID.Random(),
        UserID = UUID.Random(),
        Data = new Dictionary<string, string> { ["GridExternalName"] = gridExternalName }
    };

    [Theory]
    [InlineData("http://grid.example:8002/")]
    [InlineData("http://grid.example:8002")]
    [InlineData("http://Grid.Example:8002/")]
    [InlineData("HTTP://GRID.EXAMPLE:8002")]
    public void SessionOnThisGridIsNotElsewhere(string storedGridURI)
    {
        Assert.Equal(string.Empty, UserAgentService.ForeignGridOf(new[] { Session(storedGridURI) }, ThisGrid));
    }

    [Fact]
    public void SessionOnAnotherGridNamesThatGrid()
    {
        Assert.Equal("http://other.example:8002/",
            UserAgentService.ForeignGridOf(new[] { Session("http://other.example:8002/") }, ThisGrid));
    }

    [Fact]
    public void AnotherGridIsFoundPastASessionOnThisGrid()
    {
        HGTravelingData[] sessions = { Session("http://Grid.Example:8002"), Session("http://other.example:8002/") };

        Assert.Equal("http://other.example:8002/", UserAgentService.ForeignGridOf(sessions, ThisGrid));
    }

    [Fact]
    public void NoSessionsIsNotElsewhere()
    {
        Assert.Equal(string.Empty, UserAgentService.ForeignGridOf(null, ThisGrid));
        Assert.Equal(string.Empty, UserAgentService.ForeignGridOf(Array.Empty<HGTravelingData>(), ThisGrid));
    }
}
