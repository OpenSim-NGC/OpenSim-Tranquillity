/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * VisibilityBatchSender — orchestrates emission of the feeder's per-tick VisibilityBatch to the
 * mixer through an IPeerCtlBatchSink. Backend-agnostic (no Janus / no room number here — the sink
 * stamps the room). Driven off the VoiceVisibilityService tick via Pump(): fire-and-forget, never
 * awaited on the tick thread, never throws into it, single-flight.
 *
 * Emission paths (Docs/voice/mixer-feed-protocol.md):
 *  - steady-state DELTA: <=1 add message + 1 remove message per tick (the bound is per-op);
 *  - SNAPSHOT (replace-all) on (re)connect / full-rebuild: sets _synced + _knownListeners, with
 *    clear-tracking — a listener dropped from Current gets an explicit empty replace, because op
 *    scoping is per-listener and omission is NOT a clear;
 *  - per-listener JOIN replace (pending set): a DISTINCT path — it does NOT set _synced, NOT reset
 *    _knownListeners, NOT send the room. Triggered by WebRTC provisioning-success. Because the mixer
 *    silently drops a batch entry for a listener not yet in the room (:958) and exposes no admin
 *    room-membership query, this uses BOUNDED BLIND RE-SEND: the listener's replace re-sent once per
 *    tick up to PendingJoinMaxAttempts, then one loud give-up log (the silent-drop failure, made
 *    loud on our side). replace is listener-scoped + idempotent, so re-sends are safe.
 *
 * A ProtocolError from the sink is a config/format error (e.g. wrong AdminAPIToken / wrong plugin
 * name / broken transport in front). It latches emission off (per-region, cleared only by a service
 * rebuild / region-server restart) with one loud log — but only after ProtocolErrorLatchThreshold
 * CONSECUTIVE ProtocolErrors, not the first. Any Ok resets the run to zero; a persistent fault trips
 * all K within a second, while a lone transient stray-200 no longer permanently disables a region.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice
{
    public sealed class VisibilityBatchSender
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[VISIBILITY SENDER]";

        /// <summary>How many agents the last arming pass could not place, and so left out.</summary>
        public int Unplaced => _unplaced;

        /// <summary>Bounded blind re-sends of a joining listener's replace — no admin membership
        /// query exists to confirm presence, so we re-send this many ticks then give up loudly.</summary>
        public const int PendingJoinMaxAttempts = 6;

        /// <summary>Consecutive ProtocolErrors before emission latches off. Not the first: a single
        /// transient stray-200 must not permanently disable a region. Every fault that actually
        /// reaches ProtocolError (wrong admin_secret / wrong plugin name / broken transport in front)
        /// is persistent, so it trips all K within a second (a few ticks); a one-off does not.</summary>
        public const int ProtocolErrorLatchThreshold = 3;

        /// <summary>Self-heal a send stuck in-flight after this many admin-timeouts. Set to 8x so it
        /// sits strictly beyond JanusAdminClient's 4x HttpClient backstop — a send still in flight at
        /// 8x means the completion path itself failed (the finally never ran), not just a slow send.
        /// This turns the permanent silent wedge into a bounded, logged hiccup.</summary>
        public const int StaleInFlightMultiple = 8;

        private readonly IVisibilityFeed _feed;
        private readonly IPeerCtlBatchSink _sink;   // null => no sink registered; no-op (logged once)
        private readonly bool _enabled;

        // Single-flight is now epoch-based: 0 = idle; otherwise the epoch id of the in-flight send.
        // The finally clears the flag via CAS-against-its-own-epoch, so a late-completing abandoned
        // send (force-cleared by the staleness guard) can never clear a NEWER send's ownership.
        private long _sendInFlight;                 // 0 or the in-flight send's epoch (Interlocked)
        private long _sendEpochSeq;                 // monotonic; each acquire claims ++ as its epoch id
        private long _sendStartedAtMs;              // clock when the current send acquired (advisory; see guard)
        private volatile bool _synced;
        private volatile bool _protocolFailed;      // latched after K consecutive ProtocolErrors; stops emission
        private int _consecutiveProtocolErrors;     // touched only on RunAsync's thread (single-flight), like _knownListeners
        private bool _loggedNoSink;

        private readonly long _staleThresholdMs;    // force-heal an in-flight send after this long (StaleInFlightMultiple x admin)
        private readonly long _adminTimeoutMs;      // for the guard log
        private readonly string _region;            // for the guard log
        private readonly Func<long> _nowMs;         // monotonic clock (injectable for tests)

        private readonly HashSet<UUID> _knownListeners = new HashSet<UUID>();   // touched only on RunAsync's thread
        // Parallel clear-tracking for the MUTE channel: a listener previously sent a mute set that is
        // now empty must get an explicit empty-mute replace on the next snapshot (omission is not a
        // clear). Independent of _knownListeners because a listener may have mutes but no excl.
        private readonly HashSet<UUID> _knownMuteListeners = new HashSet<UUID>();
        private readonly object _pendingLock = new object();
        private readonly Dictionary<UUID, int> _pending = new Dictionary<UUID, int>();   // listener -> attempts left

        // Arming mode ([WebRtcVoice] VisibilityArmingEnabled). Null
        // authority = the knob is off, and every method below takes its pre-0.2 path unchanged.
        private readonly VisAuthority _authority;
        private readonly Func<UUID, int?> _resolveRoom;  // recorded room, else the resolved parcel room, else null
        private int _unplaced;                          // agents left out of the last pass, for the reader
        private long _heartbeatInFlight;                 // the heartbeat's OWN single-flight, 0 or 1
        private long _lastHeartbeatMs;
        private bool _loggedHeartbeatStart;

        // Heartbeat log volume. At one send per region per second the per-tick DEBUG line was
        // ~10,800/hour/region -- it fills an operator's console, bloats the log and slows every later grep,
        // and a shadow soak runs for hours. State for "log a change, log every failure, summarise the rest".
        private const int HeartbeatSummaryMs = 60_000;
        private int _hbLastRooms = -1;        // -1 so the first send always counts as a change
        private int _hbLastListeners = -1;
        private bool _hbLastOk = true;
        private long _hbWindowStartMs;
        private int _hbSends;
        private int _hbNonOk;

        /// <param name="authority">Non-null only when arming is enabled; requires <paramref name="resolveRoom"/>.</param>
        /// <param name="resolveRoom">The room each agent is addressed at (its record, else the fallback room).</param>
        public VisibilityBatchSender(IVisibilityFeed feed, IPeerCtlBatchSink sink, bool enabled,
            TimeSpan? adminTimeout = null, string region = null, Func<long> nowMs = null,
            VisAuthority authority = null, Func<UUID, int?> resolveRoom = null)
        {
            if (authority != null && resolveRoom == null)
                throw new ArgumentNullException(nameof(resolveRoom), "arming needs the room resolver");
            _authority = authority;
            _resolveRoom = resolveRoom;
            _feed = feed;
            _sink = sink;
            _enabled = enabled;
            _region = region ?? "?";
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            TimeSpan admin = (adminTimeout is TimeSpan t && t > TimeSpan.Zero) ? t : TimeSpan.FromSeconds(5);
            _adminTimeoutMs = (long)admin.TotalMilliseconds;
            _staleThresholdMs = _adminTimeoutMs * StaleInFlightMultiple;
        }

        /// <summary>Trigger (correction 1): call on WebRTC provisioning-success for a listener. Adds
        /// it to the pending-join set so its full column is (re)sent until present / attempts exhaust.
        /// Distinct from the recovery triggers.</summary>
        public void OnListenerProvisioned(UUID listener)
        {
            if (!_enabled || _protocolFailed || listener == UUID.Zero)
                return;
            if (_authority != null)
            {
                // Arm at the recorded room on the next pass, even with empty columns.
                _authority.RequestArm(listener);
                return;
            }
            lock (_pendingLock)
                _pending[listener] = PendingJoinMaxAttempts;
        }

        /// <summary>Called once per feeder tick with that tick's batch. Fire-and-forget; never blocks
        /// or throws on the tick thread. Single-flight: while a send is in flight, skip this tick and
        /// force a snapshot next (a skipped delta must not cause drift).</summary>
        public void Pump(VisibilityBatch batch) => _ = PumpAsync(batch);   // fire-and-forget on the tick thread

        /// <summary>The awaitable core of Pump — production uses the fire-and-forget void overload;
        /// tests await this for determinism. Returns a completed task when it no-ops or is skipped by
        /// single-flight; otherwise the send task (which clears the in-flight flag in its finally).</summary>
        public Task PumpAsync(VisibilityBatch batch)
        {
            if (!_enabled || _protocolFailed)
                return Task.CompletedTask;
            if (_sink == null)
            {
                LogNoSinkOnce();
                return Task.CompletedTask;
            }
            long myEpoch = Interlocked.Increment(ref _sendEpochSeq);   // >=1, unique to this attempt
            if (Interlocked.CompareExchange(ref _sendInFlight, myEpoch, 0L) != 0L)
            {
                // A send is already in flight. Self-heal if it has been stuck far longer than
                // possible (FIX 2) — neither the per-call token nor the HttpClient backstop resolved it.
                ForceClearStalledSend();
                _synced = false;   // a skipped tick -> snapshot next
                return Task.CompletedTask;
            }
            // Acquired this epoch. Record the start AFTER claiming the flag (still on the pump/tick
            // thread, so no other Pump interleaves before this write).
            Volatile.Write(ref _sendStartedAtMs, _nowMs());
            return RunAsync(batch, myEpoch);   // NOT awaited by Pump (the void wrapper); tests may await it
        }

        private async Task RunAsync(VisibilityBatch batch, long epoch)
        {
            try
            {
                if (_authority != null)
                {
                    await EmitArmedAsync(batch).ConfigureAwait(false);
                }
                else
                {
                    await DrainPendingAsync().ConfigureAwait(false);
                    await EmitMainAsync(batch).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // Never throw into the tick. EnsureDisjoint's feeder-bug throw lands here too — its
                // message names the offending (listener, source).
                m_log.LogError(e, $"{LogHeader} emit failed (matrix kept, will re-derive)");
            }
            finally
            {
                // Release ONLY if we still own the flag. If the staleness guard force-cleared us and a
                // newer send took over, _sendInFlight holds a different epoch and this CAS no-ops — so
                // a late completion of an abandoned send cannot clear a newer send's ownership.
                Interlocked.CompareExchange(ref _sendInFlight, 0L, epoch);
            }
        }

        // FIX 2: self-heal a send stuck in-flight far longer than possible. Called on a skipped Pump
        // (a send is already in flight). Logs once per stall episode — the CAS-clear resolves the
        // stall, so the next detection is a fresh episode — never once per tick.
        private void ForceClearStalledSend()
        {
            long inflight = Interlocked.Read(ref _sendInFlight);
            if (inflight == 0L)
                return;   // raced to idle; nothing in flight
            long elapsed = _nowMs() - Volatile.Read(ref _sendStartedAtMs);
            if (elapsed <= _staleThresholdMs)
                return;   // slow but within budget; a normal single-flight skip

            // Torn-read safety: the timestamp read above may belong to a NEWER send if the in-flight
            // send rotated between our two reads. We neutralise that by force-clearing with a CAS
            // against the epoch we measured — if the epoch moved, the CAS fails and we do nothing (no
            // erroneous clear, no log). The flag+epoch is a single Interlocked long, so there is no
            // torn STATE; the separate timestamp is advisory and any staleness is caught by this CAS.
            if (Interlocked.CompareExchange(ref _sendInFlight, 0L, inflight) != inflight)
                return;

            m_log.LogError("{LogHeader} region {RegionName}: peer_ctl_batch send stuck in-flight {InFlightMs}ms (self-heal " +
                "threshold {StaleThresholdMs}ms = {StaleMultiple}x the {AdminTimeoutMs}ms admin timeout). Emission was STALLED — neither the " +
                "per-call token nor the HttpClient backstop resolved it. Force-clearing the in-flight " +
                "flag and re-syncing (snapshot next); the abandoned send is left to complete or hang " +
                "harmlessly.", LogHeader, _region, elapsed, _staleThresholdMs, StaleInFlightMultiple, _adminTimeoutMs);
            _synced = false;   // abandoned send's applied-state is unknown -> full snapshot next
        }

        // ---- arming mode ----
        // Every pass: arm whoever is not armed at their current room, then send deltas for listeners already armed.
        // "Arm" is a replace naming the listener with its full columns, empty ones included. A snapshot (first pass,
        // transport error, skipped tick, in-flight guard, or a detected mixer restart) arms the whole population, and
        // replaces the pre-0.2 clear-tracking: a departed listener is left to the heartbeat's omission rule, because an
        // empty replace for it would ARM it.
        private async Task EmitArmedAsync(VisibilityBatch batch)
        {
            VisibilityMatrix cur = _feed.Current;
            IReadOnlyList<UUID> population = cur.Population;
            _authority.PruneTo(population, _resolveRoom);
            bool mixerRestarted = _authority.TakeRearmAll();
            if (mixerRestarted)
                _synced = false;
            bool snapshot = !_synced;

            var arm = new List<UUID>();
            int unplaced = 0;
            foreach (UUID l in population)
            {
                // An agent the sim cannot place - no room record and no parcel resolved for it -
                // is left OUT of the batch and the heartbeat rather than addressed at a guessed number. It is counted
                // so the state is loud instead of silent, and it is picked up as soon as either source answers.
                int? room = _resolveRoom(l);
                if (room is null)
                {
                    unplaced++;
                    continue;
                }
                // CanArmNow gates EVERY path, including a standing re-arm request and a
                // snapshot. A connector NPC never leaves the population, so its re-arm request never expired and the
                // first clause re-armed it into a missing room on every tick — 8,933 unknown_room lines at ~3.8/s in
                // the 0.8 soak, with the authority's own backoff sitting there unread.
                if ((snapshot || _authority.IsRearmRequested(l) || !_authority.IsArmed(room.Value, l))
                    && _authority.CanArmNow(l))
                    arm.Add(l);
            }
            _unplaced = unplaced;
            if (unplaced > 0)
                m_log.LogDebug("{LogHeader} region {RegionName}: {Count} agent(s) not addressed this pass (no room record " +
                    "and no parcel resolved); they are omitted, never sent to a guessed room", LogHeader, _region, unplaced);

            if (arm.Count > 0)
            {
                var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>(arm.Count);
                var mute = new Dictionary<UUID, IReadOnlyCollection<UUID>>(arm.Count);
                int emptyColumns = 0;
                foreach (UUID l in arm)
                {
                    var e = new List<UUID>(cur.ExcludedFor(l));
                    var m = new List<UUID>(cur.MutedFor(l));
                    if (e.Count == 0 && m.Count == 0)
                        emptyColumns++;
                    excl[l] = e;
                    mute[l] = m;
                }
                PeerCtlSendResult r = await _sink.SendAsync(VisOp.Replace, excl, mute).ConfigureAwait(false);
                switch (r)
                {
                    case PeerCtlSendResult.Ok:
                        NoteOk();
                        _synced = true;
                        m_log.LogInformation("{LogHeader} region {RegionName}: arming replace sent for {Count} listener(s), " +
                            "{EmptyCount} with empty columns, epoch {Epoch} ({Reason})", LogHeader, _region, arm.Count, emptyColumns,
                            _authority.EpochString, mixerRestarted ? "mixer restarted" : snapshot ? "snapshot" : "new, moved, provisioned or reported by the mixer");
                        break;
                    case PeerCtlSendResult.TransportError:
                        _synced = false;
                        return;
                    case PeerCtlSendResult.ProtocolError:
                    default:
                        NoteProtocolError("arming replace");
                        _synced = false;
                        return;
                }
            }
            else if (snapshot)
            {
                _synced = true;   // an empty population: nothing to arm
            }

            if (batch == null || batch.IsEmpty)
                return;
            var justArmed = new HashSet<UUID>(arm);
            Dictionary<UUID, IReadOnlyCollection<UUID>> added = ArmedOnly(batch.Added, justArmed);
            Dictionary<UUID, IReadOnlyCollection<UUID>> removed = ArmedOnly(batch.Removed, justArmed);
            Dictionary<UUID, IReadOnlyCollection<UUID>> muteAdded = ArmedOnly(batch.MuteAdded, justArmed);
            Dictionary<UUID, IReadOnlyCollection<UUID>> muteRemoved = ArmedOnly(batch.MuteRemoved, justArmed);
            PeerCtlBatchSerializer.EnsureDisjoint(added, removed);
            PeerCtlBatchSerializer.EnsureDisjoint(muteAdded, muteRemoved);
            bool ok = true;
            if (added.Count > 0 || muteAdded.Count > 0)
                ok = await SendMappedAsync(VisOp.Add, added, muteAdded).ConfigureAwait(false);
            if (ok && (removed.Count > 0 || muteRemoved.Count > 0))
                await SendMappedAsync(VisOp.Remove, removed, muteRemoved).ConfigureAwait(false);
        }

        // A delta entry is sent only for a listener armed at its room before this pass: a listener armed this pass
        // already got its full column, and an unarmed one gets it when it is armed.
        private Dictionary<UUID, IReadOnlyCollection<UUID>> ArmedOnly(
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> map, HashSet<UUID> justArmed)
        {
            var result = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in map)
            {
                if (justArmed.Contains(kv.Key))
                    continue;
                int? room = _resolveRoom(kv.Key);
                if (room is not null && _authority.IsArmed(room.Value, kv.Key))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>Sends one peer_ctl_heartbeat when arming is on, the mixer has advertised vis_protocol 2, and no
        /// heartbeat is already in flight. Independent of the batch single-flight. Fire-and-forget; never throws.
        ///
        /// The CALLER decides how often this runs - VoiceVisibilityService's heartbeat timer, every
        /// VisAuthority.HeartbeatIntervalMs. It used to be called from the feeder tick (four times as often) and
        /// throttled here, which is why the throttle existed; with the timer owning cadence the throttle only
        /// dropped beats, so it is gone. Calling this faster than the interval now really does send faster.</summary>
        public void PumpHeartbeat() => _ = PumpHeartbeatAsync(false);

        /// <summary>The awaitable core of <see cref="PumpHeartbeat"/>. <paramref name="stopping"/> sends the graceful-stop
        /// heartbeat ("state":"stopping") at once, ignoring the interval and the in-flight flag.</summary>
        public Task PumpHeartbeatAsync(bool stopping = false)
        {
            if (_authority == null || !_enabled || _protocolFailed || !(_sink is IPeerCtlHeartbeatSink heartbeatSink))
                return Task.CompletedTask;
            if (!_authority.HeartbeatCapable)
                return Task.CompletedTask;
            if (!stopping)
            {
                // NO interval gate here. The caller's timer owns the cadence, and two owners of one
                // cadence can only subtract ticks: the timer fires every HeartbeatIntervalMs, and a callback
                // landing a hair UNDER that (scheduler jitter, which is normal) was refused by a gate measured
                // from the previous send's START, so that beat was dropped and the next arrived a whole period
                // later. MEASURED live over 21 minutes before this change: 0.88 heartbeats/s against an intended
                // 1.0, worst gap 1987 ms - almost exactly two periods, i.e. one skipped beat, not starvation.
                // Single-flight still stands: it stops two sends OVERLAPPING, which is a different question from
                // how often to send. _lastHeartbeatMs is kept for diagnostics only.
                if (Interlocked.CompareExchange(ref _heartbeatInFlight, 1L, 0L) != 0L)
                    return Task.CompletedTask;
                _lastHeartbeatMs = _nowMs();
            }
            OSDMap body = _authority.BuildHeartbeat(_feed.Current.Population, _resolveRoom, stopping);
            return SendHeartbeatAsync(heartbeatSink, body, stopping);
        }

        private async Task SendHeartbeatAsync(IPeerCtlHeartbeatSink heartbeatSink, OSDMap body, bool stopping)
        {
            try
            {
                bool ok = await heartbeatSink.SendHeartbeatAsync(body).ConfigureAwait(false);
                int rooms = 0, listeners = 0;
                if (body["rooms"] is OSDMap roomMap)
                {
                    rooms = roomMap.Count;
                    foreach (KeyValuePair<string, OSD> kv in roomMap)
                        if (kv.Value is OSDMap entry && entry["listeners"] is OSDMap ls)
                            listeners += ls.Count;
                }
                if (stopping)
                    m_log.LogInformation("{LogHeader} region {RegionName}: stopping heartbeat sent for {Rooms} room(s), epoch {Epoch}: {Result}",
                        LogHeader, _region, rooms, _authority.EpochString, ok ? "ok" : "transport failed");
                else if (ok && !_loggedHeartbeatStart)
                {
                    _loggedHeartbeatStart = true;
                    m_log.LogInformation("{LogHeader} region {RegionName}: first peer_ctl_heartbeat acknowledged: {Rooms} room(s), " +
                        "{Listeners} listener(s), epoch {Epoch}, every {Interval} ms", LogHeader, _region, rooms, listeners,
                        _authority.EpochString, VisAuthority.HeartbeatIntervalMs);
                }
                // A steady-state heartbeat is one line a MINUTE, not one a second. Three cases, and
                // the middle one is the point: every NON-OK reply still logs at its old volume, because a
                // failure must never be summarised away. A change in room/listener count or in the outcome
                // logs as it happens. Everything else -- the steady state -- is folded into one summary a
                // minute carrying the counts, so a soak leaves a readable log instead of ~10,800 lines/hour.
                bool changed = rooms != _hbLastRooms || listeners != _hbLastListeners || ok != _hbLastOk;
                long hbNow = _nowMs();
                if (_hbWindowStartMs == 0)
                    _hbWindowStartMs = hbNow;
                _hbSends++;
                if (!ok)
                    _hbNonOk++;
                if (!ok || changed)
                {
                    m_log.LogDebug("{LogHeader} region {RegionName}: peer_ctl_heartbeat {Rooms} room(s), {Listeners} listener(s): {Result}",
                        LogHeader, _region, rooms, listeners, ok ? "ok" : "transport failed");
                    _hbLastRooms = rooms;
                    _hbLastListeners = listeners;
                    _hbLastOk = ok;
                }
                if (hbNow - _hbWindowStartMs >= HeartbeatSummaryMs)
                {
                    m_log.LogDebug("{LogHeader} region {RegionName}: peer_ctl_heartbeat summary: {Sends} send(s) over {WindowS} s, " +
                        "{NonOk} not ok; now {Rooms} room(s), {Listeners} listener(s), epoch {Epoch}",
                        LogHeader, _region, _hbSends, (hbNow - _hbWindowStartMs) / 1000, _hbNonOk, rooms, listeners,
                        _authority.EpochString);
                    _hbWindowStartMs = hbNow;
                    _hbSends = 0;
                    _hbNonOk = 0;
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} region {RegionName}: heartbeat failed", LogHeader, _region);
            }
            finally
            {
                if (!stopping)
                    Interlocked.Exchange(ref _heartbeatInFlight, 0L);
            }
        }

        // ---- per-listener JOIN path (bounded blind re-send; distinct from _synced/_knownListeners) ----
        private async Task DrainPendingAsync()
        {
            List<UUID> due;
            lock (_pendingLock)
                due = new List<UUID>(_pending.Keys);
            foreach (UUID listener in due)
            {
                // Vacuous-confirm guard: a listener whose excl AND mute columns are both empty enforces
                // nothing at the mixer, so there is no silent-drop to detect. Drop it from the pending
                // set WITHOUT a send or the give-up warning (which would otherwise fire on every login
                // for every avatar and drown the real dropped-ban signal). Guarded HERE at first drain,
                // not at enqueue: OnListenerProvisioned runs on the WebRTC provisioning-callback thread
                // (WebRtcVoiceRegionModule.cs:581 -> VoiceVisibilityService.cs:148), whereas the feed's
                // column accessors read the un-synchronized _feed._current and are by design touched
                // only on RunAsync's thread. DrainPendingAsync already runs there and reads the columns
                // anyway, so the check is free and thread-correct. A late ban/mute that appears before
                // the listener joins is delivered by the steady-state delta path, not here.
                IReadOnlyCollection<UUID> exclCol = ColumnFor(listener);
                IReadOnlyCollection<UUID> muteCol = MuteColumnFor(listener);
                if (exclCol.Count == 0 && muteCol.Count == 0)
                {
                    lock (_pendingLock)
                        _pending.Remove(listener);
                    m_log.LogDebug("{LogHeader} listener {ListenerId}: empty excl+mute column at join; " +
                        "nothing to confirm, skipping pending re-send", LogHeader, listener);
                    continue;
                }
                var one = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = exclCol };
                // Re-send the joining listener's FULL state in BOTH channels, so a late joiner under a
                // sticky moderation mute inherits it (its mute column travels with the replace).
                var oneMute = muteCol.Count > 0
                    ? new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = muteCol }
                    : null;
                PeerCtlSendResult r = await _sink.SendAsync(VisOp.Replace, one, oneMute).ConfigureAwait(false);
                if (r == PeerCtlSendResult.ProtocolError)
                {
                    NoteProtocolError("per-listener join replace");   // may or may not latch (K consecutive)
                    return;   // stop this tick's drain either way; do not count the attempt down (it did not apply)
                }
                if (r == PeerCtlSendResult.Ok)
                {
                    NoteOk();
                    // Invariant repair: the pending-join path just SET mod_muted on the mixer for
                    // this listener (a non-empty oneMute), but it is NOT the main snapshot/delta path
                    // and so would otherwise leave _knownMuteListeners unaware of it. Record it here,
                    // gated on the send SUCCEEDING, so a later snapshot's clear-tracking will emit the
                    // explicit empty-mute clear for this listener if it is unmuted while the main path
                    // is unsynced. Without this, that unmute could strand mod_muted for the voice session.
                    // (Touched only on RunAsync's thread, like _knownMuteListeners elsewhere.)
                    if (oneMute != null)
                        _knownMuteListeners.Add(listener);
                }
                bool giveUp = false;
                lock (_pendingLock)
                {
                    if (!_pending.TryGetValue(listener, out int left))
                        continue;   // removed elsewhere
                    // Ok != applied (listener may not be in the room yet); count down regardless.
                    left--;
                    if (left <= 0)
                    {
                        _pending.Remove(listener);
                        giveUp = true;
                    }
                    else
                    {
                        _pending[listener] = left;
                    }
                }
                if (giveUp)
                    m_log.LogWarning("{LogHeader} listener {ListenerId}: full column re-sent {ReSendAttempts}x but never confirmed in the room; " +
                        "GIVING UP — its exclusions may be absent at the mixer (silent-drop, made loud here)",
                        LogHeader, listener, PendingJoinMaxAttempts);
            }
        }

        // ---- snapshot / delta main path ----
        private async Task EmitMainAsync(VisibilityBatch batch)
        {
            if (!_synced)
            {
                await SendSnapshotAsync().ConfigureAwait(false);
                return;
            }

            // Steady-state delta: at most one add + one remove message (per-op bound). Each op
            // now carries BOTH channels (excl + mute) in one message. Disjointness is checked per
            // channel; the two channels are independently disjoint (a source is never in both add and
            // remove of the SAME channel in one tick), and excl-vs-mute disjointness is guaranteed
            // source-side by VisibilityMatrix.Build (ban wins).
            PeerCtlBatchSerializer.EnsureDisjoint(batch.Added, batch.Removed);
            PeerCtlBatchSerializer.EnsureDisjoint(batch.MuteAdded, batch.MuteRemoved);
            bool ok = true;
            if (batch.Added.Count > 0 || batch.MuteAdded.Count > 0)
                ok = await SendMappedAsync(VisOp.Add, batch.Added, batch.MuteAdded).ConfigureAwait(false);
            if (ok && (batch.Removed.Count > 0 || batch.MuteRemoved.Count > 0))
                ok = await SendMappedAsync(VisOp.Remove, batch.Removed, batch.MuteRemoved).ConfigureAwait(false);
            if (ok)
                RefreshKnownListeners();
        }

        private async Task SendSnapshotAsync()
        {
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var mute = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            VisibilityMatrix cur = _feed.Current;
            var nowListeners = new HashSet<UUID>();
            var nowMuteListeners = new HashSet<UUID>();
            foreach (UUID listener in cur.Listeners)
            {
                excl[listener] = new List<UUID>(cur.ExcludedFor(listener));
                nowListeners.Add(listener);
            }
            foreach (UUID listener in cur.MutedListeners)
            {
                mute[listener] = new List<UUID>(cur.MutedFor(listener));
                nowMuteListeners.Add(listener);
            }
            // Clear-tracking (load-bearing), PER CHANNEL: a listener we previously sent that is
            // no longer excluded / no longer muted must be reset with an EXPLICIT empty list — omission
            // is not a clear.
            foreach (UUID listener in _knownListeners)
                if (!nowListeners.Contains(listener))
                    excl[listener] = Array.Empty<UUID>();
            foreach (UUID listener in _knownMuteListeners)
                if (!nowMuteListeners.Contains(listener))
                    mute[listener] = Array.Empty<UUID>();

            if (excl.Count == 0 && mute.Count == 0)
            {
                _synced = true;   // nothing to (re)send and nothing to clear in either channel
                return;
            }

            PeerCtlSendResult r = await _sink.SendAsync(VisOp.Replace, excl, mute).ConfigureAwait(false);
            switch (r)
            {
                case PeerCtlSendResult.Ok:
                    NoteOk();
                    _synced = true;
                    _knownListeners.Clear();
                    foreach (UUID l in nowListeners)
                        _knownListeners.Add(l);
                    _knownMuteListeners.Clear();
                    foreach (UUID l in nowMuteListeners)
                        _knownMuteListeners.Add(l);
                    break;
                case PeerCtlSendResult.TransportError:
                    _synced = false;   // stay unsynced; retry snapshot next tick
                    break;
                case PeerCtlSendResult.ProtocolError:
                default:
                    NoteProtocolError("snapshot replace");   // latches only on the Kth consecutive
                    _synced = false;   // until latched, retry next tick so consecutive faults accrue
                    break;
            }
        }

        // Ok -> true. TransportError -> _synced=false (snapshot next), false. ProtocolError -> latch+stop, false.
        private async Task<bool> SendMappedAsync(VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
        {
            PeerCtlSendResult r = await _sink.SendAsync(op, excl, mute).ConfigureAwait(false);
            switch (r)
            {
                case PeerCtlSendResult.Ok:
                    NoteOk();
                    return true;
                case PeerCtlSendResult.TransportError:
                    _synced = false;
                    return false;
                case PeerCtlSendResult.ProtocolError:
                default:
                    NoteProtocolError("delta " + PeerCtlBatchSerializer.OpString(op));   // latches only on the Kth consecutive
                    _synced = false;   // until latched, snapshot next tick so consecutive faults accrue
                    return false;
            }
        }

        private IReadOnlyCollection<UUID> ColumnFor(UUID listener)
        {
            // The listener's full current exclusion column, as a Replace payload.
            VisibilityBatch snap = _feed.SnapshotFor(listener);
            return snap.Added.TryGetValue(listener, out IReadOnlyCollection<UUID> col)
                ? col : Array.Empty<UUID>();
        }

        private IReadOnlyCollection<UUID> MuteColumnFor(UUID listener)
        {
            // The listener's full current MUTE column, as a Replace payload (sticky-mute inheritance).
            VisibilityBatch snap = _feed.SnapshotFor(listener);
            return snap.MuteAdded.TryGetValue(listener, out IReadOnlyCollection<UUID> col)
                ? col : Array.Empty<UUID>();
        }

        private void RefreshKnownListeners()
        {
            _knownListeners.Clear();
            foreach (UUID l in _feed.Current.Listeners)
                _knownListeners.Add(l);
            _knownMuteListeners.Clear();
            foreach (UUID l in _feed.Current.MutedListeners)
                _knownMuteListeners.Add(l);
        }

        // A successful send clears the consecutive-ProtocolError run. Called on every Ok, on any path.
        private void NoteOk() => _consecutiveProtocolErrors = 0;

        // Count a ProtocolError; latch only once K have arrived back-to-back. TransportError does NOT
        // call this AND does NOT reset the counter — it LEAVES it unchanged: transport has its own
        // snapshot-recovery path, and a persistent config fault that only surfaces when transport
        // briefly works (P, T, P, T, P) must still accrue toward the latch rather than be masked by
        // flapping transport. So only an actual Ok resets the run.
        private void NoteProtocolError(string where)
        {
            _consecutiveProtocolErrors++;
            if (_consecutiveProtocolErrors >= ProtocolErrorLatchThreshold)
                LatchProtocolFailure(where, _consecutiveProtocolErrors);
        }

        private void LatchProtocolFailure(string where, int consecutive)
        {
            if (_protocolFailed)
                return;
            _protocolFailed = true;   // stop emission — K consecutive config/format errors are not transient
            m_log.LogError("{LogHeader} {SendStage} rejected as ProtocolError {ConsecutiveFailures}x consecutively (config/format — e.g. wrong " +
                "AdminAPIToken, wrong plugin name, or broken transport in front). Emission DISABLED for this " +
                "region until fixed and the region server is restarted. NOT entering an unbounded snapshot-retry loop.",
                LogHeader, where, consecutive);
        }

        private void LogNoSinkOnce()
        {
            if (_loggedNoSink)
                return;
            _loggedNoSink = true;
            m_log.LogWarning("{LogHeader} no IPeerCtlBatchSink registered for this region; feeder runs matrix-only " +
                "(no emission). Is the Janus service module enabled?", LogHeader);
        }
    }
}
