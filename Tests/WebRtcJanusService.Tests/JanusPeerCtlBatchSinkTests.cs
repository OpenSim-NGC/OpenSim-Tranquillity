/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for JanusPeerCtlBatchSink's per-room emission.
 *
 * The sink is the only place a room number is stamped, and the mixer cannot tell us we stamped the
 * wrong one - an unknown room replies janus:"success" like any other. So the room a message
 * carries is verified HERE or nowhere. The transport is injected through the ctor's sendOne hook
 * (the house pattern - cf. VisibilityBatchSender's nowMs), so no HttpClient is created.
 *
 * The load-bearing case is the estate no-regression one: where every agent resolves to one room the
 * sink must emit ONE message with the same body to the same room as the original single-room sink did, so those
 * tests compare the serialized body against one built the single-room way rather than eyeballing fields.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class JanusPeerCtlBatchSinkTests
    {
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
            {
                var list = new List<UUID>();
                foreach (int s in sources) list.Add(Id(s));
                d[Id(listener)] = list;
            }
            return d;
        }

        private static Func<UUID, int?> Resolver(params (int agent, int room)[] records)
        {
            var table = new Dictionary<UUID, int>();
            foreach (var (agent, room) in records) table[Id(agent)] = room;
            return a => table.TryGetValue(a, out int r) ? r : (int?)null;
        }

        /// <summary>Records every message the sink hands the transport, and answers each one.</summary>
        private sealed class Recorder
        {
            private readonly object _lock = new object();
            public readonly List<OSDMap> Sent = new List<OSDMap>();
            public Func<OSDMap, AdminSendResult> Reply = _ => AdminSendResult.Ok;

            // The sink consumes (result, body). These tests assert PeerCtlSendResult only, so
            // the body is empty (an empty body parses to an absent inner reply -> zero stats, no log).
            public Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                lock (_lock)
                    Sent.Add(request);
                return Task.FromResult((Reply(request), string.Empty));
            }

            public List<int> Rooms()
            {
                var rooms = new List<int>();
                lock (_lock)
                    foreach (OSDMap m in Sent) rooms.Add(m["room"].AsInteger());
                return rooms;
            }

            public OSDMap ForRoom(int room)
            {
                lock (_lock)
                    foreach (OSDMap m in Sent)
                        if (m["room"].AsInteger() == room) return m;
                return null;
            }
        }

        /// <summary>Every agent's record is the estate room, as if each had been provisioned into it. Tests whose
        /// subject is NOT placement use this so they keep testing what they test.</summary>
        private static Func<UUID, int?> AllEstate(JanusPeerCtlBatchSink sink) => _ => sink.FallbackRoom;

        private static JanusPeerCtlBatchSink NewSink(Recorder rec, Func<UUID, int?> roomOf,
                                                     int concurrency = JanusPeerCtlBatchSink.DefaultRoomSendConcurrency)
        {
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", concurrency, rec.SendAsync);
            sink.RoomOf = roomOf;
            return sink;
        }

        // ---- estate no-regression: one room in, one message out, same body ----

        [Fact(Timeout = 60000)]
        public async Task SendAsync_EveryAgentInTheFallbackRoom_SendsExactlyOneMessage_WithTheSingleRoomBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1 }));
            using var sink = NewSink(rec, null);
            sink.RoomOf = AllEstate(sink);   // every agent's record is the estate room
            int fallback = sink.FallbackRoom;

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, excl);

            Assert.Equal(PeerCtlSendResult.Ok, r);
            {
                var assertedValue = rec.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one room must mean one admin round-trip, as before per-room emission");
            }

            // The original single-room sink built exactly this: the room-less body, with the estate room stamped on.
            OSDMap expected = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, excl);
            expected["room"] = new OSDInteger(fallback);
            {
                var assertedValue = OSDParser.SerializeJsonString(rec.Sent[0]);
                TestAssert.WithMessage(() => Assert.Equal(OSDParser.SerializeJsonString(expected), assertedValue), "the estate-channel body must be byte-for-byte what it was before per-room emission");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task SendAsync_EveryAgentRecordedInOneRoom_SendsOneMessageToThatRoom_NotTheFallback()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            var excl = Excl((1, new[] { 2 }), (2, new[] { 1 }));
            using var sink = NewSink(rec, Resolver((1, 4242), (2, 4242)));

            await sink.SendAsync(VisOp.Add, excl);

            Assert.Single(rec.Sent);
            Assert.Equal(4242, rec.Sent[0]["room"].AsInteger());
            Assert.Equal(0, sink.LastSendFallbackListeners);
            Assert.Equal(0, sink.LastSendFallbackSources);
        }

        // ---- The MUTE channel must be counted, not just the excl channel ----

        [Fact(Timeout = 60000)]
        public async Task SendAsync_MuteOnlyBatch_AddressesTheMuteRoom_AndReportsOneRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A moderation-mute delta: empty exclusion channel, one mute entry for a recorded listener.
            // Formerly the send delivered it but every counter read the excl channel and reported 0.
            var rec = new Recorder();
            using var sink = NewSink(rec, Resolver((1, 4242), (2, 4242)));

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Add, Excl(), Excl((1, new[] { 2 })));

            Assert.Equal(PeerCtlSendResult.Ok, r);
            {
                var assertedValue = rec.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the mute is delivered to exactly one room");
            }
            Assert.Equal(4242, rec.Sent[0]["room"].AsInteger());
            Assert.True(((rec.Sent[0]["mute"] as OSDMap)?.ContainsKey(Id(1).ToString())), "the mute entry for listener 1 is on the wire");
            {
                var assertedValue = sink.LastSendRooms;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "rooms addressed is the channel union — a mute-only op reads 1, not 0");
            }
            {
                var assertedValue = sink.LastSendFallbackListeners;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "excl channel had no listeners");
            }
            {
                var assertedValue = sink.LastSendMuteFallbackListeners;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the mute listener was resolvable, not fallback");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task SendAsync_MuteOnly_UnplaceableListener_IsOmitted_AndCountedOnTheMuteCompanion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The mute channel gets the same treatment as the exclusion channel. A listener the
            // sim cannot place must not have its MUTE stamped on the estate room; a mute is live moderation state, so
            // sending it to a guessed room would be harmful. It is omitted and counted instead.
            var rec = new Recorder();
            using var sink = NewSink(rec, null);   // no resolver -> the mute listener cannot be placed

            await sink.SendAsync(VisOp.Add, Excl(), Excl((1, new[] { 2 })));

            {
                var assertedValue = rec.Sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no room is addressed, so nothing goes on the wire");
            }
            Assert.Equal(0, sink.LastSendRooms);
            {
                var assertedValue = sink.LastSendMuteFallbackListeners;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the unplaceable mute listener is counted on the mute-fallback companion");
            }
            {
                var assertedValue = sink.LastSendFallbackListeners;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the excl channel had no listeners");
            }
        }

        // ---- the null-resolver window (construction order) ----

        [Fact(Timeout = 60000)]
        public async Task SendAsync_BeforeTheServiceAssignsTheResolver_SendsNothing_AndCountsEveryone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The sink is built before VoiceVisibilityService exists, so RoomOf is null until that ctor runs. A
            // send in that window must not throw and must not guess. Addressing the estate room would be
            // wrong: it is a real room number in every region, so the send would look successful and put live
            // policy somewhere nobody asked for. It sends nothing, and both counters shout.
            var rec = new Recorder();
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", 4, rec.SendAsync);
            using (sink)
            {
                {
                    var assertedValue = sink.RoomOf;
                    TestAssert.WithMessage(() => Assert.Null(assertedValue), "unset until the service assigns it");
                }

                PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, Excl((1, new[] { 2, 3 })));

                {
                    var assertedValue = r;
                    TestAssert.WithMessage(() => Assert.Equal(PeerCtlSendResult.Ok, assertedValue), "an unwired sink degrades quietly, it does not fail");
                }
                {
                    var assertedValue = rec.Sent;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing on the wire, least of all to the estate room");
                }
                Assert.Equal(0, sink.LastSendRooms);
                Assert.Equal(1, sink.LastSendFallbackListeners);
                Assert.Equal(2, sink.LastSendFallbackSources);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task RoomOf_AssignedAfterConstruction_TakesEffectOnTheNextSend()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            using var sink = NewSink(rec, null);
            await sink.SendAsync(VisOp.Add, Excl((1, new[] { 2 })));
            {
                var assertedValue = rec.Sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "before the resolver is assigned there is no address, so no send");
            }

            sink.RoomOf = Resolver((1, 700), (2, 700));
            await sink.SendAsync(VisOp.Add, Excl((1, new[] { 2 })));

            Assert.Single(rec.Sent);
            Assert.Equal(700, rec.Sent[0]["room"].AsInteger());
        }

        // ---- partitioning and filtering, end to end through the sink ----

        [Fact(Timeout = 60000)]
        public async Task SendAsync_ListenersInTwoRooms_SendsOneMessagePerRoom_WithSameRoomSourcesOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            // Listener 1 in room 100 excludes 2 (room 100) and 3 (room 200); listener 3 in room 200.
            var excl = Excl((1, new[] { 2, 3 }), (3, new[] { 2 }));
            using var sink = NewSink(rec, Resolver((1, 100), (2, 100), (3, 200)));

            await sink.SendAsync(VisOp.Replace, excl);

            Assert.Equal(2, rec.Sent.Count);
            Assert.Equivalent(new[] { 100, 200 }, rec.Rooms(), strict: true);
            Assert.Equal(2, sink.LastSendRooms);

            var a = (OSDMap)rec.ForRoom(100)["excl"];
            Assert.True(a.ContainsKey(Id(1).ToString()));
            {
                var assertedValue = ((OSDArray)a[Id(1).ToString()]).Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "source 3 is in another room");
            }

            var b = (OSDMap)rec.ForRoom(200)["excl"];
            Assert.True(b.ContainsKey(Id(3).ToString()));
            {
                var assertedValue = ((OSDArray)b[Id(3).ToString()]).Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "source 2 is in another room");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task SendAsync_EmptyMap_SendsNothing_AndIsOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            using var sink = NewSink(rec, Resolver());

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Add, new Dictionary<UUID, IReadOnlyCollection<UUID>>());

            {
                var assertedValue = r;
                TestAssert.WithMessage(() => Assert.Equal(PeerCtlSendResult.Ok, assertedValue), "nothing to address is not a failure");
            }
            Assert.Empty(rec.Sent);
            Assert.Equal(0, sink.LastSendRooms);
        }

        [Fact(Timeout = 60000)]
        public async Task SendAsync_InvariantViolationInOneRoom_SendsNothingAtAll()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Every body is built before any is sent, so the serializer's all-or-nothing throw stays
            // all-or-nothing across rooms: no room may be updated while another aborts.
            //
            // The bad source must land in its OWN listener's room to reach the serializer at all:
            // same-room filtering drops a cross-room source first, so a zero UUID in another room is
            // filtered out rather than caught. Listener 3 and the zero source are both resolved to the
            // estate room (by their parcel, not by default) while listener 1 sits in room 100 -
            // two rooms, one of which cannot be built.
            var rec = new Recorder();
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>
            {
                [Id(1)] = new List<UUID> { Id(2) },
                [Id(3)] = new List<UUID> { UUID.Zero },   // invariant violation, in the estate room
            };
            using var sink = NewSink(rec, null);
            // Agents 1 and 2 have records in room 100; every other agent's record is the estate room.
            sink.RoomOf = a => a == Id(1) || a == Id(2) ? 100 : sink.FallbackRoom;

            {
                var assertedValue = async () => await sink.SendAsync(VisOp.Add, excl);
                await Assert.ThrowsAsync<InvalidOperationException>(assertedValue);
            }
            {
                var assertedValue = rec.Sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no room may have been updated before the throw");
            }
        }

        // ---- aggregation precedence (severity order) ----

        private async Task<PeerCtlSendResult> ThreeRooms(Func<int, AdminSendResult> replyByRoom, Recorder rec)
        {
            using var sink = NewSink(rec, Resolver((1, 100), (2, 200), (3, 300)));
            rec.Reply = req => replyByRoom(req["room"].AsInteger());
            return await sink.SendAsync(VisOp.Add, Excl((1, new int[0]), (2, new int[0]), (3, new int[0])));
        }

        [Fact(Timeout = 60000)]
        public async Task Aggregate_AllOk_IsOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            Assert.Equal(PeerCtlSendResult.Ok, await ThreeRooms(_ => AdminSendResult.Ok, rec));
            Assert.Equal(3, rec.Sent.Count);
        }

        [Fact(Timeout = 60000)]
        public async Task Aggregate_OkPlusTransportError_IsTransportError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            PeerCtlSendResult r = await ThreeRooms(
                room => room == 200 ? AdminSendResult.TransportError : AdminSendResult.Ok, rec);

            Assert.Equal(PeerCtlSendResult.TransportError, r);
            {
                var assertedValue = rec.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "a failure in one room must not suppress the others");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task Aggregate_OkPlusProtocolError_IsProtocolError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            PeerCtlSendResult r = await ThreeRooms(
                room => room == 300 ? AdminSendResult.ProtocolError : AdminSendResult.Ok, rec);

            // Must NOT read as Ok: the sender's latch counts consecutive ProtocolErrors, and an Ok
            // here would reset that run and mask a real config/format fault forever.
            Assert.Equal(PeerCtlSendResult.ProtocolError, r);
            Assert.Equal(3, rec.Sent.Count);
        }

        [Fact(Timeout = 60000)]
        public async Task Aggregate_TransportErrorPlusProtocolError_IsProtocolError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            PeerCtlSendResult r = await ThreeRooms(
                room => room == 100 ? AdminSendResult.TransportError
                      : room == 300 ? AdminSendResult.ProtocolError
                      : AdminSendResult.Ok, rec);

            {
                var assertedValue = r;
                TestAssert.WithMessage(() => Assert.Equal(PeerCtlSendResult.ProtocolError, assertedValue), "ProtocolError outranks TransportError");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task Aggregate_AllTransportErrors_IsTransportError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder();
            Assert.Equal(PeerCtlSendResult.TransportError, await ThreeRooms(_ => AdminSendResult.TransportError, rec));
        }

        [Fact(Timeout = 60000)]
        public async Task SingleRoom_PreservesTheResultUnchanged()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rec = new Recorder { Reply = _ => AdminSendResult.ProtocolError };
            using var sink = NewSink(rec, null);
            sink.RoomOf = AllEstate(sink);   // this test's subject is the result, not placement

            Assert.Equal(PeerCtlSendResult.ProtocolError, await sink.SendAsync(VisOp.Add, Excl((1, new[] { 2 }))));
        }

        // ---- the concurrency cap actually bounds ----

        /// <summary>Counts how many sends are inside the transport at once.</summary>
        private sealed class ConcurrencyProbe
        {
            private int _current;
            private int _max;
            public int Max => Volatile.Read(ref _max);
            public readonly List<OSDMap> Sent = new List<OSDMap>();

            public async Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                lock (Sent)
                    Sent.Add(request);
                int now = Interlocked.Increment(ref _current);
                int seen;
                while (now > (seen = Volatile.Read(ref _max)))
                {
                    if (Interlocked.CompareExchange(ref _max, now, seen) == seen)
                        break;
                }
                await Task.Delay(25).ConfigureAwait(false);
                Interlocked.Decrement(ref _current);
                return (AdminSendResult.Ok, string.Empty);
            }
        }

        private static async Task<ConcurrencyProbe> EightRoomsAt(int concurrency)
        {
            var probe = new ConcurrencyProbe();
            var records = new List<(int, int)>();
            var rows = new List<(int, int[])>();
            for (int i = 1; i <= 8; i++)
            {
                records.Add((i, 100 + i));       // each listener in its own room
                rows.Add((i, new int[0]));
            }
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", concurrency, probe.SendAsync);
            using (sink)
            {
                sink.RoomOf = Resolver(records.ToArray());
                await sink.SendAsync(VisOp.Add, Excl(rows.ToArray()));
            }
            return probe;
        }

        [Fact(Timeout = 60000)]
        public async Task Concurrency_BoundsRoomsInFlight()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ConcurrencyProbe probe = await EightRoomsAt(2);

            {
                var assertedValue = probe.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(8, assertedValue), "every room is still sent");
            }
            Assert.True(probe.Max <= 2, "the cap must actually bound in-flight sends");
            {
                var assertedValue = probe.Max;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "and must actually parallelise up to it, not run sequentially");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task Concurrency_Zero_IsClampedToSequential_NotAStall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // SemaphoreSlim(0) would block every send forever and wedge emission until the sender's
            // staleness guard fired - every tick. Zero must degrade to sequential instead.
            ConcurrencyProbe probe = await EightRoomsAt(0);

            Assert.Equal(8, probe.Sent.Count);
            Assert.Equal(1, probe.Max);
        }

        [Fact(Timeout = 60000)]
        public async Task Concurrency_Negative_IsClampedToSequential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ConcurrencyProbe probe = await EightRoomsAt(-4);

            Assert.Equal(8, probe.Sent.Count);
            Assert.Equal(1, probe.Max);
        }

        [Fact(Timeout = 60000)]
        public void DefaultRoomSendConcurrency_IsTheBriefsSmallDefault()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(4, JanusPeerCtlBatchSink.DefaultRoomSendConcurrency);
        }
    }
}
