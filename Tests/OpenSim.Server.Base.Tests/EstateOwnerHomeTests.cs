/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.CompilerServices;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.RegionServer;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using Xunit;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Server.Base.Tests;

/// <summary>
/// The estate owner named in [Estates] is created at a simulator's first start, before its region registers with
/// the grid. The new account's home is the grid's default region, so on a new standalone no region could be found
/// and the owner had no home ("Unable to set home for account"), and could not Teleport Home. Unless another
/// default region is online, the owner's home is now the region being started.
/// </summary>
[Collection("MainConsole")]
public class EstateOwnerHomeTests
{
    /// <summary>Answers the calls the test names; every other call answers the default of its type.</summary>
    public class Stub : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, (bool, object?)>? m_handler;

        public static T Create<T>(Func<MethodInfo, object?[]?, (bool, object?)>? handler = null) where T : class
        {
            T proxy = Create<T, Stub>();
            ((Stub)(object)proxy).m_handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method is null)
                return null;
            if (m_handler is not null)
            {
                var (handled, result) = m_handler(method, args);
                if (handled)
                    return result;
            }
            Type type = method.ReturnType;
            return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }

    private sealed class Accounts : UserAccountService
    {
        public Accounts(IConfigSource config, IGridService grid, IGridUserService gridUsers) : base(config)
        {
            m_GridService = grid;
            m_GridUserService = gridUsers;
        }
    }

    private sealed record Result(UUID Owner, UUID Region, UUID Home);

    /// <summary>
    /// Runs the first-start estate owner set-up of <see cref="OpenSimBase"/> for a new region, on a grid whose
    /// online default regions are <paramref name="defaults"/>, and answers the home the owner was given.
    /// </summary>
    private static Result SetUpEstateOwner(params GridRegion[] defaults)
    {
        ICommandConsole previous = MainConsole.Instance;
        MainConsole.Instance = new MockConsole();
        try
        {
            Dictionary<string, UUID> homes = new();
            IGridUserService gridUsers = Stub.Create<IGridUserService>((m, a) =>
            {
                if (m.Name != nameof(IGridUserService.SetHome) || a is null)
                    return (false, null);
                homes[(string)a[0]!] = (UUID)a[1]!;
                return (true, true);
            });
            IGridService grid = Stub.Create<IGridService>((m, a) =>
                m.Name == nameof(IGridService.GetDefaultRegions) ? (true, defaults.ToList()) : (false, null));

            IniConfigSource accountConfig = new();
            accountConfig.AddConfig("UserAccountService").Set("StorageProvider", "OpenSim.Data.Null.dll");
            Accounts accounts = new(accountConfig, grid, gridUsers);

            RegionInfo region = new() { RegionID = UUID.Random(), RegionName = "First Start" };
            region.EstateSettings = new EstateSettings();
            Scene scene = (Scene)RuntimeHelpers.GetUninitializedObject(typeof(Scene));
            typeof(SceneBase).GetProperty(nameof(SceneBase.RegionInfo))!.SetValue(scene, region);
            typeof(Scene).GetField("m_UserAccountService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(scene, accounts);

            UUID owner = UUID.Random();
            IniConfigSource config = new();
            IConfig estates = config.AddConfig("Estates");
            estates.Set("DefaultEstateOwnerName", "Estate Owner" + owner.ToString()[..8]);
            estates.Set("DefaultEstateOwnerUUID", owner.ToString());
            estates.Set("DefaultEstateOwnerEMail", "owner@example.invalid");
            estates.Set("DefaultEstateOwnerPassword", "secret");

            OpenSimBase application = (OpenSimBase)RuntimeHelpers.GetUninitializedObject(typeof(OpenSimBase));
            application.Config = config;
            typeof(RegionApplicationBase).GetField("m_estateDataService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(application, Stub.Create<IEstateDataService>());

            typeof(OpenSimBase).GetMethod("SetUpEstateOwner", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(application, [scene]);

            Assert.Equal(owner, region.EstateSettings.EstateOwner);
            return new Result(owner, region.RegionID, homes.TryGetValue(owner.ToString(), out UUID home) ? home : UUID.Zero);
        }
        finally
        {
            MainConsole.Instance = previous;
        }
    }

    [Fact]
    public void TheEstateOwnerMadeAtFirstStartHasTheRegionAsHome()
    {
        // the region being started is not registered yet, and no other region is online
        Result result = SetUpEstateOwner();

        Assert.Equal(result.Region, result.Home);
    }

    [Fact]
    public void AnOnlineDefaultRegionIsTheEstateOwnersHome()
    {
        GridRegion welcome = new() { RegionID = UUID.Random(), RegionName = "Welcome" };

        Result result = SetUpEstateOwner(welcome);

        Assert.Equal(welcome.RegionID, result.Home);
    }
}
