/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Hosts the interactive console prompt loop as a background service.
/// </summary>
/// <remarks>
/// Separating console I/O from <see cref="RegionService.StartAsync"/> replaces the
/// legacy <c>Application.Main()</c> <c>while (true) MainConsole.Instance.Prompt()</c>
/// loop. It is only registered for foreground (interactive) mode; in background
/// mode the host keeps the process alive without a prompt loop, removing the need
/// for the legacy <c>OpenSimBackground</c> <c>ManualResetEvent</c> lifetime owner.
///
/// The prompt loop runs on a thread-pool thread via <c>Task.Run</c> because
/// <see cref="ICommandConsole.Prompt"/> is a blocking stdin read. It waits for the
/// host to finish starting all other hosted services (so the region console is
/// available) before accepting input.
/// </remarks>
public sealed class RegionConsoleRunnerService : BackgroundService
{
    private readonly ILogger<RegionConsoleRunnerService> _logger;
    private readonly IHostApplicationLifetime _hostLifetime;

    public RegionConsoleRunnerService(
        ILogger<RegionConsoleRunnerService> logger,
        IHostApplicationLifetime hostLifetime)
    {
        _logger = logger;
        _hostLifetime = hostLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not start reading console input until the host has finished starting
        // all other hosted services (the region console is created during region
        // startup).
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostLifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        stoppingToken.Register(() => ready.TrySetCanceled());

        try
        {
            await ready.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Observe ApplicationStopping as well as the BackgroundService stopping token.
        // A console "shutdown"/"quit" command runs synchronously inside Prompt() and calls
        // IHostApplicationLifetime.StopApplication(), which cancels ApplicationStopping before
        // Prompt() returns. The BackgroundService stoppingToken is only cancelled later when
        // StopAsync runs — by which time the loop has already re-entered the blocking Prompt()
        // call, requiring a second Enter keypress to observe cancellation. Checking
        // ApplicationStopping lets the loop exit immediately after the shutdown command runs.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, _hostLifetime.ApplicationStopping);
        CancellationToken shutdownToken = linkedCts.Token;

        // Run the blocking prompt loop on a thread-pool thread.
        await Task.Run(() =>
        {
            while (!shutdownToken.IsCancellationRequested)
            {
                try
                {
                    MainConsole.Instance?.Prompt();
                }
                catch (Exception) when (shutdownToken.IsCancellationRequested)
                {
                    // Host is stopping — exit the loop cleanly.
                    break;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "[CONSOLE]: Unhandled error during console prompt.");
                }
            }
        }, shutdownToken).ConfigureAwait(false);
    }
}
