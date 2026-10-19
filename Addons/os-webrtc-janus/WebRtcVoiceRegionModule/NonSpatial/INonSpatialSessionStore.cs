/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * THE GRID SEAM.
 *
 * The store implementation here runs in-process, but nothing in the engine may assume that one region module instance
 * owns a voice session. The seam is here and nowhere else: the engine never touches a dictionary,
 * never holds a voice session between calls, and never reads a field outside a store operation. Every
 * mutation it performs is a single call to Mutate(), which the store executes atomically.
 *
 * WHY MUTATE() AND NOT GET-THEN-WRITE. A get-then-write engine cannot be made grid-wide without a
 * distributed lock, because two regions would read the same seat count and both admit the last
 * seat. Handing the store a delegate keeps the read-modify-write on ONE side of the seam: the
 * in-process store takes its lock; a future service implementation sends the intent and applies it
 * server-side. Every idempotency and cap race is therefore decided in one place.
 *
 * WHERE A GRID-WIDE SERVICE GOES. WebRtcVoiceServerConnector is the existing region->service hop for
 * voice, so the grid-wide store is an INonSpatialSessionStore implementation that calls through it,
 * swapped in at module construction. The engine, the admission policies, the room-key derivation
 * and every test are untouched by that swap -- that is the point of the seam. What
 * that implementation must additionally carry, and this one does not, is written on each member of
 * the interface.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public interface INonSpatialSessionStore
    {
        /// <summary>
        /// Return the voice session for <paramref name="sessionId"/>, creating it from
        /// <paramref name="factory"/> only if absent. MUST be atomic: this is the idempotency point
        /// for a repeated start, and for two parties starting the same P2P session at once. A
        /// grid-wide implementation makes this a conditional insert, not a get followed by a put.
        /// </summary>
        NonSpatialVoiceSession StartOrGet(UUID sessionId, Func<NonSpatialVoiceSession> factory, out bool created);

        NonSpatialVoiceSession Get(UUID sessionId);

        /// <summary>
        /// Reverse lookup by media room key. A grid-wide implementation needs this indexed, not
        /// scanned, because a provision arrives carrying only the room key.
        /// </summary>
        NonSpatialVoiceSession GetByRoomKey(string roomKey);

        /// <summary>
        /// Apply <paramref name="op"/> to the voice session atomically and return its result. The engine's
        /// ONLY mutation path. <paramref name="op"/> must be pure apart from the voice session it is
        /// handed, and must not block: a remote store may run it under a server-side lock.
        /// Returns <paramref name="ifMissing"/> when the voice session does not exist.
        /// </summary>
        T Mutate<T>(UUID sessionId, Func<NonSpatialVoiceSession, T> op, T ifMissing = default);

        bool Remove(UUID sessionId);

        /// <summary>
        /// Every live session. In-process this is the whole set; grid-wide it is necessarily a
        /// snapshot and callers must not assume it is still true when they act on it. Used only by
        /// the sweep and by tests.
        /// </summary>
        IReadOnlyList<NonSpatialVoiceSession> All();
    }

    /// <summary>
    /// The in-process store. One lock, held across the whole of each operation, so
    /// StartOrGet and Mutate are the atomic units the interface promises.
    /// </summary>
    public sealed class InMemoryNonSpatialSessionStore : INonSpatialSessionStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<UUID, NonSpatialVoiceSession> _byId = new Dictionary<UUID, NonSpatialVoiceSession>();
        private readonly Dictionary<string, UUID> _byRoomKey = new Dictionary<string, UUID>(StringComparer.Ordinal);

        public NonSpatialVoiceSession StartOrGet(UUID sessionId, Func<NonSpatialVoiceSession> factory, out bool created)
        {
            lock (_lock)
            {
                if (_byId.TryGetValue(sessionId, out NonSpatialVoiceSession existing))
                {
                    created = false;
                    return existing;
                }
                NonSpatialVoiceSession made = factory();
                _byId[made.SessionId] = made;
                _byRoomKey[made.RoomKey] = made.SessionId;
                created = true;
                return made;
            }
        }

        public NonSpatialVoiceSession Get(UUID sessionId)
        {
            lock (_lock)
                return _byId.TryGetValue(sessionId, out NonSpatialVoiceSession s) ? s : null;
        }

        public NonSpatialVoiceSession GetByRoomKey(string roomKey)
        {
            if (string.IsNullOrEmpty(roomKey)) return null;
            lock (_lock)
                return _byRoomKey.TryGetValue(roomKey, out UUID id) && _byId.TryGetValue(id, out NonSpatialVoiceSession s) ? s : null;
        }

        public T Mutate<T>(UUID sessionId, Func<NonSpatialVoiceSession, T> op, T ifMissing = default)
        {
            lock (_lock)
                return _byId.TryGetValue(sessionId, out NonSpatialVoiceSession s) ? op(s) : ifMissing;
        }

        public bool Remove(UUID sessionId)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(sessionId, out NonSpatialVoiceSession s)) return false;
                _byId.Remove(sessionId);
                _byRoomKey.Remove(s.RoomKey);
                return true;
            }
        }

        public IReadOnlyList<NonSpatialVoiceSession> All()
        {
            lock (_lock)
                return new List<NonSpatialVoiceSession>(_byId.Values);
        }
    }
}
