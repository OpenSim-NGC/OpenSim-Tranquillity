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
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Nini.Config;

namespace OpenSim.Framework;

/// <summary>
/// The [ViewerAddress] settings: which address a viewer is given for a region.
/// With none of PublicAddress and PublicDnsServer set the section is disabled and every viewer is given
/// the region's own address, as before; LocalNetworks alone changes nothing.
/// </summary>
/// <remarks>
/// Keys: PublicAddress (an IPv4 literal, or a host name), PublicDnsServer ("ip" or "ip:port", queried
/// directly instead of the operating system resolver), PublicRefreshMinutes (default 30, 0 = never),
/// LocalNetworks ("CIDR" or "CIDR=address", comma separated). The addresses handed to viewers are IPv4:
/// the region's viewer protocol messages carry four address bytes.
/// </remarks>
public sealed class ViewerAddressConfig
{
    public const string SectionName = "ViewerAddress";

    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>The public address as an IPv4 literal, or null.</summary>
    public IPAddress PublicLiteral { get; private set; }

    /// <summary>The host name to resolve for the public address, or null to use the region's own host name.</summary>
    public string PublicHost { get; private set; }

    /// <summary>The DNS server queried directly, or null for the operating system resolver.</summary>
    public IPEndPoint PublicDnsServer { get; private set; }

    /// <summary>Interval between re-resolutions; zero disables them.</summary>
    public TimeSpan RefreshInterval { get; private set; } = DefaultRefreshInterval;

    public IReadOnlyList<LocalNetwork> LocalNetworks { get; private set; } = Array.Empty<LocalNetwork>();

    /// <summary>The address this machine's regions register with the grid for viewers on a local network, or null.</summary>
    public IPAddress LocalAddress { get; private set; }

    /// <summary>The names of the keys that were set to a non-empty value, in the order they are documented.</summary>
    public IReadOnlyList<string> SetKeys { get; private set; } = Array.Empty<string>();

    /// <summary>True when a public address is configured; otherwise every viewer gets the region's own address.</summary>
    public bool Enabled => PublicLiteral is not null || PublicHost is not null || PublicDnsServer is not null;

    public static ViewerAddressConfig Disabled { get; } = new();

    public static ViewerAddressConfig Create(string publicAddress, string publicDnsServer, int refreshMinutes,
        string localNetworks, ILogger log, string localAddress = null, bool refreshSet = false)
    {
        ViewerAddressConfig config = new()
        {
            RefreshInterval = refreshMinutes > 0 ? TimeSpan.FromMinutes(refreshMinutes) : TimeSpan.Zero,
        };

        publicAddress = publicAddress?.Trim();
        if (!string.IsNullOrEmpty(publicAddress))
        {
            if (IPAddress.TryParse(publicAddress, out IPAddress literal) && (literal.AddressFamily == AddressFamily.InterNetworkV6
                    || publicAddress.Split('.').Length == 4))
            {
                if (literal.AddressFamily == AddressFamily.InterNetwork && !literal.Equals(IPAddress.Any))
                    config.PublicLiteral = literal;
                else
                    log?.LogWarning("[VIEWER ADDRESS]: PublicAddress \"{0}\" ignored: viewers can only be given an IPv4 address", publicAddress);
            }
            else if (Uri.CheckHostName(publicAddress) == UriHostNameType.Dns)
                config.PublicHost = publicAddress;
            else
                log?.LogWarning("[VIEWER ADDRESS]: PublicAddress \"{0}\" ignored: not an IPv4 address or a host name", publicAddress);
        }

        publicDnsServer = publicDnsServer?.Trim();
        if (!string.IsNullOrEmpty(publicDnsServer))
        {
            if (TryParseDnsServer(publicDnsServer, out IPEndPoint server))
                config.PublicDnsServer = server;
            else
                log?.LogWarning("[VIEWER ADDRESS]: PublicDnsServer \"{0}\" ignored: not an IP address or ip:port", publicDnsServer);
        }

        config.LocalNetworks = LocalNetwork.ParseList(localNetworks, log);

        localAddress = localAddress?.Trim();
        if (!string.IsNullOrEmpty(localAddress))
        {
            config.LocalAddress = ParseLocalAddress(localAddress, log);
        }

        List<string> keys = new();
        if (!string.IsNullOrEmpty(publicAddress))
            keys.Add("PublicAddress");
        if (!string.IsNullOrEmpty(publicDnsServer))
            keys.Add("PublicDnsServer");
        if (refreshSet)
            keys.Add("PublicRefreshMinutes");
        if (!string.IsNullOrWhiteSpace(localNetworks))
            keys.Add("LocalNetworks");
        if (!string.IsNullOrEmpty(localAddress))
            keys.Add("LocalAddress");
        config.SetKeys = keys;
        return config;
    }

    /// <summary>True for a dotted IPv4 literal other than 0.0.0.0 (the form a region registers and a viewer can be given).</summary>
    public static bool TryParseLocalAddress(string value, out IPAddress address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        value = value.Trim();
        if (!IPAddress.TryParse(value, out IPAddress literal) || literal.AddressFamily != AddressFamily.InterNetwork
            || value.Split('.').Length != 4 || literal.Equals(IPAddress.Any))
            return false;
        address = literal;
        return true;
    }

    /// <summary>An IPv4 literal other than 0.0.0.0, or null (with a warning) for anything else.</summary>
    public static IPAddress ParseLocalAddress(string value, ILogger log)
    {
        if (TryParseLocalAddress(value, out IPAddress literal))
            return literal;

        log?.LogWarning("[VIEWER ADDRESS]: LocalAddress \"{0}\" ignored: viewers can only be given an IPv4 address", value);
        return null;
    }

    /// <summary>Reads the section; null (no section) gives the disabled configuration.</summary>
    public static ViewerAddressConfig FromConfig(IConfig section, ILogger log)
    {
        if (section is null)
            return Disabled;

        return Create(section.GetString("PublicAddress", string.Empty),
            section.GetString("PublicDnsServer", string.Empty),
            section.GetInt("PublicRefreshMinutes", (int)DefaultRefreshInterval.TotalMinutes),
            section.GetString("LocalNetworks", string.Empty),
            log,
            section.GetString("LocalAddress", string.Empty),
            !string.IsNullOrWhiteSpace(section.GetString("PublicRefreshMinutes", string.Empty)));
    }

    /// <summary>
    /// Sets the local address a region registers with the grid: the region's own LocalAddress setting, else the
    /// section's LocalAddress. Neither set (or not an IPv4 address) leaves the region without one.
    /// </summary>
    public static void ApplyRegionLocalAddress(IConfig section, RegionInfo region, ILogger log)
    {
        string value = region.GetSetting("LocalAddress");
        if (string.IsNullOrWhiteSpace(value))
            value = section?.GetString("LocalAddress", string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return;

        IPAddress parsed = ParseLocalAddress(value.Trim(), log);
        region.ViewerLocalAddress = parsed?.ToString();
    }

    /// <summary>"ip" (port 53) or "ip:port" / "[v6]:port".</summary>
    public static bool TryParseDnsServer(string value, out IPEndPoint endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (IPAddress.TryParse(value, out IPAddress bare))
        {
            endpoint = new IPEndPoint(bare, 53);
            return true;
        }

        if (IPEndPoint.TryParse(value, out IPEndPoint parsed) && parsed.Port != 0)
        {
            endpoint = parsed;
            return true;
        }

        return false;
    }
}
