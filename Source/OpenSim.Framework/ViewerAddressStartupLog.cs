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
/// Writes one line at server start saying that the [ViewerAddress] section is in use.
/// </summary>
public sealed class ViewerAddressStartupLog
{
    /// <summary>The instance a server process uses, so that several regions in one process log once.</summary>
    public static ViewerAddressStartupLog Shared { get; } = new();

    private int m_reported;

    /// <summary>
    /// Logs, once per instance and only when at least one [ViewerAddress] key is set, that the section is active:
    /// which keys are set, how many local networks are listed, and the result of the first lookup of the public
    /// address. The lookup runs in the returned task, so a slow DNS server does not delay server start.
    /// </summary>
    /// <param name="fallbackHost">The host name to look up when no PublicAddress host name is set (the server's own name).</param>
    public Task ReportOnce(ILogger log, ViewerAddressConfig config, ViewerAddressSelector selector, string fallbackHost)
    {
        if (log is null || config is null || config.SetKeys.Count == 0)
            return Task.CompletedTask;
        if (Interlocked.Exchange(ref m_reported, 1) != 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            string lookup;
            try
            {
                lookup = DescribeLookup(config, selector, fallbackHost);
            }
            catch (Exception e)
            {
                lookup = $"first public lookup failed: {e.Message}";
            }

            log.LogInformation("[VIEWER ADDRESS]: [{0}] section is active; keys set: {1}; local networks: {2}; {3}",
                ViewerAddressConfig.SectionName, string.Join(", ", config.SetKeys), config.LocalNetworks.Count, lookup);
        });
    }

    private static string DescribeLookup(ViewerAddressConfig config, ViewerAddressSelector selector, string fallbackHost)
    {
        if (config.PublicLiteral is not null)
            return $"public address {config.PublicLiteral} is used as given (no lookup)";
        if (!config.Enabled)
            return "no public address is configured (no lookup), so every viewer is given the region's own address";

        string host = config.PublicHost ?? fallbackHost;
        if (string.IsNullOrWhiteSpace(host))
            return "no host name to look up for the public address";

        IPAddress found = null;
        try
        {
            found = selector?.LookupPublic(host);
        }
        catch (Exception)
        {
            // reported as a failed lookup below
        }

        return found is null
            ? $"first public lookup of {host} failed"
            : $"first public lookup of {host} succeeded: {found}";
    }
}
