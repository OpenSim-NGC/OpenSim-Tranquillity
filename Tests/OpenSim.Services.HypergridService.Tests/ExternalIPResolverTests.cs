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
    public void FromConfig_RemovedExternalIPResolverKey_IsNotRead_AndWarnsOnceNamingTheReplacements()
    {
        CapturingLogger log = new();
        string? asked = null;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("ExternalIPResolver", "192.0.2.53")),
            "gate.example.org", log, osResolver: h => { asked = h; return Ip("203.0.113.10"); });

        // The key's value is not used: the gatekeeper host is resolved through the operating system, as before.
        Assert.Equal("gate.example.org", asked);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Empty(r.DnsServers);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("192.0.2.53"));

        // One warning says the key is no longer read and names the keys that replace it.
        string warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning).Message;
        Assert.Contains("ExternalIPResolver", warning);
        Assert.Contains("no longer read", warning);
        Assert.Contains("PublicAddress", warning);
        Assert.Contains("DnsServers", warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfig_BlankExternalIPResolverKey_DoesNotWarn(string value)
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("ExternalIPResolver", value)),
            "gate.example.org", log, osResolver: _ => Ip("203.0.113.10"));

        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Equal(0, log.Count(LogLevel.Warning));
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

    // ---- [UserAgentService] DnsServers: ask named DNS servers first ------------------------
    //
    // The DNS servers are replaced by a delegate (dnsLookup), so these tests send nothing on the network.

    private const string TwoServers = "192.0.2.53, 198.51.100.53";

    /// <summary>A stand-in for the DNS servers: records each name asked and gives the next scripted answer.</summary>
    private sealed class FakeDnsServers
    {
        private readonly Queue<Func<IPAddress?>> m_answers = new();
        public readonly List<string> Asked = new();

        public FakeDnsServers Then(string? address)
        {
            m_answers.Enqueue(() => address is null ? null : Ip(address));
            return this;
        }

        public FakeDnsServers ThenThrow()
        {
            m_answers.Enqueue(() => throw new System.Net.Sockets.SocketException(10060));
            return this;
        }

        public IPAddress? Lookup(string host)
        {
            Asked.Add(host);
            return m_answers.Count > 0 ? m_answers.Dequeue()() : null;
        }
    }

    [Fact]
    public void DnsServers_IsReadFromTheSection_AsACommaOrSemicolonSeparatedListOfAddresses()
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(
            Config(("PublicAddress", "public.example.org"), ("DnsServers", " 192.0.2.53 ; 198.51.100.53, ")),
            "gate.example.org", log, osResolver: _ => Ip("203.0.113.10"),
            dnsLookup: new FakeDnsServers().Then("203.0.113.10").Lookup);

        Assert.Equal(new[] { Ip("192.0.2.53"), Ip("198.51.100.53") }, r.DnsServers);
        Assert.Equal(0, log.Count(LogLevel.Warning));
    }

    [Fact]
    public void DnsServers_EntryThatIsNotAnAddress_IsIgnoredWithAWarning()
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(
            Config(("DnsServers", "192.0.2.53, dns.example.org, 0.0.0.0")),
            "gate.example.org", log, osResolver: _ => Ip("203.0.113.10"),
            dnsLookup: new FakeDnsServers().Then("203.0.113.10").Lookup);

        Assert.Equal(new[] { Ip("192.0.2.53") }, r.DnsServers);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("dns.example.org")
            && e.Message.Contains("DnsServers"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("\"0.0.0.0\"")
            && e.Message.Contains("DnsServers"));
    }

    [Fact]
    public void DnsServersSet_TheNameIsAskedOfTheServersFirst_AndTheOperatingSystemIsNotAsked()
    {
        CapturingLogger log = new();
        FakeDnsServers dns = new FakeDnsServers().Then("203.0.113.20");
        int osCalls = 0;

        // The operating system would give the hosts-file answer (a LAN address).
        using ExternalIPResolver r = ExternalIPResolver.FromConfig(
            Config(("PublicAddress", "public.example.org"), ("DnsServers", TwoServers)),
            "gate.example.org", log, osResolver: _ => { osCalls++; return Ip("192.168.0.25"); },
            dnsLookup: dns.Lookup);

        Assert.Equal("203.0.113.20", r.CurrentIP);
        Assert.Equal(new[] { "public.example.org" }, dns.Asked);
        Assert.Equal(0, osCalls);
        Assert.Equal(0, log.Count(LogLevel.Warning));
        // The startup line names the servers the answer came from.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message == "[EXTERNAL IP RESOLVER]: public.example.org resolved to 203.0.113.20 via DNS servers 192.0.2.53, 198.51.100.53");
    }

    [Fact]
    public void DnsServersSet_PublicAddressUnset_TheGatekeeperHostIsAskedOfTheServers()
    {
        FakeDnsServers dns = new FakeDnsServers().Then("203.0.113.20");

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("DnsServers", TwoServers)),
            "gate.example.org", new CapturingLogger(), osResolver: _ => Ip("192.168.0.25"), dnsLookup: dns.Lookup);

        Assert.Equal(new[] { "gate.example.org" }, dns.Asked);
        Assert.Equal("203.0.113.20", r.CurrentIP);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DnsServersSet_NoServerAnswers_AndNoPreviousValue_TheOperatingSystemAnswers_WithAWarning(bool serversThrow)
    {
        CapturingLogger log = new();
        FakeDnsServers dns = serversThrow ? new FakeDnsServers().ThenThrow() : new FakeDnsServers().Then(null);
        int osCalls = 0;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(
            Config(("PublicAddress", "public.example.org"), ("DnsServers", TwoServers)),
            "gate.example.org", log, osResolver: _ => { osCalls++; return Ip("203.0.113.10"); }, dnsLookup: dns.Lookup);

        Assert.Equal(new[] { "public.example.org" }, dns.Asked);
        Assert.Equal(1, osCalls);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("no DNS server answered") && e.Message.Contains("operating system"));
        // The operating system's answer is not reported as coming from the servers.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("via DNS servers"));
    }

    [Fact]
    public void DnsServersSet_KeepsTheLastGoodAddress_WhenNoServerAnswersLater_AndDoesNotAskTheOperatingSystem()
    {
        CapturingLogger log = new();
        FakeDnsServers dns = new FakeDnsServers().Then("203.0.113.20").Then(null).ThenThrow().Then("203.0.113.21");
        int osCalls = 0;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(
            Config(("PublicAddress", "public.example.org"), ("DnsServers", TwoServers)),
            "gate.example.org", log, osResolver: _ => { osCalls++; return Ip("192.168.0.25"); }, dnsLookup: dns.Lookup);
        Assert.Equal("203.0.113.20", r.CurrentIP);

        Assert.False(r.Refresh());                       // no answer
        Assert.Equal("203.0.113.20", r.CurrentIP);
        Assert.False(r.Refresh());                       // servers fail
        Assert.Equal("203.0.113.20", r.CurrentIP);
        Assert.Equal(0, osCalls);
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Warning
            && e.Message.Contains("no DNS server answered") && e.Message.Contains("203.0.113.20")));

        Assert.True(r.Refresh());                        // dynamic DNS moved
        Assert.Equal("203.0.113.21", r.CurrentIP);
        Assert.Equal(4, dns.Asked.Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("changed from 203.0.113.20 to 203.0.113.21") && e.Message.Contains("via DNS servers"));
    }

    [Fact]
    public void DnsServersUnset_OnlyTheOperatingSystemIsAsked_AsBefore()
    {
        CapturingLogger log = new();
        FakeDnsServers dns = new FakeDnsServers().Then("203.0.113.20");
        int osCalls = 0;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("PublicAddress", "public.example.org")),
            "gate.example.org", log, osResolver: _ => { osCalls++; return Ip("203.0.113.10"); }, dnsLookup: dns.Lookup);
        Assert.True(r.Refresh());

        Assert.Empty(r.DnsServers);
        Assert.Empty(dns.Asked);
        Assert.Equal(2, osCalls);
        Assert.Equal("203.0.113.10", r.CurrentIP);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message == "[EXTERNAL IP RESOLVER]: public.example.org resolved to 203.0.113.10");
        Assert.Equal(0, log.Count(LogLevel.Warning));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DnsServersBlank_IsTheSameAsUnset(string value)
    {
        FakeDnsServers dns = new FakeDnsServers().Then("203.0.113.20");

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("DnsServers", value)),
            "gate.example.org", new CapturingLogger(), osResolver: _ => Ip("203.0.113.10"), dnsLookup: dns.Lookup);

        Assert.Empty(r.DnsServers);
        Assert.Empty(dns.Asked);
        Assert.Equal("203.0.113.10", r.CurrentIP);
    }

    [Theory]
    [InlineData("198.51.100.9")]
    [InlineData("2001:db8::9")]
    public void PublicAddressLiteral_IsUsedAsIs_WithNoLookupOfAnyKind(string literal)
    {
        FakeDnsServers dns = new FakeDnsServers().Then("203.0.113.20");
        int osCalls = 0;

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(
            Config(("PublicAddress", literal), ("DnsServers", TwoServers)),
            "gate.example.org", new CapturingLogger(), osResolver: _ => { osCalls++; return Ip("192.168.0.25"); },
            dnsLookup: dns.Lookup);
        Assert.True(r.Refresh());

        Assert.Equal(Ip(literal).ToString(), r.CurrentIP);
        Assert.Empty(dns.Asked);
        Assert.Equal(0, osCalls);
    }

    // ---- startup warning: the address settled on is not a public one ----------------------

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.2")]
    [InlineData("192.168.0.25")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::5")]
    public void FromConfig_APrivateLoopbackOrLinkLocalResult_LogsAWarningNamingTheFix(string address)
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(), "gate.example.org", log,
            osResolver: _ => Ip(address));

        Assert.Equal(Ip(address).ToString(), r.CurrentIP);
        string warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning).Message;
        Assert.Contains(Ip(address).ToString(), warning);
        Assert.Contains("NAT fallback", warning);
        // Visitors to grids on the same network still pass on the login address comparison, so the warning is
        // only about grids outside that network.
        Assert.Contains("users logged in from that network will be refused when they go to a grid outside it", warning);
        Assert.DoesNotContain("another grid", warning);
        Assert.Contains("PublicAddress", warning);
        Assert.Contains("DnsServers", warning);
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:db8::9")]
    public void FromConfig_APublicResult_LogsNoWarning(string address)
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(), "gate.example.org", log,
            osResolver: _ => Ip(address));

        Assert.Equal(Ip(address).ToString(), r.CurrentIP);
        Assert.Equal(0, log.Count(LogLevel.Warning));
    }

    [Fact]
    public void FromConfig_APrivateResultFromTheDnsServers_AlsoWarns()
    {
        CapturingLogger log = new();

        using ExternalIPResolver r = ExternalIPResolver.FromConfig(Config(("DnsServers", TwoServers)),
            "gate.example.org", log, osResolver: _ => Ip("203.0.113.10"),
            dnsLookup: new FakeDnsServers().Then("10.0.0.5").Lookup);

        Assert.Equal("10.0.0.5", r.CurrentIP);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("10.0.0.5")
            && e.Message.Contains("NAT fallback") && e.Message.Contains("when they go to a grid outside it"));
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.25", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("::ffff:192.168.0.25", true)]
    [InlineData("203.0.113.10", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("11.0.0.1", false)]
    [InlineData("2001:db8::9", false)]
    public void IsPrivateLoopbackOrLinkLocal_ClassifiesAddresses(string address, bool expected)
    {
        Assert.Equal(expected, ExternalIPResolver.IsPrivateLoopbackOrLinkLocal(Ip(address)));
    }
}
