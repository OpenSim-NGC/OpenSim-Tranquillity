/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.OptionalModules.World.Currency;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// A region server whose economy is not the DTL/NSL money module still loads that module, so the module's
/// AddRegion, RegionLoaded and RemoveRegion run once per region. A module that is switched off returns from them
/// without writing anything above Debug; the one line it writes at start says it is off, and says so at Debug.
///
/// <para>The process's logger factory is swapped for a capture, which is process-wide, so the class shares the
/// "ssb-log-capture" collection with the other classes in this project that set that factory.</para>
/// </summary>
[Collection("ssb-log-capture")]
public class MoneyModuleDisabledLogTests
{
    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.Lines) m_c.Lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    /// <summary>Runs the action with a capture installed and returns the [MONEY] lines it wrote.</summary>
    private static List<(LogLevel Level, string Text)> MoneyLines(Action action)
    {
        var capture = new Capture();
        var previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try { action(); }
        finally { LoggerProvider.LoggerFactory = previous; }
        lock (capture.Lines) return capture.Lines.Where(l => l.Text.StartsWith("[MONEY]")).ToList();
    }

    private static IConfigSource Config(string economyModule, string currencyServer = null)
    {
        var source = new IniConfigSource();
        IConfig economy = source.AddConfig("Economy");
        economy.Set("EconomyModule", economyModule);
        if (currencyServer != null) economy.Set("CurrencyServer", currencyServer);
        return source;
    }

    [Fact]
    public void ADisabledModuleWritesNothingAboveDebugFromTheRegionCalls()
    {
        var module = new DTLNSLMoneyModule();
        module.Initialise(Config("SomeOtherMoneyModule"));

        var lines = MoneyLines(() =>
        {
            module.AddRegion(null);
            module.RegionLoaded(null);
            module.RemoveRegion(null);
        });

        Assert.Empty(lines.Where(l => l.Level > LogLevel.Debug));
    }

    [Fact]
    public void ADisabledModuleWritesNothingAboveDebugOverItsWholeLife()
    {
        var module = new DTLNSLMoneyModule();

        var lines = MoneyLines(() =>
        {
            module.Initialise(Config("SomeOtherMoneyModule"));
            module.PostInitialise();
            module.AddRegion(null);
            module.RegionLoaded(null);
            module.RemoveRegion(null);
            module.Close();
        });

        Assert.Empty(lines.Where(l => l.Level > LogLevel.Debug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBareLifecycleLinesAreDebugWhetherOrNotTheModuleIsEnabled(bool enabled)
    {
        var module = new DTLNSLMoneyModule();

        var lines = MoneyLines(() =>
        {
            module.Initialise(enabled
                ? Config("DTLNSLMoneyModule", "https://money.example.org/")
                : Config("SomeOtherMoneyModule"));
            module.PostInitialise();
            module.Close();
        });

        foreach (string text in new[] { "[MONEY]: Initialise:", "[MONEY]: PostInitialise:", "[MONEY]: Close:" })
        {
            var written = lines.Where(l => l.Text == text).ToList();
            Assert.Single(written);
            Assert.Equal(LogLevel.Debug, written[0].Level);
        }
    }

    [Fact]
    public void ADisabledModuleSaysItIsDisabledOnceAtDebugWhenItStarts()
    {
        var module = new DTLNSLMoneyModule();

        var lines = MoneyLines(() => module.Initialise(Config("SomeOtherMoneyModule")));

        var disabled = lines.Where(l => l.Text.Contains("disabled")).ToList();
        Assert.Single(disabled);
        Assert.Equal(LogLevel.Debug, disabled[0].Level);
    }

    [Fact]
    public void AnEnabledModuleStillReportsARegionCallWithoutAScene()
    {
        var module = new DTLNSLMoneyModule();
        module.Initialise(Config("DTLNSLMoneyModule", "https://money.example.org/"));

        var lines = MoneyLines(() => module.AddRegion(null));

        Assert.Contains(lines, l => l.Level == LogLevel.Error);
    }
}
