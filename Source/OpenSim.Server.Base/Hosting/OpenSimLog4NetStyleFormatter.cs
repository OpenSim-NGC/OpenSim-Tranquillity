/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Serilog.Events;
using Serilog.Formatting;

namespace OpenSim.Server.Base.Hosting;

/// <summary>
/// Renders log lines as "%date %-5level %message%newline", matching the log4net PatternLayout the
/// file appenders used, so log-scraping scripts keyed on the full level names (INFO/WARN/ERROR)
/// keep working. Serilog's built-in "u3"/"u4" level formats abbreviate instead (INF/WRN/ERR).
/// </summary>
public sealed class OpenSimLog4NetStyleFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff"));
        output.Write(' ');
        output.Write(LevelLabel(logEvent.Level).PadRight(5));
        output.Write(' ');
        logEvent.RenderMessage(output);
        output.Write(Environment.NewLine);

        if (logEvent.Exception is not null)
            output.Write(logEvent.Exception);
    }

    private static string LevelLabel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "TRACE",
        LogEventLevel.Debug => "DEBUG",
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning => "WARN",
        LogEventLevel.Error => "ERROR",
        LogEventLevel.Fatal => "FATAL",
        _ => "NONE"
    };
}
