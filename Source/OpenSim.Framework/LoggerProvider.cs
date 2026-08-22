using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenSim.Framework;

/// <summary>
/// Ambient <see cref="ILoggerFactory"/> for types constructed outside the DI container
/// (region modules, script engine types, statics) that cannot receive an injected logger.
/// Prefer constructor injection wherever the type is resolved from the container.
/// </summary>
public static class LoggerProvider
{
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// Set once during host startup. Defaults to a null factory so call sites that run before the
    /// host is built (and tests that never build a host) do not fail.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        set => _loggerFactory = value ?? NullLoggerFactory.Instance;
    }

    public static ILogger CreateLogger<T>() => new DeferredLogger(factory => factory.CreateLogger<T>());

    public static ILogger CreateLogger(string categoryName) => new DeferredLogger(factory => factory.CreateLogger(categoryName));

    public static ILogger CreateLogger(Type type) => new DeferredLogger(factory => factory.CreateLogger(type));
}