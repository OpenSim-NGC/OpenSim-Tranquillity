/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A test appender: collects every line the voice code logs while it is the current capture, with its level and
 * the category (logger name) it was logged under, so a test can count the lines a scenario writes. A fixture with
 * its own provider passes it in, and the lines go to that provider instead.
 *
 * It does not swap the process-wide logger factory: it registers with the assembly's routing factory
 * (VoiceTestLogging) and, on Dispose, unregisters only if it is still the current capture, so a teardown that runs
 * late or out of order cannot touch the next test's logging. Only one capture is current at a time, so a fixture
 * that uses it is [NonParallelizable].
 */

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice.Tests
{
    internal sealed class LogCapture : ILoggerProvider
    {
        public readonly record struct Line(LogLevel Level, string Category, string Text);

        private readonly List<Line> _lines = new List<Line>();
        private readonly ILoggerFactory _filter;
        private readonly ConcurrentDictionary<string, ILogger> _loggers = new();
        private int _disposed;

        /// <param name="minimum">The lowest level the capture lets through.</param>
        /// <param name="configure">More logging configuration, such as a Logging section read from configuration.</param>
        public LogCapture(LogLevel minimum = LogLevel.Trace, Action<ILoggingBuilder> configure = null)
            : this(null, minimum, configure) { }

        /// <summary>A capture whose lines go to <paramref name="sink"/>, which the capture disposes with itself.</summary>
        public LogCapture(ILoggerProvider sink, LogLevel minimum = LogLevel.Trace)
            : this(sink, minimum, null) { }

        private LogCapture(ILoggerProvider sink, LogLevel minimum, Action<ILoggingBuilder> configure)
        {
            // The capture's own factory applies its level and filter rules; it is never installed process-wide.
            _filter = LoggerFactory.Create(b =>
            {
                b.AddProvider(sink ?? new Own(this)).SetMinimumLevel(minimum);
                configure?.Invoke(b);
            });
            VoiceTestLogging.Register(this);
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

        internal bool IsEnabled(string category, LogLevel level)
        {
            try { return LoggerFor(category).IsEnabled(level); }
            catch (ObjectDisposedException) { return false; }   // disposed between the router's read and this call
        }

        internal void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            try { LoggerFor(category).Log(level, eventId, state, exception, formatter); }
            catch (ObjectDisposedException) { }
        }

        private ILogger LoggerFor(string category) => _loggers.GetOrAdd(category, name => _filter.CreateLogger(name));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            VoiceTestLogging.Unregister(this);
            _filter.Dispose();
        }

        // The provider behind the capture's own lines; the capture itself is the ILoggerProvider tests know.
        private sealed class Own : ILoggerProvider
        {
            private readonly LogCapture _owner;
            public Own(LogCapture owner) { _owner = owner; }
            public ILogger CreateLogger(string categoryName) => new Category(_owner, categoryName);
            public void Dispose() { }
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
