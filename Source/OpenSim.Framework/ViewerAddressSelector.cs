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
/// Unset configuration returns the region's own endpoint for every viewer, exactly as before. When a
/// public address is configured, a viewer whose source address is in a listed local network keeps the
/// region's own address (or the address listed for that network) and every other viewer is given the
/// public address. The port is never changed. While the public address is not known the viewer is
/// given the region's own address. Server-to-server address use is not affected: only callers that
/// build a message for a viewer ask this class.
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
    public static ViewerAddressSelector Create(ViewerAddressConfig config, ILogger log)
    {
        if (config is null || !config.Enabled)
            return Disabled;

        PublicAddressResolver resolver = config.PublicLiteral is null
            ? new PublicAddressResolver(config.PublicDnsServer, config.RefreshInterval, log)
            : null;
        return new ViewerAddressSelector(config, resolver, true);
    }

    public bool Enabled => m_config.Enabled;

    /// <summary>
    /// True when the configuration holds an address of this grid (PublicAddress, or a network's own address), so
    /// that the answer depends on whether the destination is a region of another grid.
    /// </summary>
    public bool HoldsGridAddress => m_config.PublicLiteral is not null || m_config.PublicHost is not null
        || m_config.LocalNetworks.Any(network => network.Address is not null);

    /// <param name="regionHostName">The region's ExternalHostName, looked up when no public host name is configured.</param>
    /// <param name="port">The port to hand out (the region's port).</param>
    /// <param name="localEndPoint">The region's own endpoint as resolved by the operating system; may be null.</param>
    /// <param name="viewerSource">The viewer's source address; null when unknown.</param>
    /// <param name="foreignDestination">True for a region of another grid (a hypergrid destination).</param>
    /// <param name="registeredLocal">The local address the region registered with the grid, or null when it did not.</param>
    public IPEndPoint ForViewer(string regionHostName, int port, IPEndPoint localEndPoint, IPAddress viewerSource,
        bool foreignDestination = false, IPAddress registeredLocal = null)
    {
        if (!m_config.Enabled || viewerSource is null)
            return localEndPoint;

        IPAddress viewer = viewerSource.IsIPv4MappedToIPv6 ? viewerSource.MapToIPv4() : viewerSource;

        foreach (LocalNetwork network in m_config.LocalNetworks)
        {
            if (!network.Contains(viewer))
                continue;
            // A network's address, and a region's registered local address, are addresses of this grid's own regions.
            if (foreignDestination)
                return localEndPoint;
            // A network listed with its own address keeps it; a bare network gives the address the region
            // registered, else the region's own address as the operating system resolves it.
            IPAddress local = network.Address ?? registeredLocal;
            return local is null ? localEndPoint : new IPEndPoint(local, port);
        }

        // PublicAddress names this grid's own public address, so it is never given for a region of
        // another grid. Without it the region's own host name is looked up, which is right for any grid.
        if (foreignDestination && (m_config.PublicLiteral is not null || m_config.PublicHost is not null))
            return localEndPoint;

        IPAddress pub = m_config.PublicLiteral ?? m_resolver?.Resolve(m_config.PublicHost ?? regionHostName);
        return pub is null ? localEndPoint : new IPEndPoint(pub, port);
    }

    /// <summary>Looks a host up through this selector's resolver; null when it is not known or there is no resolver.</summary>
    public IPAddress LookupPublic(string host)
    {
        return m_resolver?.Resolve(host);
    }

    public void Dispose()
    {
        if (m_ownsResolver)
            m_resolver?.Dispose();
    }
}
