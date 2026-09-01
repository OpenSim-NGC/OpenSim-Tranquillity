/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.MoneyServer;
using OpenSim.Framework.Servers;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Tests for <see cref="MoneyConsoleRunnerService"/> lifecycle behaviour.
/// </summary>
public sealed class MoneyConsoleRunnerServiceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static FakeServerBase MakeServerBase(Action onPrompt) => new FakeServerBase(onPrompt);

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_DoesNotPrompt_BeforeApplicationStarted()
    {
        int promptCount = 0;
        var lifetime = new FakeHostLifetime();
        var serverBase = MakeServerBase(() => promptCount++);

        using var cts = new CancellationTokenSource();

        var svc = new MoneyConsoleRunnerService(
            serverBase,
            new NullLogger<MoneyConsoleRunnerService>(),
            lifetime);

        // Start service but do NOT fire ApplicationStarted.
        var executeTask = svc.StartAsync(cts.Token);

        // Give the service a moment — it should be waiting, not prompting.
        await Task.Delay(50);
        cts.Cancel();
        await executeTask;

        Assert.Equal(0, promptCount);
    }

    [Fact]
    public async Task ExecuteAsync_Prompts_AfterApplicationStarted()
    {
        // Use a semaphore so the test doesn't rely on arbitrary time delays.
        var promptSem = new SemaphoreSlim(0, 1);
        var lifetime = new FakeHostLifetime();

        var serverBase = MakeServerBase(() =>
        {
            promptSem.Release();
            Thread.Sleep(20); // prevent tight spin
        });

        using var cts = new CancellationTokenSource();

        var svc = new MoneyConsoleRunnerService(
            serverBase,
            new NullLogger<MoneyConsoleRunnerService>(),
            lifetime);

        await svc.StartAsync(cts.Token);

        // Signal application started — prompt loop should begin.
        lifetime.FireApplicationStarted();

        // Wait for the first prompt with a generous timeout.
        bool prompted = await promptSem.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        Assert.True(prompted, "Expected at least one console prompt after ApplicationStarted.");
    }

    [Fact]
    public async Task ExecuteAsync_ExitsCleanly_WhenCancelledBeforeStarted()
    {
        var lifetime = new FakeHostLifetime();
        var serverBase = MakeServerBase(() => { });

        // Cancel immediately.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var svc = new MoneyConsoleRunnerService(
            serverBase,
            new NullLogger<MoneyConsoleRunnerService>(),
            lifetime);

        // Should complete without throwing.
        await svc.StartAsync(cts.Token);
        await svc.StopAsync(CancellationToken.None);
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

    private sealed class FakeServerBase : IServerBase
    {
        private readonly Action _onPrompt;
        public IConfigSource Config { get; set; }
        public ICommandConsole Console { get; set; }
        public DateTime StartupTime { get; set; }
        public string Version { get; set; } = "test";

        public FakeServerBase(Action onPrompt)
        {
            _onPrompt = onPrompt;
            Console = new FakeConsole(onPrompt);
        }

        public void CreatePIDFile(string path) { }
        public string GetThreadsReport() => string.Empty;
        public string GetUptimeReport() => string.Empty;
        public string GetVersionText() => string.Empty;
        public void HandleShow(string module, string[] cmd) { }
        public void HandleThreadsAbort(string module, string[] cmd) { }
        public void LogEnvironmentInformation() { }
        public void RegisterCommonAppenders(IConfig startupConfig) { }
        public void RegisterCommonCommands() { }
        public void RegisterCommonComponents(IConfigSource configSource) { }
        public void RemovePIDFile() { }
        public void Shutdown() { }
    }

    /// <summary>
    /// ICommandConsole implementation whose no-arg Prompt() invokes a test action.
    /// Inheriting MockConsole doesn't work because MockConsole.Prompt() is not virtual.
    /// </summary>
    private sealed class FakeConsole : ICommandConsole
    {
        private readonly Action _onPrompt;
        private readonly MockCommands _commands = new();

        public FakeConsole(Action onPrompt) => _onPrompt = onPrompt;

        // ICommandConsole / IConsole boilerplate
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

        // The method under test: dispatch to the injected action.
        public void Prompt() => _onPrompt();
    }
}
