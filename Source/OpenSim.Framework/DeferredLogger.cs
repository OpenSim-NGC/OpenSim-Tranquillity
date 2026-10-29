/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// An <see cref="ILogger"/> that binds to <see cref="LoggerProvider.LoggerFactory"/> on first use
/// rather than at construction.
/// </summary>
/// <remarks>
/// Most OpenSimulator loggers are <c>static readonly</c> fields, so they are created when their
/// declaring type is first touched. That can happen before the host has finished configuring
/// logging, which would otherwise permanently bind them to the null factory and silently discard
/// every message. Rebinding when the factory changes keeps those call sites working.
/// </remarks>
internal sealed class DeferredLogger : ILogger
{
    private readonly Func<ILoggerFactory, ILogger> _factoryMethod;

    private ILoggerFactory _boundFactory;
    private ILogger _inner;

    internal DeferredLogger(Func<ILoggerFactory, ILogger> factoryMethod)
    {
        _factoryMethod = factoryMethod;
    }

    private ILogger Inner
    {
        get
        {
            ILoggerFactory current = LoggerProvider.LoggerFactory;

            // Reference comparison is deliberate: rebind only when the host swaps the factory.
            if (!ReferenceEquals(current, _boundFactory))
            {
                _inner = _factoryMethod(current);
                _boundFactory = current;
            }

            return _inner;
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
        => Inner.Log(logLevel, eventId, state, exception, formatter);
}
