/*
 * Copyright (c) Legion Builds
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

namespace OpenSim.Framework.Tests;

/// <summary>
/// Choice of the address a viewer is given for a region: unset keys change nothing, a viewer on a
/// listed local network gets the region's own address, every other viewer gets the public one.
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

    private static ViewerAddressConfig Cfg(string pub = "", string dns = "", string nets = "", int refresh = 30)
        => ViewerAddressConfig.Create(pub, dns, refresh, nets, new CapturingLogger());

    private static ViewerAddressSelector Sel(ViewerAddressConfig cfg, Func<string, IPEndPoint, IPAddress>? direct = null,
        Func<string, IPAddress>? os = null, TestClock? clock = null)
    {
        PublicAddressResolver r = new(cfg.PublicDnsServer, TimeSpan.Zero, new CapturingLogger(),
            osResolver: os ?? (_ => null!), directResolver: direct ?? ((_, _) => s_pub), time: clock);
        return new ViewerAddressSelector(cfg, r);
    }

    // ---- unset keys: today's behaviour ----

    [Theory]
    [InlineData("192.168.1.55")]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8::5")]
    public void Unset_keys_return_the_local_endpoint_for_every_source(string viewer)
    {
        ViewerAddressSelector s = ViewerAddressSelector.Disabled;
        Assert.False(s.Enabled);
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip(viewer)));
    }

    [Fact]
    public void Local_networks_alone_change_nothing()
    {
        ViewerAddressConfig cfg = Cfg(nets: "192.168.0.0/16=10.0.0.5");
        Assert.False(cfg.Enabled);
        ViewerAddressSelector s = Sel(cfg);
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9")));
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip("192.168.1.55")));
    }

    [Fact]
    public void Create_with_a_null_logger_and_empty_strings_is_disabled()
    {
        Assert.False(ViewerAddressConfig.Create(null, null, 30, null, null).Enabled);
        Assert.False(ViewerAddressConfig.Disabled.Enabled);
    }

    // ---- selection ----

    [Fact]
    public void Dns_server_only_local_viewer_keeps_the_local_address_and_other_viewers_get_the_resolved_one()
    {
        string? seenHost = null; IPEndPoint? seenServer = null;
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9", nets: "192.168.0.0/16"), (h, srv) => { seenHost = h; seenServer = srv; return s_pub; });

        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip("192.168.1.55")));
        Assert.Null(seenHost);

        IPEndPoint? got = s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9"));
        Assert.Equal(new IPEndPoint(s_pub, 9000), got);
        Assert.Equal(Host, seenHost);
        Assert.Equal(new IPEndPoint(Ip("9.9.9.9"), 53), seenServer);
    }

    [Fact]
    public void The_port_given_is_the_port_handed_out()
    {
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9"));
        Assert.Equal(9123, s.ForViewer(Host, 9123, new IPEndPoint(s_local.Address, 9123), Ip("203.0.113.9"))!.Port);
    }

    [Fact]
    public void A_literal_public_address_needs_no_lookup()
    {
        int lookups = 0;
        ViewerAddressSelector s = Sel(Cfg(pub: "198.51.100.77"), (_, _) => { lookups++; return s_pub; }, _ => { lookups++; return s_pub; });
        Assert.Equal(new IPEndPoint(Ip("198.51.100.77"), 9000), s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9")));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void A_public_host_name_is_the_name_looked_up_instead_of_the_region_name()
    {
        string? seen = null;
        ViewerAddressSelector s = Sel(Cfg(pub: "public.example.org", dns: "9.9.9.9"), (h, _) => { seen = h; return s_pub; });
        s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9"));
        Assert.Equal("public.example.org", seen);
    }

    [Fact]
    public void A_public_host_name_without_a_dns_server_uses_the_os_resolver()
    {
        string? seen = null;
        ViewerAddressSelector s = Sel(Cfg(pub: "public.example.org"), os: h => { seen = h; return s_pub; });
        Assert.Equal(new IPEndPoint(s_pub, 9000), s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9")));
        Assert.Equal("public.example.org", seen);
    }

    [Fact]
    public void A_listed_network_with_an_address_gets_that_address_first_match_wins()
    {
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9", nets: "192.168.1.0/24=192.168.1.20, 10.8.0.0/24=10.8.0.1, 192.168.0.0/16=172.16.0.9"));
        Assert.Equal(new IPEndPoint(Ip("192.168.1.20"), 9000), s.ForViewer(Host, 9000, s_local, Ip("192.168.1.55")));
        Assert.Equal(new IPEndPoint(Ip("10.8.0.1"), 9000), s.ForViewer(Host, 9000, s_local, Ip("10.8.0.77")));
        Assert.Equal(new IPEndPoint(Ip("172.16.0.9"), 9000), s.ForViewer(Host, 9000, s_local, Ip("192.168.7.7")));
        Assert.Equal(new IPEndPoint(s_pub, 9000), s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void Ipv6_and_mapped_sources()
    {
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9", nets: "192.168.0.0/16, fd00::/8"));
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip("fd12:3456::1")));
        Assert.Equal(new IPEndPoint(s_pub, 9000), s.ForViewer(Host, 9000, s_local, Ip("2001:db8::5")));
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip("::ffff:192.168.1.5")));
        Assert.Equal(new IPEndPoint(s_pub, 9000), s.ForViewer(Host, 9000, s_local, Ip("::ffff:203.0.113.9")));
    }

    [Fact]
    public void Public_address_not_known_yet_falls_back_to_the_local_address()
    {
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9"), (_, _) => null!);
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9")));
    }

    [Fact]
    public void An_unknown_viewer_source_gets_the_local_address()
    {
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9"));
        Assert.Same(s_local, s.ForViewer(Host, 9000, s_local, null));
    }

    [Fact]
    public void Unresolved_local_endpoint_still_gives_a_public_viewer_the_public_address()
    {
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9", nets: "192.168.0.0/16"));
        Assert.Equal(new IPEndPoint(s_pub, 9000), s.ForViewer(Host, 9000, null, Ip("203.0.113.9")));
        Assert.Null(s.ForViewer(Host, 9000, null, Ip("192.168.1.5")));
    }

    // ---- regions of another grid (hypergrid destinations) ----

    private static readonly IPEndPoint s_foreign = new(IPAddress.Parse("192.0.2.80"), 9100);

    [Theory]
    [InlineData("198.51.100.7")]
    [InlineData("public.example.org")]
    public void A_public_address_is_never_given_for_a_region_of_another_grid(string publicAddress)
    {
        ViewerAddressSelector s = Sel(Cfg(pub: publicAddress, nets: "192.168.0.0/16"), os: _ => s_pub);
        Assert.Same(s_foreign, s.ForViewer("foreign.example.net", 9100, s_foreign, Ip("203.0.113.9"), foreignDestination: true));
    }

    [Fact]
    public void A_network_address_is_not_given_for_a_region_of_another_grid()
    {
        ViewerAddressSelector s = Sel(Cfg(pub: "198.51.100.7", nets: "192.168.0.0/16=192.168.1.20"));
        Assert.Same(s_foreign, s.ForViewer("foreign.example.net", 9100, s_foreign, Ip("192.168.7.7"), foreignDestination: true));
        // The same viewer is still given the network's address for a region of this grid.
        Assert.Equal(new IPEndPoint(Ip("192.168.1.20"), 9100), s.ForViewer("region.example.org", 9100, s_foreign, Ip("192.168.7.7")));
    }

    [Fact]
    public void With_only_a_dns_server_a_region_of_another_grid_is_looked_up_by_its_own_name()
    {
        string? seen = null;
        ViewerAddressSelector s = Sel(Cfg(dns: "9.9.9.9"), (h, _) => { seen = h; return s_pub; });
        Assert.Equal(new IPEndPoint(s_pub, 9100), s.ForViewer("foreign.example.net", 9100, s_foreign, Ip("203.0.113.9"), foreignDestination: true));
        Assert.Equal("foreign.example.net", seen);
    }

    // ---- config parsing ----

    [Fact]
    public void Create_reads_the_four_keys()
    {
        ViewerAddressConfig c = Cfg(pub: "198.51.100.7", dns: "9.9.9.9:5353", nets: "192.168.0.0/16", refresh: 5);
        Assert.True(c.Enabled);
        Assert.Equal(s_pub, c.PublicLiteral);
        Assert.Null(c.PublicHost);
        Assert.Equal(new IPEndPoint(Ip("9.9.9.9"), 5353), c.PublicDnsServer);
        Assert.Equal(TimeSpan.FromMinutes(5), c.RefreshInterval);
        Assert.Single(c.LocalNetworks);
    }

    [Fact]
    public void Defaults_and_refresh_limits()
    {
        Assert.Equal(ViewerAddressConfig.DefaultRefreshInterval, ViewerAddressConfig.Disabled.RefreshInterval);
        Assert.Equal(TimeSpan.Zero, Cfg(dns: "9.9.9.9", refresh: 0).RefreshInterval);
        Assert.Equal(TimeSpan.Zero, Cfg(dns: "9.9.9.9", refresh: -4).RefreshInterval);
    }

    [Fact]
    public void An_ipv6_public_literal_is_rejected_with_a_warning()
    {
        CapturingLogger log = new();
        ViewerAddressConfig c = ViewerAddressConfig.Create("2001:db8::1", "", 30, "", log);
        Assert.False(c.Enabled);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public void A_bad_dns_server_is_ignored_with_a_warning()
    {
        CapturingLogger log = new();
        ViewerAddressConfig c = ViewerAddressConfig.Create("", "not-an-address", 30, "", log);
        Assert.False(c.Enabled);
        Assert.Null(c.PublicDnsServer);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public void Bad_network_entries_are_skipped_one_warning_each()
    {
        CapturingLogger log = new();
        ViewerAddressConfig c = ViewerAddressConfig.Create("", "9.9.9.9", 30,
            "192.168.1.0/24=10.0.0.5, bad, 10.0.0.0/33, 10.0.0.0/8, 192.168.2.0/24=2001:db8::1", log);
        Assert.Equal(2, c.LocalNetworks.Count);
        Assert.Equal(3, log.Warnings);
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
    }

    [Theory]
    [InlineData("")]
    [InlineData("x/24")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fd00::/129")]
    [InlineData("10.0.0.0/8=")]
    [InlineData("10.0.0.0/8=fd00::1")]
    [InlineData("10.0.0.0/8=nope")]
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
    public void Dns_server_parse()
    {
        Assert.True(ViewerAddressConfig.TryParseDnsServer("1.1.1.1", out IPEndPoint e));
        Assert.Equal(new IPEndPoint(Ip("1.1.1.1"), 53), e);
        Assert.True(ViewerAddressConfig.TryParseDnsServer("8.8.8.8:5353", out e));
        Assert.Equal(5353, e.Port);
        Assert.False(ViewerAddressConfig.TryParseDnsServer("1.1.1.1:0", out _));
        Assert.False(ViewerAddressConfig.TryParseDnsServer("", out _));
    }

    // ---- the resolver ----

    [Fact]
    public void Resolver_caches_an_answer_and_ignores_a_non_ipv4_os_answer()
    {
        int calls = 0;
        using PublicAddressResolver r = new(null, TimeSpan.Zero, new CapturingLogger(),
            osResolver: h => { calls++; return h == "v4.example.org" ? s_pub : Ip("2001:db8::1"); });
        Assert.Equal(s_pub, r.Resolve("v4.example.org"));
        Assert.Equal(s_pub, r.Resolve("v4.example.org"));
        Assert.Equal(1, calls);
        Assert.Null(r.Resolve("v6.example.org"));
    }

    [Fact]
    public void Resolver_ip_literal_is_returned_as_is_and_any_is_refused()
    {
        using PublicAddressResolver r = new(null, TimeSpan.Zero, new CapturingLogger(), osResolver: _ => throw new Exception("not called"));
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
        using PublicAddressResolver r = new(new IPEndPoint(Ip("9.9.9.9"), 53), TimeSpan.Zero, new CapturingLogger(),
            directResolver: (_, _) => { calls++; return calls < 3 ? null! : s_pub; }, time: clock);

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
        using PublicAddressResolver r = new(new IPEndPoint(Ip("9.9.9.9"), 53), TimeSpan.Zero, log,
            directResolver: (_, _) => throw new TimeoutException());
        Assert.Null(r.Resolve(Host));
        Assert.True(log.Warnings >= 1);
    }

    [Fact]
    public void Resolver_refresh_picks_up_a_changed_answer_and_keeps_the_last_good_one_on_failure()
    {
        IPAddress? answer = s_pub;
        bool fail = false;
        CapturingLogger log = new();
        using PublicAddressResolver r = new(new IPEndPoint(Ip("9.9.9.9"), 53), TimeSpan.Zero, log,
            directResolver: (_, _) => fail ? throw new TimeoutException() : answer!);
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
        using PublicAddressResolver r = new(new IPEndPoint(Ip("9.9.9.9"), 53), TimeSpan.FromMilliseconds(30), new CapturingLogger(),
            directResolver: (_, _) => Ip("198.51.100." + (Interlocked.Increment(ref calls) + 10)));
        r.Resolve(Host);
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 3, TimeSpan.FromSeconds(20));
        Assert.True(Volatile.Read(ref calls) >= 3);
    }

    [Fact]
    public void Create_builds_a_selector_from_the_config()
    {
        using ViewerAddressSelector s = ViewerAddressSelector.Create(Cfg(pub: "198.51.100.77"), new CapturingLogger());
        Assert.True(s.Enabled);
        Assert.Equal(new IPEndPoint(Ip("198.51.100.77"), 9000), s.ForViewer(Host, 9000, s_local, Ip("203.0.113.9")));
        using ViewerAddressSelector off = ViewerAddressSelector.Create(ViewerAddressConfig.Disabled, new CapturingLogger());
        Assert.False(off.Enabled);
    }
}
