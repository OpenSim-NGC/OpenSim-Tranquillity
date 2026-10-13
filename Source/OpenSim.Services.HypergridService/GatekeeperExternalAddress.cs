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
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Services.HypergridService;

/// <summary>
/// The public address of this grid's gatekeeper host, as <see cref="UserAgentService.VerifyClient"/>
/// uses it for its NAT fallback.
/// </summary>
/// <remarks>
/// With no DNS server configured the address is the operating system's answer for the gatekeeper host name,
/// refreshed every 30 minutes. [ViewerAddress] PublicDnsServer (or, as an alias, [UserAgentService]
/// ExternalIPResolver) names a DNS server to query directly instead, so a hosts-file or split-horizon entry for
/// the grid's own name on this machine cannot replace the public address. [ViewerAddress] PublicRefreshMinutes
/// (alias [UserAgentService] ExternalIPRefreshMinutes) sets the refresh period; 0 disables it. When both the
/// new and the old key are set the new one is used. Unlike the addresses given to viewers, an IPv6 answer is
/// accepted here: it is only compared with the address a simulator reports.
/// </remarks>
public sealed class GatekeeperExternalAddress : IDisposable
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly PublicAddressResolver m_resolver;
    private readonly string m_host;

    private GatekeeperExternalAddress(string host, PublicAddressResolver resolver, IPEndPoint dnsServer, TimeSpan refresh)
    {
        m_host = host;
        m_resolver = resolver;
        DnsServer = dnsServer;
        RefreshInterval = refresh;
    }

    /// <summary>The address as a string, or an empty string while no lookup has succeeded. A good answer is kept when a refresh fails.</summary>
    public string CurrentIP => string.IsNullOrWhiteSpace(m_host) ? string.Empty : m_resolver.Resolve(m_host)?.ToString() ?? string.Empty;

    /// <summary>The DNS server queried directly, or null when the operating system resolver is used.</summary>
    public IPEndPoint DnsServer { get; }

    /// <summary>Period of the automatic refresh; zero when disabled.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <param name="host">The gatekeeper's host name (or address literal).</param>
    /// <param name="userAgentSection">[UserAgentService], or null.</param>
    /// <param name="viewerAddressSection">[ViewerAddress], or null.</param>
    public static GatekeeperExternalAddress Create(string host, IConfig userAgentSection, IConfig viewerAddressSection,
        ILogger log, Func<string, IPAddress> osResolver = null, Func<string, IPEndPoint, IPAddress> directResolver = null)
    {
        string dnsText = viewerAddressSection?.GetString("PublicDnsServer", string.Empty);
        if (string.IsNullOrWhiteSpace(dnsText))
            dnsText = userAgentSection?.GetString("ExternalIPResolver", string.Empty);

        int minutes = (int)DefaultRefreshInterval.TotalMinutes;
        if (viewerAddressSection is not null && viewerAddressSection.Contains("PublicRefreshMinutes"))
            minutes = viewerAddressSection.GetInt("PublicRefreshMinutes", minutes);
        else if (userAgentSection is not null)
            minutes = userAgentSection.GetInt("ExternalIPRefreshMinutes", minutes);
        TimeSpan refresh = minutes > 0 ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero;

        IPEndPoint dnsServer = null;
        if (!string.IsNullOrWhiteSpace(dnsText))
        {
            if (ViewerAddressConfig.TryParseDnsServer(dnsText, out IPEndPoint parsed))
            {
                dnsServer = parsed;
                log?.LogInformation("[USER AGENT SERVICE]: resolving {0} via DNS server {1}, bypassing the OS resolver", host, dnsServer);
            }
            else
            {
                log?.LogWarning("[USER AGENT SERVICE]: DNS server \"{0}\" is not an IP address or ip:port; using the OS resolver", dnsText);
            }
        }

        PublicAddressResolver resolver = new(dnsServer, refresh, log, osResolver, directResolver, acceptIPv6: true);
        GatekeeperExternalAddress result = new(host, resolver, dnsServer, refresh);

        // The first lookup is made now, so a failure is reported at startup; it never throws.
        if (!string.IsNullOrWhiteSpace(host) && result.CurrentIP.Length == 0)
            log?.LogWarning("[USER AGENT SERVICE]: could not resolve gatekeeper host {0}; the NAT fallback in client verification is unavailable until a refresh succeeds", host);
        return result;
    }

    public void Dispose()
    {
        m_resolver.Dispose();
    }
}
