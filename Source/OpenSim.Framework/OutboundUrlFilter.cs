/*
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
using System.Net.Sockets;
using System.Reflection;
using Nini.Config;
using Microsoft.Extensions.Logging;
using IPNetwork = LukeSkywalker.IPNetwork.IPNetwork;


namespace OpenSim.Framework;

/// <summary>
/// The outbound filter refused every address a host answered when a connection was made. SocketsHttpHandler wraps
/// what its connect callback throws in an <see cref="HttpRequestException"/> whose message is generic; this one
/// is that inner exception, so a caller can report the filter's own message.
/// </summary>
public sealed class OutboundUrlFilterRefusedException : HttpRequestException
{
    public OutboundUrlFilterRefusedException(string message) : base(message) { }

    /// <summary>The filter's refusal if <paramref name="e"/> wraps one, otherwise <paramref name="e"/>.</summary>
    public static HttpRequestException Unwrap(HttpRequestException e)
        => e.InnerException as OutboundUrlFilterRefusedException ?? e;
}

public class OutboundUrlFilter
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public string Name { get; private set; }

    private List<IPNetwork> m_blacklistNetworks;
    private List<IPEndPoint> m_blacklistEndPoints;

    private List<IPNetwork> m_blacklistExceptionNetworks;
    private List<IPEndPoint> m_blacklistExceptionEndPoints;

    /// <summary>
    /// Resolves a host name to its addresses. Dns by default; replaceable so the address handling can be tested
    /// without a name server.
    /// </summary>
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> m_resolver
        = async (host, ct) => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

    public OutboundUrlFilter(
        string name,
        List<IPNetwork> blacklistNetworks, List<IPEndPoint> blacklistEndPoints,
        List<IPNetwork> blacklistExceptionNetworks, List<IPEndPoint> blacklistExceptionEndPoints,
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolver = null)
    {
        Name = name;
        if (resolver is not null)
            m_resolver = resolver;

        m_blacklistNetworks = blacklistNetworks;
        m_blacklistEndPoints = blacklistEndPoints;
        m_blacklistExceptionNetworks = blacklistExceptionNetworks;
        m_blacklistExceptionEndPoints = blacklistExceptionEndPoints;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSim.Framework.OutboundUrlFilter"/> class.
    /// </summary>
    /// <param name="name">Name of the filter for logging purposes.</param>
    /// <param name="config">Filter configuration</param>
    /// <param name="resolver">Replaces the name lookup; Dns when null.</param>
    public OutboundUrlFilter(
        string name, IConfigSource config, Func<string, CancellationToken, ValueTask<IPAddress[]>> resolver = null)
    {
        Name = name;
        if (resolver is not null)
            m_resolver = resolver;

        string configBlacklist
            = "0.0.0.0/8|10.0.0.0/8|100.64.0.0/10|127.0.0.0/8|169.254.0.0/16|172.16.0.0/12|192.0.0.0/24|192.0.2.0/24|192.88.99.0/24|192.168.0.0/16|198.18.0.0/15|198.51.100.0/24|203.0.113.0/24|224.0.0.0/4|240.0.0.0/4|255.255.255.255/32";
        string configBlacklistExceptions = "";

        IConfig networkConfig = config.Configs["Network"];

        if (networkConfig != null)
        {
            configBlacklist = networkConfig.GetString("OutboundDisallowForUserScripts", configBlacklist);
            configBlacklistExceptions
                = networkConfig.GetString("OutboundDisallowForUserScriptsExcept", configBlacklistExceptions);
        }

        m_log.LogDebug(
            "[OUTBOUND URL FILTER]: OutboundDisallowForUserScripts for {0} is [{1}]", Name, configBlacklist);
        m_log.LogDebug(
            "[OUTBOUND URL FILTER]: OutboundDisallowForUserScriptsExcept for {0} is [{1}]", Name, configBlacklistExceptions);

        OutboundUrlFilter.ParseConfigList(
            configBlacklist, Name, out m_blacklistNetworks, out m_blacklistEndPoints);
        OutboundUrlFilter.ParseConfigList(
            configBlacklistExceptions, Name, out m_blacklistExceptionNetworks, out m_blacklistExceptionEndPoints);
    }

    private static void ParseConfigList(
        string fullConfigEntry, string filterName, out List<IPNetwork> networks, out List<IPEndPoint> endPoints)
    {
        // Parse blacklist
        string[] configBlacklistEntries
            = fullConfigEntry.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

        configBlacklistEntries = configBlacklistEntries.Select(e => e.Trim()).ToArray();

        networks = new List<IPNetwork>();
        endPoints = new List<IPEndPoint>();

        foreach (string configEntry in configBlacklistEntries)
        {
            if (configEntry.Contains("/"))
            {
                IPNetwork network;

                if (!IPNetwork.TryParse(configEntry, out network))
                {
                    m_log.LogError(
                        "[OUTBOUND URL FILTER]: Entry [{0}] is invalid network for {1}", configEntry, filterName);

                    continue;
                }

                networks.Add(network);
            }
            else
            {
                Uri configEntryUri;

                if (!Uri.TryCreate("http://" + configEntry, UriKind.Absolute, out configEntryUri))
                {
                    m_log.LogError(
                        "[OUTBOUND URL FILTER]: EndPoint entry [{0}] is invalid endpoint for {1}",
                        configEntry, filterName);

                    continue;
                }

                IPAddress[] addresses = Dns.GetHostAddresses(configEntryUri.Host);

                foreach (IPAddress addr in addresses)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        //                        m_log.LogDebug("[OUTBOUND URL FILTER]: Found address [{0}] in config", addr);

                        IPEndPoint configEntryEp = new IPEndPoint(addr, configEntryUri.Port);
                        endPoints.Add(configEntryEp);

                        //                        m_log.LogDebug("[OUTBOUND URL FILTER]: Added blacklist exception [{0}]", configEntryEp);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Determines if an url is in a list of networks and endpoints.
    /// </summary>
    /// <returns></returns>
    /// <param name="url">IP address</param>
    /// <param name="port"></param>
    /// <param name="networks">Networks.</param>
    /// <param name="endPoints">End points.</param>
    /// <param name="filterName">Filter name.</param>
    private static bool IsInNetwork(
        IPAddress addr, int port, List<IPNetwork> networks, List<IPEndPoint> endPoints, string filterName)
    {
        foreach (IPNetwork ipn in networks)
        {
//                                            m_log.LogDebug(
//                                                "[OUTBOUND URL FILTER]: Checking [{0}] against network [{1}]", addr, ipn);

            if (IPNetwork.Contains(ipn, addr))
            {
//                                                    m_log.LogDebug(
//                                                        "[OUTBOUND URL FILTER]: Found [{0}] in network [{1}]", addr, ipn);

                return true;
            }
        }

        //                    m_log.LogDebug("[OUTBOUND URL FILTER]: Found address [{0}]", addr);

        foreach (IPEndPoint ep in endPoints)
        {
//                m_log.LogDebug(
//                    "[OUTBOUND URL FILTER]: Checking [{0}:{1}] against endpoint [{2}]",
//                    addr, port, ep);

            if (addr.Equals(ep.Address) && port == ep.Port)
            {
//                    m_log.LogDebug(
//                        "[OUTBOUND URL FILTER]: Found [{0}:{1}] in endpoint [{2}]", addr, port, ep);

                return true;
            }
        }

//            m_log.LogDebug("[OUTBOUND URL FILTER]: Did not find [{0}:{1}] in list", addr, port);

        return false;
    }

    /// <summary>
    /// The IPv4 address an answer stands for, or null for an IPv6 answer. An IPv4-mapped IPv6 answer
    /// (::ffff:a.b.c.d) is the IPv4 address it carries, so it is judged against the same ranges.
    /// </summary>
    private static IPAddress AsIPv4(IPAddress addr)
    {
        if (addr.AddressFamily == AddressFamily.InterNetwork)
            return addr;
        if (addr.AddressFamily == AddressFamily.InterNetworkV6 && addr.IsIPv4MappedToIPv6)
            return addr.MapToIPv4();
        return null;
    }

    /// <summary>
    /// Is a connection to this address and port allowed? True when the address is IPv4 (or IPv4-mapped IPv6) and is
    /// either outside the blocked ranges and endpoints or matches an exception. Other IPv6 addresses are never
    /// allowed, as in <see cref="CheckAllowed"/>.
    /// </summary>
    private bool IsAddressAllowed(IPAddress addr, int port)
    {
        addr = AsIPv4(addr);
        if (addr is null)
            return false;

        if (!OutboundUrlFilter.IsInNetwork(addr, port, m_blacklistNetworks, m_blacklistEndPoints, Name))
            return true;

        return OutboundUrlFilter.IsInNetwork(addr, port, m_blacklistExceptionNetworks, m_blacklistExceptionEndPoints, Name);
    }

    /// <summary>
    /// Checks whether the given url is allowed by the filter. This looks the host up once and refuses early; it does
    /// not decide where a request connects. A request that must only reach allowed addresses also needs
    /// <see cref="CreateConnectCallback"/> on the handler that sends it.
    /// </summary>
    /// <returns></returns>
    public bool CheckAllowed(Uri url)
    {
        // Check that we are permitted to make calls to this endpoint.
        bool foundIpv4Address = false;

        IPAddress[] addresses = null;

        try
        {
            addresses = m_resolver(url.Host, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // If there is a DNS error, we can't stop the script!
            return true;
        }

        foreach (IPAddress addr in addresses)
        {
            if (AsIPv4(addr) is null)
                continue;

            foundIpv4Address = true;

            // Found at least one address in a blacklist and not a blacklist exception
            if (!IsAddressAllowed(addr, url.Port))
                return false;
        }

        // We do not know how to handle IPv6 securely yet.
        return foundIpv4Address;
    }

    /// <summary>
    /// Opens the TCP connection to an address. Replaceable so the connect step can be tested without a network.
    /// </summary>
    public delegate ValueTask<Stream> AddressConnector(IPAddress address, int port, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the TCP connection to a proxy, which is given by name and port. Replaceable so the proxy case can be
    /// tested without a network.
    /// </summary>
    public delegate ValueTask<Stream> ProxyConnector(DnsEndPoint endPoint, CancellationToken cancellationToken);

    private static async ValueTask<Stream> ConnectProxy(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Is this connection going to the request's own host and port, rather than to a proxy that will carry the
    /// request? The handler asks the callback to connect to the proxy when a proxy carries the request, and to the
    /// request's host when the request goes straight out (no proxy, or one that bypasses the host). A connection
    /// whose request is unknown, or whose endpoint is the request's own, is taken as going to its target.
    /// </summary>
    private static bool GoesToTarget(SocketsHttpConnectionContext context)
    {
        Uri target = context.InitialRequestMessage?.RequestUri;
        if (target is null || !target.IsAbsoluteUri)
            return true;

        DnsEndPoint endPoint = context.DnsEndPoint;
        return endPoint.Port == target.Port
            && (string.Equals(endPoint.Host, target.IdnHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(endPoint.Host, target.DnsSafeHost, StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask<Stream> ConnectSocket(IPAddress address, int port, CancellationToken cancellationToken)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes a handler connect only to addresses this filter allows, whenever the connection goes straight to the
    /// request's own host. A connection to a proxy is left alone: the proxy looks the target up itself, so there is
    /// no target address to judge, and the early <see cref="CheckAllowed"/> check is all that applies to a request
    /// the proxy carries. See <see cref="ConnectToAllowedAddress"/>.
    /// </summary>
    public void ApplyTo(SocketsHttpHandler handler)
    {
        handler.ConnectCallback = CreateConnectCallback();
    }

    /// <summary>
    /// A handler for a script's request, to be placed under an <see cref="OutboundUrlFilterRedirectHandler"/>. It
    /// uses <paramref name="proxy"/> (none if null), which chooses per request, so a redirect hop is routed by the
    /// proxy's own rules like the first request; and it has the connect step, which judges each connection that
    /// goes straight to its target.
    /// </summary>
    /// <param name="proxy">The proxy that would apply by default, such as <see cref="HttpClient.DefaultProxy"/>.</param>
    public SocketsHttpHandler CreateHandler(IWebProxy proxy)
    {
        SocketsHttpHandler handler = new() { AllowAutoRedirect = false, UseProxy = proxy is not null };
        if (proxy is not null)
            handler.Proxy = proxy;
        ApplyTo(handler);
        return handler;
    }

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that looks the host up when it connects and connects only
    /// to an address <see cref="IsAddressAllowed"/> accepts, so the address that was judged is the address used,
    /// on the first request and on every redirect. The handler still uses the host name for TLS and the Host header.
    /// A host with several answers is tried in order, skipping those that are refused. If none is allowed the
    /// connection fails with an <see cref="HttpRequestException"/>.
    /// </summary>
    /// <param name="connector">Opens the connection to one address; a socket connect by default.</param>
    /// <param name="proxyConnector">Opens the connection to a proxy; a socket connect by default.</param>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(
        AddressConnector connector = null, ProxyConnector proxyConnector = null)
    {
        return (context, cancellationToken) => ConnectToAllowedAddress(context, cancellationToken, connector, proxyConnector);
    }

    /// <summary>
    /// The connect step behind <see cref="CreateConnectCallback"/>, for a caller that picks its filter when the
    /// connection is made rather than when the handler is built.
    /// </summary>
    public async ValueTask<Stream> ConnectToAllowedAddress(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken,
        AddressConnector connector = null, ProxyConnector proxyConnector = null)
    {
        connector ??= ConnectSocket;

        DnsEndPoint endPoint = context.DnsEndPoint;
        if (!GoesToTarget(context))
            return await (proxyConnector ?? ConnectProxy)(endPoint, cancellationToken).ConfigureAwait(false);

        IPAddress[] addresses = await m_resolver(endPoint.Host, cancellationToken).ConfigureAwait(false);

        Exception lastError = null;
        bool anyAllowed = false;
        foreach (IPAddress addr in addresses)
        {
            if (!IsAddressAllowed(addr, endPoint.Port))
                continue;

            anyAllowed = true;
            try
            {
                return await connector(AsIPv4(addr), endPoint.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                lastError = e;
            }
        }

        if (!anyAllowed)
            throw new OutboundUrlFilterRefusedException(string.Format("Request to {0} disallowed by filter", endPoint.Host));

        throw new HttpRequestException("Connection failed", lastError);
    }
}
