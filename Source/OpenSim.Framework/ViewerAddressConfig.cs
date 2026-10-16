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
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Nini.Config;

namespace OpenSim.Framework;

/// <summary>
/// The [ViewerAddress] settings: which address a viewer is given for a region.
/// With no Addresses entry the section is disabled and every viewer is given the region's own address,
/// as before. Otherwise the operator's ordered list decides: the first entry whose network contains the
/// viewer gives the address, and no match gives the region's own address.
/// </summary>
/// <remarks>
/// Keys: Addresses ("CIDR=address" or "CIDR", comma separated; the address is an IPv4 address or a host
/// name, looked up through the operating system resolver), RefreshMinutes (default 30, 0 = never) and
/// LocalAddress (the address a region registers for viewers on a bare "CIDR" network). Nothing is
/// detected: every address handed out is one the operator wrote. The addresses are IPv4: the region's
/// viewer protocol messages carry four address bytes.
/// </remarks>
public sealed class ViewerAddressConfig
{
    public const string SectionName = "ViewerAddress";

    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>Interval between re-resolutions of host names; zero disables them.</summary>
    public TimeSpan RefreshInterval { get; private set; } = DefaultRefreshInterval;

    /// <summary>The Addresses list, in the order the operator wrote it.</summary>
    public IReadOnlyList<LocalNetwork> LocalNetworks { get; private set; } = Array.Empty<LocalNetwork>();

    /// <summary>The host names in the list, each once (the first spelling of a name that repeats).</summary>
    public IReadOnlyList<string> Hosts { get; private set; } = Array.Empty<string>();

    /// <summary>The address this machine's regions register with the grid for viewers on a local network, or null.</summary>
    public IPAddress LocalAddress { get; private set; }

    /// <summary>The names of the keys that were set to a non-empty value, in the order they are documented.</summary>
    public IReadOnlyList<string> SetKeys { get; private set; } = Array.Empty<string>();

    /// <summary>True when the list has an entry; otherwise every viewer gets the region's own address.</summary>
    public bool Enabled => LocalNetworks.Count > 0;

    public static ViewerAddressConfig Disabled { get; } = new();

    public static ViewerAddressConfig Create(string addresses, int refreshMinutes, ILogger log,
        string localAddress = null, bool refreshSet = false)
    {
        ViewerAddressConfig config = new()
        {
            RefreshInterval = refreshMinutes > 0 ? TimeSpan.FromMinutes(refreshMinutes) : TimeSpan.Zero,
        };

        List<LocalNetwork> entries = LocalNetwork.ParseList(addresses, log);
        config.LocalNetworks = entries;
        config.Hosts = entries.Where(entry => entry.Host is not null).Select(entry => entry.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        localAddress = localAddress?.Trim();
        if (!string.IsNullOrEmpty(localAddress))
        {
            config.LocalAddress = ParseLocalAddress(localAddress, log);
        }

        List<string> keys = new();
        if (!string.IsNullOrWhiteSpace(addresses))
            keys.Add("Addresses");
        if (refreshSet)
            keys.Add("RefreshMinutes");
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

        return Create(section.GetString("Addresses", string.Empty),
            section.GetInt("RefreshMinutes", (int)DefaultRefreshInterval.TotalMinutes),
            log,
            section.GetString("LocalAddress", string.Empty),
            !string.IsNullOrWhiteSpace(section.GetString("RefreshMinutes", string.Empty)));
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
}
