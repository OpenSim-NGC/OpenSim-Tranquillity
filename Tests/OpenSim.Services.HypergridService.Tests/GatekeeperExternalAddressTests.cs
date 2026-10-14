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
using Microsoft.Extensions.Logging.Abstractions;
using Nini.Config;
using Xunit;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// The gatekeeper's public address as the client check uses it: keys unset behave as before
/// (operating system resolver, 30 minute refresh); the older [UserAgentService] keys keep their meaning;
/// the [ViewerAddress] keys take precedence when both are set.
/// </summary>
public class GatekeeperExternalAddressTests
{
    private const string Host = "grid.example.org";

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static IConfig Section(string name, params (string Key, string Value)[] values)
    {
        IniConfigSource source = new();
        IConfig config = source.AddConfig(name);
        foreach ((string key, string value) in values)
            config.Set(key, value);
        return config;
    }

    [Fact]
    public void NoKeys_UsesTheOsResolverOnly_AndRefreshesEveryThirtyMinutes()
    {
        int os = 0, direct = 0;

        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host, null, null, NullLogger.Instance,
            osResolver: h => { os++; Assert.Equal(Host, h); return Ip("203.0.113.10"); },
            directResolver: (_, _) => { direct++; return Ip("198.51.100.1"); });

        Assert.Equal("203.0.113.10", a.CurrentIP);
        Assert.Null(a.DnsServer);
        Assert.Equal(TimeSpan.FromMinutes(30), a.RefreshInterval);
        Assert.Equal(1, os);
        Assert.Equal(0, direct);
    }

    [Fact]
    public void NoKeys_AnIpv6OsAnswerIsStillGiven()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host, null, null, NullLogger.Instance,
            osResolver: _ => Ip("2001:db8::10"));

        Assert.Equal("2001:db8::10", a.CurrentIP);
    }

    [Fact]
    public void NoKeys_ASectionWithoutADnsServerStillUsesTheOsResolver()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("GridService", "x")),
            Section("ViewerAddress", ("LocalNetworks", "192.168.0.0/16"), ("PublicAddress", "198.51.100.9")),
            NullLogger.Instance,
            osResolver: _ => Ip("203.0.113.10"),
            directResolver: (_, _) => throw new InvalidOperationException("direct path must not be used"));

        Assert.Equal("203.0.113.10", a.CurrentIP);
        Assert.Null(a.DnsServer);
    }

    [Fact]
    public void OldKeys_ExternalIPResolverAndRefreshMinutes_AreAliases()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("ExternalIPResolver", "192.0.2.53"), ("ExternalIPRefreshMinutes", "5")),
            null, NullLogger.Instance,
            osResolver: _ => throw new InvalidOperationException("OS resolver must not be used"),
            directResolver: (h, server) =>
            {
                Assert.Equal(Host, h);
                Assert.Equal(new IPEndPoint(Ip("192.0.2.53"), 53), server);
                return Ip("198.51.100.7");
            });

        Assert.Equal("198.51.100.7", a.CurrentIP);
        Assert.Equal(new IPEndPoint(Ip("192.0.2.53"), 53), a.DnsServer);
        Assert.Equal(TimeSpan.FromMinutes(5), a.RefreshInterval);
    }

    [Fact]
    public void NewKeys_TakePrecedenceOverTheOldOnes()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("ExternalIPResolver", "192.0.2.53"), ("ExternalIPRefreshMinutes", "5")),
            Section("ViewerAddress", ("PublicDnsServer", "192.0.2.54:5353"), ("PublicRefreshMinutes", "12")),
            NullLogger.Instance,
            directResolver: (_, server) => server.Port == 5353 ? Ip("198.51.100.8") : Ip("198.51.100.1"));

        Assert.Equal("198.51.100.8", a.CurrentIP);
        Assert.Equal(new IPEndPoint(Ip("192.0.2.54"), 5353), a.DnsServer);
        Assert.Equal(TimeSpan.FromMinutes(12), a.RefreshInterval);
    }

    [Fact]
    public void NewDnsKey_WithTheOldRefreshKey_KeepsTheOldRefresh()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("ExternalIPRefreshMinutes", "7")),
            Section("ViewerAddress", ("PublicDnsServer", "192.0.2.54")),
            NullLogger.Instance,
            directResolver: (_, _) => Ip("198.51.100.8"));

        Assert.Equal(TimeSpan.FromMinutes(7), a.RefreshInterval);
        Assert.Equal("198.51.100.8", a.CurrentIP);
    }

    [Fact]
    public void ZeroRefreshMinutes_DisablesTheRefresh()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("ExternalIPRefreshMinutes", "0")), null, NullLogger.Instance,
            osResolver: _ => Ip("203.0.113.10"));

        Assert.Equal(TimeSpan.Zero, a.RefreshInterval);
    }

    [Fact]
    public void AnUnparsableDnsServer_FallsBackToTheOsResolver()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("ExternalIPResolver", "not an address")), null, NullLogger.Instance,
            osResolver: _ => Ip("203.0.113.10"),
            directResolver: (_, _) => throw new InvalidOperationException("direct path must not be used"));

        Assert.Null(a.DnsServer);
        Assert.Equal("203.0.113.10", a.CurrentIP);
    }

    [Fact]
    public void AFailedFirstLookup_GivesAnEmptyAddressAndNeverThrows()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(Host, null, null, NullLogger.Instance,
            osResolver: _ => throw new InvalidOperationException("resolver down"));

        Assert.Equal(string.Empty, a.CurrentIP);
    }

    [Fact]
    public void AnEmptyHost_GivesAnEmptyAddress()
    {
        using GatekeeperExternalAddress a = GatekeeperExternalAddress.Create(string.Empty, null, null, NullLogger.Instance,
            osResolver: _ => throw new InvalidOperationException("not called"));

        Assert.Equal(string.Empty, a.CurrentIP);
    }

    // The failure that made the direct lookup necessary: a hosts entry puts a private address on the grid's own
    // public name, a foreign simulator reports the viewer's public address, and the two never matched.
    [Fact]
    public void HostsEntryOnTheGridPc_RejectedWithoutADnsServer_AcceptedWithOne()
    {
        const string Reported = "198.51.100.7";
        const string LoginIp = "203.0.113.5";
        Func<string, IPAddress> hostsEntry = _ => Ip("192.168.1.10");
        Func<string, IPEndPoint, IPAddress> publicDns = (_, _) => Ip("198.51.100.7");

        using GatekeeperExternalAddress without = GatekeeperExternalAddress.Create(Host, null, null, NullLogger.Instance,
            osResolver: hostsEntry, directResolver: publicDns);
        using GatekeeperExternalAddress with = GatekeeperExternalAddress.Create(Host,
            Section("UserAgentService", ("ExternalIPResolver", "192.0.2.53")), null, NullLogger.Instance,
            osResolver: hostsEntry, directResolver: publicDns);

        Assert.False(UserAgentService.ClientIPMatches(LoginIp, Reported, without.CurrentIP));
        Assert.True(UserAgentService.ClientIPMatches(LoginIp, Reported, with.CurrentIP));
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5", "", true)]
    [InlineData("203.0.113.5", "203.0.113.5", "198.51.100.7", true)]
    [InlineData("203.0.113.5", "198.51.100.7", "198.51.100.7", true)]
    [InlineData("203.0.113.5", "198.51.100.8", "198.51.100.7", false)]
    [InlineData("203.0.113.5", "198.51.100.7", "", false)]
    [InlineData("203.0.113.5", "198.51.100.7", null, false)]
    public void ClientIPMatches_IsTheLoginAddressOrTheGridsOwnPublicAddress(string login, string reported, string? external, bool expected)
    {
        Assert.Equal(expected, UserAgentService.ClientIPMatches(login, reported, external!));
    }
}
