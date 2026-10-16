/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;

using OpenSim.Framework;
using OpenSim.Framework.Console;

using Serilog;
using Serilog.Events;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Shared Microsoft.Extensions.Logging setup for the server entry points.
/// </summary>
public static class OpenSimLoggingBuilderExtensions
{
    /// <summary>
    /// Registers the interactive console sink and a daily rolling file sink.
    /// </summary>
    /// <param name="builder">Logging builder being configured.</param>
    /// <param name="serviceName">Used as the log file base name, e.g. OpenSim.Server.GridServer.</param>
    /// <param name="logPath">Directory that log files are written to.</param>
    /// <param name="configuredLogFile">Optional log file path from the Startup ini section.</param>
    /// <param name="configuredStatsLogFile">Optional stats log file path from the Startup ini section.</param>
    public static ILoggingBuilder AddOpenSimLogging(
        this ILoggingBuilder builder,
        string serviceName,
        string logPath,
        string configuredLogFile = null,
        string configuredStatsLogFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (string.IsNullOrWhiteSpace(logPath))
            logPath = ".";

        string logFilePath = string.IsNullOrWhiteSpace(configuredLogFile)
            ? Path.Combine(logPath, $"{serviceName}.log")
            : configuredLogFile;
        string statsLogFilePath = string.IsNullOrWhiteSpace(configuredStatsLogFile)
            ? "./OpenSimStats.log"
            : configuredStatsLogFile;

        LoggingFilePaths.Configure(
            logFilePath,
            statsLogFilePath,
            !string.IsNullOrWhiteSpace(configuredStatsLogFile));

        builder.AddOpenSimConsole();

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Verbose();

        if (string.IsNullOrWhiteSpace(configuredStatsLogFile))
        {
            loggerConfiguration.WriteTo.File(
                new OpenSimLogFileFormatter(),
                logFilePath,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                rollingInterval: RollingInterval.Day,
                shared: true);
        }
        else
        {
            loggerConfiguration
                .WriteTo.Logger(configuration => configuration
                    .MinimumLevel.Verbose()
                    .Filter.ByExcluding(IsStatsLogEvent)
                    .WriteTo.File(
                        new OpenSimLogFileFormatter(),
                        logFilePath,
                        restrictedToMinimumLevel: LogEventLevel.Debug,
                        rollingInterval: RollingInterval.Day,
                        shared: true))
                .WriteTo.Logger(configuration => configuration
                    .MinimumLevel.Verbose()
                    .Filter.ByIncludingOnly(IsStatsLogEvent)
                    .WriteTo.File(
                        new OpenSimLogFileFormatter(),
                        statsLogFilePath,
                        restrictedToMinimumLevel: LogEventLevel.Debug,
                        rollingInterval: RollingInterval.Day,
                        shared: true));
        }

        Serilog.Core.Logger fileLogger = loggerConfiguration.CreateLogger();

        builder.AddSerilog(fileLogger, dispose: true);

        return builder;
    }

    private static bool IsStatsLogEvent(Serilog.Events.LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out Serilog.Events.LogEventPropertyValue sourceContext)
        && sourceContext is Serilog.Events.ScalarValue { Value: "special.StatsLogger" };
}
