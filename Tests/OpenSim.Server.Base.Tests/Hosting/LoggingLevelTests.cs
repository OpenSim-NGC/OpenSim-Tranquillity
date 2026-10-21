/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// The console and the log file take their levels from the <c>Logging:LogLevel</c> settings. The console starts at
/// Information when nothing is configured; the log file keeps Debug as its default and honours category entries.
/// The console sink is process-wide state, so these run in the serial collection.
/// </summary>
[Collection("MainConsole")]
public sealed class LoggingLevelTests
{
    private sealed class RecordingConsole : ConsoleBase
    {
        public List<string> Lines { get; } = new();

        public RecordingConsole() : base("Test> ") { }

        public override void Output(string format) => Lines.Add(format);

        public override void Output(string format, params object[] components) => Lines.Add(format);
    }

    /// <summary>What one category wrote at every level, as seen on the console and in the log file.</summary>
    private sealed record Written(string Console, string File)
    {
        public bool OnConsole(string marker) => Console.Contains(marker);

        public bool InFile(string marker) => File.Contains(marker);
    }

    private static readonly LogLevel[] AllLevels =
    {
        LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error
    };

    private static string Marker(string category, LogLevel level) => $"[MARK {category} {level}]";

    private static void WriteAllLevels(ILoggerFactory factory, params string[] categories)
    {
        foreach (string category in categories)
        {
            ILogger log = factory.CreateLogger(category);

            foreach (LogLevel level in AllLevels)
                log.Log(level, Marker(category, level));
        }
    }

    /// <summary>
    /// Builds the logging pipeline the way the servers' Program.cs does (host configuration, then
    /// AddOpenSimLogging), runs <paramref name="act"/> against the logger factory and returns what reached each sink.
    /// </summary>
    private static Written Run(IDictionary<string, string?> settings, Action<ILoggerFactory> act, bool passConfiguration = true)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "logging-level-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Example.log");

        ConsoleBase savedConsole = OpenSimConsoleLogSink.Console;
        LogLevel savedMinimum = OpenSimConsoleLogSink.MinimumLevel;
        CategoryLogLevels savedLevels = OpenSimConsoleLogSink.CategoryLevels;
        RecordingConsole console = new();
        OpenSimConsoleLogSink.Console = console;

        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            ServiceCollection services = new();
            services.AddLogging(b => b
                .AddConfiguration(configuration.GetSection("Logging"))
                .ClearProviders()
                .AddOpenSimLogging("Example", dir, file, null, passConfiguration ? configuration : null));

            using (ServiceProvider provider = services.BuildServiceProvider())
                act(provider.GetRequiredService<ILoggerFactory>());

            string text = File.ReadAllText(Directory.GetFiles(dir, "Example*.log").Single());
            return new Written(string.Join("\n", console.Lines), text);
        }
        finally
        {
            OpenSimConsoleLogSink.Console = savedConsole;
            OpenSimConsoleLogSink.MinimumLevel = savedMinimum;
            OpenSimConsoleLogSink.CategoryLevels = savedLevels;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNothingConfiguredTheConsoleShowsInformationAndAboveAndTheFileKeepsDebug(bool passConfiguration)
    {
        Written written = Run(new Dictionary<string, string?>(), f => WriteAllLevels(f, "Example.Area"), passConfiguration);

        Assert.False(written.OnConsole(Marker("Example.Area", LogLevel.Debug)));
        Assert.True(written.OnConsole(Marker("Example.Area", LogLevel.Information)));
        Assert.True(written.OnConsole(Marker("Example.Area", LogLevel.Warning)));
        Assert.True(written.OnConsole(Marker("Example.Area", LogLevel.Error)));

        foreach (LogLevel level in AllLevels)
            Assert.True(written.InFile(Marker("Example.Area", level)), $"file is missing {level}");
    }

    [Fact]
    public void AConsoleThatIsAlreadyAtDebugIsBroughtBackToInformationWhenLoggingIsSetUp()
    {
        OpenSimConsoleLogSink.MinimumLevel = LogLevel.Debug;

        Written written = Run(new Dictionary<string, string?>(), f => WriteAllLevels(f, "Example.Area"));

        Assert.False(written.OnConsole(Marker("Example.Area", LogLevel.Debug)));
    }

    [Fact]
    public void ACategorySettingReachesBothTheConsoleAndTheLogFile()
    {
        Written written = Run(
            new Dictionary<string, string?> { ["Logging:LogLevel:Example.Quiet"] = "Warning" },
            f => WriteAllLevels(f, "Example.Quiet.Part", "Example.Other"));

        // the named category: warnings and errors only, in both places
        foreach (LogLevel level in new[] { LogLevel.Debug, LogLevel.Information })
        {
            Assert.False(written.OnConsole(Marker("Example.Quiet.Part", level)), $"console shows {level}");
            Assert.False(written.InFile(Marker("Example.Quiet.Part", level)), $"file holds {level}");
        }

        foreach (LogLevel level in new[] { LogLevel.Warning, LogLevel.Error })
        {
            Assert.True(written.OnConsole(Marker("Example.Quiet.Part", level)));
            Assert.True(written.InFile(Marker("Example.Quiet.Part", level)));
        }

        // any other category: Information on the console, everything in the file
        Assert.False(written.OnConsole(Marker("Example.Other", LogLevel.Debug)));
        Assert.True(written.OnConsole(Marker("Example.Other", LogLevel.Information)));

        foreach (LogLevel level in AllLevels)
            Assert.True(written.InFile(Marker("Example.Other", level)), $"file is missing {level}");
    }

    [Fact]
    public void TheDefaultEntryMovesTheConsoleAndLeavesTheLogFileAtDebug()
    {
        Written warnings = Run(
            new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" },
            f => WriteAllLevels(f, "Example.Area"));

        Assert.False(warnings.OnConsole(Marker("Example.Area", LogLevel.Information)));
        Assert.True(warnings.OnConsole(Marker("Example.Area", LogLevel.Warning)));

        foreach (LogLevel level in AllLevels)
            Assert.True(warnings.InFile(Marker("Example.Area", level)), $"file is missing {level}");

        Written debug = Run(
            new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Debug" },
            f => WriteAllLevels(f, "Example.Area"));

        Assert.True(debug.OnConsole(Marker("Example.Area", LogLevel.Debug)));
    }

    [Fact]
    public void TheLongestMatchingCategoryWins()
    {
        Written written = Run(
            new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Example"] = "Error",
                ["Logging:LogLevel:Example.Loud"] = "Debug",
            },
            f => WriteAllLevels(f, "Example.Loud.Deep", "Example.Area"));

        Assert.True(written.OnConsole(Marker("Example.Loud.Deep", LogLevel.Debug)));
        Assert.True(written.InFile(Marker("Example.Loud.Deep", LogLevel.Debug)));

        Assert.False(written.OnConsole(Marker("Example.Area", LogLevel.Warning)));
        Assert.False(written.InFile(Marker("Example.Area", LogLevel.Warning)));
        Assert.True(written.InFile(Marker("Example.Area", LogLevel.Error)));
    }

    [Fact]
    public void ASettingForTheConsoleAloneDoesNotTouchTheLogFile()
    {
        Written written = Run(
            new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Example.Area"] = "Warning",
                ["Logging:OpenSimConsole:LogLevel:Example.Area"] = "Error",
            },
            f => WriteAllLevels(f, "Example.Area"));

        Assert.False(written.OnConsole(Marker("Example.Area", LogLevel.Warning)));
        Assert.True(written.OnConsole(Marker("Example.Area", LogLevel.Error)));

        Assert.True(written.InFile(Marker("Example.Area", LogLevel.Warning)));
        Assert.False(written.InFile(Marker("Example.Area", LogLevel.Information)));
    }

    [Fact]
    public void SetLogLevelDebugShowsDebugLinesOnAConsoleThatStartedAtInformation()
    {
        Written written = Run(new Dictionary<string, string?>(), f =>
        {
            ILogger log = f.CreateLogger("Example.Area");
            log.LogDebug("[BEFORE]");
            OpenSimConsoleLogSink.MinimumLevel = LogLevel.Debug;
            log.LogDebug("[AFTER]");
        });

        Assert.False(written.OnConsole("[BEFORE]"));
        Assert.True(written.OnConsole("[AFTER]"));
    }

    [Fact]
    public void ACategoryEntryIsKeptWhenTheConsoleLevelIsRaisedAtRunTime()
    {
        Written written = Run(
            new Dictionary<string, string?> { ["Logging:LogLevel:Example.Quiet"] = "Warning" },
            f =>
            {
                OpenSimConsoleLogSink.MinimumLevel = LogLevel.Debug;
                WriteAllLevels(f, "Example.Quiet", "Example.Other");
            });

        Assert.False(written.OnConsole(Marker("Example.Quiet", LogLevel.Information)));
        Assert.True(written.OnConsole(Marker("Example.Other", LogLevel.Debug)));
    }

    [Fact]
    public void AnAppSettingsJsonInTheContentRootIsHonouredByBothSinks()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "logging-host-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            "{ \"Logging\": { \"LogLevel\": { \"Default\": \"Information\", \"Example.Quiet\": \"Warning\" } } }");
        string file = Path.Combine(dir, "Example.log");

        ConsoleBase savedConsole = OpenSimConsoleLogSink.Console;
        LogLevel savedMinimum = OpenSimConsoleLogSink.MinimumLevel;
        CategoryLogLevels savedLevels = OpenSimConsoleLogSink.CategoryLevels;
        RecordingConsole console = new();
        OpenSimConsoleLogSink.Console = console;

        try
        {
            using (IHost host = Host.CreateDefaultBuilder()
                .UseContentRoot(dir)
                .ConfigureLogging((context, builder) =>
                {
                    builder.ClearProviders();
                    builder.AddOpenSimLogging("Example", dir, file, null, context.Configuration);
                })
                .Build())
            {
                WriteAllLevels(host.Services.GetRequiredService<ILoggerFactory>(), "Example.Quiet", "Example.Other");
            }

            string text = File.ReadAllText(Directory.GetFiles(dir, "Example*.log").Single());
            string shown = string.Join("\n", console.Lines);

            Assert.False(shown.Contains(Marker("Example.Quiet", LogLevel.Information)));
            Assert.True(shown.Contains(Marker("Example.Quiet", LogLevel.Warning)));
            Assert.False(text.Contains(Marker("Example.Quiet", LogLevel.Information)));
            Assert.True(text.Contains(Marker("Example.Quiet", LogLevel.Warning)));

            Assert.False(shown.Contains(Marker("Example.Other", LogLevel.Debug)));
            Assert.True(shown.Contains(Marker("Example.Other", LogLevel.Information)));
            Assert.True(text.Contains(Marker("Example.Other", LogLevel.Debug)));
        }
        finally
        {
            OpenSimConsoleLogSink.Console = savedConsole;
            OpenSimConsoleLogSink.MinimumLevel = savedMinimum;
            OpenSimConsoleLogSink.CategoryLevels = savedLevels;
        }
    }

    [Fact]
    public void CategoryLogLevelsParsesNamesCaseAndWildcardsAndSkipsWhatItCannotRead()
    {
        CategoryLogLevels levels = CategoryLogLevels.FromSettings(new KeyValuePair<string, string?>[]
        {
            KeyValuePair.Create("default", "debug"),
            KeyValuePair.Create("Microsoft.*", "Warning"),
            KeyValuePair.Create("System", "none"),
            KeyValuePair.Create("Broken", "loud"),
            KeyValuePair.Create<string, string?>("Empty", null),
            KeyValuePair.Create<string, string?>("*", "Error"),
        });

        Assert.Equal(LogLevel.Debug, levels.Default);
        Assert.Equal(2, levels.Count);

        Assert.True(levels.TryGetLevel("microsoft.hosting.lifetime", out LogLevel microsoft));
        Assert.Equal(LogLevel.Warning, microsoft);
        Assert.False(levels.Allows("Microsoft.Hosting", LogLevel.Information));
        Assert.True(levels.Allows("Microsoft.Hosting", LogLevel.Warning));
        Assert.False(levels.Allows("System.Net", LogLevel.Critical));
        Assert.True(levels.Allows("Example.Area", LogLevel.Trace));
        Assert.True(levels.Allows(null, LogLevel.Trace));
        Assert.False(levels.TryGetLevel("Broken", out _));
    }
}
