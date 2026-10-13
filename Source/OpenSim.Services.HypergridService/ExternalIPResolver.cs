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
using System.Net.Sockets;
using System.Reflection;
using DnsClient;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Services.HypergridService;

/// <summary>
/// Resolves and periodically re-resolves the public IP address of this grid's gatekeeper host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UserAgentService"/> compares the client IP reported by a foreign simulator against
/// this address as its NAT fallback (viewer and grid behind the same NAT present the same public
/// address to the outside world). Two things go wrong when the address is obtained once, at
/// startup, through the OS resolver:
/// </para>
/// <list type="bullet">
/// <item>If the OS resolution of the grid's own public hostname is overridden to a LAN address
/// (hosts file, split-horizon DNS, container DNS), the fallback compares the foreign-reported
/// public address against a private one and every foreign arrival is rejected. The teleport
/// appears to succeed and the agent is then logged out on arrival, with no log line naming the
/// cause.</item>
/// <item>If the hostname is on dynamic DNS, an address change silently breaks hypergrid until the
/// service is restarted.</item>
/// </list>
/// <para>
/// This class addresses both: an optional DNS server can be queried directly (bypassing the OS
/// resolver), and the answer is refreshed on a timer. When no DNS server is configured the OS
/// path is <see cref="Util.GetHostFromDNS"/>, exactly as before. A failed lookup never throws
/// and never replaces a previously obtained address.
/// </para>
/// </remarks>
public sealed class ExternalIPResolver : IDisposable
{
    /// <summary>Resolves a hostname through the operating system resolver.</summary>
    public delegate IPAddress OsResolveFunc(string host);

    /// <summary>Resolves a hostname by querying the given DNS server directly.</summary>
    public delegate IPAddress DirectResolveFunc(string host, IPEndPoint dnsServer);

    public const int DefaultDnsPort = 53;
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_directQueryTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger m_log;
    private readonly OsResolveFunc m_osResolver;
    private readonly DirectResolveFunc m_directResolver;
    private readonly object m_refreshLock = new();

    private Timer m_timer;
    private LookupClient m_lookupClient;
    private volatile string m_current = string.Empty;
    private int m_disposed;

    /// <summary>The hostname being resolved.</summary>
    public string Host { get; }

    /// <summary>The DNS server queried directly, or null when the OS resolver is used.</summary>
    public IPEndPoint DnsServer { get; }

    /// <summary>True when lookups bypass the OS resolver and query <see cref="DnsServer"/>.</summary>
    public bool UsesDirectQuery => DnsServer is not null;

    /// <summary>Interval between automatic refreshes; <see cref="TimeSpan.Zero"/> when disabled.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>
    /// The most recently resolved address as a string, or an empty string if no lookup has
    /// succeeded yet. Never reverts to empty once set.
    /// </summary>
    public string CurrentIP => m_current;

    /// <summary>
    /// Creates a resolver, performs the initial lookup synchronously and starts the refresh
    /// timer. The constructor never throws because of a lookup failure.
    /// </summary>
    /// <param name="host">Hostname (or IP literal) of the gatekeeper.</param>
    /// <param name="dnsServer">
    /// Optional DNS server to query directly, as "ip" or "ip:port". Null or empty selects the
    /// OS resolver. An unparsable value is logged and the OS resolver is used.
    /// </param>
    /// <param name="refreshInterval">Automatic refresh period; zero or negative disables it.</param>
    /// <param name="log">Logger; defaults to the ambient <see cref="LoggerProvider"/>.</param>
    /// <param name="osResolver">OS resolution function; defaults to <see cref="Util.GetHostFromDNS"/>.</param>
    /// <param name="directResolver">Direct query function; defaults to a DnsClient lookup.</param>
    public ExternalIPResolver(
        string host,
        string dnsServer,
        TimeSpan refreshInterval,
        ILogger log = null,
        OsResolveFunc osResolver = null,
        DirectResolveFunc directResolver = null)
    {
        Host = host ?? string.Empty;
        m_log = log ?? LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        m_osResolver = osResolver ?? Util.GetHostFromDNS;
        m_directResolver = directResolver ?? QueryDnsServer;
        RefreshInterval = refreshInterval > TimeSpan.Zero ? refreshInterval : TimeSpan.Zero;

        if (!string.IsNullOrWhiteSpace(dnsServer))
        {
            if (TryParseDnsServer(dnsServer, out IPEndPoint endpoint))
            {
                DnsServer = endpoint;
                m_log.LogInformation("[EXTERNAL IP RESOLVER]: resolving {0} via DNS server {1}, bypassing the OS resolver",
                    Host, DnsServer);
            }
            else
            {
                m_log.LogWarning("[EXTERNAL IP RESOLVER]: ExternalIPResolver value \"{0}\" is not an IP address or ip:port; using the OS resolver",
                    dnsServer);
            }
        }

        Refresh();

        if (RefreshInterval > TimeSpan.Zero)
            m_timer = new Timer(OnTimer, null, RefreshInterval, RefreshInterval);
    }

    /// <summary>
    /// Performs one lookup now. Returns true if an address was obtained. On failure the previous
    /// value is kept, a warning is logged and false is returned. Never throws.
    /// </summary>
    public bool Refresh()
    {
        lock (m_refreshLock)
        {
            if (string.IsNullOrWhiteSpace(Host))
                return false;

            IPAddress ip;
            try
            {
                ip = UsesDirectQuery ? m_directResolver(Host, DnsServer) : m_osResolver(Host);
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "[EXTERNAL IP RESOLVER]: lookup of {0} failed; keeping previous value \"{1}\"",
                    Host, m_current);
                return false;
            }

            if (ip is null)
            {
                m_log.LogWarning("[EXTERNAL IP RESOLVER]: lookup of {0} returned no address; keeping previous value \"{1}\"",
                    Host, m_current);
                return false;
            }

            string resolved = ip.ToString();
            if (!string.Equals(resolved, m_current, StringComparison.Ordinal))
            {
                if (m_current.Length == 0)
                    m_log.LogInformation("[EXTERNAL IP RESOLVER]: {0} resolved to {1}", Host, resolved);
                else
                    m_log.LogInformation("[EXTERNAL IP RESOLVER]: {0} changed from {1} to {2}", Host, m_current, resolved);
                m_current = resolved;
            }
            return true;
        }
    }

    private void OnTimer(object state)
    {
        if (m_disposed != 0)
            return;
        try
        {
            Refresh();
        }
        catch (Exception e)
        {
            // Refresh() already catches lookup errors; this guards the timer thread against anything else.
            m_log.LogWarning(e, "[EXTERNAL IP RESOLVER]: unexpected error during scheduled refresh of {0}", Host);
        }
    }

    /// <summary>
    /// Parses "ip" or "ip:port" (IPv6 as "[addr]:port"). Port defaults to 53.
    /// </summary>
    public static bool TryParseDnsServer(string value, out IPEndPoint endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (IPAddress.TryParse(value, out IPAddress bare))
        {
            endpoint = new IPEndPoint(bare, DefaultDnsPort);
            return true;
        }

        if (IPEndPoint.TryParse(value, out IPEndPoint parsed) && parsed.Port != 0)
        {
            endpoint = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Default direct lookup: query the given server for A (then AAAA) records with DnsClient.
    /// Mirrors <see cref="Util.GetHostFromDNS"/> in preferring IPv4 and passing IP literals through.
    /// </summary>
    private IPAddress QueryDnsServer(string host, IPEndPoint dnsServer)
    {
        if (IPAddress.TryParse(host, out IPAddress literal))
        {
            if (literal.Equals(IPAddress.Any) || literal.Equals(IPAddress.IPv6Any))
                return null;
            return literal;
        }

        LookupClient client = m_lookupClient;
        if (client is null)
        {
            LookupClientOptions options = new(dnsServer)
            {
                UseCache = false,
                Timeout = s_directQueryTimeout,
                Retries = 2,
                ThrowDnsErrors = false,
                UseTcpFallback = true,
            };
            client = new LookupClient(options);
            m_lookupClient = client;
        }

        IDnsQueryResponse response = client.Query(host, QueryType.A);
        if (response.HasError)
        {
            m_log.LogWarning("[EXTERNAL IP RESOLVER]: DNS server {0} answered A query for {1} with error: {2}",
                dnsServer, host, response.ErrorMessage);
        }
        else
        {
            foreach (DnsClient.Protocol.ARecord record in response.Answers.ARecords())
            {
                if (record.Address.AddressFamily == AddressFamily.InterNetwork)
                    return record.Address;
            }
        }

        response = client.Query(host, QueryType.AAAA);
        if (response.HasError)
        {
            m_log.LogWarning("[EXTERNAL IP RESOLVER]: DNS server {0} answered AAAA query for {1} with error: {2}",
                dnsServer, host, response.ErrorMessage);
            return null;
        }

        foreach (DnsClient.Protocol.AaaaRecord record in response.Answers.AaaaRecords())
            return record.Address;

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        m_timer?.Dispose();
        m_timer = null;
    }
}
