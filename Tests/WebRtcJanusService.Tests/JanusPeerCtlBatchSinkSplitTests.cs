/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * JanusPeerCtlBatchSink and the mixer's size limit. The slvoice mixer refuses a peer_ctl_batch of more than 65,536 bytes
 * of compact JSON, so the sink sends a room whose request is over that as several requests, one after another, cut
 * between listeners. These tests send a room of 110 listeners where everyone excludes or mutes everyone (about 472 KB)
 * through the sink and read what reached the transport.
 *
 * The transport is the sink's sendOne hook, so no HttpClient is created. The classes share no state and run in parallel.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusPeerCtlBatchSinkSplitTests
    {
        private const int Limit = PeerCtlBatchSplitter.MixerMaxRequestBytes;
        private const int RoomA = 70001;
        private const int RoomB = 70002;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        // `size` listeners, ids first..first+size-1, each naming the other size-1.
        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Everyone(int size, int first = 1)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            for (int i = 0; i < size; i++)
            {
                var others = new List<UUID>(size - 1);
                for (int j = 0; j < size; j++)
                    if (j != i)
                        others.Add(Id(first + j));
                d[Id(first + i)] = others;
            }
            return d;
        }

        /// <summary>Records every request, in the order sent, and answers with a mixer's inner reply for it.</summary>
        private sealed class Mixer
        {
            private readonly object _lock = new object();
            public readonly List<OSDMap> Sent = new List<OSDMap>();
            public Func<int, OSDMap, AdminSendResult> Transport = (_, __) => AdminSendResult.Ok;
            public Func<int, OSDMap, string> Inner = (_, request) => "applied";
            public int InFlight, MostInFlight;

            public async Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                int index;
                lock (_lock)
                {
                    index = Sent.Count;
                    Sent.Add(request);
                    InFlight++;
                    MostInFlight = Math.Max(MostInFlight, InFlight);
                }
                await Task.Yield();
                lock (_lock)
                    InFlight--;
                AdminSendResult result = Transport(index, request);
                if (result != AdminSendResult.Ok)
                    return (result, null);
                string status = Inner(index, request);
                var response = new OSDMap
                {
                    ["slvoice"] = OSD.FromString(status),
                    ["entries"] = OSD.FromInteger(Count(request, "excl")),
                    ["mute_entries"] = OSD.FromInteger(Count(request, "mute")),
                    ["skipped"] = OSD.FromInteger(0),
                };
                if (status != "applied")
                    response["reason"] = OSD.FromString(status);
                var body = new OSDMap { ["janus"] = OSD.FromString("success"), ["response"] = response };
                return (result, body.ToString());
            }

            public List<OSDMap> ForRoom(int room)
            {
                lock (_lock)
                    return Sent.Where(m => m["room"].AsInteger() == room).ToList();
            }
        }

        private static int Count(OSDMap request, string key)
            => request.TryGetValue(key, out OSD c) && c is OSDMap m ? m.Count : 0;

        private static JanusPeerCtlBatchSink NewSink(Mixer mixer, Func<UUID, int?> roomOf, int concurrency = 4)
        {
            var sink = new JanusPeerCtlBatchSink("http://example.org/admin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "Test Region", concurrency, mixer.SendAsync);
            sink.RoomOf = roomOf;
            return sink;
        }

        private static IEnumerable<string> Listeners(OSDMap request, string channel)
            => request.TryGetValue(channel, out OSD c) && c is OSDMap m ? m.Keys : Enumerable.Empty<string>();

        // ---- a room that fits is one request, exactly as before ------------------------------------------

        [Test]
        public async Task SendAsync_ARoomThatFits_IsOneRequest()
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => RoomA);
            var excl = Everyone(10);

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, excl);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(mixer.Sent.Count, Is.EqualTo(1));
            Assert.That(sink.LastSendRequests, Is.EqualTo(1));
            Assert.That(mixer.Sent[0].ToString(),
                Is.EqualTo(Expected(VisOp.Replace, excl, null, RoomA).ToString()), "the same request the sink has always sent");
        }

        private static OSDMap Expected(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute, int room)
        {
            OSDMap m = PeerCtlBatchSerializer.BuildRequest(op, excl, mute);
            m["room"] = new OSDInteger(room);
            return m;
        }

        // ---- a room of 110 over the limit -------------------------------------------------------------------

        [TestCase(VisOp.Replace)]
        [TestCase(VisOp.Add)]
        [TestCase(VisOp.Remove)]
        public async Task SendAsync_ARoomOf110WhereEveryoneIsExcluded_GoesInPartsThatEachFit(VisOp op)
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => RoomA);
            var excl = Everyone(110);

            PeerCtlSendResult r = await sink.SendAsync(op, excl);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(mixer.Sent.Count, Is.GreaterThan(1));
            Assert.That(sink.LastSendRequests, Is.EqualTo(mixer.Sent.Count));
            Assert.That(sink.LastSendRooms, Is.EqualTo(1));
            foreach (OSDMap request in mixer.Sent)
            {
                Assert.That(PeerCtlBatchSplitter.CompactBytes(request), Is.LessThanOrEqualTo(Limit));
                Assert.That(request["room"].AsInteger(), Is.EqualTo(RoomA));
                Assert.That(request["op"].AsString(), Is.EqualTo(PeerCtlBatchSerializer.OpString(op)));
                Assert.That(request.ContainsKey("mute"), Is.EqualTo(op == VisOp.Replace),
                    "a replace always carries the mute key, an add or remove with no mutes does not");
            }
            // every listener once, with the column it was given, in the order given
            Assert.That(mixer.Sent.SelectMany(q => Listeners(q, "excl")), Is.EqualTo(excl.Keys.Select(k => k.ToString())));
            foreach (OSDMap request in mixer.Sent)
                foreach (KeyValuePair<string, OSD> kv in (OSDMap)request["excl"])
                    Assert.That(((OSDArray)kv.Value).Select(v => v.AsString()),
                        Is.EqualTo(excl[UUID.Parse(kv.Key)].Select(u => u.ToString())));
        }

        [Test]
        public async Task SendAsync_ARoomOf110WhereEveryoneIsMuted_KeepsEachListenersColumnInOnePart()
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => RoomA);
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var mute = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (var kv in Everyone(110))
            {
                excl[kv.Key] = kv.Value.Take(40).ToList();
                mute[kv.Key] = kv.Value.Skip(40).ToList();
            }

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, excl, mute);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(mixer.Sent.Count, Is.GreaterThan(1));
            foreach (OSDMap request in mixer.Sent)
            {
                Assert.That(PeerCtlBatchSplitter.CompactBytes(request), Is.LessThanOrEqualTo(Limit));
                Assert.That(Listeners(request, "mute"), Is.EquivalentTo(Listeners(request, "excl")));
            }
            Assert.That(mixer.Sent.SelectMany(q => Listeners(q, "mute")).Count(), Is.EqualTo(110));
            Assert.That(sink.LastSendStats.MuteEntries, Is.EqualTo(110), "the counts of the parts add up");
            Assert.That(sink.LastSendStats.Entries, Is.EqualTo(110));
        }

        [Test]
        public async Task SendAsync_PartsOfARoomGoOneAfterAnother()
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => RoomA);

            await sink.SendAsync(VisOp.Replace, Everyone(110));

            Assert.That(mixer.MostInFlight, Is.EqualTo(1), "a room's parts are never in flight together");
        }

        [Test]
        public async Task SendAsync_ARoomOverTheLimitDoesNotHoldBackAnotherRoom()
        {
            var mixer = new Mixer();
            // ids 1..110 are in room A, ids 201..205 in room B
            using var sink = NewSink(mixer, a => a.GetBytes()[15] >= 201 || a.GetBytes()[14] != 0 ? RoomB : RoomA);
            var excl = Everyone(110);
            foreach (var kv in Everyone(5, 201))
                excl[kv.Key] = kv.Value;

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, excl);

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(sink.LastSendRooms, Is.EqualTo(2));
            Assert.That(mixer.ForRoom(RoomB).Count, Is.EqualTo(1));
            Assert.That(mixer.ForRoom(RoomA).Count, Is.GreaterThan(1));
            Assert.That(sink.LastSendRequests, Is.EqualTo(mixer.Sent.Count));
        }

        // ---- a part that does not go through ----------------------------------------------------------------

        [Test]
        public async Task SendAsync_APartThatFailsInTransport_EndsThatRoomsSend_AndIsTheResult()
        {
            var mixer = new Mixer { Transport = (index, _) => index == 1 ? AdminSendResult.TransportError : AdminSendResult.Ok };
            using var sink = NewSink(mixer, _ => RoomA);

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, Everyone(110));

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.TransportError));
            Assert.That(mixer.Sent.Count, Is.EqualTo(2), "the parts after the failed one are not sent");
            Assert.That(sink.LastSendRequests, Is.GreaterThan(2), "they were built");
        }

        [Test]
        public async Task SendAsync_APartTheMixerDoesNotApply_EndsThatRoomsSend()
        {
            var mixer = new Mixer { Inner = (index, _) => index == 0 ? "error" : "applied" };
            using var sink = NewSink(mixer, _ => RoomA);

            PeerCtlSendResult r = await sink.SendAsync(VisOp.Replace, Everyone(110));

            Assert.That(r, Is.EqualTo(PeerCtlSendResult.Ok), "an unapplied answer has never changed the result");
            Assert.That(mixer.Sent.Count, Is.EqualTo(1));
        }

        // ---- arming: each part is its own generation ----------------------------------------------------------

        [Test]
        public async Task SendAsync_Armed_EachPartCarriesTheNextGenerationAndTheBaseOfItsOwnListeners()
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => RoomA);
            var authority = new VisAuthority(0x0000018f00000001UL, "split", () => 1000);
            sink.Authority = authority;

            await sink.SendAsync(VisOp.Replace, Everyone(110));
            int replaceParts = mixer.Sent.Count;
            await sink.SendAsync(VisOp.Add, Everyone(110));

            Assert.That(replaceParts, Is.GreaterThan(1));
            var generations = mixer.Sent.Select(q => q["policy_generation"].AsInteger()).ToList();
            Assert.That(generations, Is.EqualTo(Enumerable.Range(1, mixer.Sent.Count).ToList()), "rising, one for each request");
            foreach (OSDMap request in mixer.Sent)
            {
                Assert.That(PeerCtlBatchSplitter.CompactBytes(request), Is.LessThanOrEqualTo(Limit));
                Assert.That(request["room_epoch"].AsString(), Is.EqualTo(authority.EpochString));
                if (request["op"].AsString() == "add")
                    Assert.That(((OSDMap)request["base"]).Keys, Is.EquivalentTo(Listeners(request, "excl")));
                else
                    Assert.That(request.ContainsKey("base"), Is.False);
            }
            Assert.That(authority.CurrentGeneration(RoomA), Is.EqualTo((uint)mixer.Sent.Count));
            // every part was applied, so a listener in any part is armed at its part's generation
            Assert.That(authority.IsArmed(RoomA, Id(1)), Is.True);
            Assert.That(authority.IsArmed(RoomA, Id(110)), Is.True);
            Assert.That(authority.ListenerGeneration(RoomA, Id(1)), Is.GreaterThan(0u));
        }

        // ---- one named room: a call's mute of everyone ---------------------------------------------------------

        [Test]
        public async Task SendToRoomAsync_AMuteOfEveryoneInAFullRoom_GoesInPartsAndAnswersAsOne()
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => null);
            // a call mutes 110 members: every column holds all 110, the listener's own entry included
            var columns = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var everyone = Enumerable.Range(1, 110).Select(Id).ToList();
            foreach (UUID l in everyone)
                columns[l] = everyone.ToList();

            (PeerCtlSendResult result, JanusPeerCtlBatchSink.SlvoiceReply reply) =
                await sink.SendToRoomAsync(RoomA, VisOp.Replace, null, columns);

            Assert.That(result, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(mixer.Sent.Count, Is.GreaterThan(1));
            foreach (OSDMap request in mixer.Sent)
            {
                Assert.That(PeerCtlBatchSplitter.CompactBytes(request), Is.LessThanOrEqualTo(Limit));
                Assert.That(request["room"].AsInteger(), Is.EqualTo(RoomA));
                Assert.That(request.ContainsKey("room_epoch"), Is.False, "a call's room has no declared authority");
            }
            Assert.That(mixer.Sent.SelectMany(q => Listeners(q, "mute")).Count(), Is.EqualTo(110));
            Assert.That(reply.Status, Is.EqualTo("applied"));
            Assert.That(reply.MuteEntries, Is.EqualTo(110), "the answer counts every part");
            Assert.That(sink.LastSendStats.MuteEntries, Is.EqualTo(110));
        }

        [Test]
        public async Task SendToRoomAsync_APartTheMixerAnswersUnknownRoom_IsTheAnswer_AndTheRestIsNotSent()
        {
            var mixer = new Mixer { Inner = (index, _) => index == 0 ? "applied" : "unknown_room" };
            using var sink = NewSink(mixer, _ => null);
            var everyone = Enumerable.Range(1, 110).Select(Id).ToList();
            var columns = everyone.ToDictionary(l => l, l => (IReadOnlyCollection<UUID>)everyone.ToList());

            (PeerCtlSendResult result, JanusPeerCtlBatchSink.SlvoiceReply reply) =
                await sink.SendToRoomAsync(RoomA, VisOp.Replace, null, columns);

            Assert.That(result, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(mixer.Sent.Count, Is.EqualTo(2));
            Assert.That(reply.Reason, Is.EqualTo("unknown_room"));
        }

        [Test]
        public async Task SendToRoomAsync_ARoomThatFits_IsOneRequestAndTheMixersOwnReply()
        {
            var mixer = new Mixer();
            using var sink = NewSink(mixer, _ => null);
            var everyone = Enumerable.Range(1, 5).Select(Id).ToList();
            var columns = everyone.ToDictionary(l => l, l => (IReadOnlyCollection<UUID>)new List<UUID> { Id(2) });

            (PeerCtlSendResult result, JanusPeerCtlBatchSink.SlvoiceReply reply) =
                await sink.SendToRoomAsync(RoomA, VisOp.Replace, null, columns);

            Assert.That(result, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(mixer.Sent.Count, Is.EqualTo(1));
            Assert.That(reply.MuteEntries, Is.EqualTo(5));
        }
    }
}
