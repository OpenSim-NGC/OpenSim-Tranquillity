/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Timers;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework.Monitoring;
using Timer = System.Timers.Timer;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionDiagnosticsService"/> implementation that periodically
/// logs an uptime, sim-extra-stats and threads report.
/// </summary>
public sealed class RegionDiagnosticsService : IRegionDiagnosticsService
{
    private const int DefaultPeriodMs = 60 * 60 * 1000;

    private readonly ILogger<RegionDiagnosticsService> _logger;

    private readonly object _lock = new();
    private Timer _timer;
    private Func<string> _uptimeReport;
    private Func<string> _threadsReport;

    public RegionDiagnosticsService(ILogger<RegionDiagnosticsService> logger)
    {
        _logger = logger;
    }

    public void Start(IConfig startupConfig, Func<string> uptimeReport, Func<string> threadsReport)
    {
        lock (_lock)
        {
            if (_timer is not null)
                return;

            _uptimeReport = uptimeReport;
            _threadsReport = threadsReport;

            int periodSeconds = startupConfig is null
                ? DefaultPeriodMs / 1000
                : startupConfig.GetInt("LogShowStatsSeconds", DefaultPeriodMs / 1000);

            int periodMs = periodSeconds * 1000;
            if (periodMs == 0)
                return;

            _timer = new Timer(periodMs) { AutoReset = true };
            _timer.Elapsed += LogDiagnostics;
            _timer.Enabled = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_timer is null)
                return;

            _timer.Enabled = false;
            _timer.Elapsed -= LogDiagnostics;
            _timer.Dispose();
            _timer = null;
        }
    }

    private void LogDiagnostics(object source, ElapsedEventArgs e)
    {
        StringBuilder sb = new StringBuilder("DIAGNOSTICS\n\n");
        sb.Append(_uptimeReport?.Invoke() ?? string.Empty);

        if (StatsManager.SimExtraStats is not null)
            sb.Append(StatsManager.SimExtraStats.Report());

        sb.Append(Environment.NewLine);
        sb.Append(_threadsReport?.Invoke() ?? string.Empty);

        _logger.LogDebug("{Report}", sb.ToString());
    }
}
