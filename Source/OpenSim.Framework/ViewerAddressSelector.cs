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

namespace OpenSim.Framework;

/// <summary>
/// Chooses the address a viewer is given for a region, by the viewer's own source address.
/// </summary>
/// <remarks>
/// An empty list returns the region's own endpoint for every viewer, exactly as before. Otherwise the
/// first entry of the operator's list whose network contains the viewer's source address decides: the
/// entry's address (an IPv4 address, or a host name looked up through the operating system resolver),
/// or, for an entry without one, the region's registered local address, else the region's own address.
/// No match gives the region's own address. The port is never changed. While a host name is not
/// resolved the viewer is given the region's own address. Server-to-server address use is not affected:
/// only callers that build a message for a viewer ask this class.
/// </remarks>
public sealed class ViewerAddressSelector : IDisposable
{
    private readonly ViewerAddressConfig m_config;
    private readonly PublicAddressResolver m_resolver;
    private readonly bool m_ownsResolver;

    public ViewerAddressSelector(ViewerAddressConfig config, PublicAddressResolver resolver)
        : this(config, resolver, false)
    {
    }

    private ViewerAddressSelector(ViewerAddressConfig config, PublicAddressResolver resolver, bool ownsResolver)
    {
        m_config = config ?? ViewerAddressConfig.Disabled;
        m_resolver = resolver;
        m_ownsResolver = ownsResolver;
    }

    /// <summary>A selector that always returns the region's own endpoint.</summary>
    public static ViewerAddressSelector Disabled { get; } = new(ViewerAddressConfig.Disabled, null, false);

    /// <summary>Builds the selector and, when the configuration needs one, its resolver (disposed with the selector).</summary>
    /// <param name="dnsPort">The port of the DNS servers in the configuration's DnsServers list.</param>
    /// <param name="dnsTimeout">How long each DNS server is given to answer; defaults to <see cref="DnsAddressQuery.DefaultTimeout"/>.</param>
    /// <param name="osResolver">Replaces the operating system resolver (tests).</param>
    public static ViewerAddressSelector Create(ViewerAddressConfig config, ILogger log, int dnsPort = 53,
        TimeSpan? dnsTimeout = null, Func<string, IPAddress> osResolver = null)
    {
        if (config is null || !config.Enabled)
            return Disabled;

        DnsAddressQuery dns = config.DnsServers.Count > 0
            ? new DnsAddressQuery(config.DnsServers.Select(server => new IPEndPoint(server, dnsPort)).ToList(),
                dnsTimeout ?? DnsAddressQuery.DefaultTimeout)
            : null;
        PublicAddressResolver resolver = config.Hosts.Count > 0
            ? new PublicAddressResolver(config.RefreshInterval, log, osResolver, dnsQuery: dns)
            : null;
        return new ViewerAddressSelector(config, resolver, true);
    }

    public bool Enabled => m_config.Enabled;

    /// <summary>
    /// True when the list holds an address of this grid (an entry with an address or a host name), so that the
    /// answer depends on whether the destination is a region of another grid.
    /// </summary>
    public bool HoldsGridAddress => m_config.LocalNetworks.Any(network => network.Address is not null || network.Host is not null);

    /// <param name="port">The port to hand out (the region's port).</param>
    /// <param name="localEndPoint">The region's own endpoint as resolved by the operating system; may be null.</param>
    /// <param name="viewerSource">The viewer's source address; null when unknown.</param>
    /// <param name="foreignDestination">True for a region of another grid (a hypergrid destination).</param>
    /// <param name="registeredLocal">The local address the region registered with the grid, or null when it did not.</param>
    public IPEndPoint ForViewer(int port, IPEndPoint localEndPoint, IPAddress viewerSource,
        bool foreignDestination = false, IPAddress registeredLocal = null)
    {
        if (!m_config.Enabled || viewerSource is null)
            return localEndPoint;

        IPAddress viewer = viewerSource.IsIPv4MappedToIPv6 ? viewerSource.MapToIPv4() : viewerSource;

        foreach (LocalNetwork network in m_config.LocalNetworks)
        {
            if (!network.Contains(viewer))
                continue;

            // The first entry that contains the viewer decides. Its address, and a region's registered local
            // address, are addresses of this grid's own regions: never given for a region of another grid.
            if (foreignDestination)
                return localEndPoint;

            IPAddress address = network.Address;
            if (address is null && network.Host is not null)
                address = m_resolver?.Resolve(network.Host);
            else if (address is null)
                address = registeredLocal;

            return address is null ? localEndPoint : new IPEndPoint(address, port);
        }

        return localEndPoint;
    }

    /// <summary>Looks a host name up through this selector's resolver; null when it is not known or there is no resolver.</summary>
    public IPAddress LookupHost(string host)
    {
        return m_resolver?.Resolve(host);
    }

/// <summary>Looks every host name up again now; returns how many lookups succeeded.</summary>
    public int Refresh() => m_resolver?.Refresh() ?? 0;

    public void Dispose()
    {
        if (m_ownsResolver)
            m_resolver?.Dispose();
    }
}
