/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Janus-side implementation of IPeerCtlBatchSink. Partitions each send by the
 * room its listeners are actually in and sends one peer_ctl_batch per room via JanusAdminClient
 * (Admin API message_plugin, admin_secret in the body). One instance per region; owns its
 * JanusAdminClient (and its HttpClient) for its lifetime.
 *
 * Per-room emission. Stamping ONE room on every batch (the estate room) would address exclusions
 * for agents in per-parcel rooms to a room those agents are not in, and the mixer would silently
 * drop them. Instead:
 *   - PeerCtlBatchPartitioner splits the map by roomOf(listener), keeping for each listener
 *     only the sources in that same room (inert cross-room sources cost cap and nothing else);
 *   - one admin message goes to each room, issued in parallel under a small cap;
 *   - a room's message that would be over the mixer's size limit (65,536 bytes of compact JSON) is cut between
 *     listeners into parts, sent one after another (PeerCtlBatchSplitter); a message under the limit is sent whole;
 *   - the per-room results aggregate into ONE PeerCtlSendResult in severity order, so the
 *     sender's contract - and its consecutive-ProtocolError latch arithmetic - is unchanged.
 *
 * The constructor-computed estate room is the region's estate-channel room. It is computed by the same
 * JanusAudioBridge.CalcRoomNumber the mixer uses, and still logged once at Info so it is
 * eyeball-comparable against handle_info - a wrong room is invisible on the wire.
 *
 * RoomOf is a SETTABLE property, not a ctor argument, because construction order forces it: the
 * region module builds this sink and then passes it INTO VoiceVisibilityService's constructor
 * (WebRtcVoiceRegionModule.cs:174-176), so the service - which owns the table - does not exist yet
 * when the sink does. The service assigns it in its own constructor, before Start(). While it is
 * null the partitioner reads it as "no agent has a record": every agent resolves to the fallback
 * room, one message goes out, and both fallback counters read the full
 * population, which is the loud version of that state rather than a crash on the send path.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;   // RefusalLogGate

namespace osWebRtcVoice
{
    public sealed class JanusPeerCtlBatchSink : IPeerCtlBatchSink, IPeerCtlHeartbeatSink, IPeerPosSink, IDisposable
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        // Reused empty excl collection for a room that has only mute changes this op.
        private static readonly IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> EmptyChannel
            = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
        private const string LogHeader = "[JANUS PEERCTL SINK]";
        private const string SlvoicePlugin = "janus.plugin.slvoice";

        /// <summary>Rooms addressed concurrently within ONE SendAsync ([WebRtcVoice]
        /// VisibilityRoomSendConcurrency). Small by design: this is a latency budget, not a
        /// throughput knob. R rooms sequentially at t ms per admin round-trip fit the 250 ms tick
        /// only while 2R.t &lt; 250; past that the sender's single-flight skips a tick, the skip
        /// forces a snapshot of R messages, and the storm sustains itself. A concurrency of 4 turns a
        /// 64-parcel region's 128 round-trips into 32 rounds.</summary>
        public const int DefaultRoomSendConcurrency = 4;

        private readonly JanusAdminClient _admin;               // null when a send delegate was injected
        private readonly Func<OSDMap, Task<(AdminSendResult Result, string Body)>> _sendOne;
        private readonly SemaphoreSlim _roomGate;
        private readonly int _fallbackRoom;
        private readonly string _region;

        private volatile int _lastSendRooms;
        private volatile int _lastSendRequests;                 // requests built: one for each room unless a room was cut into parts
        private volatile int _lastSendHeldBackRooms;           // rooms left out of the last send: the mixer refused them as unknown
        private long _roomsHeldBack;                           // the same, summed over every send
        private volatile int[] _lastSendRoomNumbers = Array.Empty<int>();   // which ones, for the console
        private volatile int _lastSendFallbackListeners;
        private volatile int _lastSendFallbackSources;
        // The MUTE channel's fallback counts, parallel to the excl ones above. Kept
        // SEPARATE (not summed into the excl fields) because the partition exposes counts, not listener
        // sets, so a distinct cross-channel union cannot be computed without changing the partitioner
        // (not done here). _lastSendRooms below is the room UNION (rooms addressed by either channel).
        private volatile int _lastSendMuteFallbackListeners;
        private volatile int _lastSendMuteFallbackSources;

        // The vis_protocol last advertised by the mixer; see MixerVisProtocol.
        private volatile int _mixerVisProtocol;
        private volatile bool _mixerProtocolKnown;

        private readonly Func<long> _nowMs;
        private readonly RefusalLogGate _positionRefusalGate = new RefusalLogGate();   // one warning a minute for the mixer's refusals
        private static readonly DateTime s_gateEpoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Inner-reply stats for the most recent send, summed across rooms (see LastSendStats).
        // Volatile ints mirror the LastSend* pattern above; SendAsync is single-flight from the sender.
        private volatile int _statRepliesParsed;
        private volatile int _statDeferred;
        private volatile int _statSkipped;
        private volatile int _statEntries;
        private volatile int _statMuteEntries;
        private volatile int _statAnomalies;

        /// <param name="roomSendConcurrency">Rooms in flight at once within one send. Absent, zero
        /// or negative is not honoured: zero would make SemaphoreSlim block every send forever and
        /// wedge emission until the sender's staleness guard fired, every tick, so a non-positive
        /// value is clamped to 1 (sequential) with a loud warning. There is no upper clamp - a large
        /// value simply means no throttling, and the real parallelism is bounded by the number of
        /// occupied rooms either way.</param>
        /// <param name="sendOne">Per-message transport, injectable for tests (the house pattern -
        /// cf. VisibilityBatchSender's nowMs). Null in production, where it posts through this
        /// sink's own JanusAdminClient.</param>
        /// <param name="caCertificateFile">Private CA certificate file trusted for an https admin URL, or null
        /// (see JanusTls).</param>
        public JanusPeerCtlBatchSink(string adminUri, string adminToken, TimeSpan timeout,
                                     UUID regionId, string regionName,
                                     int roomSendConcurrency = DefaultRoomSendConcurrency,
                                     Func<OSDMap, Task<(AdminSendResult Result, string Body)>> sendOne = null,
                                     string caCertificateFile = null,
                                     Func<long> nowMs = null)
        {
            _region = regionName;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            if (sendOne == null)
            {
                _admin = new JanusAdminClient(adminUri, adminToken, timeout, JanusTls.CreateHandler(caCertificateFile));
                _sendOne = req => _admin.SendPluginMessageWithReplyAsync(SlvoicePlugin, req);
            }
            else
            {
                _sendOne = sendOne;
            }

            int concurrency = roomSendConcurrency;
            if (concurrency < 1)
            {
                m_log.LogWarning("{LogHeader} region {RegionName}: [WebRtcVoice] VisibilityRoomSendConcurrency is {Configured}, " +
                    "which would stall every per-room send; using 1 (sequential). Set a small positive value ({Default} is the default).",
                    LogHeader, regionName, concurrency, DefaultRoomSendConcurrency);
                concurrency = 1;
            }
            _roomGate = new SemaphoreSlim(concurrency, concurrency);

            // Estate/shared channel room: the "local" channel at REGION_ROOM_ID (-999), hashed by
            // the identical CalcRoomNumber the mixer computes on the Janus side. It is not
            // the only room, and it is not a fallback either - see the class comment.
            // The grid id only enters the "multiagent" arm; the local derivation ignores it.
            _fallbackRoom = JanusAudioBridge.CalcRoomNumber(
                string.Empty, regionId.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            // Nothing is addressed at this number merely because it is the default: an agent with no
            // record is omitted. On a parcel-channel region this room
            // may never have been created, so the log line below says exactly when it is used.
            m_log.LogInformation("{LogHeader} region {RegionName} ({RegionId}) -> estate room {RoomNumber} (the region's " +
                "estate/local channel; compare vs handle_info). Each agent is addressed at the room its last provision " +
                "joined, else not at all; this number is addressed only where it IS that room. Room send " +
                "concurrency {Concurrency}.",
                LogHeader, regionName, regionId, _fallbackRoom, concurrency);
        }

        /// <summary>Recorded room per agent, or null for "no record" - AgentRoomTable.Resolve, handed
        /// over by VoiceVisibilityService's constructor. Settable because this sink is built BEFORE
        /// that service (see the class comment). Null until then, and read as "nothing is recorded".</summary>
        public Func<UUID, int?> RoomOf { get; set; }

        /// <summary>Set only when [WebRtcVoice] VisibilityArmingEnabled is true. Null (the default)
        /// leaves every request without arming fields. When set, every per-room request carries room_epoch and
        /// policy_generation (plus base on add/remove), and each room's reply is applied to it.</summary>
        public VisAuthority Authority { get; set; }

        /// <summary>The region's estate-channel room. An agent with no
        /// record is not addressed here - such an agent is omitted. It is the number the reader prints as the region's
        /// estate room.</summary>
        public int FallbackRoom => _fallbackRoom;

        /// <summary>The room numbers the most recent send actually addressed, ascending. The console
        /// reader prints these, so an operator sees where policy went rather than a number it might have gone to.</summary>
        public IReadOnlyList<int> LastSendRoomNumbers => _lastSendRoomNumbers;

        /// <summary>Rooms addressed by the most recent send (the "rooms addressed per tick" figure).</summary>
        public int LastSendRooms => _lastSendRooms;

        /// <summary>The rooms the mixer answered "unknown_room" for. Set by VoiceVisibilityService, like <see cref="RoomOf"/>;
        /// null (the default) holds nothing back. A room in it is left out of every send until a join into it is recorded.</summary>
        public RoomRefusals Refusals { get; set; }

        /// <summary>Rooms the most recent send left out because the mixer had refused them as unknown and no join into them
        /// has been recorded since.</summary>
        public int LastSendHeldBackRooms => _lastSendHeldBackRooms;

        /// <summary>Rooms left out for that reason, summed over every send.</summary>
        public long RoomsHeldBack => Interlocked.Read(ref _roomsHeldBack);

        /// <summary>Requests the most recent send built. The same as <see cref="LastSendRooms"/> unless a room's request
        /// was over the mixer's size limit and was cut into parts.</summary>
        public int LastSendRequests => _lastSendRequests;

        /// <summary>Distinct listeners in the most recent send with no room record.</summary>
        public int LastSendFallbackListeners => _lastSendFallbackListeners;

        /// <summary>Distinct sources in the most recent send with no room record. Non-zero says the sim
        /// has no record of a room some agent joined (for example an agent only in a group call).</summary>
        public int LastSendFallbackSources => _lastSendFallbackSources;

        /// <summary>Distinct MUTE-channel listeners/sources in the most recent send with no room
        /// record (fallback), parallel to the excl fallback figures. Non-zero on a mute-only op whose
        /// listener has no AgentRoomTable record; zero otherwise.</summary>
        public int LastSendMuteFallbackListeners => _lastSendMuteFallbackListeners;

        /// <summary>See <see cref="LastSendMuteFallbackListeners"/> — the mute-channel source companion.</summary>
        public int LastSendMuteFallbackSources => _lastSendMuteFallbackSources;

        /// <summary>Inner-reply stats from the most recent SendAsync (see <see cref="PeerCtlSendStats"/>),
        /// summed across the rooms the send addressed. Default/all-zero when the mixer reply carried no such
        /// fields (old mixer). PLUMBING only -- read by nobody today; surfaced for a future decision.</summary>
        public PeerCtlSendStats LastSendStats => new PeerCtlSendStats
        {
            RepliesParsed = _statRepliesParsed,
            DeferredListeners = _statDeferred,
            Skipped = _statSkipped,
            Entries = _statEntries,
            MuteEntries = _statMuteEntries,
            Anomalies = _statAnomalies,
        };

        /// <summary>The vis_protocol the mixer advertised in its latest reply to a peer_ctl request, or 0 when it has
        /// not yet advertised one (no reply seen, or an older mixer whose replies carry none). The position feed sends
        /// nothing until this reaches <see cref="VisAuthority.PositionProtocol"/>.</summary>
        public int MixerVisProtocol => _mixerVisProtocol;

        /// <summary>True once a reply has told this sink what the mixer speaks (see <see cref="MixerVisProtocol"/>; 0 is an
        /// older mixer that advertises no version). Reset when a peer_pos request or the version check fails in transport,
        /// because a mixer that went away may come back as another version.</summary>
        public bool MixerProtocolKnown => _mixerProtocolKnown;

        // A mixer's epoch is only read per room, so this value is never adopted by a room: the check names none.
        private const string CheckEpoch = "0000000000000001";

        /// <summary>The request that asks for the mixer's version. A peer_ctl_heartbeat naming no room: the mixer parses it,
        /// walks the (empty) rooms object, and answers with vis_protocol and mixer_instance. It adopts no epoch, marks no
        /// record stale and arms or disarms no listener (janus_slvoice_handle_heartbeat, janus_slvoice.c, in the mixer
        /// repository: the room loop at the "rooms" object is the only place a heartbeat touches room state).</summary>
        public static OSDMap BuildProtocolCheck() => new OSDMap
        {
            ["request"] = OSD.FromString("peer_ctl_heartbeat"),
            ["room_epoch"] = OSD.FromString(CheckEpoch),
            ["interval_ms"] = OSD.FromInteger(VisAuthority.DefaultHeartbeatIntervalMs),
            ["rooms"] = new OSDMap(),
        };

        /// <summary>Ask the mixer which vis_protocol it speaks, for a region that sends no peer_ctl request of its own (no
        /// heartbeat, nobody excluded). An older mixer answers without a version, or with an error; either way the answer
        /// is recorded and it is not asked again until a request fails. Never throws.</summary>
        public async Task<bool> CheckMixerProtocolAsync()
        {
            try
            {
                (AdminSendResult admin, string replyBody) = await _sendOne(BuildProtocolCheck()).ConfigureAwait(false);
                if (admin != AdminSendResult.Ok)
                {
                    _mixerProtocolKnown = false;
                    return false;
                }
                SlvoiceReply reply = ParseInnerReply(replyBody);
                _mixerVisProtocol = reply.Present ? reply.VisProtocol : 0;
                _mixerProtocolKnown = true;
                return true;
            }
            catch (Exception e)
            {
                m_log.LogDebug(e, "{LogHeader} region {RegionName}: the mixer version check failed", LogHeader, _region);
                _mixerProtocolKnown = false;
                return false;
            }
        }

        /// <summary>What the mixer has answered to the peer_pos requests this sink has sent.</summary>
        public PositionReplyStats Replies { get; } = new PositionReplyStats();

        /// <summary>Send one peer_pos body, stamped with the room epoch when the region runs with arming. True when the
        /// transport succeeded. Never throws.</summary>
        public async Task<bool> SendPositionsAsync(OSDMap body)
        {
            VisAuthority authority = Authority;
            if (authority != null)
                body["room_epoch"] = OSD.FromString(authority.EpochString);
            long stamp = Refusals?.Stamp() ?? 0;   // before the request goes out: see RoomRefusals
            try
            {
                (AdminSendResult admin, string replyBody) = await _sendOne(body).ConfigureAwait(false);
                if (admin != AdminSendResult.Ok)
                {
                    _mixerProtocolKnown = false;   // the mixer may be restarting: ask again when it answers
                    return false;
                }
                SlvoiceReply reply = ParseInnerReply(replyBody);
                // Every reply the mixer builds for a peer_pos, applied or refused, carries vis_protocol; one without is
                // an older mixer, so a mixer replaced by an older one stops the feed.
                if (reply.Present)
                {
                    _mixerVisProtocol = reply.VisProtocol;
                    _mixerProtocolKnown = true;
                }
                CountPositionReply(body, reply, stamp);
                return true;
            }
            catch (Exception e)
            {
                // The pump writes the once-a-minute line about a failing feed; this one is for tracing a single failure.
                m_log.LogDebug(e, "{LogHeader} region {RegionName}: peer_pos send failed", LogHeader, _region);
                _mixerProtocolKnown = false;
                return false;
            }
        }

        // The mixer answers a peer_pos it will not apply with an error reply inside a successful transport. Count what it
        // answered, and write one warning a minute for the refusals, with the number held back.
        private void CountPositionReply(OSDMap request, in SlvoiceReply reply, long stamp)
        {
            PositionReplyKind kind = PositionReply.Classify(reply.Present, reply.Status, reply.Reason);
            Replies.Record(kind);
            if (kind == PositionReplyKind.None || kind == PositionReplyKind.Applied)
                return;
            int room = request.TryGetValue("room", out OSD ro) ? ro.AsInteger() : 0;
            if (kind == PositionReplyKind.UnknownRoom)
                Refusals?.Refused(room, stamp);   // the pump sends nothing more to it until a join into it is recorded
            string what = PositionReply.WireName(kind);
            if (_positionRefusalGate.ShouldLog("peer_pos refused", UUID.Zero, s_gateEpoch.AddMilliseconds(_nowMs()), out int held))
                m_log.LogWarning("{LogHeader} region {RegionName}: the mixer refused a peer_pos request for room {Room} ({Reason}); " +
                    "positions for it are not being applied{More}", LogHeader, _region, room, what,
                    held > 0 ? $"; {held} more refused since the last one logged" : string.Empty);
            else
                m_log.LogDebug("{LogHeader} region {RegionName}: the mixer refused a peer_pos request for room {Room} ({Reason})",
                    LogHeader, _region, room, what);
        }

        // Remember the protocol version a reply advertises. A successful reply with no version is an older
        // mixer; a reply that carries no inner object at all (an error, or a transport that returns none) says
        // nothing and leaves what was learned.
        private void ObserveProtocol(in SlvoiceReply reply)
        {
            if (reply.Present && reply.VisProtocol > 0)
            {
                _mixerVisProtocol = reply.VisProtocol;
                _mixerProtocolKnown = true;
            }
            else if (reply.Present && string.Equals(reply.Status, "applied", StringComparison.Ordinal))
            {
                _mixerVisProtocol = 0;
                _mixerProtocolKnown = true;
            }
        }

        public void Dispose()
        {
            _admin?.Dispose();
            _roomGate?.Dispose();
        }

        public async Task<PeerCtlSendResult> SendAsync(VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
        {
            RoomRefusals refusals = Refusals;
            long stamp = refusals?.Stamp() ?? 0;   // before the first request goes out: see RoomRefusals
            PeerCtlBatchPartition part = PeerCtlBatchPartitioner.Partition(excl, RoomOf, _fallbackRoom);
            // Exclusion-channel fallback counts keep their exact meaning; _lastSendRooms is set from the
            // CHANNEL UNION further below (a mute-only op addresses rooms the excl partition never sees).
            _lastSendFallbackListeners = part.FallbackListeners;
            _lastSendFallbackSources = part.FallbackSources;

            // ADDITIVE mute channel: partition it with the SAME per-room policy (the partitioner is
            // untouched — called a second time). Rooms present only in the mute partition are unioned
            // in below, so a mute-only op still addresses its room(s). Empty/null mute => no second
            // partition and the excl-only path is byte-for-byte the pre-mute behaviour. mutePart is
            // captured IN FULL (not just .Rooms) so its fallback counts feed the mute companions and the
            // log; muteRooms keeps its exact pre-fix value (null when empty), so the SEND LOOP below is
            // untouched (this adds counters and logging only, no wire change).
            PeerCtlBatchPartition mutePart = (mute != null && mute.Count > 0)
                ? PeerCtlBatchPartitioner.Partition(mute, RoomOf, _fallbackRoom)
                : PeerCtlBatchPartition.Empty;
            IReadOnlyDictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> muteRooms
                = (mute != null && mute.Count > 0) ? mutePart.Rooms : null;
            _lastSendMuteFallbackListeners = mutePart.FallbackListeners;
            _lastSendMuteFallbackSources = mutePart.FallbackSources;

            // Union the room keys across both channels, preserving order-independence.
            var roomKeys = new HashSet<int>();
            foreach (int r in part.Rooms.Keys) roomKeys.Add(r);
            if (muteRooms != null)
                foreach (int r in muteRooms.Keys) roomKeys.Add(r);
            int heldBack = refusals == null ? 0 : roomKeys.RemoveWhere(refusals.IsRefused);
            _lastSendHeldBackRooms = heldBack;
            if (heldBack > 0)
                Interlocked.Add(ref _roomsHeldBack, heldBack);
            _lastSendRooms = roomKeys.Count;   // rooms addressed by EITHER channel (the true count)
            var roomNumbers = new List<int>(roomKeys);
            roomNumbers.Sort();
            _lastSendRoomNumbers = roomNumbers.ToArray();   // the console reader names them

            // Build EVERY body BEFORE sending any of them. The serializer's invariant throw stays
            // all-or-nothing as it was when there was one message: a zero UUID in any room aborts the
            // whole tick with nothing on the wire, rather than leaving some rooms updated and others not.
            // A room's request is cut into parts when it would be over the mixer's limit (see PeerCtlBatchSplitter);
            // nearly always a room is one part, and that part is the request this method always sent.
            VisAuthority authority = Authority;
            var roomSends = new List<RoomSend>(roomKeys.Count);
            int requestCount = 0;
            foreach (int roomKey in roomKeys)
            {
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> exclSlice =
                    part.Rooms.TryGetValue(roomKey, out var es) ? es : EmptyChannel;
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteSlice =
                    (muteRooms != null && muteRooms.TryGetValue(roomKey, out var ms)) ? ms : null;
                var send = new RoomSend(roomKey);
                foreach (PeerCtlBatchPart piece in PeerCtlBatchSplitter.Split(op, exclSlice, muteSlice, roomKey, authority != null))
                {
                    OSDMap request = PeerCtlBatchSerializer.BuildRequest(op, piece.Excl, piece.Mute);
                    request["room"] = new OSDInteger(roomKey);   // the sink stamps the room, per room
                    send.Requests.Add(request);
                    if (authority != null)
                        send.Stamps.Add(StampAuthority(authority, request, op, roomKey, piece.Excl, piece.Mute));
                }
                requestCount += send.Requests.Count;
                roomSends.Add(send);
            }
            _lastSendRequests = requestCount;

            LogRoomsAddressed(op, part, mutePart, roomKeys.Count, requestCount);

            if (roomSends.Count == 0)
            {
                RecordStats(Array.Empty<SlvoiceReply>());    // an empty send resets last stats
                return PeerCtlSendResult.Ok;                 // nothing to address is not a failure
            }

            // One room: no gate, one round-trip for each part - a single message as an unpartitioned send (plus the reply parse).
            var sends = new Task<List<(PeerCtlSendResult Result, SlvoiceReply Reply)>>[roomSends.Count];
            if (roomSends.Count == 1)
                sends[0] = SendPartsAsync(roomSends[0]);
            else
                for (int i = 0; i < roomSends.Count; i++)
                    sends[i] = SendGatedAsync(roomSends[i]);
            List<(PeerCtlSendResult Result, SlvoiceReply Reply)>[] results = await Task.WhenAll(sends).ConfigureAwait(false);

            // Aggregate in severity order, most severe wins. Every room is attempted - a failure
            // in one must not suppress the others, because rooms are independent and `replace` is
            // per-listener idempotent, so the sender's re-snapshot repairs a partial send safely.
            PeerCtlSendResult worst = PeerCtlSendResult.Ok;
            var replies = new List<SlvoiceReply>(requestCount);
            for (int i = 0; i < results.Length; i++)
            {
                for (int j = 0; j < results[i].Count; j++)
                {
                    (PeerCtlSendResult result, SlvoiceReply reply) = results[i][j];
                    replies.Add(reply);
                    if (IsUnknownRoom(in reply))
                        refusals?.Refused(roomSends[i].Room, stamp);
                    if (Severity(result) > Severity(worst))
                        worst = result;
                    if (authority != null)
                        authority.OnBatchOutcome(roomSends[i].Room, op, roomSends[i].Stamps[j].Generation, roomSends[i].Stamps[j].Named,
                            result == PeerCtlSendResult.Ok, in reply);
                }
            }
            RecordStats(replies);
            return worst;
        }

        // One room's requests, in the order they are sent, with the arming stamp each was given.
        private sealed class RoomSend
        {
            public RoomSend(int room) { Room = room; }
            public readonly int Room;
            public readonly List<OSDMap> Requests = new List<OSDMap>();
            public readonly List<(int Room, uint Generation, List<UUID> Named)> Stamps = new List<(int Room, uint Generation, List<UUID> Named)>();
        }

        // Send a room's parts one after another: the mixer takes a room's policy generations in rising order, and a
        // listener's part must not overtake another's. A part that did not go through, or that the mixer answered
        // without applying (an unknown room, a stale epoch), ends the room's send; the parts after it would meet the same
        // answer. The parts not sent are repaired as a failed single request is: by the sender's next snapshot.
        private async Task<List<(PeerCtlSendResult Result, SlvoiceReply Reply)>> SendPartsAsync(RoomSend send)
        {
            var results = new List<(PeerCtlSendResult Result, SlvoiceReply Reply)>(send.Requests.Count);
            foreach (OSDMap request in send.Requests)
            {
                (PeerCtlSendResult result, SlvoiceReply reply) = await SendAndReadAsync(request).ConfigureAwait(false);
                results.Add((result, reply));
                if (result != PeerCtlSendResult.Ok || NotApplied(in reply))
                    break;
            }
            return results;
        }

        // The mixer answered, and the answer says it did not apply the request.
        private static bool NotApplied(in SlvoiceReply reply)
            => reply.Present && !string.Equals(reply.Status, "applied", StringComparison.Ordinal);

        /// <summary>
        /// Send one batch to ONE named room, as it is: no partitioning by where the sim thinks each agent is, nothing
        /// sent to the estate room, and no arming fields. For rooms the visibility feed does not own (a group call's or
        /// a conference's), which are not in the room table and have no declared authority; the mixer applies a mute
        /// batch to such a room all the same. The mixer's inner reply is returned so the caller can tell a mute that was
        /// applied from one the mixer does not understand.
        /// </summary>
        public async Task<(PeerCtlSendResult Result, SlvoiceReply Reply)> SendToRoomAsync(int roomNumber, VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute)
        {
            // A room's whole picture can be over the mixer's size limit (a mute of everyone in a full room is), so it goes
            // in as many requests as it takes, one after another. The answer is the last part's, with the counts of the parts
            // added up; a part that fails, or that the mixer does not apply, is the answer and the rest are not sent.
            var replies = new List<SlvoiceReply>();
            PeerCtlSendResult result = PeerCtlSendResult.Ok;
            SlvoiceReply answer = default;
            foreach (PeerCtlBatchPart piece in PeerCtlBatchSplitter.Split(op, excl ?? EmptyChannel, mute, roomNumber, false))
            {
                OSDMap request = PeerCtlBatchSerializer.BuildRequest(op, piece.Excl, piece.Mute);
                request["room"] = new OSDInteger(roomNumber);
                (result, SlvoiceReply reply) = await SendAndReadAsync(request).ConfigureAwait(false);
                answer = replies.Count == 0 ? reply : Accumulate(answer, reply);
                replies.Add(reply);
                if (result != PeerCtlSendResult.Ok || NotApplied(in reply))
                {
                    answer = reply;
                    break;
                }
            }
            RecordStats(replies);
            return (result, answer);
        }

        // The reply to a room sent in several parts: the latest part's answer, with the counts of all the parts so far.
        private static SlvoiceReply Accumulate(in SlvoiceReply earlier, in SlvoiceReply latest)
        {
            SlvoiceReply sum = latest;
            sum.Entries += earlier.Entries;
            sum.MuteEntries += earlier.MuteEntries;
            sum.Skipped += earlier.Skipped;
            sum.DeferredListeners += earlier.DeferredListeners;
            return sum;
        }

        // Stamp the room's epoch and next generation, and on add/remove the base generation the
        // sim believes the mixer holds for each named listener. Keys are appended after "room", so a knob-off request,
        // which never reaches here, keeps its exact key order.
        private static (int Room, uint Generation, List<UUID> Named) StampAuthority(VisAuthority authority, OSDMap request,
            VisOp op, int room, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> exclSlice,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteSlice)
        {
            var named = new List<UUID>(exclSlice.Keys);
            if (muteSlice != null)
                foreach (UUID l in muteSlice.Keys)
                    if (!exclSlice.ContainsKey(l))
                        named.Add(l);
            uint generation = authority.NextGeneration(room);
            request["room_epoch"] = OSD.FromString(authority.EpochString);
            request["policy_generation"] = OSD.FromInteger((int)generation);
            if (op != VisOp.Replace)
            {
                var baseMap = new OSDMap();
                foreach (UUID l in named)
                    baseMap[l.ToString()] = OSD.FromInteger((int)authority.ListenerGeneration(room, l));
                request["base"] = baseMap;
            }
            return (room, generation, named);
        }

        /// <summary>Send one peer_ctl_heartbeat and apply its reply to <see cref="Authority"/>.
        /// Never throws.</summary>
        public async Task<bool> SendHeartbeatAsync(OSDMap body)
        {
            AdminSendResult admin;
            string reply;
            try
            {
                (admin, reply) = await _sendOne(body).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} region {RegionName}: peer_ctl_heartbeat send failed", LogHeader, _region);
                return false;
            }
            bool ok = admin == AdminSendResult.Ok;
            VisAuthority.HeartbeatReply parsed = ok ? VisAuthority.ParseHeartbeatReply(reply) : new VisAuthority.HeartbeatReply();
            if (parsed.IsHeartbeat && parsed.VisProtocol > 0)
            {
                _mixerVisProtocol = parsed.VisProtocol;
                _mixerProtocolKnown = true;
            }
            Authority?.OnHeartbeatOutcome(ok, parsed);
            return ok;
        }

        /// <summary>A JSON array of UUID strings under <paramref name="key"/>, or null when absent. Unparseable entries
        /// are skipped.</summary>
        public static UUID[] ParseUuidList(OSDMap map, string key)
        {
            if (map == null || !(map.TryGetValue(key, out OSD o) && o is OSDArray arr))
                return null;
            var list = new List<UUID>(arr.Count);
            foreach (OSD item in arr)
                if (UUID.TryParse(item.AsString(), out UUID id) && id != UUID.Zero)
                    list.Add(id);
            return list.ToArray();
        }

        private async Task<List<(PeerCtlSendResult Result, SlvoiceReply Reply)>> SendGatedAsync(RoomSend send)
        {
            await _roomGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await SendPartsAsync(send).ConfigureAwait(false);
            }
            finally
            {
                _roomGate.Release();
            }
        }

        // Send one room's request, map the transport outcome, and (only on a janus:"success") parse and
        // LOG that room's inner slvoice reply. The PeerCtlSendResult is UNCHANGED by the inner reply: a
        // successful transport is Ok even if the inner reply reports skipped items or a non-applied
        // status -- that anomaly is reported (WARN) and surfaced in stats, never converted into a
        // transport/protocol failure (which would wrongly trip the sender's ProtocolError latch or
        // staleness guard, i.e. change behaviour). See ClassifyReply for the severity policy.
        private async Task<(PeerCtlSendResult Result, SlvoiceReply Reply)> SendAndReadAsync(OSDMap request)
        {
            (AdminSendResult admin, string body) = await _sendOne(request).ConfigureAwait(false);
            PeerCtlSendResult result = Map(admin);
            SlvoiceReply reply = default;
            if (admin == AdminSendResult.Ok)   // only a janus:"success" carries an inner slvoice reply
            {
                reply = ParseInnerReply(body);
                ObserveProtocol(in reply);
                int room = request.TryGetValue("room", out OSD ro) ? ro.AsInteger() : 0;
                LogReply(room, in reply);
            }
            return (result, reply);
        }

        // Sum this send's per-room inner replies into the LastSendStats snapshot (volatile ints,
        // single-writer: SendAsync is single-flight from the sender).
        private void RecordStats(IReadOnlyList<SlvoiceReply> replies)
        {
            int parsed = 0, deferred = 0, skipped = 0, entries = 0, muteEntries = 0, anomalies = 0;
            for (int i = 0; i < replies.Count; i++)
            {
                SlvoiceReply r = replies[i];
                if (r.Present) parsed++;
                deferred += r.DeferredListeners;
                skipped += r.Skipped;
                entries += r.Entries;
                muteEntries += r.MuteEntries;
                (bool warn, bool _) = ClassifyReply(in r);
                if (warn) anomalies++;
            }
            _statRepliesParsed = parsed;
            _statDeferred = deferred;
            _statSkipped = skipped;
            _statEntries = entries;
            _statMuteEntries = muteEntries;
            _statAnomalies = anomalies;
        }

        // Deliberate severities: deferred_listeners>0 is the join-window deferral self-heal working
        // as designed -> INFO, not a fault. skipped>0, a non-applied inner status, or a malformed inner
        // reply is real loss or protocol drift -> WARN. An absent inner reply (old mixer) or an all-zero
        // applied reply (quiet steady state) logs NOTHING.
        public static (bool Warn, bool Info) ClassifyReply(in SlvoiceReply reply)
        {
            if (!reply.Present && !reply.Malformed)
                return (false, false);   // absent: no info, no log (old mixer / empty)
            bool applied = reply.Present && string.Equals(reply.Status, "applied", StringComparison.Ordinal);
            bool warn = reply.Malformed || !applied || reply.Skipped > 0;
            bool info = reply.DeferredListeners > 0;
            return (warn, info);
        }

        private static bool IsUnknownRoom(in SlvoiceReply reply)
            => reply.Reason == "unknown_room" || reply.StatusField == "unknown_room";

        private void LogReply(int room, in SlvoiceReply reply)
        {
            (bool warn, bool info) = ClassifyReply(in reply);
            if (warn)
            {
                // The first unknown_room for a room that never answered, or whose last listener left, is expected: the
                // mixer removes an empty room and the room comes back with the next provision. The authority knows
                // which that is; without one every reply is reported as before.
                bool unknownRoom = IsUnknownRoom(in reply);
                if (unknownRoom && Authority?.ExpectsUnknownRoom(room) == true)
                    m_log.LogDebug("{LogHeader} region {RegionName} room {Room}: peer_ctl_batch reply unknown_room; the mixer " +
                        "has not created this room yet, or removed it once it stood empty [{Raw}]",
                        LogHeader, _region, room, reply.RawSummary);
                else
                    m_log.LogWarning("{LogHeader} region {RegionName} room {Room}: peer_ctl_batch inner-reply anomaly " +
                        "(status={Status}, skipped={Skipped}, malformed={Malformed}) [{Raw}]",
                        LogHeader, _region, room, reply.Status ?? "(none)", reply.Skipped, reply.Malformed, reply.RawSummary);
            }
            if (info)
                m_log.LogDebug("{LogHeader} region {RegionName} room {Room}: mixer deferred {Deferred} listener " +
                    "entr(y/ies) for not-yet-joined listener(s) (join-window self-heal; replays on join, not a fault)",
                    LogHeader, _region, room, reply.DeferredListeners);
        }

        /// <summary>Parsed mixer inner reply for one room. All-default = absent (old mixer): logs
        /// nothing, contributes zero. Public so the pure ParseInnerReply / ClassifyReply are unit-testable.</summary>
        public struct SlvoiceReply
        {
            public bool Present;              // an inner "response" object was found
            public bool Malformed;            // janus:success but the inner reply is not the expected shape
            public string Status;             // inner "slvoice" value ("applied"/"error"/"empty"/...), or null
            public int Entries;
            public int MuteEntries;
            public int Skipped;
            public int DeferredListeners;
            public string RawSummary;         // compact inner payload for the WARN log
            // Absent from an older mixer, which leaves them default.
            public int VisProtocol;           // "vis_protocol"; 0 when absent
            public string MixerInstance;      // "mixer_instance"
            public string StatusField;        // "status": ok | stale_epoch | unknown_room | undeclared_room
            public string Reason;             // "reason", e.g. unknown_room on slvoice:"error"
            public string AuthorityEpoch;     // "authority_epoch"
            public UUID[] StaleListeners;     // "stale_listeners"
            public UUID[] UnarmedListeners;   // "unarmed_listeners"
        }

        /// <summary>Parse the mixer's peer_ctl_batch inner reply. The plugin response is nested under the
        /// top-level "response" key: {janus:success, response:{slvoice, op, room, entries, mute_entries,
        /// skipped, deferred_listeners}} (mixer janus_slvoice.c:1552-1557 + deferred_listeners from 27977c8).
        /// EVERY inner field is optional: absent (old / pre-mute mixer) yields a default (Present=false)
        /// reply that logs nothing and contributes zero. A
        /// janus:"success" with no/!object "response", or a "response" with no "slvoice", is flagged
        /// Malformed (protocol drift). Pure and unit-testable.</summary>
        public static SlvoiceReply ParseInnerReply(string body)
        {
            OSDMap top;
            try { top = OSDParser.DeserializeJson(body ?? string.Empty) as OSDMap; }
            catch { top = null; }
            if (top == null)
                return default;   // unparseable/empty at the top level: the transport layer already handled it
            if (!(top.TryGetValue("response", out OSD ro) && ro is OSDMap resp))
                return new SlvoiceReply { Present = false, Malformed = true, RawSummary = Summarize(top) };
            string status = resp.TryGetValue("slvoice", out OSD st) ? st.AsString() : null;
            return new SlvoiceReply
            {
                Present = true,
                Malformed = string.IsNullOrEmpty(status),
                Status = status,
                Entries = resp.TryGetValue("entries", out OSD e) ? e.AsInteger() : 0,
                MuteEntries = resp.TryGetValue("mute_entries", out OSD me) ? me.AsInteger() : 0,
                Skipped = resp.TryGetValue("skipped", out OSD sk) ? sk.AsInteger() : 0,
                DeferredListeners = resp.TryGetValue("deferred_listeners", out OSD dl) ? dl.AsInteger() : 0,
                RawSummary = Summarize(resp),
                VisProtocol = resp.TryGetValue("vis_protocol", out OSD vp) ? vp.AsInteger() : 0,
                MixerInstance = resp.TryGetValue("mixer_instance", out OSD mi) ? mi.AsString() : null,
                StatusField = resp.TryGetValue("status", out OSD sf) ? sf.AsString() : null,
                Reason = resp.TryGetValue("reason", out OSD rs) ? rs.AsString() : null,
                AuthorityEpoch = resp.TryGetValue("authority_epoch", out OSD ae) ? ae.AsString() : null,
                StaleListeners = ParseUuidList(resp, "stale_listeners"),
                UnarmedListeners = ParseUuidList(resp, "unarmed_listeners"),
            };
        }

        // Compact, capped one-line summary of an inner reply map for the WARN log (a surprise payload
        // must not flood the log).
        private static string Summarize(OSDMap map)
        {
            if (map == null) return "(none)";
            string str = map.ToString();
            const int capLen = 300;
            return str.Length <= capLen ? str : str.Substring(0, capLen) + "\u2026";
        }

        /// <summary>Severity order: ProtocolError beats TransportError beats NotApplied beats Ok.
        /// NotApplied slots in at 1, renumbering nothing.</summary>
        private static int Severity(PeerCtlSendResult r)
        {
            switch (r)
            {
                case PeerCtlSendResult.Ok: return 0;
                case PeerCtlSendResult.TransportError: return 2;
                case PeerCtlSendResult.ProtocolError: return 3;
                default: return 3;
            }
        }

        private static PeerCtlSendResult Map(AdminSendResult r)
        {
            switch (r)
            {
                case AdminSendResult.Ok:
                    return PeerCtlSendResult.Ok;
                case AdminSendResult.ProtocolError:
                    return PeerCtlSendResult.ProtocolError;
                case AdminSendResult.TransportError:
                default:
                    return PeerCtlSendResult.TransportError;
            }
        }

        // Which rooms a tick actually addressed is the one thing no other instrument reports: the
        // mixer's reply cannot say and the wire carries no per-region view. Debug, because
        // it is per tick.
        // The line reports BOTH channels. "addressed N" is the room UNION (rooms that got any channel); the
        // per-room breakdown shows excl+mute listener counts, and both channels' fallback counts are
        // logged. Otherwise a mute-only op would log "addressed 0 rooms / fallback 0" while the mute
        // was delivered to its room.
        private void LogRoomsAddressed(VisOp op, PeerCtlBatchPartition part, PeerCtlBatchPartition mutePart, int roomsAddressed, int requests)
        {
            if (!m_log.IsEnabled(LogLevel.Debug))
                return;

            var roomKeys = new SortedSet<int>();
            foreach (int r in part.Rooms.Keys) roomKeys.Add(r);
            foreach (int r in mutePart.Rooms.Keys) roomKeys.Add(r);

            var sb = new StringBuilder();
            foreach (int roomKey in roomKeys)
            {
                int exclCount = part.Rooms.TryGetValue(roomKey, out var es) ? es.Count : 0;
                int muteCount = mutePart.Rooms.TryGetValue(roomKey, out var ms) ? ms.Count : 0;
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(roomKey).Append(":excl").Append(exclCount).Append("+mute").Append(muteCount);
            }
            if (sb.Length == 0)
                sb.Append("none");

            m_log.LogDebug("{LogHeader} region {RegionName}: {Op} addressed {RoomCount} room(s) in {Requests} request(s) [room:excl+mute {Rooms}]; " +
                "unplaced excl listeners {FbExclL}/sources {FbExclS}, mute listeners {FbMuteL}/sources {FbMuteS} (estate room {EstateRoom})",
                LogHeader, _region, PeerCtlBatchSerializer.OpString(op), roomsAddressed, requests, sb.ToString(),
                part.FallbackListeners, part.FallbackSources, mutePart.FallbackListeners, mutePart.FallbackSources, _fallbackRoom);
        }
    }
}
