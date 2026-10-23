/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Pure splitter for one room's peer_ctl_batch request.
 *
 * The slvoice mixer measures the compact JSON of the whole plugin message and refuses a message of more than
 * 65,536 bytes without parsing it (SLV_VISBATCH_MAX_BYTES, src/visbatch.h:43, checked in slv_visbatch_parse,
 * src/visbatch.c:140, on the text janus_slvoice.c:2891 builds with json_dumps(message, JSON_COMPACT)). A listener's
 * column holds at most one UUID for every other listener in the room, 38 bytes each as JSON, so a room of 110
 * listeners where everyone excludes or mutes everyone is about 472,000 bytes.
 *
 * Every op is applied listener by listener, never room-wide: replace rewrites only the listeners it names, add and
 * remove change only the listeners they name (docs/voice/mixer-feed-protocol.md, "Op scoping is per-listener"). So a
 * request can be cut between listeners without changing what the mixer ends up holding. A listener's excl column and
 * mute column stay in the same part, so the mixer still applies that listener's exclusions before its mutes, as it
 * does inside one message. One listener's two columns come to about 10 KB at the mixer's own caps of 128 sources a
 * column (SLV_VIS_MAX_EXCL, src/visbatch.h:49), so a part always has room for at least one listener.
 *
 * The size is worked out from the shape of the request (a UUID string is 36 characters plus two quotes) rather than by
 * serialising each candidate part, and the tests check it against the real serialised size. A request that fits is
 * returned as it came, so the common case sends exactly what it sent before. Part sizes include the arming fields the
 * sink adds afterwards (epoch, policy generation and, on add and remove, the base map) at their longest.
 */

using System;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    /// <summary>One request's worth of a room's listeners. <see cref="Mute"/> is null when no listener in the part has a
    /// mute column.</summary>
    public sealed class PeerCtlBatchPart
    {
        public PeerCtlBatchPart(IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
                                  IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute)
        {
            Excl = excl;
            Mute = mute;
        }

        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Excl { get; }
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Mute { get; }
    }

    public static class PeerCtlBatchSplitter
    {
        /// <summary>The largest message the mixer takes, in bytes of compact JSON (src/visbatch.h:43). A message of
        /// exactly this size is accepted; one byte more is refused.</summary>
        public const int MixerMaxRequestBytes = 65536;

        // A UUID as a JSON string: 36 characters and two quotes.
        private const int UuidJsonBytes = 38;
        // The longest a generation prints as: "-2147483648".
        private const int IntegerJsonBytesMax = 11;
        // The epoch is 16 lowercase hex digits (VisAuthority.FormatEpoch).
        private const int EpochChars = 16;

        /// <summary>Cut one room's request into parts that each serialise to at most <paramref name="limit"/> bytes.
        /// A request that already fits comes back as the one part holding the maps it was given. Listeners keep their
        /// order, excl listeners first and then the listeners that only have a mute column; a listener is never cut.</summary>
        /// <param name="armed">The sink will add the arming fields to every part.</param>
        public static IReadOnlyList<PeerCtlBatchPart> Split(VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute,
            int room, bool armed, int limit = MixerMaxRequestBytes)
        {
            excl ??= new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            bool anyMute = mute != null && mute.Count > 0;

            var keys = new List<UUID>(excl.Count + (anyMute ? mute.Count : 0));
            foreach (UUID k in excl.Keys)
                keys.Add(k);
            if (anyMute)
                foreach (UUID k in mute.Keys)
                    if (!excl.ContainsKey(k))
                        keys.Add(k);

            // The request with empty columns, with and without the mute key an add or remove only carries when it names mutes.
            long fixedNoMute = Fixed(op, room, armed, false);
            long fixedMute = Fixed(op, room, armed, true);

            var whole = new Tally();
            foreach (UUID k in keys)
                whole.Add(op, armed, ExclCount(excl, k), MuteCount(mute, k));
            if (whole.Size(op, armed, fixedNoMute, fixedMute) <= limit)
                return new[] { new PeerCtlBatchPart(excl, mute) };

            var parts = new List<PeerCtlBatchPart>();
            var cur = new Tally();
            var curExcl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            Dictionary<UUID, IReadOnlyCollection<UUID>> curMute = null;
            foreach (UUID k in keys)
            {
                int e = ExclCount(excl, k);
                int m = MuteCount(mute, k);
                Tally trial = cur;
                trial.Add(op, armed, e, m);
                if (cur.Listeners > 0 && trial.Size(op, armed, fixedNoMute, fixedMute) > limit)
                {
                    parts.Add(new PeerCtlBatchPart(curExcl, curMute));
                    cur = new Tally();
                    curExcl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
                    curMute = null;
                    trial = cur;
                    trial.Add(op, armed, e, m);
                }
                cur = trial;
                if (e >= 0)
                    curExcl[k] = excl[k];
                if (m >= 0)
                    (curMute ??= new Dictionary<UUID, IReadOnlyCollection<UUID>>())[k] = mute[k];
            }
            if (cur.Listeners > 0)
                parts.Add(new PeerCtlBatchPart(curExcl, curMute));
            return parts;
        }

        /// <summary>The bytes of compact JSON the mixer measures for <paramref name="request"/>: no whitespace, strings in
        /// UTF-8 with the escapes json_dumps writes. Used by the tests as the reference for the size model above.</summary>
        public static int CompactBytes(OSD request)
        {
            switch (request)
            {
                case OSDMap map:
                {
                    long total = 2;
                    bool first = true;
                    foreach (KeyValuePair<string, OSD> kv in map)
                    {
                        if (!first) total++;
                        first = false;
                        total += StringBytes(kv.Key) + 1 + CompactBytes(kv.Value);
                    }
                    return checked((int)total);
                }
                case OSDArray arr:
                {
                    long total = 2;
                    bool first = true;
                    foreach (OSD item in arr)
                    {
                        if (!first) total++;
                        first = false;
                        total += CompactBytes(item);
                    }
                    return checked((int)total);
                }
                case OSDString s:
                    return StringBytes(s.AsString());
                case OSDInteger i:
                    return i.AsLong().ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
                case OSDBoolean b:
                    return b.AsBoolean() ? 4 : 5;
                case OSD o when o.Type == OSDType.Unknown:
                    return 4;
                default:
                    throw new NotSupportedException("peer_ctl request carries a " + request.Type + " value");
            }
        }

        private static int StringBytes(string s)
        {
            int total = 2 + Encoding.UTF8.GetByteCount(s);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\' || c == '\n' || c == '\r' || c == '\t' || c == '\b' || c == '\f')
                    total += 1;
                else if (c < 0x20)
                    total += 5;
            }
            return total;
        }

        // Sources in the listener's excl (or mute) column, or -1 when the listener has no such column.
        private static int ExclCount(IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl, UUID k)
            => excl.TryGetValue(k, out IReadOnlyCollection<UUID> c) ? (c?.Count ?? 0) : -1;

        private static int MuteCount(IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute, UUID k)
            => mute != null && mute.TryGetValue(k, out IReadOnlyCollection<UUID> c) ? (c?.Count ?? 0) : -1;

        // One map entry whose value is an array of n UUID strings: "<uuid>":[...]
        private static long ColumnBytes(int n) => UuidJsonBytes + 1 + 2 + (long)n * UuidJsonBytes + Math.Max(n - 1, 0);

        // The listeners gathered so far, as byte counts.
        private struct Tally
        {
            public int Listeners;
            private int _exclListeners;
            private int _muteListeners;
            private long _exclBytes;
            private long _muteBytes;
            private long _baseBytes;

            public void Add(VisOp op, bool armed, int exclSources, int muteSources)
            {
                Listeners++;
                if (exclSources >= 0)
                {
                    _exclBytes += ColumnBytes(exclSources);
                    _exclListeners++;
                }
                if (muteSources >= 0)
                {
                    _muteBytes += ColumnBytes(muteSources);
                    _muteListeners++;
                }
                // base: {"<uuid>":<generation>,...}, one entry for every listener the request names
                if (armed && op != VisOp.Replace)
                    _baseBytes += UuidJsonBytes + 1 + IntegerJsonBytesMax;
            }

            public long Size(VisOp op, bool armed, long fixedNoMute, long fixedMute)
            {
                // Commas between the entries of each map.
                long size = (_muteListeners > 0 ? fixedMute : fixedNoMute)
                    + _exclBytes + Math.Max(_exclListeners - 1, 0)
                    + _muteBytes + Math.Max(_muteListeners - 1, 0);
                if (armed && op != VisOp.Replace)
                    size += _baseBytes + Math.Max(Listeners - 1, 0);
                return size;
            }
        }

        // The request with empty columns: the keys the serializer writes, the room, and the arming fields.
        private static long Fixed(VisOp op, int room, bool armed, bool muteKey)
        {
            OSDMap req = PeerCtlBatchSerializer.BuildRequest(op, new Dictionary<UUID, IReadOnlyCollection<UUID>>());
            if (muteKey && !req.ContainsKey("mute"))
                req["mute"] = new OSDMap();
            req["room"] = new OSDInteger(room);
            if (armed)
            {
                req["room_epoch"] = new OSDString(new string('0', EpochChars));
                req["policy_generation"] = new OSDInteger(int.MinValue);
                if (op != VisOp.Replace)
                    req["base"] = new OSDMap();
            }
            return CompactBytes(req);
        }
    }
}
