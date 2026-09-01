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

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Host-lifetime orchestration for the RegionServer.
/// </summary>
/// <remarks>
/// All region boot and shutdown work is delegated to <see cref="IRegionRuntime"/>.
/// This service only coordinates startup and shutdown through the generic host and
/// never takes ownership of the process lifetime or performs side effects in its
/// constructor.
/// </remarks>
public sealed class RegionService : IHostedService
{
    private readonly ILogger<RegionService> _logger;
    private readonly IRegionRuntime _runtime;

    public RegionService(
        ILogger<RegionService> logger,
        IRegionRuntime runtime)
    {
        _logger = logger;
        _runtime = runtime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{Service} is running.", nameof(RegionService));

        _runtime.Initialize();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{Service} is stopping.", nameof(RegionService));

        _runtime.Stop();

        return Task.CompletedTask;
    }
}
