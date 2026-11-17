/*
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
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nini.Config;
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
/// This class addresses both: the answer is refreshed on a timer, and the host to resolve can be
/// named by the operator (<c>[UserAgentService] PublicAddress</c>). With no DNS servers given, every
/// lookup goes through the operating system resolver (<see cref="Util.GetHostFromDNS"/>), so the hosts
/// file and the system's resolver configuration decide the answer. When DNS servers are given
/// (<c>[UserAgentService] DnsServers</c>), the name is asked of those servers first through
/// <see cref="DnsAddressQuery"/>, so a hosts-file entry that maps the name to a LAN address does not decide
/// the answer; the operating system answers only when no server answers and no address has been obtained
/// yet, the same rule <see cref="PublicAddressResolver"/> follows for <c>[ViewerAddress] DnsServers</c>.
/// An IP address given as the host is used as it is, with no lookup. A failed lookup never throws and
/// never replaces a previously obtained address.
/// </para>
/// </remarks>
public sealed class ExternalIPResolver : IDisposable
{
    /// <summary>Resolves a hostname through the operating system resolver.</summary>
    public delegate IPAddress OsResolveFunc(string host);

    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);

    private const string LogTag = "[EXTERNAL IP RESOLVER]";

    private readonly ILogger m_log;
    private readonly OsResolveFunc m_osResolver;
    private readonly Func<string, IPAddress> m_dnsLookup;
    private readonly string m_dnsServerNames;
    private readonly object m_refreshLock = new();

    private Timer m_timer;
    private volatile string m_current = string.Empty;
    private int m_disposed;

    /// <summary>The hostname (or IP literal) being resolved.</summary>
    public string Host { get; }

    /// <summary>Interval between automatic refreshes; <see cref="TimeSpan.Zero"/> when disabled.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>The DNS servers the host is asked of first, in order; empty means the operating system resolver only.</summary>
    public IReadOnlyList<IPAddress> DnsServers { get; }

    /// <summary>
    /// The most recently resolved address as a string, or an empty string if no lookup has
    /// succeeded yet. Never reverts to empty once set.
    /// </summary>
    public string CurrentIP => m_current;

    /// <summary>
    /// Creates a resolver, performs the initial lookup synchronously and starts the refresh
    /// timer. The constructor never throws because of a lookup failure.
    /// </summary>
    /// <param name="host">Hostname (or IP literal) to resolve.</param>
    /// <param name="refreshInterval">Automatic refresh period; zero or negative disables it.</param>
    /// <param name="log">Logger; defaults to the ambient <see cref="LoggerProvider"/>.</param>
    /// <param name="osResolver">OS resolution function; defaults to <see cref="Util.GetHostFromDNS"/>.</param>
    /// <param name="dnsServers">DNS servers to ask first; null or empty means the operating system resolver only.</param>
    /// <param name="dnsLookup">Replaces the query to <paramref name="dnsServers"/> (tests); defaults to
    /// <see cref="DnsAddressQuery.Lookup"/> on UDP port 53. Not used when no servers are given.</param>
    public ExternalIPResolver(
        string host,
        TimeSpan refreshInterval,
        ILogger log = null,
        OsResolveFunc osResolver = null,
        IReadOnlyList<IPAddress> dnsServers = null,
        Func<string, IPAddress> dnsLookup = null)
    {
        Host = host ?? string.Empty;
        m_log = log ?? LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        m_osResolver = osResolver ?? Util.GetHostFromDNS;
        RefreshInterval = refreshInterval > TimeSpan.Zero ? refreshInterval : TimeSpan.Zero;

        DnsServers = dnsServers?.Where(server => server is not null).ToList() ?? new List<IPAddress>();
        if (DnsServers.Count > 0)
        {
            m_dnsServerNames = string.Join(", ", DnsServers);
            m_dnsLookup = dnsLookup ?? new DnsAddressQuery(
                DnsServers.Select(server => new IPEndPoint(server, DnsAddressQuery.DefaultPort)).ToList(),
                DnsAddressQuery.DefaultTimeout).Lookup;
        }

        Refresh();

        if (RefreshInterval > TimeSpan.Zero)
            m_timer = new Timer(OnTimer, null, RefreshInterval, RefreshInterval);
    }

    /// <summary>
    /// Creates the resolver for a gatekeeper from its <c>[UserAgentService]</c> section:
    /// <c>PublicAddress</c> (an IP address or host name; empty means <paramref name="gatekeeperHost"/>),
    /// <c>DnsServers</c> (DNS server addresses to ask first, the same form as <c>[ViewerAddress] DnsServers</c>;
    /// empty means the operating system resolver only) and <c>ExternalIPRefreshMinutes</c> (default 30,
    /// zero or negative never repeats). Logs a warning when the section still sets the
    /// <c>ExternalIPResolver</c> key, which is not read, and when the address obtained at startup is
    /// private, loopback or link-local.
    /// </summary>
    public static ExternalIPResolver FromConfig(
        IConfig serverConfig,
        string gatekeeperHost,
        ILogger log = null,
        OsResolveFunc osResolver = null,
        Func<string, IPAddress> dnsLookup = null)
    {
        log ??= LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        string publicAddress = serverConfig?.GetString("PublicAddress", string.Empty)?.Trim() ?? string.Empty;
        int refreshMinutes = serverConfig?.GetInt("ExternalIPRefreshMinutes", 30) ?? 30;
        List<IPAddress> dnsServers = DnsAddressQuery.ParseServerList(
            serverConfig?.GetString("DnsServers", string.Empty), log, LogTag);

        // Older releases read ExternalIPResolver (a DNS server address) for this lookup. Nothing reads it
        // now, so a grid that still sets it would otherwise lose that behaviour without a word.
        if (!string.IsNullOrWhiteSpace(serverConfig?.GetString("ExternalIPResolver", string.Empty)))
            log.LogWarning("{0}: [UserAgentService] ExternalIPResolver is set but is no longer read. " +
                "Use PublicAddress (the grid's public host name or IP address) and DnsServers (DNS servers to ask " +
                "for that name instead of the operating system resolver) in its place", LogTag);

        ExternalIPResolver resolver = new(
            publicAddress.Length > 0 ? publicAddress : gatekeeperHost,
            TimeSpan.FromMinutes(refreshMinutes),
            log, osResolver, dnsServers, dnsLookup);

        if (IPAddress.TryParse(resolver.CurrentIP, out IPAddress settled) && IsPrivateLoopbackOrLinkLocal(settled))
            log.LogWarning("{0}: {1} resolved to {2}, a private, loopback or link-local address. Client verification's " +
                "NAT fallback compares a visitor's public address with this one, so users logged in from that network " +
                "will be refused when they go to a grid outside it. Set [UserAgentService] PublicAddress to the grid's " +
                "public host name or IP address, and DnsServers if this machine resolves that name to a private " +
                "address (a hosts-file entry or router loopback)", LogTag, resolver.Host, settled);

        return resolver;
    }

    /// <summary>
    /// Creates the resolver for a gatekeeper from its <c>[UserAgentService]</c> section.
    /// </summary>
    public static ExternalIPResolver FromConfig(
        IConfigSource config,
        string gatekeeperHost,
        ILogger log = null,
        OsResolveFunc osResolver = null,
        Func<string, IPAddress> dnsLookup = null)
        => FromConfig(config?.Configs["UserAgentService"], gatekeeperHost, log, osResolver, dnsLookup);

    /// <summary>
    /// True for an address outside the public internet: IPv4 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
    /// (RFC 1918), 127.0.0.0/8 and 169.254.0.0/16 (RFC 3927); IPv6 ::1, fe80::/10, fec0::/10 and
    /// fc00::/7 (RFC 4193); and those IPv4 ranges written as IPv4-mapped IPv6 addresses.
    /// </summary>
    public static bool IsPrivateLoopbackOrLinkLocal(IPAddress ip)
    {
        if (ip is null)
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
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

            // An address given as the host is the answer; no resolver is asked.
            if (IPAddress.TryParse(Host, out IPAddress literal))
            {
                if (literal.Equals(IPAddress.Any) || literal.Equals(IPAddress.IPv6Any))
                {
                    m_log.LogWarning("{0}: {1} is not an address this grid can be reached at; keeping previous value \"{2}\"",
                        LogTag, Host, m_current);
                    return false;
                }
                Store(literal.ToString(), null);
                return true;
            }

            IPAddress ip = null;
            if (m_dnsLookup is not null)
            {
                try
                {
                    ip = m_dnsLookup(Host);
                }
                catch (Exception e)
                {
                    m_log.LogWarning(e, "{0}: DNS lookup of {1} through {2} failed", LogTag, Host, m_dnsServerNames);
                }

                if (ip is not null)
                {
                    Store(ip.ToString(), m_dnsServerNames);
                    return true;
                }

                if (m_current.Length > 0)
                {
                    m_log.LogWarning("{0}: no DNS server answered for {1} ({2}); keeping previous value \"{3}\"",
                        LogTag, Host, m_dnsServerNames, m_current);
                    return false;
                }

                m_log.LogWarning("{0}: no DNS server answered for {1} ({2}); using the operating system's answer",
                    LogTag, Host, m_dnsServerNames);
            }

            try
            {
                ip = m_osResolver(Host);
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{0}: lookup of {1} failed; keeping previous value \"{2}\"",
                    LogTag, Host, m_current);
                return false;
            }

            if (ip is null)
            {
                m_log.LogWarning("{0}: lookup of {1} returned no address; keeping previous value \"{2}\"",
                    LogTag, Host, m_current);
                return false;
            }

            Store(ip.ToString(), null);
            return true;
        }
    }

    /// <summary>Records a new answer and logs it when it differs; <paramref name="via"/> names the DNS servers it came from.</summary>
    private void Store(string resolved, string via)
    {
        if (string.Equals(resolved, m_current, StringComparison.Ordinal))
            return;

        string source = via is null ? string.Empty : " via DNS servers " + via;
        if (m_current.Length == 0)
            m_log.LogInformation("{0}: {1} resolved to {2}{3}", LogTag, Host, resolved, source);
        else
            m_log.LogInformation("{0}: {1} changed from {2} to {3}{4}", LogTag, Host, m_current, resolved, source);
        m_current = resolved;
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
            m_log.LogWarning(e, "{0}: unexpected error during scheduled refresh of {1}", LogTag, Host);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        m_timer?.Dispose();
        m_timer = null;
    }
}
