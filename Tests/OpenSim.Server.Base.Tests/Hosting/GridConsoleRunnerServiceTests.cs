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
using OpenSim.Framework.Servers;
using OpenSim.Server.GridServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Tests for <see cref="GridConsoleRunnerService"/> lifecycle behaviour. This service
/// replaces the legacy <c>ServicesServerBase.Run()</c> blocking prompt loop, so the key
/// guarantees are: it does not read the console before the host has started, it does
/// prompt once the host is started, and it exits cleanly on cancellation.
/// </summary>
public sealed class GridConsoleRunnerServiceTests
{
    private static FakeServerBase MakeServerBase(Action onPrompt) => new FakeServerBase(onPrompt);

    [Fact]
    public async Task ExecuteAsync_DoesNotPrompt_BeforeApplicationStarted()
    {
        int promptCount = 0;
        var lifetime = new FakeHostLifetime();
        var serverBase = MakeServerBase(() => promptCount++);

        using var cts = new CancellationTokenSource();

        var svc = new GridConsoleRunnerService(
            serverBase,
            new NullLogger<GridConsoleRunnerService>(),
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

        var svc = new GridConsoleRunnerService(
            serverBase,
            new NullLogger<GridConsoleRunnerService>(),
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

        var svc = new GridConsoleRunnerService(
            serverBase,
            new NullLogger<GridConsoleRunnerService>(),
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
        public IConfigSource Config { get; set; }
        public ICommandConsole Console { get; set; }
        public DateTime StartupTime { get; set; }
        public string Version { get; set; } = "test";

        public FakeServerBase(Action onPrompt)
        {
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
