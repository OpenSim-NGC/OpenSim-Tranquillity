/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using System.Net.Http;
using Nini.Config;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// A host named in OutboundDisallowForUserScriptsExcept is looked up again after the refresh interval, so an exception
/// for a host whose address changes keeps working. The name lookup, the clock and the TCP connect are stand-ins, so
/// no test uses the network or waits in real time. Addresses are from the private and documentation ranges.
/// </summary>
/// <remarks>
/// No process-wide state is touched, so the class runs in parallel with the others.
/// </remarks>
public class OutboundExceptionRefreshTests
{
    private const string ExceptionHost = "ddns.example.org";
    private const string OldAddress = "10.0.0.5";
    private const string NewAddress = "10.0.0.6";

    // Script targets that sit on the exception host's old and new address. They are separate host names so that
    // looking a target up does not count as a refresh lookup of the exception host.
    private static readonly Uri AtOld = new("http://at-old.example.org:8003/");
    private static readonly Uri AtNew = new("http://at-new.example.org:8003/");

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset m_now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => m_now;
        public void Advance(TimeSpan by) => m_now += by;
    }

    private sealed class Dns
    {
        private readonly Dictionary<string, Func<IPAddress[]>> m_hosts = new();
        private readonly Dictionary<string, int> m_calls = new();

        /// <summary>When set, the lookup of the exception host waits for this before it answers.</summary>
        public TaskCompletionSource Gate;
        public TaskCompletionSource GateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dns Host(string host, params string[] addresses)
        {
            IPAddress[] parsed = addresses.Select(IPAddress.Parse).ToArray();
            lock (m_hosts)
                m_hosts[host] = () => parsed;
            return this;
        }

        public Dns Fails(string host)
        {
            lock (m_hosts)
                m_hosts[host] = () => throw new System.Net.Sockets.SocketException();
            return this;
        }

        public int Calls(string host)
        {
            lock (m_calls)
                return m_calls.GetValueOrDefault(host);
        }

        public async ValueTask<IPAddress[]> Resolve(string host, CancellationToken ct)
        {
            Func<IPAddress[]> answer;
            lock (m_calls)
                m_calls[host] = m_calls.GetValueOrDefault(host) + 1;
            lock (m_hosts)
                answer = m_hosts[host];

            if (host == ExceptionHost && Gate is not null && Calls(host) > 1)
            {
                GateEntered.TrySetResult();
                await Gate.Task.ConfigureAwait(false);
            }

            return answer();
        }
    }

    private static Dns Fresh()
        => new Dns()
            .Host(AtOld.Host, OldAddress)
            .Host(AtNew.Host, NewAddress)
            .Host(ExceptionHost, OldAddress);

    private static OutboundUrlFilter Filter(Dns dns, FakeClock clock, string except, int? refreshMinutes = null)
    {
        IConfigSource config = new IniConfigSource();
        IConfig network = config.AddConfig("Network");
        network.Set("OutboundDisallowForUserScripts", "10.0.0.0/8|127.0.0.0/8");
        network.Set("OutboundDisallowForUserScriptsExcept", except);
        if (refreshMinutes is not null)
            network.Set("OutboundDisallowForUserScriptsExceptRefreshMinutes", refreshMinutes.Value.ToString());
        return new OutboundUrlFilter("Test", config, dns.Resolve, clock);
    }

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public void AnAddressOrNetworkExceptionBehavesAsBefore()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, "10.0.0.5:8003|10.9.0.0/16");
        dns.Host("net.example.org", "10.9.1.1").Host("other.example.org", "10.0.0.6");

        Assert.True(filter.CheckAllowed(AtOld));
        Assert.True(filter.CheckAllowed(new Uri("http://net.example.org:1234/")));
        Assert.False(filter.CheckAllowed(AtNew));
        Assert.False(filter.CheckAllowed(new Uri("http://at-old.example.org:8004/")));

        clock.Advance(TimeSpan.FromDays(2));

        Assert.True(filter.CheckAllowed(AtOld));
        Assert.True(filter.CheckAllowed(new Uri("http://net.example.org:1234/")));
        Assert.False(filter.CheckAllowed(AtNew));
        Assert.Equal(0, dns.Calls(ExceptionHost));
    }

    [Fact]
    public void AHostExceptionAllowsTheAddressTheHostHadAtStart()
    {
        Dns dns = Fresh();
        OutboundUrlFilter filter = Filter(dns, new FakeClock(), ExceptionHost + ":8003");

        Assert.True(filter.CheckAllowed(AtOld));
        Assert.False(filter.CheckAllowed(AtNew));
        Assert.False(filter.CheckAllowed(new Uri("http://at-old.example.org:8004/")));
        Assert.Equal(1, dns.Calls(ExceptionHost));
    }

    [Fact]
    public void AfterTheAddressChangesAndTheIntervalPassesTheNewAddressIsAllowedAndTheOldOneIsNot()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003");

        dns.Host(ExceptionHost, NewAddress);

        // Before the interval is up the start-time answer still stands.
        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.True(filter.CheckAllowed(AtOld));
        Assert.False(filter.CheckAllowed(AtNew));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(filter.CheckAllowed(AtNew));
        Assert.False(filter.CheckAllowed(AtOld));
    }

    [Fact]
    public void AFailedLookupKeepsTheLastGoodAddresses()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003");

        dns.Fails(ExceptionHost);
        clock.Advance(Interval);
        Assert.True(filter.CheckAllowed(AtOld));
        Assert.False(filter.CheckAllowed(AtNew));

        // An answer with no IPv4 address is as good as no answer.
        dns.Host(ExceptionHost);
        clock.Advance(Interval);
        Assert.True(filter.CheckAllowed(AtOld));
        Assert.False(filter.CheckAllowed(AtNew));
        dns.Host(ExceptionHost, "2001:db8::1");
        clock.Advance(Interval);
        Assert.True(filter.CheckAllowed(AtOld));

        // The next good answer is taken up.
        dns.Host(ExceptionHost, NewAddress);
        clock.Advance(Interval);
        Assert.True(filter.CheckAllowed(AtNew));
        Assert.False(filter.CheckAllowed(AtOld));
    }

    [Fact]
    public void WithTheIntervalAtZeroTheStartTimeAddressesAreKeptForGood()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003", refreshMinutes: 0);

        dns.Host(ExceptionHost, NewAddress);
        clock.Advance(TimeSpan.FromDays(30));

        Assert.True(filter.CheckAllowed(AtOld));
        Assert.False(filter.CheckAllowed(AtNew));
        Assert.Equal(1, dns.Calls(ExceptionHost));
    }

    [Fact]
    public void TheIntervalIsReadFromTheConfig()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003", refreshMinutes: 5);

        dns.Host(ExceptionHost, NewAddress);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(filter.CheckAllowed(AtOld));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(filter.CheckAllowed(AtNew));
    }

    [Fact]
    public async Task TheConnectStepUsesTheRefreshedAddresses()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003");
        List<string> connected = new();

        // A connect that fails after it is let through, so an allowed address shows as a failed connection and a
        // refused one as the filter's own refusal.
        using HttpClient client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = filter.CreateConnectCallback((addr, port, ct) =>
            {
                connected.Add(addr.ToString());
                throw new IOException("stand-in connect");
            }),
        });

        async Task<bool> Refused(Uri uri)
        {
            HttpRequestException e = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(uri));
            return OutboundUrlFilterRefusedException.Unwrap(e) is OutboundUrlFilterRefusedException;
        }

        Assert.False(await Refused(AtOld));
        Assert.True(await Refused(AtNew));
        Assert.Equal(new[] { OldAddress }, connected);

        dns.Host(ExceptionHost, NewAddress);
        clock.Advance(Interval);

        Assert.False(await Refused(AtNew));
        Assert.True(await Refused(AtOld));
        Assert.Equal(new[] { OldAddress, NewAddress }, connected);
    }

    [Fact]
    public void TheAnswerIsCachedUntilTheIntervalPasses()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003");

        for (int i = 0; i < 40; i++)
        {
            Assert.True(filter.CheckAllowed(AtOld));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // Start, plus the one refresh that fell due at 30 minutes.
        Assert.Equal(2, dns.Calls(ExceptionHost));

        for (int i = 0; i < 20; i++)
            Assert.True(filter.CheckAllowed(AtOld));
        Assert.Equal(2, dns.Calls(ExceptionHost));

        clock.Advance(Interval);
        for (int i = 0; i < 20; i++)
            Assert.True(filter.CheckAllowed(AtOld));
        Assert.Equal(3, dns.Calls(ExceptionHost));
    }

    [Fact]
    public async Task TwoRequestsAtOnceDoNotBothLookTheHostUp()
    {
        Dns dns = Fresh();
        FakeClock clock = new();
        OutboundUrlFilter filter = Filter(dns, clock, ExceptionHost + ":8003");

        dns.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dns.Host(ExceptionHost, NewAddress);
        clock.Advance(Interval);

        // The first request finds the refresh due and waits in the lookup.
        Task<bool> first = Task.Run(() => filter.CheckAllowed(AtNew));
        await dns.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The second does not wait for it or repeat it: it is judged on the addresses held until now.
        Assert.True(filter.CheckAllowed(AtOld));
        Assert.Equal(2, dns.Calls(ExceptionHost));

        dns.Gate.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, dns.Calls(ExceptionHost));
        Assert.False(filter.CheckAllowed(AtOld));
    }
}
