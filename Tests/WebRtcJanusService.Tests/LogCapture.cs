/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A test appender: collects every line the voice code logs while it is alive, with its level and the
 * category (logger name) it was logged under, so a test can count the lines a scenario writes.
 *
 * It swaps the process-wide logger factory, so a fixture that uses it is [NonParallelizable]. The voice
 * classes hold deferred loggers (LoggerProvider.CreateLogger), so the swap reaches loggers created earlier.
 */

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    internal sealed class LogCapture : ILoggerProvider
    {
        public readonly record struct Line(LogLevel Level, string Category, string Text);

        private readonly List<Line> _lines = new List<Line>();
        private readonly ILoggerFactory _previous;
        private readonly ILoggerFactory _ours;

        /// <param name="minimum">The lowest level the factory lets through.</param>
        /// <param name="configure">More logging configuration, such as a Logging section read from configuration.</param>
        public LogCapture(LogLevel minimum = LogLevel.Trace, Action<ILoggingBuilder> configure = null)
        {
            _previous = LoggerProvider.LoggerFactory;
            _ours = LoggerFactory.Create(b =>
            {
                b.AddProvider(this).SetMinimumLevel(minimum);
                configure?.Invoke(b);
            });
            LoggerProvider.LoggerFactory = _ours;
        }

        public List<Line> Lines
        {
            get { lock (_lines) return new List<Line>(_lines); }
        }

        /// <summary>The lines at <paramref name="level"/> whose text contains <paramref name="contains"/>.</summary>
        public List<Line> At(LogLevel level, string contains = "")
            => Lines.Where(l => l.Level == level && l.Text.Contains(contains)).ToList();

        /// <summary>The lines at Information or above whose text contains <paramref name="contains"/>.</summary>
        public List<Line> AtInfoOrAbove(string contains = "")
            => Lines.Where(l => l.Level >= LogLevel.Information && l.Text.Contains(contains)).ToList();

        public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

        public void Dispose()
        {
            LoggerProvider.LoggerFactory = _previous;
            _ours.Dispose();
        }

        private sealed class Category : ILogger
        {
            private readonly LogCapture _owner;
            private readonly string _name;
            public Category(LogCapture owner, string name) { _owner = owner; _name = name; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (_owner._lines)
                    _owner._lines.Add(new Line(logLevel, _name, formatter(state, exception)));
            }
        }
    }
}
