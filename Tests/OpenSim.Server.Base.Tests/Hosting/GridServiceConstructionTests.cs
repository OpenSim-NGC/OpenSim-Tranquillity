/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.GridServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Verifies the Sprint 4 / D1 (phase 1) outcome: constructing <see cref="GridService"/> performs
/// no startup side effects. The constructor must not boot the HTTP server, read configuration,
/// load connectors, run the console loop, or exit the process. It must only capture its injected
/// dependencies. The throwing stubs below ensure no dependency member is touched during construction.
/// </summary>
public sealed class GridServiceConstructionTests
{
    [Fact]
    public void Constructor_DoesNotTouchInjectedDependencies()
    {
        IServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        var exception = Record.Exception(() => new GridService(
            serviceProvider,
            configuration,
            NullLogger<GridService>.Instance,
            new ThrowingServerBase(),
            new ThrowingMainServerAccessor(),
            new ThrowingRuntimeMonitoringController()));

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_ReturnsInstance()
    {
        IServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        var sut = new GridService(
            serviceProvider,
            configuration,
            NullLogger<GridService>.Instance,
            new ThrowingServerBase(),
            new ThrowingMainServerAccessor(),
            new ThrowingRuntimeMonitoringController());

        Assert.NotNull(sut);
    }

    private sealed class ThrowingMainServerAccessor : IMainServerAccessor
    {
        public IHttpServer DefaultServer => throw new InvalidOperationException("Constructor must not access the main server.");
        public IHttpServer GetHttpServer(uint port) => throw new InvalidOperationException("Constructor must not access the main server.");
        public void Stop() => throw new InvalidOperationException("Constructor must not stop the main server.");
    }

    private sealed class ThrowingRuntimeMonitoringController : IRuntimeMonitoringController
    {
        public void EnableWatchdog() => throw new InvalidOperationException("Constructor must not touch monitoring.");
        public void DisableWatchdog() => throw new InvalidOperationException("Constructor must not touch monitoring.");
        public void EnableMemoryWatchdog() => throw new InvalidOperationException("Constructor must not touch monitoring.");
        public void DisableMemoryWatchdog() => throw new InvalidOperationException("Constructor must not touch monitoring.");
        public void StopWorkManager() => throw new InvalidOperationException("Constructor must not touch monitoring.");
    }

    private sealed class ThrowingServerBase : IServerBase
    {
        public IConfigSource Config
        {
            get => throw new InvalidOperationException("Constructor must not read config.");
            set => throw new InvalidOperationException("Constructor must not write config.");
        }

        public ICommandConsole Console
        {
            get => throw new InvalidOperationException("Constructor must not access the console.");
            set => throw new InvalidOperationException("Constructor must not assign the console.");
        }

        public DateTime StartupTime
        {
            get => throw new InvalidOperationException("Constructor must not access startup time.");
            set => throw new InvalidOperationException("Constructor must not assign startup time.");
        }

        public string Version
        {
            get => throw new InvalidOperationException("Constructor must not access version.");
            set => throw new InvalidOperationException("Constructor must not assign version.");
        }

        public void CreatePIDFile(string path) => throw new InvalidOperationException("Constructor must not create a PID file.");
        public string GetThreadsReport() => throw new InvalidOperationException("Constructor must not query threads.");
        public string GetUptimeReport() => throw new InvalidOperationException("Constructor must not query uptime.");
        public string GetVersionText() => throw new InvalidOperationException("Constructor must not query version text.");
        public void HandleShow(string module, string[] cmd) => throw new InvalidOperationException("Constructor must not handle commands.");
        public void HandleThreadsAbort(string module, string[] cmd) => throw new InvalidOperationException("Constructor must not handle commands.");
        public void LogEnvironmentInformation() => throw new InvalidOperationException("Constructor must not log environment information.");
        public void RegisterCommonAppenders(IConfig startupConfig) => throw new InvalidOperationException("Constructor must not register appenders.");
        public void RegisterCommonCommands() => throw new InvalidOperationException("Constructor must not register commands.");
        public void RegisterCommonComponents(IConfigSource configSource) => throw new InvalidOperationException("Constructor must not register components.");
        public void RemovePIDFile() => throw new InvalidOperationException("Constructor must not remove a PID file.");
        public void Shutdown() => throw new InvalidOperationException("Constructor must not shut down.");
    }
}
