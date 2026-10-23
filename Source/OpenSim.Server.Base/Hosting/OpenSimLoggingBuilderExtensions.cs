/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Configuration;
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
    /// <remarks>
    /// Levels come from the <c>Logging:LogLevel</c> settings (normally appsettings.json) when <c>configuration</c> is given.
    /// The console starts at the <c>Default</c> entry, or at Information when there is none, and
    /// <c>Logging:OpenSimConsole:LogLevel</c> entries replace the general ones for the console only.
    /// The log file starts at Debug whatever <c>Default</c> says, so diagnostics stay in it; category entries other than
    /// <c>Default</c> apply to it, so a noisy category can be quietened in the file as well as on the console.
    /// </remarks>
    /// <param name="builder">Logging builder being configured.</param>
    /// <param name="serviceName">Used as the log file base name, e.g. OpenSim.Server.GridServer.</param>
    /// <param name="logPath">Directory that log files are written to.</param>
    /// <param name="configuredLogFile">Optional log file path from the Startup ini section.</param>
    /// <param name="configuredStatsLogFile">Optional stats log file path from the Startup ini section.</param>
    /// <param name="configuration">Optional host configuration holding the <c>Logging</c> section.</param>
    public static ILoggingBuilder AddOpenSimLogging(
        this ILoggingBuilder builder,
        string serviceName,
        string logPath,
        string configuredLogFile = null,
        string configuredStatsLogFile = null,
        IConfiguration configuration = null)
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

        CategoryLogLevels fileLevels = ReadLevels(configuration, "Logging:LogLevel");
        CategoryLogLevels consoleLevels = ReadLevels(configuration, "Logging:LogLevel", "Logging:OpenSimConsole:LogLevel");

        OpenSimConsoleLogSink.Configure(consoleLevels);
        builder.AddOpenSimConsole();

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Verbose();

        if (fileLevels.Count > 0)
            loggerConfiguration.Filter.ByExcluding(logEvent => !AllowedByCategory(fileLevels, logEvent));

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

    /// <summary>
    /// Reads the level entries of the given configuration sections; an entry in a later section replaces an earlier one.
    /// </summary>
    private static CategoryLogLevels ReadLevels(IConfiguration configuration, params string[] sections)
    {
        if (configuration is null)
            return CategoryLogLevels.Empty;

        return CategoryLogLevels.FromSettings(sections.SelectMany(section =>
            configuration.GetSection(section).GetChildren().Select(child => KeyValuePair.Create(child.Key, child.Value))));
    }

    private static bool AllowedByCategory(CategoryLogLevels levels, Serilog.Events.LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out Serilog.Events.LogEventPropertyValue sourceContext)
            || sourceContext is not Serilog.Events.ScalarValue { Value: string category })
            return true;

        LogLevel level = logEvent.Level switch
        {
            LogEventLevel.Verbose => LogLevel.Trace,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            _ => LogLevel.Critical
        };

        return levels.Allows(category, level);
    }

    private static bool IsStatsLogEvent(Serilog.Events.LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out Serilog.Events.LogEventPropertyValue sourceContext)
        && sourceContext is Serilog.Events.ScalarValue { Value: "special.StatsLogger" };
}
