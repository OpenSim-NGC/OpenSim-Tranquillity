/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;

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
    /// Matches the log4net PatternLayout "%date %-5level %message%newline" used by the appenders
    /// this replaces, so log files stay greppable across the migration.
    /// </summary>
    private const string FileOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss,fff} {Level:u5} {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Registers the interactive console sink and a daily rolling file sink.
    /// </summary>
    /// <param name="builder">Logging builder being configured.</param>
    /// <param name="serviceName">Used as the log file base name, e.g. OpenSim.Server.GridServer.</param>
    /// <param name="logPath">Directory that log files are written to.</param>
    public static ILoggingBuilder AddOpenSimLogging(this ILoggingBuilder builder, string serviceName, string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (string.IsNullOrWhiteSpace(logPath))
            logPath = ".";

        builder.AddOpenSimConsole();

        Serilog.Core.Logger fileLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                Path.Combine(logPath, $"{serviceName}.log"),
                restrictedToMinimumLevel: LogEventLevel.Debug,
                rollingInterval: RollingInterval.Day,
                outputTemplate: FileOutputTemplate,
                shared: true)
            .CreateLogger();

        builder.AddSerilog(fileLogger, dispose: true);

        return builder;
    }
}
