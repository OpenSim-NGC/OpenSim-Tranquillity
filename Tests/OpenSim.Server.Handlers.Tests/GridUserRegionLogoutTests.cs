/*
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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A simulator that stops without logging its avatars out (a crash, a kill) left their grid user records online: the
/// presence detector clears their sessions when the region starts again, but nothing cleared the online flag, which
/// llRequestAgentData, the login service and "show grid users online" read. A starting region now marks offline the
/// users still marked online in it. Uses the SQLite grid user store (see SqliteQueryParameterTests for the files).
/// </summary>
[Collection("SQLite stores")]
public class GridUserRegionLogoutTests
{
    private static readonly string s_dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR")
        is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;

    private static GridUserService NewService()
    {
        MainConsole.Instance ??= new MockConsole();
        IniConfigSource config = new();
        IConfig gridUser = config.AddConfig("GridUserService");
        gridUser.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        gridUser.Set("ConnectionString",
            "Data Source=" + Path.Combine(s_dir, "griduser-region-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3");
        return new GridUserService(config);
    }

    private static string Online(GridUserService s, UUID region)
    {
        string user = UUID.Random().ToString();
        s.LoggedIn(user);
        s.SetLastPosition(user, UUID.Zero, region, new Vector3(1, 2, 3), Vector3.UnitX);
        return user;
    }

    [Fact]
    public void AStartingRegionMarksOfflineTheUsersLeftOnlineInIt()
    {
        GridUserService s = NewService();
        UUID region = UUID.Random();
        UUID elsewhere = UUID.Random();

        string left = Online(s, region);
        string moved = Online(s, elsewhere);
        string gone = Online(s, region);
        s.LoggedOut(gone, UUID.Zero, region, Vector3.Zero, Vector3.UnitX);
        DateTime goneAt = s.GetGridUserInfo(gone).Logout;

        Assert.True(s.LoggedOutRegion(region));

        GridUserInfo l = s.GetGridUserInfo(left);
        Assert.False(l.Online, "a user left online in the region is still online");
        Assert.True(l.Logout >= l.Login);
        // where the user was is kept: a login to "last" goes back there
        Assert.Equal(region, l.LastRegionID);
        Assert.Equal(new Vector3(1, 2, 3), l.LastPosition);

        Assert.True(s.GetGridUserInfo(moved).Online, "a user in another region was marked offline");
        Assert.Equal(goneAt, s.GetGridUserInfo(gone).Logout);
    }

    [Fact]
    public void ARegionWithNobodyLeftChangesNothing()
    {
        GridUserService s = NewService();
        string other = Online(s, UUID.Random());

        Assert.True(s.LoggedOutRegion(UUID.Random()));

        Assert.True(s.GetGridUserInfo(other).Online);
    }
}
