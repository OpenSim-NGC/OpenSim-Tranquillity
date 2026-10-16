/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using Microsoft.Extensions.Logging;
using OpenSim.Services.HypergridService;
using Xunit;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// Unit tests for <see cref="ExternalIPResolver"/>. No network access: both resolution paths are
/// replaced with delegates so the tests exercise selection, failure handling and refresh only.
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

    // ---- config absent -> OS resolver path, unchanged ------------------------------------

    [Fact]
    public void ConfigAbsent_UsesOsResolver_AndNeverTouchesDirectPath()
    {
        int osCalls = 0, directCalls = 0;
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, dnsServer: null, TimeSpan.Zero, log,
            osResolver: h => { osCalls++; Assert.Equal(Host, h); return Ip("203.0.113.10"); },
            directResolver: (_, _) => { directCalls++; return Ip("198.51.100.1"); });

        Assert.False(r.UsesDirectQuery);
        Assert.Null(r.DnsServer);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Equal(1, osCalls);
        Assert.Equal(0, directCalls);
        Assert.Equal(0, log.Count(LogLevel.Warning));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrBlankResolver_IsTreatedAsAbsent(string dnsServer)
    {
        using ExternalIPResolver r = new(Host, dnsServer, TimeSpan.Zero, new CapturingLogger(),
            osResolver: _ => Ip("203.0.113.10"),
            directResolver: (_, _) => throw new InvalidOperationException("direct path must not be used"));

        Assert.False(r.UsesDirectQuery);
        Assert.Equal("203.0.113.10", r.CurrentIP);
    }

    // ---- config set -> direct query path ---------------------------------------------------

    [Fact]
    public void ConfigSet_UsesDirectQuery_AgainstConfiguredServer_AndSkipsOsResolver()
    {
        int osCalls = 0;
        IPEndPoint? seenServer = null;
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, "1.1.1.1", TimeSpan.Zero, log,
            osResolver: _ => { osCalls++; return Ip("192.168.1.10"); },   // the LAN answer a hosts file would give
            directResolver: (h, server) => { Assert.Equal(Host, h); seenServer = server; return Ip("198.51.100.7"); });

        Assert.True(r.UsesDirectQuery);
        Assert.Equal(new IPEndPoint(Ip("1.1.1.1"), ExternalIPResolver.DefaultDnsPort), r.DnsServer);
        Assert.Equal(new IPEndPoint(Ip("1.1.1.1"), 53), seenServer);
        Assert.Equal("198.51.100.7", r.CurrentIP);
        Assert.Equal(0, osCalls);
    }

    [Fact]
    public void ConfigSet_WithPort_UsesThatPort()
    {
        using ExternalIPResolver r = new(Host, "9.9.9.9:5353", TimeSpan.Zero, new CapturingLogger(),
            directResolver: (_, _) => Ip("198.51.100.7"));

        Assert.Equal(new IPEndPoint(Ip("9.9.9.9"), 5353), r.DnsServer);
    }

    [Fact]
    public void ConfigSet_Unparsable_WarnsAndFallsBackToOsResolver()
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, "not-an-ip", TimeSpan.Zero, log,
            osResolver: _ => Ip("203.0.113.10"),
            directResolver: (_, _) => throw new InvalidOperationException("direct path must not be used"));

        Assert.False(r.UsesDirectQuery);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Equal(1, log.Count(LogLevel.Warning));
        Assert.Contains("not-an-ip", log.Entries.Single(e => e.Level == LogLevel.Warning).Message);
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1", 53)]
    [InlineData("8.8.8.8:5353", "8.8.8.8", 5353)]
    [InlineData("2606:4700:4700::1111", "2606:4700:4700::1111", 53)]
    [InlineData("[2606:4700:4700::1111]:53", "2606:4700:4700::1111", 53)]
    public void TryParseDnsServer_AcceptsIpAndIpPort(string value, string expectedIp, int expectedPort)
    {
        Assert.True(ExternalIPResolver.TryParseDnsServer(value, out IPEndPoint? ep));
        Assert.Equal(new IPEndPoint(Ip(expectedIp), expectedPort), ep);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dns.example.org")]
    [InlineData("1.1.1.1:0")]
    [InlineData("1.1.1.1:notaport")]
    public void TryParseDnsServer_RejectsNonIpValues(string value)
    {
        Assert.False(ExternalIPResolver.TryParseDnsServer(value, out _));
    }

    // ---- failed lookup -> previous value retained, warning logged --------------------------

    [Fact]
    public void FailedLookup_NullAnswer_KeepsPreviousValue_AndLogsWarning()
    {
        IPAddress? answer = Ip("198.51.100.7");
        CapturingLogger log = new();

        using ExternalIPResolver r = new(Host, "1.1.1.1", TimeSpan.Zero, log,
            directResolver: (_, _) => answer);
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

        using ExternalIPResolver r = new(Host, null, TimeSpan.Zero, log,
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

        ExternalIPResolver r = new(Host, "1.1.1.1", TimeSpan.Zero, log,
            directResolver: (_, _) => throw new TimeoutException("dns timeout"));

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
        using ExternalIPResolver r = new(Host, "1.1.1.1", TimeSpan.Zero, new CapturingLogger(),
            directResolver: (_, _) => call++ switch
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
        using ExternalIPResolver r = new(Host, "1.1.1.1", TimeSpan.Zero, new CapturingLogger(),
            directResolver: (_, _) => answer);
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
        using ExternalIPResolver r = new(Host, null, TimeSpan.FromMilliseconds(50), new CapturingLogger(),
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
        using ExternalIPResolver r = new(Host, "1.1.1.1", TimeSpan.FromMilliseconds(30), log,
            directResolver: (_, _) => Interlocked.Increment(ref call) == 1 ? Ip("198.51.100.7") : throw new TimeoutException());

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
        using ExternalIPResolver r = new(Host, null, TimeSpan.FromMinutes(minutes), new CapturingLogger(),
            osResolver: _ => Ip("203.0.113.10"));
        Assert.Equal(TimeSpan.Zero, r.RefreshInterval);
    }

    [Fact]
    public async Task Dispose_StopsTimer()
    {
        int calls = 0;
        ExternalIPResolver r = new(Host, null, TimeSpan.FromMilliseconds(20), new CapturingLogger(),
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
        using ExternalIPResolver r = new(string.Empty, "1.1.1.1", TimeSpan.Zero, new CapturingLogger(),
            directResolver: (_, _) => throw new InvalidOperationException("must not be called"));
        Assert.Equal(string.Empty, r.CurrentIP);
        Assert.False(r.Refresh());
    }
}
