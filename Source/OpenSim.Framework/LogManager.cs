using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// Shared logger
/// </summary>
public static class LogManager
{
    public static ILoggerFactory LoggerFactory { get; set; } 
    public static ILogger CreateLogger<T>() => LoggerFactory.CreateLogger<T>();        
    public static ILogger CreateLogger(string categoryName) => LoggerFactory.CreateLogger(categoryName);
    public static ILogger GetLogger(Type declaringType) => LoggerFactory.CreateLogger(declaringType.FullName);
}
