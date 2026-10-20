/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for VisibilityBatchSender — the peer_ctl_batch orchestrator (region module).
 *
 * The sender is fire-and-forget on the tick thread; these tests drive it through the awaitable
 * PumpAsync core so each tick's send completes deterministically (and the single-flight flag is
 * cleared) before the next Pump. A FakeSink records (op, excl) and returns a scripted result; a
 * FakeFeed injects Current (for the snapshot/delta path) and per-listener columns (for the
 * pending-join path). Covers: disabled/no-sink no-ops, snapshot-then-delta bootstrap, delta
 * add+remove ordering, clear-tracking, result mapping (Transport/Protocol), the
 * disjoint-bug guard, never-throw, and the bounded blind re-send + loud give-up (correction 1).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // the log capture swaps the ambient LoggerProvider.LoggerFactory, a process-wide static
    public class VisibilityBatchSenderTests
    {
        private const int Room = -999;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Excl(params (int listener, int[] sources)[] rows)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (var (listener, sources) in rows)
                d[Id(listener)] = sources.Select(Id).ToList();
            return d;
        }

        // ---- fakes -------------------------------------------------------------------------------

        private sealed class FakeSink : IPeerCtlBatchSink
        {
            public PeerCtlSendResult NextResult = PeerCtlSendResult.Ok;
            public readonly Queue<PeerCtlSendResult> ResultQueue = new();   // per-call results; else NextResult
            public bool Throw;
            public readonly List<(VisOp op, Dictionary<UUID, List<UUID>> excl)> Calls = new();
            public readonly List<Dictionary<UUID, List<UUID>>> MuteCalls = new();   // parallel to Calls
            private readonly object _lock = new object();

            public Task<PeerCtlSendResult> SendAsync(VisOp op,
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
            {
                if (Throw)
                    throw new InvalidOperationException("sink boom");
                PeerCtlSendResult result;
                lock (_lock)
                {
                    result = ResultQueue.Count > 0 ? ResultQueue.Dequeue() : NextResult;
                    var copy = new Dictionary<UUID, List<UUID>>();
                    foreach (var kv in excl)
                        copy[kv.Key] = new List<UUID>(kv.Value);
                    Calls.Add((op, copy));
                    var mcopy = new Dictionary<UUID, List<UUID>>();
                    if (mute != null)
                        foreach (var kv in mute)
                            mcopy[kv.Key] = new List<UUID>(kv.Value);
                    MuteCalls.Add(mcopy);
                }
                return Task.FromResult(result);
            }

            public int Count { get { lock (_lock) return Calls.Count; } }
            public PeerCtlSendStats LastSendStats => default;   // not exercised by these tests
        }

        private sealed class FakeFeed : IVisibilityFeed
        {
            public VisibilityMatrix Current { get; set; } = VisibilityMatrix.Empty;
            public readonly Dictionary<UUID, IReadOnlyCollection<UUID>> Columns = new();
            public readonly Dictionary<UUID, IReadOnlyCollection<UUID>> MuteColumns = new();

#pragma warning disable CS0067 // required by the interface; the sender does not subscribe
            public event Action<VisibilityBatch> BatchProduced;
#pragma warning restore CS0067

            public VisibilityBatch SnapshotFor(UUID listener)
            {
                IReadOnlyCollection<UUID> col = Columns.TryGetValue(listener, out IReadOnlyCollection<UUID> c)
                    ? c : Array.Empty<UUID>();
                IReadOnlyCollection<UUID> mcol = MuteColumns.TryGetValue(listener, out IReadOnlyCollection<UUID> m)
                    ? m : Array.Empty<UUID>();
                return VisibilityBatch.Snapshot(Room, listener, col, mcol);
            }
        }

        // A minimal world producing a mutual-exclusion (banned) pair: `a` is banned from the parcel
        // `b` stands on, so the matrix excludes a<->b (both become listeners). Mirrors the feeder
        // test's ban scenario; used to get a non-empty Current without a live Scene.
        private sealed class BanWorld : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public readonly Dictionary<UUID, ParcelView> ByGid = new();
            public IReadOnlyList<AgentView> SnapshotAgents() => Agents;
            public ParcelView GetParcelAt(Vector3 p) => ByGid.Values.First();
            public ParcelView GetParcelByGlobalId(UUID id) => ByGid[id];
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        private static VisibilityMatrix BannedPairMatrix(UUID a, UUID b)
        {
            var w = new BanWorld();
            UUID pGid = Id(201), qGid = Id(202);
            ParcelView p = new ParcelView(pGid, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: x => x == a, isRestrictedFromLand: _ => false);   // P bans `a`
            ParcelView q = new ParcelView(qGid, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: _ => false, isRestrictedFromLand: _ => false);
            w.ByGid[pGid] = p;
            w.ByGid[qGid] = q;
            w.Agents.Add(new AgentView(a, false, Vector3.Zero, qGid, false));   // a on Q (banned from P)
            w.Agents.Add(new AgentView(b, false, Vector3.Zero, pGid, false));   // b on P
            return VisibilityMatrix.Build(w);
        }

        // A matrix where `mutedSource` is moderation-muted (source-side) on the shared parcel, so it
        // is in the MUTE channel for `listener` (MutedFor(listener) = {mutedSource}) and in nobody's
        // exclusion set. Mirror of BannedPairMatrix for the mute channel.
        private static VisibilityMatrix MutedSourceMatrix(UUID listener, UUID mutedSource)
        {
            var w = new BanWorld();
            UUID gid = Id(201);
            ParcelView p = new ParcelView(gid, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: _ => false, isRestrictedFromLand: _ => false,
                isVoiceModerated: x => x == mutedSource);
            w.ByGid[gid] = p;
            w.Agents.Add(new AgentView(listener, false, Vector3.Zero, gid, false));
            w.Agents.Add(new AgentView(mutedSource, false, Vector3.Zero, gid, false));
            return VisibilityMatrix.Build(w);
        }

        // ---- stuck-mute window (pending-join sets mute, main emit fails, unmute while unsynced) ----

        // (2026-08-28 trace): the [VOICE VISIBILITY] service line logs b.MuteAdded/b.MuteRemoved.
        // A moderation-mute-only change must produce a delta with a NON-ZERO mute count and a ZERO
        // exclusion count — the exact batch that formerly logged "+0/-0 listeners".
        [Test]
        public void MuteOnlyDelta_HasNonZeroMuteCount_AndZeroExclusionCount()
        {
            UUID L = Id(40), S = Id(41);
            VisibilityBatch b = DeltaComputer.Diff(VisibilityMatrix.Empty, MutedSourceMatrix(L, S), Room);
            Assert.That(b.IsEmpty, Is.False, "a mute-only change is not an empty batch");
            Assert.That(b.Added.Count, Is.Zero, "no exclusion listeners — the excl counters read zero, correctly");
            Assert.That(b.Removed.Count, Is.Zero);
            Assert.That(b.MuteAdded.Count, Is.EqualTo(1), "the muted count the service line now logs is non-zero");
            Assert.That(b.MuteAdded.ContainsKey(L), Is.True);
        }

        [Test]
        public async Task PendingJoinMute_MainEmitFails_ThenUnmuteWhileUnsynced_SnapshotClearsMute()
        {
            UUID L = Id(1), S = Id(2);
            var feed = new FakeFeed();
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            // L moderation-mutes S. The pending-join replace SUCCEEDS (mixer mod_muted[L] set),
            // but the following main snapshot FAILS, so _synced stays false and the main path never
            // records L. Only the deliverable-1 fix puts L into _knownMuteListeners here.
            feed.Current = MutedSourceMatrix(L, S);
            feed.MuteColumns[L] = new[] { S };
            sender.OnListenerProvisioned(L);
            sink.ResultQueue.Enqueue(PeerCtlSendResult.Ok);              // pending-join replace: applied
            sink.ResultQueue.Enqueue(PeerCtlSendResult.TransportError);  // main snapshot: fails
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            // Sanity: did send L's mute (non-empty) on the pending-join replace.
            Assert.That(sink.MuteCalls.Any(m => m.TryGetValue(L, out var s0) && s0.Count == 1 && s0[0] == S),
                Is.True, "first-phase pending-join must have sent L's non-empty mute");

            int callsAfterFirstPhase = sink.Calls.Count;

            // S is unmuted -> Current has no mutes and L's mute column is empty. Still unsynced,
            // so this Pump runs a snapshot whose clear-tracking must name L with an explicit empty set.
            feed.Current = VisibilityMatrix.Empty;
            feed.MuteColumns.Remove(L);
            sink.ResultQueue.Enqueue(PeerCtlSendResult.Ok);   // pending-join replace (excl only, mute null)
            sink.ResultQueue.Enqueue(PeerCtlSendResult.Ok);   // main snapshot: applied
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            // The snapshot (a call) must carry an EXPLICIT empty mute set for L — the clear.
            bool cleared = sink.MuteCalls.Skip(callsAfterFirstPhase)
                .Any(m => m.TryGetValue(L, out var s) && s.Count == 0);
            Assert.That(cleared, Is.True,
                "after the fix, the snapshot clear-tracking must emit mute[L]=[] to clear the stranded mute");
        }

        // ---- no-op paths -------------------------------------------------------------------------

        [Test]
        public async Task Disabled_SendsNothing()
        {
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(new FakeFeed(), sink, enabled: false);
            sender.OnListenerProvisioned(Id(1));
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void NullSink_NeverThrows_AndSendsNothing()
        {
            var sender = new VisibilityBatchSender(new FakeFeed(), null, enabled: true);
            Assert.That(async () => await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room)), Throws.Nothing);
        }

        // ---- bootstrap: first tick snapshots, then deltas flow -----------------------------------

        [Test]
        public async Task EmptyMatrix_FirstTickSyncsWithoutSending_ThenDeltaFlows()
        {
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            // Tick 1: unsynced -> snapshot path, but the empty matrix has nothing to send -> synced.
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(0), "empty-matrix snapshot sends nothing and preempts the delta");

            // Tick 2: now synced -> the delta is sent as an Add.
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(1));
            Assert.That(sink.Calls[0].op, Is.EqualTo(VisOp.Add));
            Assert.That(sink.Calls[0].excl[Id(1)], Is.EquivalentTo(new[] { Id(2) }));
        }

        [Test]
        public async Task NonEmptyMatrix_FirstTickSendsSnapshotReplace()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            Assert.That(sink.Count, Is.EqualTo(1));
            Assert.That(sink.Calls[0].op, Is.EqualTo(VisOp.Replace));
            Assert.That(sink.Calls[0].excl[a], Is.EquivalentTo(new[] { b }), "a excludes b in a banned pair");
            Assert.That(sink.Calls[0].excl[b], Is.EquivalentTo(new[] { a }), "symmetric");
        }

        [Test]
        public async Task Delta_SendsAtMostOneAddAndOneRemove_InOrder()
        {
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // sync

            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 3 })), Excl((2, new[] { 4 }))));

            Assert.That(sink.Count, Is.EqualTo(2));
            Assert.That(sink.Calls[0].op, Is.EqualTo(VisOp.Add));
            Assert.That(sink.Calls[0].excl[Id(1)], Is.EquivalentTo(new[] { Id(3) }));
            Assert.That(sink.Calls[1].op, Is.EqualTo(VisOp.Remove));
            Assert.That(sink.Calls[1].excl[Id(2)], Is.EquivalentTo(new[] { Id(4) }));
        }

        // ---- clear-tracking: a dropped listener gets an EXPLICIT empty replace ----------

        [Test]
        public async Task ClearTracking_DroppedListenerGetsExplicitEmptyReplace()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            // Tick 1: snapshot Ok -> known listeners = {a, b}.
            sink.NextResult = PeerCtlSendResult.Ok;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            // Tick 2: a delta whose send fails with TransportError -> _synced flips false.
            sink.NextResult = PeerCtlSendResult.TransportError;
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((3, new[] { 4 })), null));

            // Tick 3: matrix now empty; unsynced -> re-snapshot. a and b are gone but were known,
            // so they must appear as EXPLICIT empty lists (omission would not clear them).
            sink.NextResult = PeerCtlSendResult.Ok;
            feed.Current = VisibilityMatrix.Empty;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            var replace = sink.Calls.Last();
            Assert.That(replace.op, Is.EqualTo(VisOp.Replace));
            Assert.That(replace.excl.ContainsKey(a), Is.True, "dropped listener a present in the clearing replace");
            Assert.That(replace.excl[a], Is.Empty, "a cleared with an explicit empty list");
            Assert.That(replace.excl.ContainsKey(b), Is.True);
            Assert.That(replace.excl[b], Is.Empty);
        }

        // ---- result mapping ----------------------------------------------------------------------

        [Test]
        public async Task TransportError_OnDelta_ForcesSnapshotNextTick()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));      // snapshot #1 (Ok)
            sink.NextResult = PeerCtlSendResult.TransportError;
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((5, new[] { 6 })), null));  // delta -> TransportError
            sink.NextResult = PeerCtlSendResult.Ok;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));      // must be a snapshot, not a delta

            Assert.That(sink.Calls.First().op, Is.EqualTo(VisOp.Replace));
            Assert.That(sink.Calls.Last().op, Is.EqualTo(VisOp.Replace), "after a TransportError the next tick re-snapshots");
        }

        [Test]
        public async Task ProtocolError_TwoThenOk_DoesNotLatch()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink { NextResult = PeerCtlSendResult.ProtocolError };
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #1 (snapshot, stays unsynced)
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #2
            sink.NextResult = PeerCtlSendResult.Ok;
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // Ok -> resets the consecutive run
            Assert.That(sink.Count, Is.EqualTo(3));

            // Not latched: a further send still reaches the sink (a latch would block it).
            sink.NextResult = PeerCtlSendResult.ProtocolError;
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            Assert.That(sink.Count, Is.EqualTo(4),
                "two ProtocolErrors then an Ok must NOT latch — the Ok reset the run, emission continues");
        }

        [Test]
        public async Task ProtocolError_ThreeConsecutive_Latches()
        {
            Assert.That(VisibilityBatchSender.ProtocolErrorLatchThreshold, Is.EqualTo(3), "guard: the test assumes K=3");

            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink { NextResult = PeerCtlSendResult.ProtocolError };
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #1
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #2
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // ProtocolError #3 -> LATCH
            Assert.That(sink.Count, Is.EqualTo(3));

            // Latched off: further pumps and even a fresh provision do nothing.
            sink.NextResult = PeerCtlSendResult.Ok;
            sender.OnListenerProvisioned(Id(9));
            await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), null));
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
            Assert.That(sink.Count, Is.EqualTo(3), "three consecutive ProtocolErrors latch — emission stays disabled");
        }

        // ---- disjoint-bug guard ------------------------------------------------------------------

        [Test]
        public async Task Delta_WithOverlappingAddAndRemove_IsCaught_NoThrow_NoSend()
        {
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // sync

            // (listener 1, source 2) in BOTH add and remove -> EnsureDisjoint throws inside the
            // never-throw guard; the tick swallows it and sends nothing.
            var bad = VisibilityBatch.Delta(Room, Excl((1, new[] { 2 })), Excl((1, new[] { 2 })));
            Assert.That(async () => await sender.PumpAsync(bad), Throws.Nothing);
            Assert.That(sink.Count, Is.EqualTo(0), "a disjoint-violating tick emits nothing");
        }

        [Test]
        public void SinkThrows_PumpNeverThrows()
        {
            UUID a = Id(1), b = Id(2);
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new FakeSink { Throw = true };
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);
            Assert.That(async () => await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room)), Throws.Nothing);
        }

        // ---- correction 1: bounded blind re-send of a joining listener's full column -------------

        [Test]
        public async Task PendingJoin_ResendsFullColumnBoundedTimes_ThenGivesUp()
        {
            UUID listener = Id(10), source = Id(11);
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };   // main path stays a no-op after sync
            feed.Columns[listener] = new List<UUID> { source };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            sender.OnListenerProvisioned(listener);

            // Pump well past the bound; the join column must be re-sent exactly PendingJoinMaxAttempts
            // times (Ok != applied — we cannot confirm presence), then give up silently to the sink.
            for (int i = 0; i < VisibilityBatchSender.PendingJoinMaxAttempts + 3; i++)
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            Assert.That(sink.Count, Is.EqualTo(VisibilityBatchSender.PendingJoinMaxAttempts),
                "exactly the bounded number of re-sends, then it stops");
            Assert.That(sink.Calls.All(c => c.op == VisOp.Replace), Is.True, "join uses a listener-scoped Replace");
            Assert.That(sink.Calls[0].excl.Keys, Is.EquivalentTo(new[] { listener }), "listener-scoped, not the whole room");
            Assert.That(sink.Calls[0].excl[listener], Is.EquivalentTo(new[] { source }), "the listener's full column");
        }

        [Test]
        public async Task PendingJoin_EmptyColumnListener_SkipsPending_WhileExclusionListenerStillResends()
        {
            // Vacuous-confirm guard: a listener whose excl AND mute columns are both empty must never
            // enter the pending machinery -- no re-sends, no GIVING-UP warning (the constant false alarm
            // that fired on every login and made the silent-drop instrument useless). A listener WITH an
            // exclusion is unaffected and still re-sends exactly PendingJoinMaxAttempts times.
            UUID empty = Id(30), banned = Id(31), source = Id(32);
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };   // main path a no-op after sync
            feed.Columns[banned] = new List<UUID> { source };              // `empty` has NO column at all
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            sender.OnListenerProvisioned(empty);
            sender.OnListenerProvisioned(banned);

            for (int i = 0; i < VisibilityBatchSender.PendingJoinMaxAttempts + 3; i++)
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));

            Assert.That(sink.Calls.Any(c => c.excl.ContainsKey(empty)), Is.False,
                "empty-column listener is never sent (skipped at first drain: no re-send, no give-up)");
            Assert.That(sink.Calls.Count(c => c.excl.ContainsKey(banned)), Is.EqualTo(VisibilityBatchSender.PendingJoinMaxAttempts),
                "a listener with an exclusion still gets the full bounded re-send");
        }

        [Test]
        public async Task PendingJoin_DoesNotAffectMainSnapshotState()
        {
            // The pending path must not touch _synced/_knownListeners. With an empty Current, the main
            // path syncs on tick 1 and thereafter sends nothing; the ONLY sends are the join re-sends.
            // (If the pending path had marked the main path synced/known, the counts below would differ.)
            UUID listener = Id(20), source = Id(21);
            var feed = new FakeFeed { Current = VisibilityMatrix.Empty };
            feed.Columns[listener] = new List<UUID> { source };
            var sink = new FakeSink();
            var sender = new VisibilityBatchSender(feed, sink, enabled: true);

            sender.OnListenerProvisioned(listener);
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // join send #1 + main snapshot (empty, no send)
            await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // join send #2 + main delta (empty, no send)

            Assert.That(sink.Count, Is.EqualTo(2), "only the two join re-sends; the main path emitted nothing");
            Assert.That(sink.Calls.All(c => c.op == VisOp.Replace && c.excl.ContainsKey(listener)), Is.True);
        }

        // ==== FIX 2: in-flight staleness self-heal ========================================

        // A sink whose SendAsync never completes until the test releases it — models a send stuck
        // in flight (the CTS + HttpClient backstop both failed) so _sendInFlight stays claimed.
        private sealed class HangingSink : IPeerCtlBatchSink
        {
            public volatile bool Hang;
            public readonly List<VisOp> Ops = new();
            private readonly object _lock = new object();
            public TaskCompletionSource<PeerCtlSendResult> LastPending;

            public Task<PeerCtlSendResult> SendAsync(VisOp op,
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
            {
                lock (_lock) Ops.Add(op);
                if (Hang)
                {
                    var tcs = new TaskCompletionSource<PeerCtlSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    LastPending = tcs;
                    return tcs.Task;   // never completes until the test SetResult's it
                }
                return Task.FromResult(PeerCtlSendResult.Ok);
            }

            public int Count { get { lock (_lock) return Ops.Count; } }
            public VisOp LastOp { get { lock (_lock) return Ops[Ops.Count - 1]; } }
            public PeerCtlSendStats LastSendStats => default;   // not exercised by these tests
        }

        // Capture the sender's log4net Error output so "logs once" is asserted against the real log.
        /// <summary>Captures what the sender actually logs. It logs through <see cref="ILogger"/> obtained from
        /// <see cref="LoggerProvider"/> (commit 5d43d3e1d3, "convert the last four log4net call sites to ILogger"),
        /// whose default factory is <c>NullLoggerFactory</c> - so a log4net appender, which is what these tests used
        /// before, receives nothing and every assertion about a log line reads 0..</summary>
        private sealed class LogCapture : ILoggerProvider, ILogger
        {
            public readonly List<(LogLevel Level, string Text)> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }
        }

        private static (LogCapture capture, Action detach) CaptureVisibilityLog()
        {
            var capture = new LogCapture();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            ILoggerFactory ours = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
            LoggerProvider.LoggerFactory = ours;
            return (capture, () =>
            {
                LoggerProvider.LoggerFactory = previous;
                ours.Dispose();
            });
        }

        private static int StallLogCount(LogCapture capture)
        {
            lock (capture.Lines)
                return capture.Lines.Count(l => l.Level == LogLevel.Error
                    && l.Text.Contains("stuck in-flight") && l.Text.Contains("region Example Region"));
        }

        private static VisibilityBatchSender NewStallSender(FakeFeed feed, IPeerCtlBatchSink sink, long[] now)
            => new VisibilityBatchSender(feed, sink, enabled: true,
                   adminTimeout: TimeSpan.FromMilliseconds(100), region: "Example Region",
                   nowMs: () => System.Threading.Volatile.Read(ref now[0]));   // stale threshold = 8 x 100 = 800ms

        // Hardening note: these tests NEVER await an uncompleted gate. Time is advanced via the fake
        // clock (now[]) so the guard fires without any real wait, and every hung send's gate is
        // completed and its task drained in `finally` — so a leaked never-completing task can't wedge
        // the runner. The assembly-level [CancelAfter] (AssemblyInfo.cs) is the last-resort backstop.

        [Test]
        public async Task StalledSend_GuardFiresAfterThreshold_ClearsFlag_NextPumpSnapshots()
        {
            UUID a = Id(1), b = Id(2);
            long[] now = { 0 };
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new HangingSink { Hang = true };
            var sender = NewStallSender(feed, sink, now);
            var (capture, detach) = CaptureVisibilityLog();
            Task hung1 = null;
            TaskCompletionSource<PeerCtlSendResult> gate1 = null;
            try
            {
                // Send #1 (bootstrap snapshot Replace) hangs. Capture its task/gate — do NOT await it.
                hung1 = sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                gate1 = sink.LastPending;
                Assert.That(sink.Count, Is.EqualTo(1));
                Assert.That(sink.LastOp, Is.EqualTo(VisOp.Replace));

                // Before the threshold: a pump skips (returns a COMPLETED task); the guard does NOT fire.
                System.Threading.Volatile.Write(ref now[0], 500);          // < 800
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                Assert.That(sink.Count, Is.EqualTo(1), "still in flight; no new send");
                Assert.That(StallLogCount(capture), Is.EqualTo(0), "guard must not fire before the threshold");

                // Past the threshold: the guard fires — force-clears, logs once, forces snapshot next.
                System.Threading.Volatile.Write(ref now[0], 900);          // > 800
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                Assert.That(sink.Count, Is.EqualTo(1), "the guard itself sends nothing");
                Assert.That(StallLogCount(capture), Is.EqualTo(1), "guard logs exactly once");

                // Next pump: flag cleared -> acquires; _synced=false -> SNAPSHOT (not a delta). Completes.
                sink.Hang = false;
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                Assert.That(sink.Count, Is.EqualTo(2), "emission resumed after self-heal");
                Assert.That(sink.LastOp, Is.EqualTo(VisOp.Replace), "recovery send is a snapshot, not a delta");
                Assert.That(StallLogCount(capture), Is.EqualTo(1), "still exactly one stall log (once per episode)");
            }
            finally
            {
                gate1?.TrySetResult(PeerCtlSendResult.Ok);   // drain the abandoned send -> no leaked task
                if (hung1 != null) await hung1;
                detach();
            }
        }

        [Test]
        public async Task SlowButCompletingSend_DoesNotTripGuard()
        {
            UUID a = Id(1), b = Id(2);
            long[] now = { 0 };
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new HangingSink { Hang = true };
            var sender = NewStallSender(feed, sink, now);
            var (capture, detach) = CaptureVisibilityLog();
            Task s1 = null;
            TaskCompletionSource<PeerCtlSendResult> gate = null;
            try
            {
                s1 = sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));   // snapshot send is slow (gated)
                gate = sink.LastPending;

                // Advance repeatedly but stay UNDER the threshold: skips, guard never fires.
                foreach (int t in new[] { 100, 300, 500, 700 })
                {
                    System.Threading.Volatile.Write(ref now[0], t);
                    await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                }
                Assert.That(StallLogCount(capture), Is.EqualTo(0), "a slow send within budget must not trip the guard");
                Assert.That(sink.Count, Is.EqualTo(1), "no force-clear, no extra sends");

                // The send now completes normally -> its finally releases the flag.
                gate.SetResult(PeerCtlSendResult.Ok);
                await s1;

                // Emission works normally afterwards (synced now -> a non-empty delta emits an add).
                // Un-hang the sink FIRST so this awaited delta send completes rather than gating.
                sink.Hang = false;
                System.Threading.Volatile.Write(ref now[0], 750);
                await sender.PumpAsync(VisibilityBatch.Delta(Room, Excl((3, new[] { 4 })), null));
                Assert.That(sink.Count, Is.EqualTo(2), "emission resumed via the normal single-flight release");
                Assert.That(sink.LastOp, Is.EqualTo(VisOp.Add));
                Assert.That(StallLogCount(capture), Is.EqualTo(0), "guard never fired for a completing send");
            }
            finally
            {
                gate?.TrySetResult(PeerCtlSendResult.Ok);   // idempotent if an assert failed pre-completion
                if (s1 != null) await s1;
                detach();
            }
        }

        [Test]
        public async Task AbandonedSendLateCompletion_DoesNotClearANewerSendsFlag()
        {
            UUID a = Id(1), b = Id(2);
            long[] now = { 0 };
            var feed = new FakeFeed { Current = BannedPairMatrix(a, b) };
            var sink = new HangingSink { Hang = true };
            var sender = NewStallSender(feed, sink, now);
            var (capture, detach) = CaptureVisibilityLog();
            Task hung1 = null, hung2 = null;
            TaskCompletionSource<PeerCtlSendResult> gate1 = null, gate2 = null;
            try
            {
                // Send #1 (epoch1) hangs; capture its task + gate.
                hung1 = sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                gate1 = sink.LastPending;

                // Guard fires -> force-clears epoch1.
                System.Threading.Volatile.Write(ref now[0], 900);
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                Assert.That(StallLogCount(capture), Is.EqualTo(1));

                // A NEW send (epoch2) acquires and ALSO hangs; it now owns the flag.
                hung2 = sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                gate2 = sink.LastPending;
                Assert.That(sink.Count, Is.EqualTo(2), "epoch2 started a fresh snapshot while epoch1 is abandoned");

                // The ABANDONED epoch1 now completes late (while epoch2 is still gated, so no concurrent
                // shared-state mutation). Its finally must NOT clear epoch2's flag — CAS-against-its-own
                // -epoch no-ops because epoch2 owns the flag now.
                gate1.SetResult(PeerCtlSendResult.Ok);
                await hung1;

                // Proof epoch2 still holds single-flight: a pump within epoch2's budget SKIPS (no send).
                System.Threading.Volatile.Write(ref now[0], 950);   // epoch2 started at 900; 50ms elapsed, not stale
                await sender.PumpAsync(VisibilityBatch.EmptyDelta(Room));
                Assert.That(sink.Count, Is.EqualTo(2),
                    "epoch2 still owns the flag; the abandoned late completion did not corrupt single-flight");
            }
            finally
            {
                gate1?.TrySetResult(PeerCtlSendResult.Ok);
                gate2?.TrySetResult(PeerCtlSendResult.Ok);
                if (hung1 != null) await hung1;
                if (hung2 != null) await hung2;
                detach();
            }
        }
    }
}
