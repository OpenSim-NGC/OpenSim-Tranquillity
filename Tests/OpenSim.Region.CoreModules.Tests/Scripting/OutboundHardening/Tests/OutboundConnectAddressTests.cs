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
using System.Net.Http;
using System.Text;
using Nini.Config;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// An outbound request connects only to an address the filter approved: the host is looked up when the connection
/// is made, not only when the URL is checked. The name lookup and the TCP connect are stand-ins, so no test uses
/// the network. Addresses are from the documentation ranges, and the blocked ranges are the test's own.
/// </summary>
/// <remarks>
/// No process-wide state is touched, so the class runs in parallel with the others.
/// </remarks>
public class OutboundConnectAddressTests
{
    private const string Public1 = "198.51.100.7";
    private const string Public2 = "198.51.100.8";
    private const string Blocked = "10.0.0.5";

    private const string Ok =
        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
    private const string RedirectToB =
        "HTTP/1.1 302 Found\r\nLocation: http://b.example.org/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    /// <summary>One connection: replays a canned response and keeps what the client wrote.</summary>
    private sealed class CannedStream : Stream
    {
        private readonly MemoryStream m_response;
        private readonly MemoryStream m_written = new();

        public CannedStream(string response) => m_response = new MemoryStream(Encoding.ASCII.GetBytes(response));

        public string Written => Encoding.ASCII.GetString(m_written.ToArray());

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => m_response.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => m_response.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(m_response.Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => m_written.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => m_written.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            m_written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Stand-in name server and network: answers by call number and records each connect.</summary>
    private sealed class Network
    {
        private readonly Dictionary<string, IPAddress[][]> m_answers = new();
        private readonly Dictionary<string, int> m_calls = new();
        private readonly Dictionary<string, string> m_responses = new();

        public List<string> Connected { get; } = new();
        public List<CannedStream> Streams { get; } = new();

        /// <summary>The nth lookup of the host gets the nth answer; the last answer repeats.</summary>
        public Network Host(string host, params string[][] answers)
        {
            m_answers[host] = answers.Select(a => a.Select(IPAddress.Parse).ToArray()).ToArray();
            return this;
        }

        /// <summary>What a connection to this address replies.</summary>
        public Network Serves(string address, string response)
        {
            m_responses[address] = response;
            return this;
        }

        public ValueTask<IPAddress[]> Resolve(string host, CancellationToken ct)
        {
            lock (m_calls)
            {
                m_calls.TryGetValue(host, out int n);
                m_calls[host] = n + 1;
                IPAddress[][] answers = m_answers[host];
                return new ValueTask<IPAddress[]>(answers[Math.Min(n, answers.Length - 1)]);
            }
        }

        public ValueTask<Stream> Connect(IPAddress address, int port, CancellationToken ct)
        {
            string key = address.ToString();
            lock (Connected)
                Connected.Add(key);
            CannedStream stream = new(m_responses.GetValueOrDefault(key, Ok));
            lock (Streams)
                Streams.Add(stream);
            return new ValueTask<Stream>(stream);
        }
    }

    private static OutboundUrlFilter Filter(Network net, string except = "")
    {
        IConfigSource config = new IniConfigSource();
        IConfig network = config.AddConfig("Network");
        network.Set("OutboundDisallowForUserScripts", "10.0.0.0/8|127.0.0.0/8");
        network.Set("OutboundDisallowForUserScriptsExcept", except);
        return new OutboundUrlFilter("Test", config, net.Resolve);
    }

    /// <summary>The handler chain a script request uses: redirect-following filter over a connect-checked handler.</summary>
    private static HttpClient Client(OutboundUrlFilter filter, Network net)
    {
        SocketsHttpHandler inner = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = filter.CreateConnectCallback(net.Connect),
        };
        return new HttpClient(new OutboundUrlFilterRedirectHandler(filter, inner, 10)) { Timeout = TimeSpan.FromSeconds(30) };
    }

    [Fact]
    public async Task AHostThatAnswersAPublicAddressAtTheCheckAndABlockedOneAtTheConnectIsRefused()
    {
        Network net = new Network().Host("rebind.example.org", new[] { Public1 }, new[] { Blocked });
        using HttpClient client = Client(Filter(net), net);

        HttpRequestException e = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync("http://rebind.example.org/"));

        Assert.IsType<OutboundUrlFilterRefusedException>(e);
        Assert.Equal("Request to rebind.example.org disallowed by filter", e.Message);
        Assert.Empty(net.Connected);
    }

    [Fact]
    public async Task ARedirectToAHostThatTurnsBlockedAtTheConnectIsRefused()
    {
        Network net = new Network()
            .Host("a.example.org", new[] { Public1 })
            .Host("b.example.org", new[] { Public2 }, new[] { Blocked })
            .Serves(Public1, RedirectToB);
        using HttpClient client = Client(Filter(net), net);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://a.example.org/"));

        Assert.Equal(new[] { Public1 }, net.Connected);
    }

    [Fact]
    public async Task ARedirectToABlockedAddressIsRefused()
    {
        Network net = new Network()
            .Host("a.example.org", new[] { Public1 })
            .Host("b.example.org", new[] { Blocked })
            .Serves(Public1, RedirectToB);
        using HttpClient client = Client(Filter(net), net);

        HttpRequestException e = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://a.example.org/"));

        Assert.StartsWith(OutboundUrlFilterRedirectHandler.RedirectBlockedPrefix, e.Message);
        Assert.Equal(new[] { Public1 }, net.Connected);
    }

    [Fact]
    public async Task AnIPv4MappedIPv6FormOfABlockedAddressIsRefused()
    {
        Network net = new Network().Host("mapped.example.org", new[] { "::ffff:10.0.0.5" });
        OutboundUrlFilter filter = Filter(net);
        using HttpClient client = Client(filter, net);

        Assert.False(filter.CheckAllowed(new Uri("http://mapped.example.org/")));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://mapped.example.org/"));

        // Through the connect step alone, with the address check passing: the lookup answers a public address
        // first and the mapped form second.
        Network rebind = new Network().Host("m2.example.org", new[] { Public1 }, new[] { "::ffff:127.0.0.1" });
        using HttpClient client2 = Client(Filter(rebind), rebind);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client2.GetAsync("http://m2.example.org/"));

        Assert.Empty(net.Connected);
        Assert.Empty(rebind.Connected);
    }

    [Fact]
    public async Task AnIPv4MappedIPv6FormOfAnAllowedAddressConnectsToTheIPv4Address()
    {
        Network net = new Network().Host("mapped.example.org", new[] { "::ffff:" + Public1 });
        using HttpClient client = Client(Filter(net), net);

        using HttpResponseMessage response = await client.GetAsync("http://mapped.example.org/");

        Assert.Equal(new[] { Public1 }, net.Connected);
    }

    [Theory]
    [InlineData(Blocked, Public1)]
    [InlineData(Public1, Blocked)]
    public async Task AHostWithABlockedAndAnAllowedAddressConnectsOnlyToTheAllowedOne(string first, string second)
    {
        Network net = new Network().Host("multi.example.org", new[] { first, second }, new[] { first, second });
        // The connect step alone: the early check would refuse this host outright, so it is not in the chain.
        OutboundUrlFilter filter = Filter(net);
        using HttpClient client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = filter.CreateConnectCallback(net.Connect),
        });

        using HttpResponseMessage response = await client.GetAsync("http://multi.example.org/");

        Assert.Equal(new[] { Public1 }, net.Connected);
    }

    [Fact]
    public async Task AConfiguredExceptionIsStillHonoured()
    {
        Network net = new Network()
            .Host("net.example.org", new[] { "10.1.2.3" })
            .Host("other.example.org", new[] { "10.2.0.1" })
            .Host("port.example.org", new[] { "10.0.0.9" });
        OutboundUrlFilter filter = Filter(net, "10.1.0.0/16|10.0.0.9:8080");
        using HttpClient client = Client(filter, net);

        using (HttpResponseMessage r1 = await client.GetAsync("http://net.example.org/"))
            Assert.True(r1.IsSuccessStatusCode);
        using (HttpResponseMessage r2 = await client.GetAsync("http://port.example.org:8080/"))
            Assert.True(r2.IsSuccessStatusCode);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://other.example.org/"));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://port.example.org/"));

        Assert.Equal(new[] { "10.1.2.3", "10.0.0.9" }, net.Connected);
    }

    [Fact]
    public async Task AnOrdinaryAllowedRequestIsUnchanged()
    {
        Network net = new Network().Host("a.example.org", new[] { Public1 });
        using HttpClient client = Client(Filter(net), net);

        using HttpResponseMessage response = await client.GetAsync("http://a.example.org/path");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { Public1 }, net.Connected);
        Assert.Contains("Host: a.example.org", net.Streams[0].Written);
    }

    [Fact]
    public async Task AnAllowedRedirectIsFollowedAndEachHopConnectsToItsCheckedAddress()
    {
        Network net = new Network()
            .Host("a.example.org", new[] { Public1 })
            .Host("b.example.org", new[] { Public2 })
            .Serves(Public1, RedirectToB);
        using HttpClient client = Client(Filter(net), net);

        using HttpResponseMessage response = await client.GetAsync("http://a.example.org/");

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { Public1, Public2 }, net.Connected);
        Assert.Contains("Host: b.example.org", net.Streams[1].Written);
    }

    [Fact]
    public void ADnsFailureAtTheCheckStillLetsTheRequestThrough()
    {
        OutboundUrlFilter filter = new("Test", new IniConfigSource(),
            (host, ct) => throw new System.Net.Sockets.SocketException());

        Assert.True(filter.CheckAllowed(new Uri("http://down.example.org/")));
    }

    [Fact]
    public void ApplyToInstallsTheConnectStep()
    {
        SocketsHttpHandler handler = new();

        Filter(new Network()).ApplyTo(handler);

        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task AnIPv4MappedAddressMatchingAnExceptionIsAllowedAndOneMatchingNoneIsRefused()
    {
        Network net = new Network()
            .Host("ok.example.org", new[] { "::ffff:10.1.2.3" })
            .Host("no.example.org", new[] { "::ffff:10.2.0.1" })
            .Host("late-ok.example.org", new[] { Public1 }, new[] { "::ffff:10.1.2.3" })
            .Host("late-no.example.org", new[] { Public1 }, new[] { "::ffff:10.2.0.1" });
        OutboundUrlFilter filter = Filter(net, "10.1.0.0/16");
        using HttpClient client = Client(filter, net);

        // At the early check and at the connect step alike.
        using (HttpResponseMessage r1 = await client.GetAsync("http://ok.example.org/"))
            Assert.True(r1.IsSuccessStatusCode);
        using (HttpResponseMessage r2 = await client.GetAsync("http://late-ok.example.org/"))
            Assert.True(r2.IsSuccessStatusCode);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://no.example.org/"));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://late-no.example.org/"));

        Assert.True(filter.CheckAllowed(new Uri("http://ok.example.org/")));
        Assert.False(filter.CheckAllowed(new Uri("http://no.example.org/")));
        Assert.Equal(new[] { "10.1.2.3", "10.1.2.3" }, net.Connected);
    }

    [Fact]
    public async Task TheLookupHonoursTheHandlersConnectTimeout()
    {
        int lookups = 0;
        bool cancelled = false;
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Network").Set("OutboundDisallowForUserScripts", "10.0.0.0/8|127.0.0.0/8");
        OutboundUrlFilter filter = new("Test", config, async (host, ct) =>
        {
            if (Interlocked.Increment(ref lookups) > 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            }
            return new[] { IPAddress.Parse(Public1) };
        });
        Network net = new();
        SocketsHttpHandler inner = new()
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            ConnectCallback = filter.CreateConnectCallback(net.Connect),
        };
        using HttpClient client = new(new OutboundUrlFilterRedirectHandler(filter, inner, 10)) { Timeout = TimeSpan.FromSeconds(30) };

        // The first lookup is the early check; the second is the connect step, which never answers.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://slow.example.org/"));

        Assert.True(cancelled, "the lookup was not cancelled by the handler's connect timeout");
        Assert.Empty(net.Connected);
    }

    /// <summary>A proxy that carries every URL except those of the bypassed host.</summary>
    private sealed class FakeProxy : IWebProxy
    {
        private readonly string m_bypassedHost;
        public FakeProxy(string bypassedHost = null) => m_bypassedHost = bypassedHost;
        public ICredentials Credentials { get; set; }
        public Uri GetProxy(Uri destination) => new("http://proxy.example.org:3128/");
        public bool IsBypassed(Uri host) => host.Host == m_bypassedHost;
    }

    private const string RedirectToDirect =
        "HTTP/1.1 302 Found\r\nLocation: http://direct.example.org/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    /// <summary>
    /// The chain a script request uses with a proxy that carries every URL but direct.example.org: the proxy
    /// connection is a stand-in recorded in <paramref name="proxyEndPoints"/>, and answers with <paramref name="proxyReply"/>.
    /// </summary>
    private static HttpClient ProxiedClient(OutboundUrlFilter filter, Network net, string proxyReply, List<string> proxyEndPoints)
    {
        SocketsHttpHandler inner = new()
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            Proxy = new FakeProxy("direct.example.org"),
            ConnectCallback = filter.CreateConnectCallback(net.Connect, (endPoint, ct) =>
            {
                proxyEndPoints.Add(endPoint.Host + ":" + endPoint.Port);
                return new ValueTask<Stream>(new CannedStream(proxyReply));
            }),
        };
        return new HttpClient(new OutboundUrlFilterRedirectHandler(filter, inner, 10)) { Timeout = TimeSpan.FromSeconds(30) };
    }

    [Fact]
    public async Task ARequestTheProxyCarriesReachesTheProxyWithOnlyTheEarlyCheck()
    {
        Network net = new Network().Host("a.example.org", new[] { Public1 });
        List<string> proxyEndPoints = new();
        using HttpClient client = ProxiedClient(Filter(net), net, Ok, proxyEndPoints);

        using HttpResponseMessage response = await client.GetAsync("http://a.example.org/path");

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "proxy.example.org:3128" }, proxyEndPoints);
        Assert.Empty(net.Connected);
    }

    [Fact]
    public async Task ARedirectHopTheProxyBypassesIsJudgedAtTheConnect()
    {
        // The first request is carried by the proxy, which redirects to a host it bypasses. That host turns blocked
        // at the connect.
        Network net = new Network()
            .Host("a.example.org", new[] { Public1 })
            .Host("direct.example.org", new[] { Public2 }, new[] { Blocked });
        List<string> proxyEndPoints = new();
        using HttpClient client = ProxiedClient(Filter(net), net, RedirectToDirect, proxyEndPoints);

        HttpRequestException e = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("http://a.example.org/"));

        Assert.IsType<OutboundUrlFilterRefusedException>(e);
        Assert.Equal(new[] { "proxy.example.org:3128" }, proxyEndPoints);
        Assert.Empty(net.Connected);
    }

    [Fact]
    public async Task ARedirectHopTheProxyBypassesConnectsStraightToItsCheckedAddress()
    {
        Network net = new Network()
            .Host("a.example.org", new[] { Public1 })
            .Host("direct.example.org", new[] { Public2 });
        List<string> proxyEndPoints = new();
        using HttpClient client = ProxiedClient(Filter(net), net, RedirectToDirect, proxyEndPoints);

        using HttpResponseMessage response = await client.GetAsync("http://a.example.org/");

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "proxy.example.org:3128" }, proxyEndPoints);
        Assert.Equal(new[] { Public2 }, net.Connected);
    }

    [Fact]
    public void CreateHandlerUsesTheGivenProxyAndHasTheConnectStep()
    {
        OutboundUrlFilter filter = Filter(new Network());
        FakeProxy proxy = new();

        SocketsHttpHandler none = filter.CreateHandler(null);
        SocketsHttpHandler proxied = filter.CreateHandler(proxy);

        Assert.False(none.UseProxy);
        Assert.True(proxied.UseProxy);
        Assert.Same(proxy, proxied.Proxy);
        Assert.NotNull(none.ConnectCallback);
        Assert.NotNull(proxied.ConnectCallback);
        Assert.False(none.AllowAutoRedirect);
        Assert.False(proxied.AllowAutoRedirect);
    }

    [Fact]
    public async Task AHandlerFromCreateHandlerRefusesAHostThatTurnsBlockedAtTheConnect()
    {
        // The refused answer is blocked, so nothing is connected and no network is needed.
        Network net = new Network().Host("rebind.example.org", new[] { Public1 }, new[] { Blocked });
        OutboundUrlFilter filter = Filter(net);
        Uri url = new("http://rebind.example.org/");
        using HttpClient client = new(new OutboundUrlFilterRedirectHandler(filter, filter.CreateHandler(null), 10));

        HttpRequestException e = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.IsType<OutboundUrlFilterRefusedException>(e);
    }
}
