/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenSim.Framework.Servers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.MoneyServer;

/// <summary>
/// Hosts the interactive console prompt loop as a background service.
/// </summary>
/// <remarks>
/// Separating console I/O from <see cref="MoneyService.StartAsync"/> is the key
/// step that allows the host to finish its start sequence before user input is
/// accepted, and allows <see cref="MoneyService.StartAsync"/> to return promptly.
///
/// The prompt loop runs on a thread-pool thread via <c>Task.Run</c> because
/// <see cref="OpenSim.Framework.ICommandConsole.Prompt"/> is a blocking call that
/// reads from stdin.  This does not block the host's async machinery.
///
/// Shutdown: when the host cancels the stopping token the loop exits after the
/// current prompt completes.  If stdin is blocking the host shutdown timeout
/// (default 5 s) will elapse and the process exits normally.
/// </remarks>
public sealed class MoneyConsoleRunnerService : BackgroundService
{
    private readonly IServerBase _serverBase;
    private readonly ILogger<MoneyConsoleRunnerService> _logger;
    private readonly IHostApplicationLifetime _hostLifetime;

    public MoneyConsoleRunnerService(
        IServerBase serverBase,
        ILogger<MoneyConsoleRunnerService> logger,
        IHostApplicationLifetime hostLifetime)
    {
        _serverBase = serverBase;
        _logger = logger;
        _hostLifetime = hostLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not start reading console input until the host has finished
        // starting all other hosted services.
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

        // Run the blocking prompt loop on a thread-pool thread.
        await Task.Run(() =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _serverBase.Console.Prompt();
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    // Host is stopping — exit the loop cleanly.
                    break;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "[CONSOLE]: Unhandled error during console prompt.");
                }
            }
        }, stoppingToken).ConfigureAwait(false);
    }
}
