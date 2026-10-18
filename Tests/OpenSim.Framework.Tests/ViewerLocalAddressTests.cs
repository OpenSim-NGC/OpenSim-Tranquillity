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
/// The local address a region registers with the grid so that regions on other machines can hand it to a
/// viewer on a local network, the keys that are reported at start, and the one start-up line.
/// Only documentation (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) and generic private ranges are used.
/// </summary>
public class ViewerLocalAddressTests
{
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
        public int Warnings => Entries.Count(e => e.Level == LogLevel.Warning);
        public List<string> Infos() => Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
    }

    private const string Host = "region.example.org";
    private static readonly IPAddress s_pub = IPAddress.Parse("198.51.100.7");
    private static readonly IPEndPoint s_local = new(IPAddress.Parse("192.168.1.10"), 9000);
    private static readonly IPAddress s_registered = IPAddress.Parse("10.0.0.20");
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static ViewerAddressConfig Cfg(string addresses = "", string local = "",
        int refresh = 30, bool refreshSet = false, ILogger? log = null)
        => ViewerAddressConfig.Create(addresses, refresh, log, local, refreshSet);

    /// <summary>A selector whose host name lookups go to <paramref name="os"/> (default: the public test address).</summary>
    private static ViewerAddressSelector Sel(ViewerAddressConfig cfg, Func<string, IPAddress?>? os = null)
    {
        PublicAddressResolver r = new(TimeSpan.Zero, new CapturingLogger(), osResolver: os ?? (_ => s_pub)!);
        return new ViewerAddressSelector(cfg, r);
    }

    // ---- the keys that were set ----

    [Fact]
    public void No_key_set_gives_an_empty_key_list_and_no_local_address()
    {
        ViewerAddressConfig cfg = Cfg();
        Assert.Empty(cfg.SetKeys);
        Assert.Null(cfg.LocalAddress);
        Assert.Empty(ViewerAddressConfig.Disabled.SetKeys);
    }

    [Fact]
    public void Every_key_set_is_listed_in_the_documented_order()
    {
        ViewerAddressConfig cfg = Cfg("10.0.0.0/8=region.example.org", "192.168.1.10", 15, refreshSet: true);
        Assert.Equal(new[] { "Addresses", "RefreshMinutes", "LocalAddress" }, cfg.SetKeys);
    }

    [Fact]
    public void A_refresh_value_counts_as_a_key_only_when_it_was_set()
    {
        Assert.Empty(Cfg(refresh: 30).SetKeys);
        Assert.Equal(new[] { "RefreshMinutes" }, Cfg(refresh: 30, refreshSet: true).SetKeys);
    }

    [Fact]
    public void From_a_section_only_the_keys_present_are_listed()
    {
        IniConfigSource source = new();
        IConfig section = source.AddConfig(ViewerAddressConfig.SectionName);
        section.Set("Addresses", "192.168.0.0/16");
        section.Set("RefreshMinutes", "30");
        section.Set("LocalAddress", "");

        ViewerAddressConfig cfg = ViewerAddressConfig.FromConfig(section, null);
        Assert.Equal(new[] { "Addresses", "RefreshMinutes" }, cfg.SetKeys);
    }

    // ---- LocalAddress ----

    [Fact]
    public void A_valid_local_address_is_read_and_alone_does_not_enable_the_selector()
    {
        ViewerAddressConfig cfg = Cfg(local: " 192.168.1.10 ");
        Assert.Equal(Ip("192.168.1.10"), cfg.LocalAddress);
        Assert.False(cfg.Enabled);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("2001:db8::1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1")]
    public void A_local_address_that_is_not_an_ipv4_address_is_ignored_with_a_warning(string value)
    {
        CapturingLogger log = new();
        ViewerAddressConfig cfg = Cfg(local: value, log: log);
        Assert.Null(cfg.LocalAddress);
        Assert.Equal(1, log.Warnings);
    }

    // ---- the selector with a registered local address ----

    private const string LanAndPublic = "192.168.0.0/16, 0.0.0.0/0=198.51.100.7";

    [Fact]
    public void A_bare_network_gives_a_local_viewer_the_address_the_region_registered()
    {
        ViewerAddressSelector s = Sel(Cfg(LanAndPublic));
        Assert.Equal(new IPEndPoint(s_registered, 9000),
            s.ForViewer(9000, s_local, Ip("192.168.1.55"), false, s_registered));
    }

    [Fact]
    public void Without_a_registered_address_a_local_viewer_gets_the_resolved_one_as_before()
    {
        ViewerAddressSelector s = Sel(Cfg(LanAndPublic));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("192.168.1.55"), false, null));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("192.168.1.55")));
    }

    [Fact]
    public void A_network_with_its_own_address_keeps_that_address_over_the_registered_one()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16=192.168.1.99"));
        Assert.Equal(new IPEndPoint(Ip("192.168.1.99"), 9000),
            s.ForViewer(9000, s_local, Ip("192.168.1.55"), false, s_registered));
    }

    [Fact]
    public void A_network_with_a_host_name_keeps_that_address_over_the_registered_one()
    {
        ViewerAddressSelector s = Sel(Cfg("192.168.0.0/16=lan.example.org"), os: _ => Ip("192.168.1.98"));
        Assert.Equal(new IPEndPoint(Ip("192.168.1.98"), 9000),
            s.ForViewer(9000, s_local, Ip("192.168.1.55"), false, s_registered));
    }

    [Fact]
    public void A_viewer_outside_the_bare_network_gets_its_own_entrys_address_not_the_registered_one()
    {
        ViewerAddressSelector s = Sel(Cfg(LanAndPublic));
        Assert.Equal(new IPEndPoint(s_pub, 9000),
            s.ForViewer(9000, s_local, Ip("203.0.113.9"), false, s_registered));
    }

    [Fact]
    public void A_region_of_another_grid_never_gets_a_registered_address()
    {
        ViewerAddressSelector s = Sel(Cfg(LanAndPublic));
        Assert.Same(s_local, s.ForViewer(9000, s_local, Ip("192.168.1.55"), true, s_registered));
    }

    [Fact]
    public void With_no_list_the_registered_address_is_not_used()
    {
        Assert.Same(s_local, ViewerAddressSelector.Disabled.ForViewer(9000, s_local, Ip("192.168.1.55"), false, s_registered));
        Assert.Same(s_local, Sel(Cfg()).ForViewer(9000, s_local, Ip("192.168.1.55"), false, s_registered));
    }

    [Fact]
    public void An_unknown_viewer_source_gets_the_local_endpoint()
    {
        ViewerAddressSelector s = Sel(Cfg(LanAndPublic));
        Assert.Same(s_local, s.ForViewer(9000, s_local, null, false, s_registered));
    }

    // ---- LocalAddress for a region ----

    private static RegionInfo Region(string? ownSetting = null)
    {
        RegionInfo region = new();
        if (ownSetting is not null)
            region.SetExtraSetting("LocalAddress", ownSetting);
        return region;
    }

    private static IConfig Section(string? localAddress)
    {
        IniConfigSource source = new();
        IConfig section = source.AddConfig(ViewerAddressConfig.SectionName);
        if (localAddress is not null)
            section.Set("LocalAddress", localAddress);
        return section;
    }

    [Fact]
    public void The_sections_local_address_is_used_for_a_region_that_has_none_of_its_own()
    {
        RegionInfo region = Region();
        ViewerAddressConfig.ApplyRegionLocalAddress(Section("192.168.1.10"), region, null);
        Assert.Equal("192.168.1.10", region.ViewerLocalAddress);
    }

    [Fact]
    public void A_regions_own_local_address_replaces_the_sections()
    {
        RegionInfo region = Region("10.0.0.20");
        ViewerAddressConfig.ApplyRegionLocalAddress(Section("192.168.1.10"), region, null);
        Assert.Equal("10.0.0.20", region.ViewerLocalAddress);
    }

    [Fact]
    public void A_region_setting_applies_without_a_section()
    {
        RegionInfo region = Region("10.0.0.20");
        ViewerAddressConfig.ApplyRegionLocalAddress(null, region, null);
        Assert.Equal("10.0.0.20", region.ViewerLocalAddress);
    }

    [Fact]
    public void Nothing_set_leaves_the_region_without_a_local_address()
    {
        RegionInfo region = Region();
        ViewerAddressConfig.ApplyRegionLocalAddress(Section(null), region, null);
        Assert.Null(region.ViewerLocalAddress);
        ViewerAddressConfig.ApplyRegionLocalAddress(null, region, null);
        Assert.Null(region.ViewerLocalAddress);
    }

    [Fact]
    public void An_invalid_local_address_is_not_registered_and_is_warned_about()
    {
        CapturingLogger log = new();
        RegionInfo region = Region("2001:db8::1");
        ViewerAddressConfig.ApplyRegionLocalAddress(Section(null), region, log);
        Assert.Null(region.ViewerLocalAddress);
        Assert.Equal(1, log.Warnings);
    }

    // ---- the start-up line ----

    private static async Task<List<string>> Report(ViewerAddressConfig cfg, ViewerAddressStartupLog? startup = null,
        Func<string, IPAddress?>? os = null, CapturingLogger? log = null)
    {
        log ??= new CapturingLogger();
        ViewerAddressSelector selector = Sel(cfg, os);
        await (startup ?? new ViewerAddressStartupLog()).ReportOnce(log, cfg, selector);
        return log.Infos();
    }

    [Fact]
    public async Task With_no_key_set_nothing_is_logged()
    {
        CapturingLogger log = new();
        int lookups = 0;
        await Report(Cfg(), os: _ => { lookups++; return s_pub; }, log: log);
        Assert.Empty(log.Entries);
        Assert.Equal(0, lookups);

        await new ViewerAddressStartupLog().ReportOnce(log, ViewerAddressConfig.Disabled, ViewerAddressSelector.Disabled);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task With_keys_set_one_line_names_the_keys_the_entries_and_the_lookup_result()
    {
        List<string> lines = await Report(Cfg("192.168.0.0/16, 10.0.0.0/8=10.0.0.1, 0.0.0.0/0=region.example.org"));

        string line = Assert.Single(lines);
        Assert.Contains("section is active", line);
        Assert.Contains("Addresses", line);
        Assert.DoesNotContain("LocalAddress", line);
        Assert.Contains("entries: 3", line);
        Assert.Contains(Host, line);
        Assert.Contains("succeeded", line);
        Assert.Contains("198.51.100.7", line);
    }

    [Fact]
    public async Task A_failed_first_lookup_is_reported_as_failed()
    {
        List<string> lines = await Report(Cfg("0.0.0.0/0=region.example.org"), os: _ => null);

        string line = Assert.Single(lines);
        Assert.Contains("failed", line);
        Assert.DoesNotContain("succeeded", line);
        Assert.Contains("entries: 1", line);
    }

    [Fact]
    public async Task Every_host_name_in_the_list_is_looked_up_once()
    {
        List<string> asked = new();
        await Report(Cfg("10.0.0.0/8=grid.example.org, 0.0.0.0/0=other.example.org, 192.168.0.0/16=GRID.example.org"),
            os: host => { lock (asked) asked.Add(host); return s_pub; });
        Assert.Equal(new[] { "grid.example.org", "other.example.org" }, asked.OrderBy(h => h).ToArray());
    }

    [Fact]
    public async Task A_list_of_address_literals_needs_no_lookup()
    {
        int lookups = 0;
        List<string> lines = await Report(Cfg("0.0.0.0/0=203.0.113.9"), os: _ => { lookups++; return s_pub; });

        string line = Assert.Single(lines);
        Assert.Contains("Addresses", line);
        Assert.Contains("no host names", line);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task Only_a_local_address_set_is_reported_without_a_lookup()
    {
        int lookups = 0;
        List<string> lines = await Report(Cfg(local: "192.168.1.10"), os: _ => { lookups++; return s_pub; });

        string line = Assert.Single(lines);
        Assert.Contains("LocalAddress", line);
        Assert.Contains("entries: 0", line);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task The_line_is_written_once_per_instance()
    {
        CapturingLogger log = new();
        ViewerAddressStartupLog startup = new();
        ViewerAddressConfig cfg = Cfg("0.0.0.0/0=region.example.org");

        await Report(cfg, startup, log: log);
        await Report(cfg, startup, log: log);

        Assert.Single(log.Infos());
    }

    [Fact]
    public async Task A_lookup_that_throws_is_reported_as_failed_and_does_not_escape()
    {
        List<string> lines = await Report(Cfg("0.0.0.0/0=region.example.org"), os: _ => throw new InvalidOperationException("boom"));
        Assert.Contains("failed", Assert.Single(lines));
    }
}
