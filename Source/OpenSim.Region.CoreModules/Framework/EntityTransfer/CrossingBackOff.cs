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
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.Framework.EntityTransfer;

/// <summary>
/// Remembers that an avatar's crossing into a neighbouring region failed, so that the next crossing of the
/// same avatar into the same region fails at once instead of waiting for the neighbour again.
/// </summary>
public sealed class CrossingBackOff
{
    /// <summary>How long a failed crossing blocks further crossings of that avatar into that region.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(UUID Agent, ulong Region), DateTimeOffset> m_until = new();
    private readonly TimeProvider m_time;

    public CrossingBackOff(TimeProvider time = null)
    {
        m_time = time ?? TimeProvider.System;
    }

    public void Mark(UUID agent, ulong region)
    {
        DateTimeOffset now = m_time.GetUtcNow();
        foreach (KeyValuePair<(UUID Agent, ulong Region), DateTimeOffset> entry in m_until)
        {
            if (entry.Value <= now)
                m_until.TryRemove(entry);
        }
        m_until[(agent, region)] = now + Period;
    }

    public bool IsBlocked(UUID agent, ulong region)
    {
        return m_until.TryGetValue((agent, region), out DateTimeOffset until) && m_time.GetUtcNow() < until;
    }

    public void Clear(UUID agent, ulong region)
    {
        m_until.TryRemove((agent, region), out _);
    }
}
