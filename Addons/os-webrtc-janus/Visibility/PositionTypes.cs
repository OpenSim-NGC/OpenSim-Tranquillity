/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Value types and seams for the position feed: the sim sends the mixer where each voice participant
 * really is, so the mixer can compare it with the position a viewer claims for itself.
 *
 * Positions are global centimetres, the frame a viewer reports its own position in (the region's
 * world origin plus the position inside the region, times 100). Everything here is Scene-free so the
 * feed unit-tests without a Scene.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    /// How the mixer is asked to treat the positions. Only Observe is active today: the mixer counts what
    /// it would have changed and changes nothing.
    public enum PositionFeedMode
    {
        Observe = 0,
        Enforce = 1,
    }

    public static class PositionFeedModes
    {
        /// Parses the configured mode. Absent or empty is Observe. A value that is neither word returns
        /// Observe and sets <paramref name="recognised"/> false so the caller can say so.
        public static PositionFeedMode Parse(string text, out bool recognised)
        {
            recognised = true;
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "observe", StringComparison.OrdinalIgnoreCase))
                return PositionFeedMode.Observe;
            if (string.Equals(text.Trim(), "enforce", StringComparison.OrdinalIgnoreCase))
                return PositionFeedMode.Enforce;
            recognised = false;
            return PositionFeedMode.Observe;
        }

        /// The mode the feed actually runs in. Enforce is accepted by the configuration but nothing acts on
        /// it yet, so it runs as Observe.
        public static PositionFeedMode Active(PositionFeedMode configured) => PositionFeedMode.Observe;

        /// The word sent to the mixer.
        public static string WireName(PositionFeedMode mode)
            => mode == PositionFeedMode.Enforce ? "enforce" : "observe";
    }

    /// Tunables of the position feed. Immutable once built; <see cref="Normalised"/> clamps a hand-edited set
    /// into ranges the feed can work with.
    public sealed class PositionSettings
    {
        public const int DefaultTickMs = 500;
        public const int DefaultKeepAliveMs = 1000;
        public const int DefaultMoveThresholdCm = 100;
        public const int DefaultMarginCm = 500;
        public const int DefaultSpeedFloorCmS = 800;
        /// Used as the speed ceiling only when the physics engine declares no maximum speed of its own.
        public const int DefaultFallbackSpeedMaxCmS = 10000;

        public PositionFeedMode Mode { get; init; } = PositionFeedMode.Observe;
        /// How often positions are evaluated.
        public int TickMs { get; init; } = DefaultTickMs;
        /// A full snapshot of the room is sent this often, so a standing avatar never goes unreported for long.
        public int KeepAliveMs { get; init; } = DefaultKeepAliveMs;
        /// An entry is sent between snapshots once it has moved this far from the last point sent.
        public int MoveThresholdCm { get; init; } = DefaultMoveThresholdCm;
        /// The distance a viewer's claimed position may differ from the sim's before it counts as off.
        public int MarginCm { get; init; } = DefaultMarginCm;
        /// The smallest speed allowance sent, so a standing avatar that starts to run is not flagged.
        public int SpeedFloorCmS { get; init; } = DefaultSpeedFloorCmS;
        /// The speed ceiling for an engine that declares none.
        public int FallbackSpeedMaxCmS { get; init; } = DefaultFallbackSpeedMaxCmS;

        public PositionSettings Normalised()
        {
            int tick = Math.Max(100, TickMs);
            int floor = Math.Max(0, SpeedFloorCmS);
            return new PositionSettings
            {
                Mode = Mode,
                TickMs = tick,
                KeepAliveMs = Math.Max(tick, KeepAliveMs),
                MoveThresholdCm = Math.Max(1, MoveThresholdCm),
                MarginCm = Math.Max(0, MarginCm),
                SpeedFloorCmS = floor,
                FallbackSpeedMaxCmS = Math.Max(floor, FallbackSpeedMaxCmS),
            };
        }
    }

    /// One participant as the feed sees it, at one instant. The position is global centimetres.
    public readonly struct PositionView
    {
        public readonly UUID Id;
        public readonly bool IsChild;
        public readonly int XCm, YCm, ZCm;
        /// The sim's own speed for this participant, cm/s: the avatar's velocity, or that of what it sits on,
        /// whichever is greater. Not used for a child agent, whose velocity the sim does not keep.
        public readonly double SpeedCmS;

        public PositionView(UUID id, bool isChild, int xCm, int yCm, int zCm, double speedCmS)
        {
            Id = id;
            IsChild = isChild;
            XCm = xCm;
            YCm = yCm;
            ZCm = zCm;
            SpeedCmS = speedCmS;
        }
    }

    /// What goes on the wire for one participant: the point, a speed allowance and a tolerance.
    public readonly struct PositionEntry
    {
        public readonly UUID Id;
        public readonly int XCm, YCm, ZCm;
        /// Centimetres per second the mixer may add for each second since the sample.
        public readonly int SpeedCmS;
        /// Centimetres of difference the mixer should accept at the instant of the sample.
        public readonly int ToleranceCm;

        public PositionEntry(UUID id, int xCm, int yCm, int zCm, int speedCmS, int toleranceCm)
        {
            Id = id;
            XCm = xCm;
            YCm = yCm;
            ZCm = zCm;
            SpeedCmS = speedCmS;
            ToleranceCm = toleranceCm;
        }
    }

    /// One position request for one room.
    public sealed class PositionMessage
    {
        public int Room;
        /// Strictly increasing per room, so the mixer can drop a request that arrives late.
        public long Seq;
        /// True: the entries are the whole room, and anyone not listed has left. False: only the listed
        /// participants are updated.
        public bool Full;
        public List<PositionEntry> Entries = new List<PositionEntry>();
    }

    /// The scene side of the feed: where every voice participant is now.
    public interface IPositionWorld
    {
        IReadOnlyList<PositionView> SnapshotPositions();
    }

    /// The transport of the feed, and what it has learned about the mixer.
    public interface IPeerPosSink
    {
        /// The vis_protocol the mixer last advertised in a reply, or 0 when none has been seen.
        int MixerVisProtocol { get; }

        /// Sends one peer_pos body. True when the transport succeeded. Never throws.
        Task<bool> SendPositionsAsync(OSDMap body);
    }
}
