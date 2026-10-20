/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Tests.Common;

/// <summary>
/// Records everything the code under test logs, at every level, so a test can assert that a value never reached
/// the log. Swapping <see cref="LoggerProvider.LoggerFactory"/> is enough because loggers held in static fields
/// are deferred loggers that rebind when the factory changes. The previous factory is restored on dispose.
/// Tests that use it touch a process-wide factory, so they must not run in parallel with other users of it.
/// </summary>
public sealed class LogCapture : IDisposable, ILoggerFactory
{
    private readonly ILoggerFactory m_previous;
    private readonly List<(LogLevel Level, string Message)> m_entries = new();

    public LogCapture()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = this;
    }

    /// <summary>Every message logged so far, formatted as the sink would receive it.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (m_entries)
                return m_entries.Select(e => e.Message).ToList();
        }
    }

    /// <summary>The level of the first message that contains <paramref name="fragment"/>, or null.</summary>
    public LogLevel? LevelOf(string fragment)
    {
        lock (m_entries)
            foreach (var (level, message) in m_entries)
                if (message.Contains(fragment, StringComparison.Ordinal))
                    return level;
        return null;
    }

    /// <summary>True if any logged message contains <paramref name="value"/>.</summary>
    public bool Contains(string value)
    {
        lock (m_entries)
            return m_entries.Any(e => e.Message.Contains(value, StringComparison.Ordinal));
    }

    public void Dispose() => LoggerProvider.LoggerFactory = m_previous;

    ILogger ILoggerFactory.CreateLogger(string categoryName) => new Recorder(this);
    void ILoggerFactory.AddProvider(ILoggerProvider provider) { }

    private sealed class Recorder : ILogger
    {
        private readonly LogCapture m_owner;
        public Recorder(LogCapture owner) { m_owner = owner; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            lock (m_owner.m_entries)
                m_owner.m_entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Builds a service object without running its constructor, which would load a database plugin, and sets the
/// named fields, so a test can drive one method of the real class against a fake store.
/// </summary>
public static class UninitializedService
{
    public static T Create<T>(params (string Field, object Value)[] fields) where T : class
    {
        T instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        foreach (var (name, value) in fields)
        {
            FieldInfo field = null;
            for (Type t = typeof(T); t != null && field == null; t = t.BaseType)
                field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null)
                throw new MissingFieldException(typeof(T).Name, name);
            field.SetValue(instance, value);
        }
        return instance;
    }
}
