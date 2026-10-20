/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests that the sim parses the mixer's peer_ctl_batch inner reply and surfaces its counts.
 *
 * The mixer reply nests the plugin response under "response":
 *   {janus:success, response:{slvoice, op, room, entries, mute_entries, skipped, deferred_listeners}}
 * (mixer janus_slvoice.c:1552-1557 lineage + the additive deferred_listeners from 27977c8). EVERY inner
 * field is optional: an old / pre-mute-channel mixer omits them, and the parse must then be a quiet
 * "no info" that logs nothing and contributes zero stats -- behaviourally identical to before.
 *
 * Covered here:
 *  - ParseInnerReply (pure): deferred_listeners parsed; absent fields -> zero, not malformed; no
 *    "response" -> malformed; empty body -> absent default.
 *  - ClassifyReply (pure, the deliberate severity policy): deferred>0 -> INFO not WARN; skipped>0 /
 *    non-applied / malformed -> WARN; quiet applied reply and absent reply -> nothing.
 *  - The sink surfaces the counts on LastSendStats, and a malformed/anomalous inner reply is REPORTED
 *    but NOT converted into a transport failure: SendAsync still returns Ok when janus=="success".
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class PeerCtlReplyStatsTests
    {
        private static UUID Id(int n) => new UUID($"{n:D8}-0000-0000-0000-000000000000");

        // Build a mixer reply body. Pass null for mute_entries / deferred to OMIT the key (old mixer).
        private static string Body(string slvoice = "applied", int entries = 1,
            int? muteEntries = null, int skipped = 0, int? deferred = null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"janus\":\"success\",\"transaction\":\"t\",\"response\":{");
            sb.Append("\"slvoice\":\"").Append(slvoice).Append("\",\"op\":\"add\",\"room\":1,\"entries\":").Append(entries);
            if (muteEntries.HasValue) sb.Append(",\"mute_entries\":").Append(muteEntries.Value);
            sb.Append(",\"skipped\":").Append(skipped);
            if (deferred.HasValue) sb.Append(",\"deferred_listeners\":").Append(deferred.Value);
            sb.Append("}}");
            return sb.ToString();
        }

        private static IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Excl(UUID l, UUID s)
            => new Dictionary<UUID, IReadOnlyCollection<UUID>> { [l] = new UUID[] { s } };

        private static JanusPeerCtlBatchSink SinkReturning(
            Func<OSDMap, (AdminSendResult, string)> sendOne, Func<UUID, int?> roomOf = null)
        {
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                Id(999), "TestRegion", 4, req => Task.FromResult(sendOne(req)));
            // With no resolver the sink addresses nobody, so these tests - whose subject is
            // the mixer's INNER reply, not placement - stand their agents on an estate-channel parcel, which resolves
            // to the estate room (the sink's FallbackRoom).
            sink.RoomOf = roomOf ?? (_ => sink.FallbackRoom);
            return sink;
        }

        // ---- ParseInnerReply (pure) ----------------------------------------------------------

        [Test]
        public void ParseInnerReply_ParsesAllFields_IncludingDeferred()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(entries: 2, muteEntries: 1, skipped: 0, deferred: 3));
            Assert.That(r.Present, Is.True);
            Assert.That(r.Malformed, Is.False);
            Assert.That(r.Status, Is.EqualTo("applied"));
            Assert.That(r.Entries, Is.EqualTo(2));
            Assert.That(r.MuteEntries, Is.EqualTo(1));
            Assert.That(r.Skipped, Is.EqualTo(0));
            Assert.That(r.DeferredListeners, Is.EqualTo(3));
        }

        [Test]
        public void ParseInnerReply_OldMixer_AbsentFields_DefaultZero_NotMalformed()
        {
            // Pre-mute / pre-deferral mixer: response with only slvoice+entries+skipped, no mute_entries,
            // no deferred_listeners. Must parse cleanly to zeros (no-info), NOT be flagged malformed.
            const string body = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"op\":\"add\",\"room\":1,\"entries\":1,\"skipped\":0}}";
            var r = JanusPeerCtlBatchSink.ParseInnerReply(body);
            Assert.That(r.Present, Is.True);
            Assert.That(r.Malformed, Is.False);
            Assert.That(r.MuteEntries, Is.EqualTo(0));
            Assert.That(r.DeferredListeners, Is.EqualTo(0), "absent deferred_listeners reads as zero (no info)");
        }

        [Test]
        public void ParseInnerReply_NoResponseObject_IsMalformed()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply("{\"janus\":\"success\",\"transaction\":\"t\"}");
            Assert.That(r.Present, Is.False);
            Assert.That(r.Malformed, Is.True);
        }

        [Test]
        public void ParseInnerReply_EmptyOrNullBody_IsAbsentDefault()
        {
            var r1 = JanusPeerCtlBatchSink.ParseInnerReply(string.Empty);
            Assert.That(r1.Present, Is.False);
            Assert.That(r1.Malformed, Is.False, "an empty body is absent, not malformed (no new log)");
            var r2 = JanusPeerCtlBatchSink.ParseInnerReply(null);
            Assert.That(r2.Present, Is.False);
            Assert.That(r2.Malformed, Is.False);
        }

        [Test]
        public void ParseInnerReply_NonAppliedStatus_IsCaptured()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(slvoice: "error"));
            Assert.That(r.Present, Is.True);
            Assert.That(r.Status, Is.EqualTo("error"));
            Assert.That(r.Malformed, Is.False, "a present status is not malformed; it is just not \"applied\"");
        }

        // ---- ClassifyReply (pure severity policy) --------------------------------------------

        [Test]
        public void Classify_DeferredOnly_IsInfoNotWarn()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(deferred: 4));
            (bool warn, bool info) = JanusPeerCtlBatchSink.ClassifyReply(in r);
            Assert.That(warn, Is.False, "the deferral self-heal is not a fault");
            Assert.That(info, Is.True, "deferred_listeners>0 is INFO");
        }

        [Test]
        public void Classify_Skipped_IsWarn()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(skipped: 2));
            Assert.That(JanusPeerCtlBatchSink.ClassifyReply(in r).Warn, Is.True, "skipped>0 is real loss -> WARN");
        }

        [Test]
        public void Classify_NonApplied_IsWarn()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(slvoice: "error"));
            Assert.That(JanusPeerCtlBatchSink.ClassifyReply(in r).Warn, Is.True, "a non-applied status -> WARN");
        }

        [Test]
        public void Classify_Malformed_IsWarn()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply("{\"janus\":\"success\"}");
            Assert.That(JanusPeerCtlBatchSink.ClassifyReply(in r).Warn, Is.True, "a malformed inner reply -> WARN");
        }

        [Test]
        public void Classify_QuietApplied_LogsNothing()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(skipped: 0));
            (bool warn, bool info) = JanusPeerCtlBatchSink.ClassifyReply(in r);
            Assert.That(warn, Is.False);
            Assert.That(info, Is.False, "an applied, all-zero reply logs nothing (quiet steady state)");
        }

        [Test]
        public void Classify_AbsentReply_LogsNothing()
        {
            var r = JanusPeerCtlBatchSink.ParseInnerReply(string.Empty);
            (bool warn, bool info) = JanusPeerCtlBatchSink.ClassifyReply(in r);
            Assert.That(warn, Is.False);
            Assert.That(info, Is.False, "an absent reply (old mixer) logs nothing -- identical to a mixer that sends no inner stats");
        }

        // ---- Sink surfacing (LastSendStats) --------------------------------------------------

        [Test]
        public async Task Sink_SurfacesDeferredListeners_OnLastSendStats()
        {
            var sink = SinkReturning(_ => (AdminSendResult.Ok, Body(deferred: 3)));
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.That(res, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(sink.LastSendStats.DeferredListeners, Is.EqualTo(3));
            Assert.That(sink.LastSendStats.RepliesParsed, Is.EqualTo(1));
            Assert.That(sink.LastSendStats.Anomalies, Is.EqualTo(0));
        }

        [Test]
        public async Task Sink_SkippedInner_SurfacedAsAnomaly_ResultStillOk()
        {
            var sink = SinkReturning(_ => (AdminSendResult.Ok, Body(skipped: 2)));
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.That(res, Is.EqualTo(PeerCtlSendResult.Ok), "skipped is a mixer detail, not a transport failure");
            Assert.That(sink.LastSendStats.Skipped, Is.EqualTo(2));
            Assert.That(sink.LastSendStats.Anomalies, Is.EqualTo(1));
        }

        [Test]
        public async Task Sink_MalformedInner_ReportedButResultStaysOk()
        {
            // The transport succeeded (janus:"success"); a malformed inner reply is reported (anomaly),
            // NOT converted into a transport/protocol failure -- doing so would wrongly trip the sender's
            // latch / staleness guard (a behaviour change). This is the deliberate call.
            var sink = SinkReturning(_ => (AdminSendResult.Ok, "{\"janus\":\"success\"}"));
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.That(res, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(sink.LastSendStats.Anomalies, Is.EqualTo(1));
        }

        [Test]
        public async Task Sink_AbsentInner_ZeroStats_NoInfo()
        {
            var sink = SinkReturning(_ => (AdminSendResult.Ok, string.Empty));   // old mixer / empty body
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.That(res, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(sink.LastSendStats.RepliesParsed, Is.EqualTo(0));
            Assert.That(sink.LastSendStats.DeferredListeners, Is.EqualTo(0));
            Assert.That(sink.LastSendStats.Anomalies, Is.EqualTo(0));
        }

        [Test]
        public async Task Sink_MultiRoom_AggregatesDeferredAcrossRooms()
        {
            // Two listeners in two rooms, each excluding a source in its OWN room -> two rooms addressed.
            Func<UUID, int?> roomOf = u => u == Id(1) || u == Id(3) ? 100 : u == Id(2) || u == Id(4) ? 200 : (int?)null;
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>
            {
                [Id(1)] = new UUID[] { Id(3) },
                [Id(2)] = new UUID[] { Id(4) },
            };
            var sink = SinkReturning(
                req => (AdminSendResult.Ok, Body(deferred: req["room"].AsInteger() == 100 ? 2 : 5)),
                roomOf);
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, excl);
            Assert.That(res, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(sink.LastSendStats.DeferredListeners, Is.EqualTo(7), "summed across the two rooms");
            Assert.That(sink.LastSendStats.RepliesParsed, Is.EqualTo(2));
        }
    }
}
