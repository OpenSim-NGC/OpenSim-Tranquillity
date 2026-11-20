/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The voice test assemblies' one logger factory. It is installed as LoggerProvider.LoggerFactory once, before any
 * test runs, and never disposed during the run, so the deferred loggers the voice classes hold always bind to a
 * live factory. Its loggers send each line to the LogCapture that is current when the line is logged, or nowhere.
 *
 * Tests never swap LoggerProvider.LoggerFactory themselves. Swapping it per test broke when teardowns ran out of
 * order: a timed-out test's teardown runs late (NUnit's [Timeout] does not stop a test on .NET), and putting back
 * "the factory found at setup" could put back one that was already disposed, after which every logger in the
 * process threw ObjectDisposedException.
 */

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    internal static class VoiceTestLogging
    {
        private static LogCapture s_current;

        private static readonly ILoggerFactory s_factory =
            LoggerFactory.Create(b => b.AddProvider(new Router()).SetMinimumLevel(LogLevel.Trace));

        /// <summary>Makes the routing factory the process's logger factory, if it is not already.</summary>
        public static void Install()
        {
            if (!ReferenceEquals(LoggerProvider.LoggerFactory, s_factory))
                LoggerProvider.LoggerFactory = s_factory;
        }

        /// <summary>Sends every line logged from now on to <paramref name="capture"/>.</summary>
        internal static void Register(LogCapture capture)
        {
            Install();
            Volatile.Write(ref s_current, capture);
        }

        /// <summary>Stops sending lines to <paramref name="capture"/>, only if it is still the current one.</summary>
        internal static void Unregister(LogCapture capture) =>
            Interlocked.CompareExchange(ref s_current, null, capture);

        private sealed class Router : ILoggerProvider
        {
            private readonly ConcurrentDictionary<string, Routed> _loggers = new();
            public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new Routed(name));
            public void Dispose() { }
        }

        private sealed class Routed : ILogger
        {
            private readonly string _category;
            public Routed(string category) { _category = category; }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                Volatile.Read(ref s_current)?.IsEnabled(_category, logLevel) ?? false;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter) =>
                Volatile.Read(ref s_current)?.Write(_category, logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>Installs the voice tests' logger factory once for the whole assembly, before any fixture runs.</summary>
[SetUpFixture]
public sealed class VoiceTestLoggingSetUp
{
    [OneTimeSetUp]
    public void InstallLogging() => osWebRtcVoice.Tests.VoiceTestLogging.Install();
}
