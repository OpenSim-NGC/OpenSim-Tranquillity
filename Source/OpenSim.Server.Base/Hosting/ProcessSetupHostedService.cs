/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Applies process-level defaults once at host start.
/// </summary>
public sealed class ProcessSetupHostedService : IHostedService
{
    private readonly IProcessSetupService _processSetupService;

    public ProcessSetupHostedService(IProcessSetupService processSetupService)
    {
        _processSetupService = processSetupService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processSetupService.ApplyDefaults();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
