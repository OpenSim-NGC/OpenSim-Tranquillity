/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Reflection;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.ApplicationPlugins.RemoteController;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A new account's home is the grid's default region. admin_create_user with start_region_x/y 0, 0 on a grid with
/// no region flagged DefaultRegion left the account without a home, and logged two warnings; it now uses the region
/// at that place, else the default region, else the region the request was made to. An account without a home
/// cannot Teleport Home. The estate owner made at first start is tested in OpenSim.Server.Base.Tests.
/// </summary>
[Xunit.Collection("LogCapture")]
public class NewAccountHomeTests
{
    private sealed class Homes
    {
        public readonly Dictionary<string, UUID> ByUser = new();

        public IGridUserService Service() => StubProxy.Create<IGridUserService>((m, a) =>
        {
            if (m.Name == nameof(IGridUserService.SetHome))
            {
                lock (ByUser)
                    ByUser[(string)a[0]] = (UUID)a[1];
                return (true, true);
            }
            return (false, null);
        });

        public UUID Of(UUID user)
        {
            lock (ByUser)
                return ByUser.TryGetValue(user.ToString(), out UUID home) ? home : UUID.Zero;
        }
    }

    /// <summary>A grid with the given regions online, of which <paramref name="defaults"/> are flagged DefaultRegion.</summary>
    private static IGridService Grid(IEnumerable<GridRegion> regions, params GridRegion[] defaults)
    {
        List<GridRegion> all = regions.ToList();
        return StubProxy.Create<IGridService>((m, a) => m.Name switch
        {
            nameof(IGridService.GetDefaultRegions) => (true, defaults.ToList()),
            nameof(IGridService.GetRegionByPosition) => (true, all.FirstOrDefault(r =>
                r.RegionLocX == (int)a[1] && r.RegionLocY == (int)a[2])),
            nameof(IGridService.GetRegionByUUID) => (true, all.FirstOrDefault(r => r.RegionID == (UUID)a[1])),
            _ => (false, null),
        });
    }

    private static GridRegion Region(Scene scene) => new(scene.RegionInfo);

    private static GridRegion Region(string name, uint x, uint y) => new()
    {
        RegionID = UUID.Random(),
        RegionName = name,
        RegionLocX = (int)Util.RegionToWorldLoc(x),
        RegionLocY = (int)Util.RegionToWorldLoc(y),
    };

    private static void SetSceneField(Scene scene, string field, object value) =>
        typeof(Scene).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scene, value);

    private static bool HomeWarned(LogCapture log) =>
        log.Messages.Any(m => m.Contains("Unable to set home", StringComparison.Ordinal)
            || m.Contains("No region at", StringComparison.Ordinal));

    #region UserAccountService.CreateUser

    private sealed class EstateOwnerAccounts : UserAccountService
    {
        public EstateOwnerAccounts(IConfigSource config, IGridService grid, IGridUserService gridUsers) : base(config)
        {
            m_GridService = grid;
            m_GridUserService = gridUsers;
        }
    }

    private static UserAccountService Accounts(IGridService grid, IGridUserService gridUsers)
    {
        IniConfigSource config = new();
        config.AddConfig("UserAccountService").Set("StorageProvider", "OpenSim.Data.Null.dll");
        return new EstateOwnerAccounts(config, grid, gridUsers);
    }

    [Xunit.Fact]
    public void WithoutADefaultRegionTheWarningNamesTheSetting()
    {
        Homes homes = new();
        UserAccountService accounts = Accounts(Grid([]), homes.Service());
        UUID id = UUID.Random();

        using LogCapture log = new();
        Xunit.Assert.NotNull(accounts.CreateUser(UUID.Zero, id, "No", "Home" + id.ToString()[..8], "secret", ""));

        Xunit.Assert.Equal(UUID.Zero, homes.Of(id));
        string warning = log.Messages.Single(m => m.Contains("Unable to set home", StringComparison.Ordinal));
        Xunit.Assert.Equal(LogLevel.Warning, log.LevelOf("Unable to set home"));
        Xunit.Assert.Contains("DefaultRegion", warning);
    }

    #endregion

    #region admin_create_user

    private static (RemoteAdminPlugin Plugin, Homes Homes, Scene Alpha, Scene Beta) RemoteAdmin(params GridRegion[] defaults)
    {
        SceneHelpers sh = new();
        Scene alpha = sh.SetupScene("Alpha", UUID.Random(), 1000, 1000);
        Scene beta = sh.SetupScene("Beta", UUID.Random(), 1001, 1000);
        Xunit.Assert.True(sh.SceneManager.TrySetCurrentScene(alpha.RegionInfo.RegionID));

        Homes homes = new();
        IGridService grid = Grid([Region(alpha), Region(beta), .. defaults], defaults);
        IGridUserService gridUsers = homes.Service();
        foreach (Scene scene in new[] { alpha, beta })
        {
            SetSceneField(scene, "m_GridService", grid);
            SetSceneField(scene, "m_GridUserService", gridUsers);
        }

        IniConfigSource config = new();
        IOpenSimBase application = StubProxy.Create<IOpenSimBase>((m, a) =>
            m.Name == "get_SceneManager" ? (true, sh.SceneManager) : (false, null));
        RemoteAdminPlugin plugin = UninitializedService.Create<RemoteAdminPlugin>(
            ("m_application", application), ("m_configSource", config), ("m_config", config.AddConfig("RemoteAdmin")));
        return (plugin, homes, alpha, beta);
    }

    private static UUID CreateUser(RemoteAdminPlugin plugin, uint x, uint y)
    {
        XmlRpcRequest request = new();
        request.Params.Add(new Hashtable
        {
            ["user_firstname"] = "Remote",
            ["user_lastname"] = "User" + UUID.Random().ToString()[..8],
            ["user_password"] = "secret",
            ["start_region_x"] = (int)x,
            ["start_region_y"] = (int)y,
        });
        XmlRpcResponse response = new() { Value = new Hashtable() };
        typeof(RemoteAdminPlugin).GetMethod("XmlRpcCreateUserMethod", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(plugin, [request, response, new IPEndPoint(IPAddress.Loopback, 9000)]);

        Hashtable result = (Hashtable)response.Value;
        Xunit.Assert.Equal(true, result["success"]);
        return new UUID((string)result["avatar_uuid"]!);
    }

    [Xunit.Fact]
    public void AdminCreateUserWithoutAStartRegionOrDefaultRegionHasTheRequestRegionAsHome()
    {
        var (plugin, homes, alpha, _) = RemoteAdmin();

        using LogCapture log = new();
        UUID user = CreateUser(plugin, 0, 0);

        Xunit.Assert.Equal(alpha.RegionInfo.RegionID, homes.Of(user));
        Xunit.Assert.False(HomeWarned(log), string.Join("\n", log.Messages));
    }

    [Xunit.Fact]
    public void AdminCreateUserWithoutAStartRegionHasTheDefaultRegionAsHome()
    {
        GridRegion welcome = Region("Welcome", 1005, 1005);
        var (plugin, homes, _, _) = RemoteAdmin(welcome);

        UUID user = CreateUser(plugin, 0, 0);

        Xunit.Assert.Equal(welcome.RegionID, homes.Of(user));
    }

    [Xunit.Fact]
    public void AdminCreateUserStartRegionIsTheHome()
    {
        GridRegion welcome = Region("Welcome", 1005, 1005);
        var (plugin, homes, _, beta) = RemoteAdmin(welcome);

        UUID user = CreateUser(plugin, 1001, 1000);

        Xunit.Assert.Equal(beta.RegionInfo.RegionID, homes.Of(user));
    }

    [Xunit.Fact]
    public void AdminCreateUserStartRegionWithoutARegionWarnsAndUsesTheDefaultHome()
    {
        var (plugin, homes, alpha, _) = RemoteAdmin();

        using LogCapture log = new();
        UUID user = CreateUser(plugin, 5, 7);

        Xunit.Assert.Equal(alpha.RegionInfo.RegionID, homes.Of(user));
        Xunit.Assert.Equal(LogLevel.Warning, log.LevelOf("No region at 5,7"));
    }

    #endregion
}
