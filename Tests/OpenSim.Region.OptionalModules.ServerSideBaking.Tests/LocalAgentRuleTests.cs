/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// Who a flag-on region bakes for, as <see cref="ServerSideBakingModule"/> wires it when the region loads: an agent
/// whose account the grid's account service holds as local to this grid. A Hypergrid visitor's viewer bakes for
/// itself, and a region with no account service to ask bakes for everyone, as it did before visitors were told apart.
/// </summary>
// Process-wide state: SceneHelpers sets MainConsole.Instance and adds the scene to SceneManager. No other class in
// this project touches either, and the tests within one class run one at a time.
public class LocalAgentRuleTests
{
    private static Scene FlagOnScene(IUserAccountService accounts)
    {
        var scene = new SceneHelpers().SetupScene();

        // the account service the region asks: replaced by the test's own, or removed
        scene.UnregisterModuleInterface(scene.RequestModuleInterface<IUserAccountService>());
        if (accounts is not null)
            scene.RegisterModuleInterface(accounts);
        Assert.Same(accounts, scene.UserAccountService);

        var config = new IniConfigSource();
        config.AddConfig(ServerSideBakingModule.ConfigSection).Set("ServerSideBaking", "true");
        var module = new ServerSideBakingModule();
        SceneHelpers.SetupSceneModules(scene, config, module);
        Assert.True(module.RegionOf(scene).ServerSideBakingEnabled);
        return scene;
    }

    /// <summary>An account service that holds <paramref name="accounts"/> and nobody else.</summary>
    private static IUserAccountService Holding(params UserAccount[] accounts)
        => StubProxy.Create<IUserAccountService>((method, args) =>
            method.Name == nameof(IUserAccountService.GetUserAccount) && args.Length == 2 && args[1] is UUID id
                ? (true, accounts.FirstOrDefault(a => a.PrincipalID == id))
                : (false, null));

    [Fact]
    public void AnAccountTheGridHoldsAsNotLocalIsAVisitorAndIsNotBaked()
    {
        var resident = new UserAccount(UUID.Random());
        var visitor = new UserAccount(UUID.Random()) { LocalToGrid = false };
        var scene = FlagOnScene(Holding(resident, visitor));
        var ssb = scene.RequestModuleInterface<IServerSideBakingRegion>();

        Assert.True(ssb.BakesFor(resident.PrincipalID));
        Assert.False(ssb.BakesFor(visitor.PrincipalID));
    }

    [Fact]
    public void AnAgentWithNoAccountHereIsNotBaked()
    {
        var scene = FlagOnScene(Holding(new UserAccount(UUID.Random())));

        Assert.False(scene.RequestModuleInterface<IServerSideBakingRegion>().BakesFor(UUID.Random()));
    }

    [Fact]
    public void WithNoAccountServiceEveryAgentIsBaked()
    {
        var scene = FlagOnScene(null);

        Assert.True(scene.RequestModuleInterface<IServerSideBakingRegion>().BakesFor(UUID.Random()));
    }
}
