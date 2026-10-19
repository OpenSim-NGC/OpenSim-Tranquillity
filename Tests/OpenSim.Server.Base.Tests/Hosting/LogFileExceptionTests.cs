/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// An exception passed to a logger (<c>LogError(exception, message)</c>) reaches the daily log file with its type, its
/// message and its stack, whether the logger was made by the host or by <see cref="LoggerProvider"/> (the loggers that
/// the region's static fields hold).
/// </summary>
[Collection("MainConsole")]
public sealed class LogFileExceptionTests
{
    [Fact]
    public void AnExceptionLoggedThroughTheLoggerProviderIsWrittenToTheLogFileWithItsStack()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "log-file-exception-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Example.log");
        ILoggerFactory saved = LoggerProvider.LoggerFactory;

        try
        {
            ServiceCollection services = new();
            services.AddLogging(b => b.ClearProviders().AddOpenSimLogging("Example", dir, file, null));
            using (ServiceProvider provider = services.BuildServiceProvider())
            {
                LoggerProvider.LoggerFactory = provider.GetRequiredService<ILoggerFactory>();
                ILogger log = LoggerProvider.CreateLogger("Example.Category");

                try { throw new InvalidOperationException("example failure text"); }
                catch (Exception e) { log.LogError(e, "[EXAMPLE]: the step failed"); }
                log.LogInformation("[EXAMPLE]: the next line");
            }

            string text = File.ReadAllText(Directory.GetFiles(dir, "Example*.log").Single());
            Assert.Contains("[EXAMPLE]: the step failed", text);
            Assert.Contains("System.InvalidOperationException: example failure text", text);
            Assert.Contains("AnExceptionLoggedThroughTheLoggerProviderIsWrittenToTheLogFileWithItsStack", text);

            // the next entry starts on a line of its own, not at the end of the stack's last line
            Assert.Matches(@"(?m)^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3} INFO  \[EXAMPLE\]: the next line", text);
        }
        finally
        {
            LoggerProvider.LoggerFactory = saved;
        }
    }
}
