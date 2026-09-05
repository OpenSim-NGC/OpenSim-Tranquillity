/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
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

using System;
using System.Collections.Generic;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// Replay defence for signature nonces (Design Brief §5). A nonce seen within the retention
/// window is a replay. Entries older than the window are pruned lazily on access, so the cache
/// stays bounded without a background timer.
/// </summary>
public sealed class NonceCache
{
    private readonly TimeSpan m_window;
    private readonly object m_lock = new();
    private readonly Dictionary<string, DateTime> m_seen = new();

    public NonceCache() : this(HGSignatureEnvelope.NonceWindow) { }

    public NonceCache(TimeSpan window)
    {
        m_window = window;
    }

    /// <summary>
    /// Register a nonce as seen at <paramref name="nowUtc"/>. Returns true if it is fresh,
    /// false if it is a replay within the window.
    /// </summary>
    public bool TryRegister(string nonce, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;

        lock (m_lock)
        {
            Prune(nowUtc);

            if (m_seen.ContainsKey(nonce))
                return false;

            m_seen[nonce] = nowUtc;
            return true;
        }
    }

    private void Prune(DateTime nowUtc)
    {
        if (m_seen.Count == 0)
            return;

        DateTime cutoff = nowUtc - m_window;
        List<string> expired = null;
        foreach (KeyValuePair<string, DateTime> kvp in m_seen)
        {
            if (kvp.Value < cutoff)
                (expired ??= new List<string>()).Add(kvp.Key);
        }

        if (expired != null)
        {
            foreach (string k in expired)
                m_seen.Remove(k);
        }
    }
}
