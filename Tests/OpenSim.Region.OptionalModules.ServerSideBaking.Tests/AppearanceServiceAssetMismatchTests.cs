/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Services.AvatarService;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// An [AppearanceService] whose AssetService is not the grid's [AssetService] is warned about when the service
/// starts, and the service still starts.
///
/// <para>The process's logger factory is swapped for a capture, which is process-wide, so the class shares the
/// "ssb-log-capture" collection with the other classes in this project that set that factory.</para>
/// </summary>
[Collection("ssb-log-capture")]
public class AppearanceServiceAssetMismatchTests
{
    private static string Plugin<T>() => typeof(T).Assembly.Location + ":" + typeof(T).Name;

    private static IConfigSource Config(string ownAssetService, string gridAssetService)
    {
        var config = new IniConfigSource();
        IConfig own = config.AddConfig(AppearanceService.ConfigName);
        own.Set("AvatarService", Plugin<FakeAvatarService>());
        own.Set("AssetService", ownAssetService);
        config.AddConfig("AssetService").Set("LocalServiceModule", gridAssetService);
        return config;
    }

    [Fact]
    public void AnAssetServiceThatIsNotTheGridsIsWarnedAboutAndTheServiceStarts()
    {
        string ours = Plugin<FakeAssetService>();
        const string grid = "OpenSim.Services.FSAssetService.dll:FSAssetConnector";
        using var log = new LogCapture();

        var service = new AppearanceService(Config(ours, grid));

        Assert.NotNull(service);
        string warning = Assert.Single(log.Messages, m => m.Contains("the grid stores assets through", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, log.LevelOf("the grid stores assets through"));
        Assert.Equal(
            $"[APPEARANCE SERVICE]: [AppearanceService] AssetService is {ours}, but the grid stores assets through {grid}. "
            + "Bakes stored there cannot be served from here; remove AssetService from [AppearanceService] to use the grid's.",
            warning);
    }

    [Fact]
    public void TheGridsOwnAssetServiceIsNotWarnedAbout()
    {
        string ours = Plugin<FakeAssetService>();
        using var log = new LogCapture();

        var service = new AppearanceService(Config(ours, ours));

        Assert.NotNull(service);
        Assert.DoesNotContain(log.Messages, m => m.Contains("the grid stores assets through", StringComparison.Ordinal));
    }
}
