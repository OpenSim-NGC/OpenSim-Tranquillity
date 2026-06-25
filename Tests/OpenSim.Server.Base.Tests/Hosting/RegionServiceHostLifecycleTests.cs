/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Host-lifetime integration tests for RegionService.
/// These tests use a real HostBuilder and verify lifecycle behavior through host APIs,
/// confirming the region runtime is started and stopped by the generic host rather than
/// by a static <c>Application.Main()</c> loop.
/// </summary>
public sealed class RegionServiceHostLifecycleTests
{
    [Fact]
    public async Task Host_StartAndStop_CallsRuntimeInitializeAndStop()
    {
        var runtime = new FakeRegionRuntime();

        using IHost host = BuildHost(runtime);

        await host.StartAsync();

        Assert.Equal(1, runtime.InitializeCalls);
        Assert.Equal(0, runtime.StopCalls);

        await host.StopAsync();

        Assert.Equal(1, runtime.StopCalls);
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenRuntimeInitializeFails()
    {
        var runtime = new FakeRegionRuntime
        {
            InitializeException = new InvalidOperationException("runtime init failed"),
        };

        using IHost host = BuildHost(runtime);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    private static IHost BuildHost(FakeRegionRuntime runtime)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();

                services.AddSingleton<IRegionRuntime>(runtime);
                services.AddHostedService<RegionService>();
            })
            .Build();
    }

    private sealed class FakeRegionRuntime : IRegionRuntime
    {
        public int InitializeCalls { get; private set; }
        public int StopCalls { get; private set; }

        public Exception InitializeException { get; set; }

        public void Initialize()
        {
            InitializeCalls++;
            if (InitializeException is not null)
                throw InitializeException;
        }

        public void Stop() => StopCalls++;
    }
}
