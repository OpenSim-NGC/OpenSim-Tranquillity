/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A per-(agent, region) negative cache for refused voice provisions.
 *
 * The LL/Firestorm viewer treats every non-2xx provision answer except 401/409 as SESSION_EXIT and
 * re-provisions immediately, with no backoff (llvoicewebrtc.cpp :2913-2927, :3182-3201) - and a 200 whose
 * body is a failure map lands in the same place. Live 2026-09-13 that was ~2 Hz for as long as a provision
 * was refused or the mixer was down. The sim cannot stop the retries, so it makes them cheap and quiet:
 * after a refusal, identical attempts within the window get the SAME answer without re-running the checks
 * or touching Janus, and the caller logs at most one line per (agent, region) per window.
 *
 * Pure and clock-injectable so it unit-tests; the region module and the service module each hold one.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

public sealed class ProvisionRefusalCache
{
    /// <summary>[WebRtcVoice] RefusalCacheSeconds default; 0 disables the cache.</summary>
    public const int DefaultSeconds = 5;

    private const int PruneThreshold = 256;

    private sealed class Entry
    {
        public long ExpiresMs;
        public object Answer;
        public int Suppressed;
    }

    private readonly Dictionary<(UUID Agent, UUID Region), Entry> _entries = new();
    private readonly Func<long> _clockMs;

    public TimeSpan Window { get; }
    public bool Enabled => Window > TimeSpan.Zero;

    public ProvisionRefusalCache(TimeSpan pWindow, Func<long> pClockMs = null)
    {
        Window = pWindow < TimeSpan.Zero ? TimeSpan.Zero : pWindow;
        _clockMs = pClockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>A refusal for (agent, region) still inside its window: count this retry as suppressed and
    /// return the cached answer (which may be null). False when nothing is cached or the window has passed.</summary>
    public bool TryGetRefusal(UUID pAgent, UUID pRegion, out object pAnswer)
    {
        pAnswer = null;
        if (!Enabled)
            return false;
        lock (_entries)
        {
            if (_entries.TryGetValue((pAgent, pRegion), out Entry e) && _clockMs() < e.ExpiresMs)
            {
                e.Suppressed++;
                pAnswer = e.Answer;
                return true;
            }
        }
        return false;
    }

    /// <summary>Cache a freshly evaluated refusal for one window. Returns how many retries the PREVIOUS window
    /// for this key suppressed (0 if there was none) - the caller's cue for its one "refused again" line.</summary>
    public int RecordRefusal(UUID pAgent, UUID pRegion, object pAnswer)
    {
        if (!Enabled)
            return 0;
        long now = _clockMs();
        lock (_entries)
        {
            int previous = 0;
            if (_entries.TryGetValue((pAgent, pRegion), out Entry old))
                previous = old.Suppressed;
            else if (_entries.Count >= PruneThreshold)
                Prune(now);
            _entries[(pAgent, pRegion)] = new Entry { ExpiresMs = now + (long)Window.TotalMilliseconds, Answer = pAnswer };
            return previous;
        }
    }

    /// <summary>A successful provision or a logout: forget (agent, region). Returns the suppressed count.</summary>
    public int Clear(UUID pAgent, UUID pRegion)
    {
        lock (_entries)
        {
            if (_entries.Remove((pAgent, pRegion), out Entry e))
                return e.Suppressed;
        }
        return 0;
    }

    public int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    // Lock held. Drops entries whose window has passed (agents that stopped retrying).
    private void Prune(long pNowMs)
    {
        List<(UUID, UUID)> expired = new List<(UUID, UUID)>();
        foreach (KeyValuePair<(UUID Agent, UUID Region), Entry> kvp in _entries)
        {
            if (kvp.Value.ExpiresMs <= pNowMs)
                expired.Add(kvp.Key);
        }
        foreach ((UUID, UUID) key in expired)
            _entries.Remove(key);
    }
}
