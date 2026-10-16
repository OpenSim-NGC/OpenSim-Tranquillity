/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Services.HypergridService;
using Xunit;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// Unit tests for <see cref="ExternalIPResolver"/>. No network access: the operating system
/// lookup is replaced with a delegate so the tests exercise host selection, failure handling and
/// refresh only.
/// </summary>
public class ExternalIPResolverTests
{
    private const string Host = "grid.example.org";

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception), exception));
        }

        public int Count(LogLevel level)
        {
            lock (Entries)
                return Entries.Count(e => e.Level == level);
        }
    }

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static IConfigSource Config(params (string Key, string Value)[] keys)
    {
        IniConfigSource source = new();
        IConfig section = source.AddConfig("UserAgentService");
        foreach ((string key, string value) in keys)
            section.Set(key, value);
        return source;
    }

    // ---- every lookup goes through the operating system resolver --------------------------

    [Fact]
    public void Lookup_UsesTheOperatingSystemResolver_ForTheGivenHost()
    {
        int osCalls = 0;
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, TimeSpan.Zero, log,
            osResolver: h => { osCalls++; Assert.Equal(Host, h); return Ip("203.0.113.10"); });

        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Equal(1, osCalls);
        Assert.Equal(0, log.Count(LogLevel.Warning));
    }

    [Fact]
    public void TheServiceSendsNoDnsQueriesOfItsOwn()
    {
        // Every name goes through the operating system resolver: the assembly must not use a DNS client library.
        string[] references = typeof(ExternalIPResolver).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, name => name.Contains("DnsClient", StringComparison.OrdinalIgnoreCase));
    }

    // ---- which host is resolved: [UserAgentService] PublicAddress, else the gatekeeper host -

    [Fact]
    public void FromConfig_NothingSet_ResolvesTheGatekeeperHost()
    {
        string? asked = null;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(), "gate.example.org", new CapturingLogger(),
            osResolver: h => { asked = h; return Ip("203.0.113.10"); });

        Assert.Equal("gate.example.org", r.Host);
        Assert.Equal("gate.example.org", asked);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Equal(TimeSpan.FromMinutes(30), r.RefreshInterval);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfig_BlankPublicAddress_IsTreatedAsUnset(string value)
    {
        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("PublicAddress", value)), "gate.example.org",
            new CapturingLogger(), osResolver: _ => Ip("203.0.113.10"));

        Assert.Equal("gate.example.org", r.Host);
    }

    [Fact]
    public void FromConfig_PublicAddressHostName_IsResolvedInsteadOfTheGatekeeperHost()
    {
        List<string> asked = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("PublicAddress", " public.example.org ")),
            "gate.example.org", new CapturingLogger(),
            osResolver: h => { asked.Add(h); return Ip("198.51.100.7"); });

        Assert.Equal("public.example.org", r.Host);
        Assert.Equal(new[] { "public.example.org" }, asked);
        Assert.Equal("198.51.100.7", r.CurrentIP);
    }

    [Fact]
    public void FromConfig_PublicAddressLiteral_IsUsedAsGiven_ThroughTheDefaultResolver()
    {
        // No osResolver override: the default operating system path hands an IP literal back unchanged.
        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("PublicAddress", "198.51.100.9")),
            "gate.example.org", new CapturingLogger());

        Assert.Equal("198.51.100.9", r.CurrentIP);
    }

    [Fact]
    public void FromConfig_RemovedExternalIPResolverKey_IsIgnored_WithoutAWarning()
    {
        CapturingLogger log = new();
        string? asked = null;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("ExternalIPResolver", "192.0.2.53")),
            "gate.example.org", log, osResolver: h => { asked = h; return Ip("203.0.113.10"); });

        Assert.Equal("gate.example.org", asked);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Equal(0, log.Count(LogLevel.Warning));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("ExternalIPResolver") || e.Message.Contains("192.0.2.53"));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("-3", 0)]
    public void FromConfig_RefreshMinutes_IsRead_AndNonPositiveDisablesIt(string value, int expectedMinutes)
    {
        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("ExternalIPRefreshMinutes", value)),
            "gate.example.org", new CapturingLogger(), osResolver: _ => Ip("203.0.113.10"));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), r.RefreshInterval);
    }

    // ---- failed lookup -> previous value retained, warning logged --------------------------

    [Fact]
    public void FailedLookup_NullAnswer_KeepsPreviousValue_AndLogsWarning()
    {
        IPAddress? answer = Ip("198.51.100.7");
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, TimeSpan.Zero, log,
            osResolver: _ => answer);
        Assert.Equal("198.51.100.7", r.CurrentIP);

        answer = null;
        Assert.False(r.Refresh());

        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.Equal(1, log.Count(LogLevel.Warning));
        Assert.Contains("198.51.100.7", log.Entries.Single(e => e.Level == LogLevel.Warning).Message);
    }

    [Fact]
    public void FailedLookup_Exception_KeepsPreviousValue_LogsWarning_DoesNotThrow()
    {
        bool fail = false;
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, TimeSpan.Zero, log,
            osResolver: _ => fail ? throw new System.Net.Sockets.SocketException(11001) : Ip("203.0.113.10"));
        Assert.Equal("203.0.113.10", r.CurrentIP);

        fail = true;
        bool ok = r.Refresh();   // must not throw

        Assert.False(ok);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        (LogLevel, string, Exception?) warning = log.Entries.Single(e => e.Level == LogLevel.Warning);
        Assert.IsType<System.Net.Sockets.SocketException>(warning.Item3);
    }

    [Fact]
    public void InitialLookupFailure_DoesNotThrowFromConstructor_LeavesValueEmpty_AndWarns()
    {
        CapturingLogger log = new();

        ExternalIPResolver r = new(Host, TimeSpan.Zero, log,
            osResolver: _ => throw new TimeoutException("dns timeout"));

        using (r)
        {
            Assert.Equal(string.Empty, r.CurrentIP);
            Assert.Equal(1, log.Count(LogLevel.Warning));
        }
    }

    [Fact]
    public void GoodValue_IsNeverOverwrittenByEmptyOrFailedResult()
    {
        int call = 0;
        using ExternalIPResolver r = new(Host, TimeSpan.Zero, new CapturingLogger(),
            osResolver: _ => call++ switch
            {
                0 => Ip("198.51.100.7"),
                1 => null,
                2 => throw new InvalidOperationException("boom"),
                _ => Ip("198.51.100.8"),
            });

        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.False(r.Refresh());                       // null
        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.False(r.Refresh());                       // exception
        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.True(r.Refresh());                        // new good answer
        Assert.Equal("198.51.100.8", r.CurrentIP);
    }

    // ---- refresh picks up a changed answer without restart ------------------------------

    [Fact]
    public void ManualRefresh_PicksUpChangedAnswer()
    {
        IPAddress answer = Ip("198.51.100.7");
        using ExternalIPResolver r = new(Host, TimeSpan.Zero, new CapturingLogger(),
            osResolver: _ => answer);
        Assert.Equal("198.51.100.7", r.CurrentIP);

        answer = Ip("203.0.113.77");                     // DDNS moved
        Assert.True(r.Refresh());
        Assert.Equal("203.0.113.77", r.CurrentIP);
    }

    [Fact]
    public async Task TimerRefresh_PicksUpChangedAnswer_WithoutRestart()
    {
        IPAddress answer = Ip("198.51.100.7");
        int calls = 0;
        using ExternalIPResolver r = new(Host, TimeSpan.FromMilliseconds(50), new CapturingLogger(),
            osResolver: _ => { Interlocked.Increment(ref calls); return answer; });
        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.Equal(TimeSpan.FromMilliseconds(50), r.RefreshInterval);

        answer = Ip("203.0.113.77");

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (r.CurrentIP != "203.0.113.77" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal("203.0.113.77", r.CurrentIP);
        Assert.True(calls >= 2, $"expected the timer to re-resolve; calls={calls}");
    }

    [Fact]
    public async Task TimerRefresh_SurvivesLookupFailures_AndKeepsLastGoodValue()
    {
        int call = 0;
        CapturingLogger log = new();
        using ExternalIPResolver r = new(Host, TimeSpan.FromMilliseconds(30), log,
            osResolver: _ => Interlocked.Increment(ref call) == 1 ? Ip("198.51.100.7") : throw new TimeoutException());

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref call) < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(Volatile.Read(ref call) >= 4, "timer stopped firing after a failure");
        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.True(log.Count(LogLevel.Warning) >= 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroOrNegativeInterval_DisablesRefresh(int minutes)
    {
        using ExternalIPResolver r = new(Host, TimeSpan.FromMinutes(minutes), new CapturingLogger(),
            osResolver: _ => Ip("203.0.113.10"));
        Assert.Equal(TimeSpan.Zero, r.RefreshInterval);
    }

    [Fact]
    public async Task Dispose_StopsTimer()
    {
        int calls = 0;
        ExternalIPResolver r = new(Host, TimeSpan.FromMilliseconds(20), new CapturingLogger(),
            osResolver: _ => { Interlocked.Increment(ref calls); return Ip("203.0.113.10"); });

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref calls) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(Volatile.Read(ref calls) >= 3);

        r.Dispose();
        await Task.Delay(100);
        int after = Volatile.Read(ref calls);
        await Task.Delay(200);
        Assert.Equal(after, Volatile.Read(ref calls));
    }

    [Fact]
    public void EmptyHost_NeverResolves_AndDoesNotThrow()
    {
        using ExternalIPResolver r = new(string.Empty, TimeSpan.Zero, new CapturingLogger(),
            osResolver: _ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(string.Empty, r.CurrentIP);
        Assert.False(r.Refresh());
    }
}
