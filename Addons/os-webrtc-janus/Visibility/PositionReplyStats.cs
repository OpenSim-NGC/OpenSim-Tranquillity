/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the mixer answered to the position requests. A peer_pos request that reached the mixer is "sent" as far as
 * the transport can tell, but the mixer may still refuse it (a sequence number that is not above the last one, a
 * room it does not know, a request over its size limit). These counters let the region say how many it applied and
 * how many it refused, and why.
 */

using System;
using System.Threading;

namespace osWebRtcVoice
{
    /// How the mixer answered one peer_pos request.
    public enum PositionReplyKind
    {
        /// No inner reply to read (an older transport, an empty body): nothing is counted.
        None,
        Applied,
        /// reason "stale_seq": the sequence number was not above the last accepted one.
        StaleSeq,
        /// reason "unknown_room": the mixer has no such room.
        UnknownRoom,
        /// reason "too_big": more than 128 entries or 65536 bytes.
        TooBig,
        /// Any other refusal (malformed, stale_epoch) and any reply that is not a known answer.
        Other,
    }

    public static class PositionReply
    {
        /// The kind of an inner reply: its "slvoice" status and, for an error, its "reason".
        public static PositionReplyKind Classify(bool present, string status, string reason)
        {
            if (!present)
                return PositionReplyKind.None;
            if (string.Equals(status, "applied", StringComparison.Ordinal))
                return PositionReplyKind.Applied;
            if (string.Equals(status, "error", StringComparison.Ordinal))
            {
                switch (reason)
                {
                    case "stale_seq": return PositionReplyKind.StaleSeq;
                    case "unknown_room": return PositionReplyKind.UnknownRoom;
                    case "too_big": return PositionReplyKind.TooBig;
                }
            }
            return PositionReplyKind.Other;
        }

        public static string WireName(PositionReplyKind kind)
        {
            switch (kind)
            {
                case PositionReplyKind.StaleSeq: return "stale_seq";
                case PositionReplyKind.UnknownRoom: return "unknown_room";
                case PositionReplyKind.TooBig: return "too_big";
                case PositionReplyKind.Applied: return "applied";
                default: return "other";
            }
        }
    }

    /// A point-in-time copy of the counters.
    public readonly struct PositionReplyCounts
    {
        public readonly long Applied, StaleSeq, UnknownRoom, TooBig, Other;

        public PositionReplyCounts(long applied, long staleSeq, long unknownRoom, long tooBig, long other)
        {
            Applied = applied; StaleSeq = staleSeq; UnknownRoom = unknownRoom; TooBig = tooBig; Other = other;
        }

        public long Refused => StaleSeq + UnknownRoom + TooBig + Other;
        public long Total => Applied + Refused;

        /// What was counted since <paramref name="earlier"/>.
        public PositionReplyCounts Since(PositionReplyCounts earlier)
            => new PositionReplyCounts(Applied - earlier.Applied, StaleSeq - earlier.StaleSeq,
                UnknownRoom - earlier.UnknownRoom, TooBig - earlier.TooBig, Other - earlier.Other);

        public override string ToString()
            => $"{Applied} applied, {Refused} refused (stale_seq {StaleSeq}, unknown_room {UnknownRoom}, too_big {TooBig}, other {Other})";
    }

    /// Running totals of the mixer's replies to peer_pos, safe to update from any thread.
    public sealed class PositionReplyStats
    {
        private long _applied, _staleSeq, _unknownRoom, _tooBig, _other;

        public void Record(PositionReplyKind kind)
        {
            switch (kind)
            {
                case PositionReplyKind.Applied: Interlocked.Increment(ref _applied); break;
                case PositionReplyKind.StaleSeq: Interlocked.Increment(ref _staleSeq); break;
                case PositionReplyKind.UnknownRoom: Interlocked.Increment(ref _unknownRoom); break;
                case PositionReplyKind.TooBig: Interlocked.Increment(ref _tooBig); break;
                case PositionReplyKind.Other: Interlocked.Increment(ref _other); break;
            }
        }

        public PositionReplyCounts Counts
            => new PositionReplyCounts(Interlocked.Read(ref _applied), Interlocked.Read(ref _staleSeq),
                Interlocked.Read(ref _unknownRoom), Interlocked.Read(ref _tooBig), Interlocked.Read(ref _other));
    }
}
