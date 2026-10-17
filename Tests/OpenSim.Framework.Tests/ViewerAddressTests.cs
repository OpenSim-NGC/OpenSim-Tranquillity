/*
 * Copyright (c) Legion Builds
 * Copyright (c) 2026 Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Net;
using Microsoft.Extensions.Logging;
using Nini.Config;

namespace OpenSim.Framework.Tests;

/// <summary>
/// Choice of the address a viewer is given for a region: with nothing set every viewer gets the region's
/// own address; otherwise the first entry of the operator's list whose network contains the viewer decides.
/// Only documentation (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) and generic private ranges are used.
/// </summary>
public class ViewerAddressTests
{
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
        public int Warnings => Entries.Count(e => e.Level == LogLevel.Warning);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    private const string Host = "region.example.org";
    private static readonly IPAddress s_pub = IPAddress.Parse("198.51.100.7");
    private static readonly IPEndPoint s_local = new(IPAddress.Parse("192.168.1.10"), 9000);
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static ViewerAddressConfig Cfg(string addresses = "", int refresh = 30, ILogger? log = null)
        => ViewerAddressConfig.Create(addresses, refresh, log ?? new CapturingLogger());

    /// <summary>A selector whose host name lookups go to <paramref name="os"/> (default: no answer), the stand-in for the operating system resolver.</summary>
    private static ViewerAddressSelector Sel(ViewerAddressConfig cfg, Func<string, IPAddress?>? os = null, TestClock? clock = null)
    {
        PublicAddressResolver r = new(TimeSpan.Zero, new CapturingLogger(), osResolver: os ?? (_ => null!), time: clock);
        return new ViewerAddressSelector(cfg, r);
    }

    // ---- nothing set: today's behaviour ----

    [Theory]
    [InlineData("192.168.1.55")]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8::5")]
    public void Nothing_set_returns_the_local_endpoint_for_every_source(string viewer)
    {
        ViewerAddressSelector s = ViewerAddressSelector.Disabled;
        Assert.False(s.Enabled);
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip(viewer)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void An_empty_list_is_disabled_and_changes_nothing(string? addresses)
    {
        ViewerAddressConfig cfg = ViewerAddressConfig.Create(addresses, 30, null);
        Assert.False(cfg.Enabled);
        Assert.Empty(cfg.LocalNetworks);
        Assert.False(ViewerAddressConfig.Disabled.Enabled);
        Assert.Same(s_local, Sel(cfg).ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void A_list_where_every_entry_is_bad_is_disabled()
    {
        CapturingLogger log = new();
        ViewerAddressConfig cfg = Cfg("bad, 10.0.0.0/33", log: log);
        Assert.False(cfg.Enabled);
        Assert.Equal(2, log.Warnings);
        Assert.Same(s_local, Sel(cfg).ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void No_entry_matching_gives_the_local_address()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.1.0/24=192.168.1.20"));
        Assert.True(s.Enabled);
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    // ---- selection: first match wins ----

    [Fact]
    public void A_matching_entry_with_an_address_gives_that_address_and_the_port_handed_in()
    {
        ViewerAddressSelector s = Sel(Cfg("203.0.113.0/24=198.51.100.20"));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.20"), 9123),
            s.ForViewer(9123, new IPEndPoint(s_local.Address, 9123), Ip("203.0.113.9")));
    }

    [Fact]
    public void The_first_matching_entry_wins_whatever_follows()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.1.0/24=192.168.1.20, 10.8.0.0/24=10.8.0.1, 192.168.0.0/16=172.16.0.9, 0.0.0.0/0=198.51.100.20"));
        Assert.Equal(new IPEndPoint(Ip("192.168.1.20"), 9000), s.ForViewer(9000, s_local, Ip("192.168.1.55")));
        Assert.Equal(new IPEndPoint(Ip("10.8.0.1"), 9000), s.ForViewer(9000, s_local, Ip("10.8.0.77")));
        Assert.Equal(new IPEndPoint(Ip("172.16.0.9"), 9000), s.ForViewer(9000, s_local, Ip("192.168.7.7")));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.20"), 9000), s.ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void Order_decides_when_two_entries_contain_the_viewer()
    {
        IPEndPoint broadFirst = Sel(Cfg("192.168.0.0/16=198.51.100.1, 192.168.1.0/24=198.51.100.2")).ForViewer(9000, s_local, Ip("192.168.1.5"))!;
        IPEndPoint narrowFirst = Sel(Cfg("192.168.1.0/24=198.51.100.2, 192.168.0.0/16=198.51.100.1")).ForViewer(9000, s_local, Ip("192.168.1.5"))!;
        Assert.Equal(Ip("198.51.100.1"), broadFirst.Address);
        Assert.Equal(Ip("198.51.100.2"), narrowFirst.Address);
    }

    [Fact]
    public void A_bare_network_keeps_the_local_address_and_stops_the_search()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16, 0.0.0.0/0=198.51.100.20"));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("192.168.1.55")));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.20"), 9000), s.ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void Ipv6_and_mapped_sources()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16, fd00::/8, 0.0.0.0/0=198.51.100.20, ::/0=198.51.100.21"));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("fd12:3456::1")));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.21"), 9000), s.ForViewer(9000, s_local, Ip("2001:db8::5")));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("::ffff:192.168.1.5")));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.20"), 9000), s.ForViewer(9000, s_local, Ip("::ffff:203.0.113.9")));
    }

    [Fact]
    public void An_unknown_viewer_source_gets_the_local_address()
    {
        ViewerAddressSelector s = Sel(Cfg("0.0.0.0/0=198.51.100.20"));
        Assert.Same(s_local, s.ForViewer(9000, s_local, null));
    }

    [Fact]
    public void An_unresolved_local_endpoint_still_gives_a_matching_viewer_its_address()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16, 0.0.0.0/0=198.51.100.20"));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.20"), 9000), s.ForViewer(9000, null, Ip("203.0.113.9")));
        Assert.Null(s.ForViewer(9000, null, Ip("192.168.1.5")));
    }

    // ---- host names resolve through the operating system resolver ----

    [Fact]
    public void A_host_name_entry_is_looked_up_through_the_os_resolver()
    {
        List<string> asked = new();
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16, 0.0.0.0/0=public.example.org"), os: h => { asked.Add(h); return s_pub; });

        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("192.168.1.55")));
        Assert.Empty(asked);

        Assert.Equal(new IPEndPoint(s_pub, 9000), s.ForViewer(9000, s_local, Ip("203.0.113.9")));
        Assert.Equal(new[] { "public.example.org" }, asked);
    }

    [Fact]
    public void A_host_name_is_looked_up_once_and_the_answer_cached()
    {
        int lookups = 0;
        ViewerAddressSelector s = Sel(Cfg("0.0.0.0/0=public.example.org"), os: _ => { lookups++; return s_pub; });
        for (int i = 0; i < 5; i++)
            s.ForViewer(9000, s_local, Ip("203.0.113.9"));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void An_address_literal_entry_needs_no_lookup()
    {
        int lookups = 0;
        ViewerAddressSelector s = Sel(Cfg("0.0.0.0/0=198.51.100.77"), os: _ => { lookups++; return s_pub; });
        Assert.Equal(new IPEndPoint(Ip("198.51.100.77"), 9000), s.ForViewer(9000, s_local, Ip("203.0.113.9")));
        Assert.Equal(0, lookups);
        Assert.Empty(ViewerAddressConfig.Create("0.0.0.0/0=198.51.100.77", 30, null).Hosts);
    }

    [Fact]
    public void A_host_name_that_does_not_resolve_gives_the_local_address()
    {
        ViewerAddressSelector s = Sel(Cfg("0.0.0.0/0=public.example.org"), os: _ => null);
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void A_host_name_that_resolves_to_an_ipv6_address_gives_the_local_address()
    {
        ViewerAddressSelector s = Sel(Cfg("0.0.0.0/0=public.example.org"), os: _ => Ip("2001:db8::1"));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void Two_entries_with_the_same_name_ask_once_and_the_hosts_are_listed_once()
    {
        ViewerAddressConfig cfg = Cfg("203.0.113.0/24=Public.Example.org, 198.51.100.0/24=public.example.org, 192.0.2.0/24=other.example.org");
        Assert.Equal(new[] { "Public.Example.org", "other.example.org" }, cfg.Hosts);
    }

    // ---- regions of another grid (hypergrid destinations) ----

    private static readonly IPEndPoint s_foreign = new(IPAddress.Parse("192.0.2.80"), 9100);

    [Theory]
    [InlineData("0.0.0.0/0=198.51.100.7")]
    [InlineData("0.0.0.0/0=public.example.org")]
    [InlineData("203.0.113.0/24, 0.0.0.0/0=198.51.100.7")]
    public void An_address_of_this_grid_is_never_given_for_a_region_of_another_grid(string addresses)
    {
        ViewerAddressSelector s = Sel(Cfg(addresses), os: _ => s_pub);
        Assert.Same(s_foreign, s.ForViewer(9100, s_foreign, Ip("203.0.113.9"), foreignDestination: true));
    }

    [Fact]
    public void A_network_address_is_not_given_for_a_region_of_another_grid_but_is_for_one_of_this_grid()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16=192.168.1.20"));
        Assert.True(s.HoldsGridAddress);
        Assert.Same(s_foreign, s.ForViewer(9100, s_foreign, Ip("192.168.7.7"), foreignDestination: true));
        Assert.Equal(new IPEndPoint(Ip("192.168.1.20"), 9100), s.ForViewer(9100, s_foreign, Ip("192.168.7.7")));
    }

    [Fact]
    public void A_list_of_bare_networks_holds_no_address_of_the_grid()
    {
        Assert.False(Sel(Cfg("192.168.0.0/16")).HoldsGridAddress);
    }

    // ---- config parsing ----

    [Fact]
    public void Create_reads_the_list_and_the_refresh()
    {
        ViewerAddressConfig c = Cfg("192.168.0.0/16, 0.0.0.0/0=public.example.org", refresh: 5);
        Assert.True(c.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(5), c.RefreshInterval);
        Assert.Equal(2, c.LocalNetworks.Count);
        Assert.Null(c.LocalNetworks[0].Address);
        Assert.Null(c.LocalNetworks[0].Host);
        Assert.Equal("public.example.org", c.LocalNetworks[1].Host);
    }

    [Fact]
    public void Defaults_and_refresh_limits()
    {
        Assert.Equal(ViewerAddressConfig.DefaultRefreshInterval, ViewerAddressConfig.Disabled.RefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), ViewerAddressConfig.DefaultRefreshInterval);
        Assert.Equal(TimeSpan.Zero, Cfg("0.0.0.0/0=public.example.org", refresh: 0).RefreshInterval);
        Assert.Equal(TimeSpan.Zero, Cfg("0.0.0.0/0=public.example.org", refresh: -4).RefreshInterval);
    }

    [Fact]
    public void Entries_are_split_on_commas_and_semicolons_and_trimmed()
    {
        ViewerAddressConfig c = Cfg("  192.168.1.0/24 = 192.168.1.20 ;10.0.0.0/8,, 0.0.0.0/0=198.51.100.20  ");
        Assert.Equal(3, c.LocalNetworks.Count);
        Assert.Equal(Ip("192.168.1.20"), c.LocalNetworks[0].Address);
        Assert.Equal(Ip("198.51.100.20"), c.LocalNetworks[2].Address);
    }

    [Fact]
    public void Bad_entries_are_logged_one_warning_each_and_skipped_the_rest_still_work()
    {
        CapturingLogger log = new();
        ViewerAddressConfig c = Cfg("192.168.1.0/24=10.0.0.5, bad, 10.0.0.0/33, 10.0.0.0/8, 192.168.2.0/24=2001:db8::1, 192.168.3.0/24=-bad-.example.org, 0.0.0.0/0=198.51.100.20", log: log);
        Assert.Equal(3, c.LocalNetworks.Count);
        Assert.Equal(4, log.Warnings);
        Assert.Contains(log.Entries, e => e.Message.Contains("bad") && e.Message.Contains("Addresses"));

        ViewerAddressSelector s = Sel(c);
        Assert.Equal(new IPEndPoint(Ip("10.0.0.5"), 9000), s.ForViewer(9000, s_local, Ip("192.168.1.9")));
        Assert.Equal(new IPEndPoint(Ip("198.51.100.20"), 9000), s.ForViewer(9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void The_removed_keys_are_not_read_from_a_section()
    {
        IniConfigSource source = new();
        IConfig section = source.AddConfig(ViewerAddressConfig.SectionName);
        section.Set("PublicAddress", "198.51.100.7");
        section.Set("PublicDnsServer", "192.0.2.53");
        section.Set("LocalNetworks", "192.168.0.0/16");
        section.Set("PublicRefreshMinutes", "5");

        ViewerAddressConfig cfg = ViewerAddressConfig.FromConfig(section, null);
        Assert.False(cfg.Enabled);
        Assert.Empty(cfg.SetKeys);
        Assert.Equal(ViewerAddressConfig.DefaultRefreshInterval, cfg.RefreshInterval);
    }

    [Fact]
    public void Addresses_and_RefreshMinutes_are_read_from_a_section()
    {
        IniConfigSource source = new();
        IConfig section = source.AddConfig(ViewerAddressConfig.SectionName);
        section.Set("Addresses", "0.0.0.0/0=public.example.org");
        section.Set("RefreshMinutes", "7");

        ViewerAddressConfig cfg = ViewerAddressConfig.FromConfig(section, null);
        Assert.True(cfg.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(7), cfg.RefreshInterval);
        Assert.Equal(new[] { "Addresses", "RefreshMinutes" }, cfg.SetKeys);
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0", 24, null)]
    [InlineData("10.0.0.0/8=10.0.0.1", "10.0.0.0", 8, "10.0.0.1")]
    [InlineData("  192.168.1.77  ", "192.168.1.77", 32, null)]
    [InlineData("fd00::/8", "fd00::", 8, null)]
    [InlineData("192.168.1.77/24", "192.168.1.0", 24, null)]
    public void Network_entries_parse(string text, string network, int prefix, string? address)
    {
        Assert.True(LocalNetwork.TryParse(text, out LocalNetwork n, out string error), error);
        Assert.Equal(Ip(network), n.Network);
        Assert.Equal(prefix, n.PrefixLength);
        Assert.Equal(address is null ? null : Ip(address), n.Address);
        Assert.Null(n.Host);
    }

    [Theory]
    [InlineData("0.0.0.0/0=public.example.org", "public.example.org")]
    [InlineData("10.0.0.0/8 = Grid.Example.ORG ", "Grid.Example.ORG")]
    [InlineData("10.0.0.0/8=gateway", "gateway")]
    [InlineData("10.0.0.0/8=a-b.example.org", "a-b.example.org")]
    public void A_host_name_after_the_equals_sign_parses_as_a_host(string text, string host)
    {
        Assert.True(LocalNetwork.TryParse(text, out LocalNetwork n, out string error), error);
        Assert.Equal(host, n.Host);
        Assert.Null(n.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x/24")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fd00::/129")]
    [InlineData("10.0.0.0/8=")]
    [InlineData("10.0.0.0/8=fd00::1")]
    [InlineData("10.0.0.0/8=-nope.example.org")]
    [InlineData("10.0.0.0/8=nope-.example.org")]
    [InlineData("10.0.0.0/8=no pe.example.org")]
    [InlineData("10.0.0.0/8=a..example.org")]
    [InlineData("10.0.0.0/8=http://grid.example.org")]
    [InlineData("10.0.0.0/8=grid.example.org:9000")]
    [InlineData("10.0.0.0/8=300.1.1.1")]
    [InlineData("10.0.0.0/8=10.1")]
    [InlineData("10.0.0.0/8=0.0.0.0")]
    public void Bad_network_entries_do_not_parse(string text)
    {
        Assert.False(LocalNetwork.TryParse(text, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.255", true)]
    [InlineData("192.168.1.0/24", "192.168.2.0", false)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("0.0.0.0/0", "203.0.113.9", true)]
    [InlineData("192.168.1.77", "192.168.1.77", true)]
    [InlineData("192.168.1.77", "192.168.1.78", false)]
    [InlineData("192.168.1.0/25", "192.168.1.130", false)]
    public void Network_contains(string net, string ip, bool expected)
    {
        Assert.True(LocalNetwork.TryParse(net, out LocalNetwork n, out _));
        Assert.Equal(expected, n.Contains(Ip(ip)));
    }

    [Fact]
    public void The_framework_has_no_dns_client_of_its_own()
    {
        // Every name goes through the operating system resolver: nothing here may send DNS queries itself.
        string[] references = typeof(PublicAddressResolver).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, name => name.Contains("DnsClient", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the resolver ----

    [Fact]
    public void Resolver_caches_an_answer_and_ignores_a_non_ipv4_os_answer()
    {
        int calls = 0;
        using PublicAddressResolver r = new(TimeSpan.Zero, new CapturingLogger(),
            osResolver: h => { calls++; return h == "v4.example.org" ? s_pub : Ip("2001:db8::1"); });
        Assert.Equal(s_pub, r.Resolve("v4.example.org"));
        Assert.Equal(s_pub, r.Resolve("v4.example.org"));
        Assert.Equal(1, calls);
        Assert.Null(r.Resolve("v6.example.org"));
    }

    [Fact]
    public void Resolver_ip_literal_is_returned_as_is_and_any_is_refused()
    {
        using PublicAddressResolver r = new(TimeSpan.Zero, new CapturingLogger(), osResolver: _ => throw new Exception("not called"));
        Assert.Equal(Ip("198.51.100.9"), r.Resolve("198.51.100.9"));
        Assert.Null(r.Resolve("0.0.0.0"));
        Assert.Null(r.Resolve(""));
        Assert.Null(r.Resolve(null));
    }

    [Fact]
    public void Resolver_failed_lookup_is_not_repeated_until_the_retry_interval_has_passed()
    {
        TestClock clock = new();
        int calls = 0;
        using PublicAddressResolver r = new(TimeSpan.Zero, new CapturingLogger(),
            osResolver: _ => { calls++; return calls < 3 ? null! : s_pub; }, time: clock);

        Assert.Null(r.Resolve(Host));
        Assert.Null(r.Resolve(Host));
        Assert.Equal(1, calls);

        clock.Advance(PublicAddressResolver.FailureRetryInterval - TimeSpan.FromSeconds(1));
        Assert.Null(r.Resolve(Host));
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(r.Resolve(Host));
        Assert.Equal(2, calls);

        clock.Advance(PublicAddressResolver.FailureRetryInterval);
        Assert.Equal(s_pub, r.Resolve(Host));
    }

    [Fact]
    public void Resolver_lookup_that_throws_is_a_failure_not_an_exception()
    {
        CapturingLogger log = new();
        using PublicAddressResolver r = new(TimeSpan.Zero, log,
            osResolver: _ => throw new TimeoutException());
        Assert.Null(r.Resolve(Host));
        Assert.True(log.Warnings >= 1);
    }

    [Fact]
    public void Resolver_refresh_picks_up_a_changed_answer_and_keeps_the_last_good_one_on_failure()
    {
        IPAddress? answer = s_pub;
        bool fail = false;
        CapturingLogger log = new();
        using PublicAddressResolver r = new(TimeSpan.Zero, log,
            osResolver: _ => fail ? throw new TimeoutException() : answer!);
        Assert.Equal(s_pub, r.Resolve(Host));

        answer = Ip("198.51.100.8");
        Assert.Equal(1, r.Refresh());
        Assert.Equal(Ip("198.51.100.8"), r.Resolve(Host));

        fail = true;
        Assert.Equal(0, r.Refresh());
        Assert.Equal(Ip("198.51.100.8"), r.Resolve(Host));
        Assert.True(log.Warnings >= 1);
    }

    [Fact]
    public void Resolver_timer_refreshes_on_its_own()
    {
        int calls = 0;
        using PublicAddressResolver r = new(TimeSpan.FromMilliseconds(30), new CapturingLogger(),
            osResolver: _ => Ip("198.51.100." + (Interlocked.Increment(ref calls) + 10)));
        r.Resolve(Host);
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 3, TimeSpan.FromSeconds(20));
        Assert.True(Volatile.Read(ref calls) >= 3);
    }

    [Fact]
    public void Create_builds_a_selector_from_the_config()
    {
        using ViewerAddressSelector s = ViewerAddressSelector.Create(Cfg("0.0.0.0/0=198.51.100.77"), new CapturingLogger());
        Assert.True(s.Enabled);
        Assert.Equal(new IPEndPoint(Ip("198.51.100.77"), 9000), s.ForViewer(9000, s_local, Ip("203.0.113.9")));
        using ViewerAddressSelector off = ViewerAddressSelector.Create(ViewerAddressConfig.Disabled, new CapturingLogger());
        Assert.False(off.Enabled);
    }
}
