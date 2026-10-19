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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// Resolves the host names of the [ViewerAddress] Addresses list to the IPv4 address a viewer should be
/// given, and keeps the answers fresh.
/// </summary>
/// <remarks>
/// Every name is looked up through the operating system resolver, so the hosts file and the system's
/// resolver settings decide the answer; no DNS query is sent by this class. When a <see cref="DnsAddressQuery"/>
/// is given (the [ViewerAddress] DnsServers key), names are asked of those DNS servers instead, and the
/// operating system answers only for a name no server has ever answered. An answer is kept until a
/// later lookup gives another one: a failed refresh never replaces a good address. A name that cannot be
/// resolved is not asked again for <see cref="FailureRetryInterval"/>, so a dead DNS server cannot stall
/// every viewer message. Only IPv4 answers are accepted (the viewer messages carry four address bytes).
/// </remarks>
public sealed class PublicAddressResolver : IDisposable
{
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public readonly object Lock = new();
        public IPAddress Good;
        public DateTimeOffset RetryAt;
    }

    private readonly ILogger m_log;
    private readonly TimeSpan m_refreshInterval;
    private readonly Func<string, IPAddress> m_osResolver;
    private readonly TimeProvider m_time;
    private readonly DnsAddressQuery m_dns;
    private readonly ConcurrentDictionary<string, Entry> m_entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object m_timerLock = new();
    private Timer m_timer;
    private int m_disposed;

    /// <param name="refreshInterval">Period of the automatic refresh; zero or less disables it. The timer starts with the first resolved name.</param>
    /// <param name="osResolver">Name lookup; defaults to <see cref="Util.GetHostFromDNS"/>, the operating system resolver.</param>
    /// <param name="dnsQuery">When set, names are looked up through these DNS servers; see the class remarks.</param>
    public PublicAddressResolver(TimeSpan refreshInterval, ILogger log,
        Func<string, IPAddress> osResolver = null,
        TimeProvider time = null,
        DnsAddressQuery dnsQuery = null)
    {
        m_refreshInterval = refreshInterval > TimeSpan.Zero ? refreshInterval : TimeSpan.Zero;
        m_log = log ?? LoggerProvider.CreateLogger<PublicAddressResolver>();
        m_osResolver = osResolver ?? Util.GetHostFromDNS;
        m_time = time ?? TimeProvider.System;
        m_dns = dnsQuery;
    }

    private static bool Acceptable(IPAddress ip)
    {
        return ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any);
    }

    /// <summary>The IPv4 address of the host, or null when it is not known (yet).</summary>
    public IPAddress Resolve(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        if (IPAddress.TryParse(host, out IPAddress literal))
            return Acceptable(literal) ? literal : null;

        Entry entry = m_entries.GetOrAdd(host, _ => new Entry());
        lock (entry.Lock)
        {
            if (entry.Good is not null)
                return entry.Good;
            if (m_time.GetUtcNow() < entry.RetryAt)
                return null;

            Lookup(host, entry);
            return entry.Good;
        }
    }

    /// <summary>Looks every known name up again. Returns how many lookups succeeded.</summary>
    public int Refresh()
    {
        int ok = 0;
        foreach (KeyValuePair<string, Entry> pair in m_entries)
        {
            lock (pair.Value.Lock)
            {
                if (Lookup(pair.Key, pair.Value))
                    ok++;
            }
        }
        return ok;
    }

    private bool Lookup(string host, Entry entry)
    {
        IPAddress ip = null;
        if (m_dns is not null)
        {
            try
            {
                ip = m_dns.Lookup(host);
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "[VIEWER ADDRESS]: DNS lookup of {0} failed", host);
            }

            if (ip is null && entry.Good is not null)
            {
                m_log.LogWarning("[VIEWER ADDRESS]: no DNS server answered for {0}; keeping previous value \"{1}\"", host, entry.Good);
                entry.RetryAt = m_time.GetUtcNow() + FailureRetryInterval;
                return false;
            }
            if (ip is null)
                m_log.LogWarning("[VIEWER ADDRESS]: no DNS server answered for {0}; using the operating system's answer", host);
        }

        if (ip is null)
        {
            try
            {
                ip = m_osResolver(host);
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "[VIEWER ADDRESS]: lookup of {0} failed; keeping previous value \"{1}\"", host, entry.Good);
            }
        }

        if (ip is null || !Acceptable(ip))
        {
            if (ip is null)
                m_log.LogWarning("[VIEWER ADDRESS]: no IPv4 address found for {0}; keeping previous value \"{1}\"", host, entry.Good);
            else
                m_log.LogWarning("[VIEWER ADDRESS]: {0} resolved to {1}, which cannot be given to a viewer; keeping previous value \"{2}\"",
                    host, ip, entry.Good);
            entry.RetryAt = m_time.GetUtcNow() + FailureRetryInterval;
            return false;
        }

        if (!ip.Equals(entry.Good))
        {
            if (entry.Good is null)
                m_log.LogInformation("[VIEWER ADDRESS]: {0} resolved to {1}", host, ip);
            else
                m_log.LogInformation("[VIEWER ADDRESS]: {0} changed from {1} to {2}", host, entry.Good, ip);
            entry.Good = ip;
        }

        StartTimer();
        return true;
    }

    private void StartTimer()
    {
        if (m_refreshInterval <= TimeSpan.Zero)
            return;
        lock (m_timerLock)
        {
            if (m_timer is not null || m_disposed != 0)
                return;
            m_timer = new Timer(OnTimer, null, m_refreshInterval, m_refreshInterval);
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
            m_log.LogWarning(e, "[VIEWER ADDRESS]: unexpected error during scheduled refresh");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        lock (m_timerLock)
        {
            m_timer?.Dispose();
            m_timer = null;
        }
    }
}
