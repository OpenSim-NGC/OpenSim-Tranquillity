/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework.Console;

/// <summary>
/// Mutable state shared by every <see cref="OpenSimConsoleLogger"/>.
/// </summary>
/// <remarks>
/// The interactive console is created long after the logging pipeline is configured, and the
/// "set log level" command changes the console threshold at runtime, so both are held here rather
/// than captured at provider construction.
/// </remarks>
public static class OpenSimConsoleLogSink
{
    private static volatile ConsoleBase _console;
    private static volatile object _minimumLevel = LogLevel.Information;
    private static volatile CategoryLogLevels _categoryLevels = CategoryLogLevels.Empty;

    /// <summary>
    /// Console that log output is routed through. While null, output falls back to System.Console
    /// so that startup messages emitted before the console exists are not lost.
    /// </summary>
    public static ConsoleBase Console
    {
        get => _console;
        set => _console = value;
    }

    /// <summary>
    /// Threshold applied to console output only; file sinks keep their own configured level.
    /// Information until the logging settings or the "set log level" command change it.
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get => (LogLevel)_minimumLevel;
        set => _minimumLevel = value;
    }

    /// <summary>
    /// Per-category thresholds for console output. A category with an entry here uses it instead of
    /// <see cref="MinimumLevel"/>, so "set log level" does not undo a deliberate per-category setting.
    /// </summary>
    public static CategoryLogLevels CategoryLevels
    {
        get => _categoryLevels;
        set => _categoryLevels = value ?? CategoryLogLevels.Empty;
    }

    /// <summary>
    /// Applies the console's logging settings: the category table, and its Default entry as the starting
    /// <see cref="MinimumLevel"/>, or Information when the settings have none.
    /// </summary>
    public static void Configure(CategoryLogLevels levels)
    {
        CategoryLevels = levels;
        MinimumLevel = levels?.Default ?? LogLevel.Information;
    }

    /// <summary>
    /// Parses a log level name so existing "set log level" usage keeps working.
    /// </summary>
    public static bool TryParseLevel(string rawLevel, out LogLevel level)
    {
        switch (rawLevel?.Trim().ToUpperInvariant())
        {
            case "ALL":
            case "VERBOSE":
            case "TRACE":
                level = LogLevel.Trace;
                return true;
            case "DEBUG":
                level = LogLevel.Debug;
                return true;
            case "INFO":
            case "INFORMATION":
                level = LogLevel.Information;
                return true;
            case "WARN":
            case "WARNING":
                level = LogLevel.Warning;
                return true;
            case "ERROR":
                level = LogLevel.Error;
                return true;
            case "FATAL":
            case "CRITICAL":
                level = LogLevel.Critical;
                return true;
            case "OFF":
            case "NONE":
                level = LogLevel.None;
                return true;
            default:
                level = LogLevel.None;
                return false;
        }
    }
}

/// <summary>
/// Routes <see cref="ILogger"/> output to the OpenSimulator interactive console.
/// Writing through <see cref="ConsoleBase"/> keeps log lines from corrupting the readline prompt
/// and preserves category colourization.
/// </summary>
[ProviderAlias("OpenSimConsole")]
public sealed class OpenSimConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new OpenSimConsoleLogger(categoryName);

    public void Dispose()
    {
    }
}

internal sealed class OpenSimConsoleLogger : ILogger
{
    private readonly string _category;

    // The category's entry is looked up again only when the table is replaced.
    private sealed record Resolved(CategoryLogLevels Table, LogLevel? Level);

    private volatile Resolved _resolved;

    public OpenSimConsoleLogger(string category)
    {
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
    {
        LogLevel minimum = CategoryMinimum() ?? OpenSimConsoleLogSink.MinimumLevel;
        return logLevel != LogLevel.None && minimum != LogLevel.None && logLevel >= minimum;
    }

    private LogLevel? CategoryMinimum()
    {
        CategoryLogLevels table = OpenSimConsoleLogSink.CategoryLevels;
        Resolved resolved = _resolved;

        if (resolved is null || !ReferenceEquals(resolved.Table, table))
        {
            resolved = new Resolved(table, table.TryGetLevel(_category, out LogLevel level) ? level : null);
            _resolved = resolved;
        }

        return resolved.Level;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
            return;

        string message = formatter(state, exception);

        if (string.IsNullOrEmpty(message) && exception is null)
            return;

        if (exception is not null)
            message = string.IsNullOrEmpty(message) ? exception.ToString() : $"{message}{Environment.NewLine}{exception}";

        // Formats as "%date %-5level %message%newline" matching the legacy pattern layout.
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {LevelLabel(logLevel),-5} {message}";

        try
        {
            ConsoleBase console = OpenSimConsoleLogSink.Console;

            if (console is not null)
            {
                ConsoleLevel level = logLevel switch
                {
                    LogLevel.Critical or LogLevel.Error => "error",
                    LogLevel.Warning => "warn",
                    _ => "normal"
                };

                console.Output(line, level);
            }
            else
            {
                System.Console.WriteLine(line);
            }
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Couldn't write out log message: {0}", e);
        }
    }

    private static string LevelLabel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "NONE"
    };

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

public static class OpenSimConsoleLoggerExtensions
{
    /// <summary>
    /// Registers the console provider. The provider filters by level itself (see <see cref="OpenSimConsoleLogSink"/>),
    /// so the host's own filter rules are told to let everything through to it; otherwise they would stop
    /// "set log level debug" from showing anything below the host's default of Information.
    /// </summary>
    public static ILoggingBuilder AddOpenSimConsole(this ILoggingBuilder builder)
    {
        builder.Services.AddSingleton<ILoggerProvider, OpenSimConsoleLoggerProvider>();
        builder.AddFilter<OpenSimConsoleLoggerProvider>(null, LogLevel.Trace);
        return builder;
    }
}
