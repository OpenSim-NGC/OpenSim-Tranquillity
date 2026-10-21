/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Decides what position requests to send, tick by tick.
 *
 * Scene-free: it is handed a list of PositionView and a clock. For each room it keeps the last point it
 * sent for every participant, and sends
 *   - a full snapshot of the room when the room is new or the keep-alive interval has passed, so a standing
 *     avatar is never silent for long and a lost request heals within one interval;
 *   - between snapshots, only the participants that are new to the room or have moved at least the
 *     movement threshold from the last point sent.
 * A room whose participants have all left gets one empty full snapshot, then nothing.
 *
 * A child agent's speed is estimated here, from the two most recent different positions the region has
 * seen for it, because the region does not keep the child's velocity. An estimate older than twice the
 * interval at which core reports children is treated as standing still.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class VoicePositionFeeder
    {
        private sealed class RoomState
        {
            public readonly Dictionary<UUID, (int X, int Y, int Z)> Sent = new Dictionary<UUID, (int, int, int)>();
            public long LastFullMs = long.MinValue;
        }

        private sealed class ChildTrack
        {
            public int X, Y, Z;
            public long ChangedAtMs;
            public double SpeedCmS;
        }

        private readonly PositionSettings _settings;
        private readonly Dictionary<int, RoomState> _rooms = new Dictionary<int, RoomState>();
        private readonly Dictionary<int, long> _seq = new Dictionary<int, long>();
        private readonly Dictionary<UUID, ChildTrack> _children = new Dictionary<UUID, ChildTrack>();
        private readonly long _seqBase;

        /// <param name="seqBase">The first sequence number minus one. Pass a value that is larger than any a
        /// previous run of the sim could have reached (the current time in milliseconds), so a restart is
        /// not mistaken for old, late traffic.</param>
        public VoicePositionFeeder(PositionSettings settings, long seqBase)
        {
            _settings = (settings ?? new PositionSettings()).Normalised();
            _seqBase = seqBase;
        }

        /// Participants in the last call that could not be placed in a room and so were left out.
        public int Unplaced { get; private set; }

        /// <summary>Requests to send now, possibly none. Must be called from one thread.</summary>
        /// <param name="roomOf">The room a participant is in, or null when it cannot be placed.</param>
        /// <param name="engineMaxCmS">The physics engine's maximum speed in cm/s, or zero when it declares none.</param>
        public IReadOnlyList<PositionMessage> Build(long nowMs, IReadOnlyList<PositionView> views,
            Func<UUID, int?> roomOf, double engineMaxCmS)
        {
            var byRoom = new Dictionary<int, List<(PositionView View, PositionEntry Entry)>>();
            var present = new HashSet<UUID>();
            int unplaced = 0;
            foreach (PositionView v in views)
            {
                present.Add(v.Id);
                int? room = roomOf(v.Id);
                if (!room.HasValue)
                {
                    unplaced++;
                    continue;
                }
                double speed = v.IsChild ? TrackChild(v, nowMs) : v.SpeedCmS;
                PositionAllowance allowance = PositionTolerance.For(v.IsChild, speed, engineMaxCmS, _settings);
                var entry = new PositionEntry(v.Id, v.XCm, v.YCm, v.ZCm, allowance.SpeedCmS, allowance.ToleranceCm);
                if (!byRoom.TryGetValue(room.Value, out var list))
                    byRoom[room.Value] = list = new List<(PositionView, PositionEntry)>();
                list.Add((v, entry));
            }
            Unplaced = unplaced;

            foreach (UUID gone in new List<UUID>(_children.Keys))
                if (!present.Contains(gone))
                    _children.Remove(gone);

            var roomKeys = new SortedSet<int>(byRoom.Keys);
            foreach (int r in _rooms.Keys)
                roomKeys.Add(r);

            var messages = new List<PositionMessage>();
            foreach (int room in roomKeys)
            {
                if (!_rooms.TryGetValue(room, out RoomState state))
                    _rooms[room] = state = new RoomState();
                var current = byRoom.TryGetValue(room, out var cur) ? cur : null;

                if (current == null)
                {
                    // Everyone has left: tell the mixer once, then forget the room.
                    if (state.Sent.Count > 0)
                        AddChunks(messages, room, full: true, new List<PositionEntry>());
                    _rooms.Remove(room);
                    continue;
                }

                bool full = state.LastFullMs == long.MinValue || nowMs - state.LastFullMs >= _settings.KeepAliveMs;
                var entries = new List<PositionEntry>();
                foreach (var (_, entry) in current)
                {
                    if (full || !state.Sent.TryGetValue(entry.Id, out var last) || Moved(last, entry))
                        entries.Add(entry);
                }
                if (entries.Count == 0)
                    continue;

                if (full)
                {
                    state.Sent.Clear();
                    state.LastFullMs = nowMs;
                }
                foreach (PositionEntry e in entries)
                    state.Sent[e.Id] = (e.XCm, e.YCm, e.ZCm);
                AddChunks(messages, room, full, entries);
            }
            return messages;
        }

        private bool Moved((int X, int Y, int Z) last, PositionEntry now)
        {
            double dx = (double)now.XCm - last.X, dy = (double)now.YCm - last.Y, dz = (double)now.ZCm - last.Z;
            long threshold = _settings.MoveThresholdCm;
            return dx * dx + dy * dy + dz * dz >= (double)threshold * threshold;
        }

        // Splits entries into requests of at most PositionBatchSerializer.MaxEntries. Only the first chunk of
        // a full snapshot replaces the room; the rest add to it.
        private void AddChunks(List<PositionMessage> messages, int room, bool full, List<PositionEntry> entries)
        {
            int offset = 0;
            do
            {
                int count = Math.Min(PositionBatchSerializer.MaxEntries, entries.Count - offset);
                messages.Add(new PositionMessage
                {
                    Room = room,
                    Seq = NextSeq(room),
                    Full = full && offset == 0,
                    Entries = entries.GetRange(offset, count),
                });
                offset += count;
            }
            while (offset < entries.Count);
        }

        private long NextSeq(int room)
        {
            long next = (_seq.TryGetValue(room, out long last) ? last : _seqBase) + 1;
            _seq[room] = next;
            return next;
        }

        // Speed of a child agent from its two most recent different positions, in cm/s.
        private double TrackChild(PositionView v, long nowMs)
        {
            if (!_children.TryGetValue(v.Id, out ChildTrack t))
            {
                _children[v.Id] = new ChildTrack { X = v.XCm, Y = v.YCm, Z = v.ZCm, ChangedAtMs = nowMs, SpeedCmS = 0.0 };
                return 0.0;
            }
            if (t.X != v.XCm || t.Y != v.YCm || t.Z != v.ZCm)
            {
                double dx = (double)v.XCm - t.X, dy = (double)v.YCm - t.Y, dz = (double)v.ZCm - t.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                long dt = Math.Max(1, nowMs - t.ChangedAtMs);
                t.SpeedCmS = dist / (dt / 1000.0);
                t.X = v.XCm;
                t.Y = v.YCm;
                t.Z = v.ZCm;
                t.ChangedAtMs = nowMs;
            }
            // Core reports a moving child at most every ChildUpdateIntervalMs; silence for twice that means it stopped.
            return nowMs - t.ChangedAtMs > 2L * PositionTolerance.ChildUpdateIntervalMs ? 0.0 : t.SpeedCmS;
        }
    }
}
