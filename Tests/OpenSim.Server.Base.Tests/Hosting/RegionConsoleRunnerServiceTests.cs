/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Tests for <see cref="RegionConsoleRunnerService"/> lifecycle behaviour. This service
/// replaces the legacy <c>Application.Main()</c> <c>while (true) MainConsole.Instance.Prompt()</c>
/// loop, so the key guarantees are: it does not read the console before the host has
/// started, it does prompt once the host is started, and it exits cleanly on cancellation.
/// Because the region console is the process-wide <see cref="MainConsole.Instance"/>, these
/// tests swap that static in and restore it afterwards.
/// </summary>
[Collection("MainConsole")]
public sealed class RegionConsoleRunnerServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DoesNotPrompt_BeforeApplicationStarted()
    {
        int promptCount = 0;
        var lifetime = new FakeHostLifetime();
        ICommandConsole previous = MainConsole.Instance;
        MainConsole.Instance = new FakeConsole(() => Interlocked.Increment(ref promptCount));

        try
        {
            using var cts = new CancellationTokenSource();

            var svc = new RegionConsoleRunnerService(
                new NullLogger<RegionConsoleRunnerService>(),
                lifetime);

            // Start service but do NOT fire ApplicationStarted.
            await svc.StartAsync(cts.Token);

            // Give the service a moment — it should be waiting, not prompting.
            await Task.Delay(50);
            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            Assert.Equal(0, promptCount);
        }
        finally
        {
            MainConsole.Instance = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Prompts_AfterApplicationStarted()
    {
        var promptSem = new SemaphoreSlim(0, 1);
        var lifetime = new FakeHostLifetime();
        ICommandConsole previous = MainConsole.Instance;
        MainConsole.Instance = new FakeConsole(() =>
        {
            promptSem.Release();
            Thread.Sleep(20); // prevent tight spin
        });

        try
        {
            using var cts = new CancellationTokenSource();

            var svc = new RegionConsoleRunnerService(
                new NullLogger<RegionConsoleRunnerService>(),
                lifetime);

            await svc.StartAsync(cts.Token);

            // Signal application started — prompt loop should begin.
            lifetime.FireApplicationStarted();

            bool prompted = await promptSem.WaitAsync(TimeSpan.FromSeconds(5));

            cts.Cancel();
            await svc.StopAsync(CancellationToken.None);

            Assert.True(prompted, "Expected at least one console prompt after ApplicationStarted.");
        }
        finally
        {
            MainConsole.Instance = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ExitsCleanly_WhenCancelledBeforeStarted()
    {
        var lifetime = new FakeHostLifetime();
        ICommandConsole previous = MainConsole.Instance;
        MainConsole.Instance = new FakeConsole(() => { });

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var svc = new RegionConsoleRunnerService(
                new NullLogger<RegionConsoleRunnerService>(),
                lifetime);

            // Should complete without throwing.
            await svc.StartAsync(cts.Token);
            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            MainConsole.Instance = previous;
        }
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public CancellationToken ApplicationStarted  => _startedCts.Token;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped  => _stoppedCts.Token;

        public void StopApplication() => _stoppingCts.Cancel();
        public void FireApplicationStarted() => _startedCts.Cancel();
    }

    /// <summary>
    /// ICommandConsole implementation whose no-arg Prompt() invokes a test action.
    /// </summary>
    private sealed class FakeConsole : ICommandConsole
    {
        private readonly Action _onPrompt;
        private readonly MockCommands _commands = new();

        public FakeConsole(Action onPrompt) => _onPrompt = onPrompt;

        public event OnOutputDelegate OnOutput;
        public ICommands Commands => _commands;
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
        public void ReadConfig(IConfigSource configSource) { }
        public void SetCntrCHandler(OnCntrCCelegate handler) { }

        public void Prompt() => _onPrompt();
    }
}
