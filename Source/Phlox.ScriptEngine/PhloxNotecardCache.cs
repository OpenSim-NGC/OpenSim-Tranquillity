/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Ported from LSL_Api.cs
 * Massively adapted by InWorldz, LLC in March 2011
 */

/*
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * Two small pieces of Halcyon the anti-abuse slowdowns need.
 *
 * PhloxNotecardCache is Halcyon's NotecardCache (InWorldz.Phlox.Engine/LSLSystemAPI.cs:18541-18727): parsed notecards
 * by asset id, so repeated reads of one notecard are answered from memory instead of an asset fetch each. As Halcyon's:
 * an entry is refreshed by every read of it, IsCached does not look at age, and CacheCheck - run on every read that
 * missed the cache - drops the entries not read for more than NOTECARD_CACHE_TIMEOUT (60 s). Unlike Halcyon's (one
 * static cache per process), there is one per region's engine. It holds the lines Phlox already parsed a notecard into
 * without a cache (LSLSystemAPI.StripNotecardHeader, then Card.SplitLines), so a cached read answers exactly what a fetch does.
 * Asset ids are content addresses: a notecard saved with new text gets a new asset id, so an entry never goes stale.
 *
 * MovingIntegerAverage is Halcyon's (OpenSim/Framework MovingIntegerAverage): the last n values, averaged with integer
 * division, 0 when empty.
 *
 * Adapted 2026 by Legion Builds
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace Phlox.ScriptEngine
{
    public sealed class PhloxNotecardCache
    {
        /// <summary>Halcyon's NOTECARD_CACHE_TIMEOUT: 60 seconds since the last read, in ms of the engine's clock.</summary>
        public const ulong NOTECARD_CACHE_TIMEOUT_MS = 60_000;

        /// <summary>One notecard's text as the read calls see it.</summary>
        public sealed class Card
        {
            private readonly string[] m_lines;

            /// <param name="body">the notecard's text, header already stripped</param>
            public Card(string body)
            {
                m_lines = SplitLines(body);
            }

            /// <summary>
            /// The notecard's lines, as every notecard reader answers them: the text split at '\n', where a '\n' ends
            /// the line before it and does not start another, so a text that ends with '\n' has no empty line after
            /// it, and an empty text has no lines. The SL wiki does not say; this is YEngine's reader
            /// (SLUtil.ParseNotecardToArray), whose loop ends at the end of the text.
            /// </summary>
            public static string[] SplitLines(string body)
            {
                if (body.Length == 0) return System.Array.Empty<string>();
                string[] lines = body.Split('\n');
                return body[body.Length - 1] == '\n' ? lines[..^1] : lines;
            }

            /// <summary>What llGetNumberOfNotecardLines answers.</summary>
            public int LineCount => m_lines.Length;

            /// <summary>Line <paramref name="n"/> without its '\r', or null past either end.</summary>
            public string Line(int n) => n < 0 || n >= m_lines.Length ? null : m_lines[n].TrimEnd('\r');
        }

        private sealed class Entry
        {
            public Card Card;
            public ulong LastRef;
        }

        private readonly Dictionary<UUID, Entry> m_cards = new();

        private static ulong Now => InWorldz.Phlox.Util.Clock.Now;

        /// <summary>Halcyon Cache: the first text stored for an asset id stays.</summary>
        public void Cache(UUID assetID, Card card)
        {
            lock (m_cards)
            {
                if (m_cards.ContainsKey(assetID)) return;
                m_cards[assetID] = new Entry { Card = card, LastRef = Now };
            }
        }

        public bool IsCached(UUID assetID)
        {
            lock (m_cards) return m_cards.ContainsKey(assetID);
        }

        /// <summary>The cached text, refreshing its last use (Halcyon GetLines / GetLine set lastRef).</summary>
        public bool TryGet(UUID assetID, out Card card)
        {
            lock (m_cards)
            {
                if (!m_cards.TryGetValue(assetID, out Entry e)) { card = null; return false; }
                e.LastRef = Now;
                card = e.Card;
                return true;
            }
        }

        /// <summary>Halcyon CacheCheck: drop every entry whose last read is more than 60 s ago.</summary>
        public void CacheCheck()
        {
            ulong now = Now;
            lock (m_cards)
            {
                List<UUID> expired = null;
                foreach (var kvp in m_cards)
                    if (kvp.Value.LastRef + NOTECARD_CACHE_TIMEOUT_MS < now)
                        (expired ??= new List<UUID>()).Add(kvp.Key);
                if (expired == null) return;
                foreach (UUID id in expired) m_cards.Remove(id);
            }
        }

        public int Count
        {
            get { lock (m_cards) return m_cards.Count; }
        }
    }

    internal sealed class MovingIntegerAverage
    {
        private readonly Queue<long> m_values;
        private readonly int m_sampleSize;

        public MovingIntegerAverage(int sampleSize)
        {
            m_sampleSize = sampleSize;
            m_values = new Queue<long>(sampleSize + 1);
        }

        public void AddValue(long value)
        {
            lock (m_values)
            {
                m_values.Enqueue(value);
                if (m_values.Count > m_sampleSize) m_values.Dequeue();
            }
        }

        public int CalculateAverage()
        {
            int total = 0;
            lock (m_values)
            {
                if (m_values.Count == 0) return 0;
                foreach (long value in m_values) total += (int)value;
                return total / m_values.Count;
            }
        }
    }
}
