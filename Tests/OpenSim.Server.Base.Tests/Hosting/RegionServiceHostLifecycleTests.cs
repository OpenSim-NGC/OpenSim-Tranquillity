/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Host-lifetime integration tests for RegionService.
/// These tests use a real HostBuilder and verify lifecycle behavior through host APIs,
/// confirming the region runtime is started and stopped by the generic host rather than
/// by a static <c>Application.Main()</c> loop. The interactive variant also hosts the
/// console runner, which reads the process-wide <see cref="OpenSim.Framework.MainConsole.Instance"/>;
/// the <c>MainConsole</c> collection serializes these with the console-runner tests.
/// </summary>
[Collection("MainConsole")]
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

    [Fact]
    public async Task Host_Background_StartsRuntimeWithoutConsoleRunner_StopsCleanly()
    {
        // Background mode registers only the region runtime host; there is no
        // interactive prompt loop, and the generic host keeps the process alive.
        var runtime = new FakeRegionRuntime();

        using IHost host = BuildHost(runtime, interactive: false);

        await host.StartAsync();
        Assert.Equal(1, runtime.InitializeCalls);

        await host.StopAsync();
        Assert.Equal(1, runtime.StopCalls);
    }

    [Fact]
    public async Task Host_Interactive_StartsRuntimeAndConsoleRunner_StopsCleanly()
    {
        // Interactive mode additionally hosts the console prompt loop. Starting and
        // stopping must remain deterministic through the host lifetime APIs.
        var runtime = new FakeRegionRuntime();

        using IHost host = BuildHost(runtime, interactive: true);

        // The console runner blocks on MainConsole.Instance.Prompt(); use a fake that
        // parks until cancelled so the loop neither busy-spins nor reaches real stdin.
        ICommandConsole previous = MainConsole.Instance;
        MainConsole.Instance = new ParkingConsole();

        try
        {
            await host.StartAsync();
            Assert.Equal(1, runtime.InitializeCalls);

            // Stopping the host must cancel the console runner and stop the runtime
            // within a bounded time (no hang on the blocking prompt loop).
            Task stop = host.StopAsync();
            Task completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(stop, completed);
            await stop;

            Assert.Equal(1, runtime.StopCalls);
        }
        finally
        {
            MainConsole.Instance = previous;
        }
    }

    private static IHost BuildHost(FakeRegionRuntime runtime, bool interactive = false)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();

                services.AddSingleton<IRegionRuntime>(runtime);
                services.AddHostedService<RegionService>();

                if (interactive)
                    services.AddHostedService<RegionConsoleRunnerService>();
            })
            .Build();
    }

    /// <summary>
    /// A console whose blocking <c>Prompt()</c> parks until the calling thread is
    /// interrupted/aborted by host shutdown, avoiding both a busy-spin and real stdin.
    /// </summary>
    private sealed class ParkingConsole : ICommandConsole
    {
        private readonly System.Threading.ManualResetEventSlim _gate = new(false);

        public event OnOutputDelegate OnOutput;
        public ICommands Commands { get; } = new MockCommands();
        public string DefaultPrompt { get; set; } = string.Empty;
        public IScene ConsoleScene { get; set; }
        public void RunCommand(string cmd) { }
        public string ReadLine(string p, bool isCommand, bool e) => string.Empty;
        public void WriteLine(string s) { }
        public void Output(string format) { }
        public void Output(string format, params object[] components) { }
        public string Prompt(string p) => string.Empty;
        public string Prompt(string p, string def) => string.Empty;
        public string Prompt(string p, List<char> excludedCharacters) => string.Empty;
        public string Prompt(string p, string def, List<char> excludedCharacters, bool echo) => string.Empty;
        public string Prompt(string prompt, string defaultresponse, List<string> options) => string.Empty;
        public string PasswdPrompt(string p) => string.Empty;
        public void ReadConfig(Nini.Config.IConfigSource configSource) { }
        public void SetCntrCHandler(OnCntrCCelegate handler) { }

        public void Prompt() => _gate.Wait(50);
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

/// <summary>
/// xUnit collection definition used to serialize tests that mutate the process-wide
/// <see cref="OpenSim.Framework.MainConsole.Instance"/> static, preventing cross-test
/// interference between the console runner and host-lifetime integration tests.
/// </summary>
[CollectionDefinition("MainConsole", DisableParallelization = true)]
public sealed class MainConsoleCollection
{
}

