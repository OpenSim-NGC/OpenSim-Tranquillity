using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

public static class LoggerProvider
{
   public static ILoggerFactory LoggerFactory { get; set; }
   public static ILogger CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
   public static ILogger CreateLogger(string categoryName) => LoggerFactory.CreateLogger(categoryName);
}