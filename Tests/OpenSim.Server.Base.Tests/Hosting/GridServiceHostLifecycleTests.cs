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
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.GridServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// D6 host-lifetime integration tests for GridService.
/// These tests use a real HostBuilder and verify lifecycle behavior through host APIs.
/// </summary>
public sealed class GridServiceHostLifecycleTests
{
    [Fact]
    public async Task Host_StartAndStop_CallsRuntimeInitializeAndStop()
    {
        var runtime = new FakeGridServerRuntime();
        var serverBase = new FakeServerBase();
        var coordinator = new FakeStartupFailureCoordinator();

        using IHost host = BuildHost(runtime, serverBase, coordinator);

        await host.StartAsync();

        Assert.Equal(1, runtime.InitializeCalls);
        Assert.Equal(0, runtime.StopCalls);

        await host.StopAsync();

        Assert.Equal(1, runtime.StopCalls);
        Assert.Equal(1, serverBase.RegisterCommonCommandsCalls);
        Assert.Equal(1, serverBase.RegisterCommonComponentsCalls);
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenRuntimeInitializeFails()
    {
        var runtime = new FakeGridServerRuntime
        {
            InitializeException = new InvalidOperationException("runtime init failed"),
        };

        using IHost host = BuildHost(runtime, new FakeServerBase(), new FakeStartupFailureCoordinator());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Host_StartAsync_Throws_WhenStartupRegistrationFails_ThroughCoordinator()
    {
        var runtime = new FakeGridServerRuntime();
        var serverBase = new FakeServerBase
        {
            ThrowOnRegisterCommonCommands = new Exception("register failed"),
        };
        var coordinator = new FakeStartupFailureCoordinator
        {
            ThrowException = new InvalidOperationException("fatal startup"),
        };

        using IHost host = BuildHost(runtime, serverBase, coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Equal(1, coordinator.ThrowFatalCalls);
        Assert.Contains("Fatal error while registering startup components.", coordinator.LastFatalMessage);
    }

    private static IHost BuildHost(
        FakeGridServerRuntime runtime,
        FakeServerBase serverBase,
        FakeStartupFailureCoordinator coordinator)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();

                services.AddSingleton<IServerBase>(serverBase);
                services.AddSingleton<IStartupFailureCoordinator>(coordinator);
                services.AddSingleton<IGridServerRuntime>(runtime);

                services.AddSingleton<GridService>();
                services.AddHostedService(sp => sp.GetRequiredService<GridService>());
            })
            .Build();
    }

    private sealed class FakeGridServerRuntime : IGridServerRuntime
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

    private sealed class FakeStartupFailureCoordinator : IStartupFailureCoordinator
    {
        public int ThrowFatalCalls { get; private set; }
        public string LastFatalMessage { get; private set; } = string.Empty;

        public Exception ThrowException { get; set; }

        public void ThrowFatal(string message, Exception exception = null)
        {
            ThrowFatalCalls++;
            LastFatalMessage = message;
            throw ThrowException ?? new InvalidOperationException(message, exception);
        }

        public void RequestStop(string message, Exception exception = null)
        {
            // Not used in this D6 GridService path.
        }
    }

    private sealed class FakeServerBase : IServerBase
    {
        public IConfigSource Config { get; set; } = new IniConfigSource();
        public ICommandConsole Console { get; set; } = new MockConsole();
        public DateTime StartupTime { get; set; }
        public string Version { get; set; } = "test-version";

        public int RegisterCommonCommandsCalls { get; private set; }
        public int RegisterCommonComponentsCalls { get; private set; }

        public Exception ThrowOnRegisterCommonCommands { get; set; }

        public void RegisterCommonCommands()
        {
            RegisterCommonCommandsCalls++;
            if (ThrowOnRegisterCommonCommands is not null)
                throw ThrowOnRegisterCommonCommands;
        }

        public void RegisterCommonComponents(IConfigSource configSource)
        {
            RegisterCommonComponentsCalls++;
        }

        public void CreatePIDFile(string path) { }
        public string GetThreadsReport() => string.Empty;
        public string GetUptimeReport() => string.Empty;
        public string GetVersionText() => string.Empty;
        public void HandleShow(string module, string[] cmd) { }
        public void HandleThreadsAbort(string module, string[] cmd) { }
        public void LogEnvironmentInformation() { }
        public void RegisterCommonAppenders(IConfig startupConfig) { }
        public void RemovePIDFile() { }
        public void Shutdown() { }
    }
}
