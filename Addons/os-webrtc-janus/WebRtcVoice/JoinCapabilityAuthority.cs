/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The arming state the sim believed current for a room, so a capability minted
 * before an epoch change does not survive one.
 *
 * Why a process-wide table rather than a reference: the authority (VisAuthority) lives in the region module with
 * the visibility feeder, while the join that carries the capability is performed by the Janus service in another
 * assembly, and neither references the other. The visibility service PUBLISHES each room's (epoch, generation) as
 * it arms, and the join seam RESOLVES it. An unpublished room resolves to (NoEpoch, 0), which is exactly what a
 * sim with arming off mints and what a mixer with no authority expects.
 *
 * This holds no secrets: an epoch and a generation are both reported by the mixer's admin API already.
 */

using System.Collections.Generic;

namespace osWebRtcVoice;

public static class JoinCapabilityAuthority
{
    private static readonly object _lock = new object();
    private static readonly Dictionary<int, (string Epoch, uint Generation)> _rooms = new();

    /// <summary>Record the authority state for one room. Called by the visibility sender as it arms.</summary>
    public static void Publish(int room, string epochHex, uint generation)
    {
        if (room <= 0 || string.IsNullOrEmpty(epochHex))
            return;
        lock (_lock)
            _rooms[room] = (epochHex, generation);
    }

    /// <summary>The room's last published state, or (JoinCapability.NoEpoch, 0) when the sim never armed it.</summary>
    public static (string Epoch, uint Generation) Resolve(int room)
    {
        lock (_lock)
            return _rooms.TryGetValue(room, out (string Epoch, uint Generation) state)
                ? state
                : (JoinCapability.NoEpoch, 0u);
    }

    /// <summary>Drop a room (its service stopped, or the room was destroyed).</summary>
    public static void Forget(int room)
    {
        lock (_lock)
            _rooms.Remove(room);
    }

    /// <summary>Tests only: forget everything.</summary>
    public static void Clear()
    {
        lock (_lock)
            _rooms.Clear();
    }

    /// <summary>Rooms with a published state (diagnostics / tests).</summary>
    public static int Count
    {
        get { lock (_lock) return _rooms.Count; }
    }
}
