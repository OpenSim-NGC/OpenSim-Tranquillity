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

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Default hosted-service failure policy.
/// </summary>
public sealed class StartupFailureCoordinator : IStartupFailureCoordinator
{
    private readonly ILogger<StartupFailureCoordinator> _logger;
    private readonly IHostApplicationLifetime _hostLifetime;

    public StartupFailureCoordinator(
        ILogger<StartupFailureCoordinator> logger,
        IHostApplicationLifetime hostLifetime)
    {
        _logger = logger;
        _hostLifetime = hostLifetime;
    }

    /// <inheritdoc/>
    public void ThrowFatal(string message, Exception exception = null)
    {
        if (exception is null)
            _logger.LogCritical("[STARTUP]: {Message}", message);
        else
            _logger.LogCritical(exception, "[STARTUP]: {Message}", message);

        throw new InvalidOperationException(message, exception);
    }

    /// <inheritdoc/>
    public void RequestStop(string message, Exception exception = null)
    {
        if (exception is null)
            _logger.LogError("[LIFETIME]: {Message}", message);
        else
            _logger.LogError(exception, "[LIFETIME]: {Message}", message);

        _hostLifetime.StopApplication();
    }
}
