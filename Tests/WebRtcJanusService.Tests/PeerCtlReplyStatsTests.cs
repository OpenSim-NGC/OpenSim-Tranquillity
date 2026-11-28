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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
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
            // the mixer's INNER reply, not placement - give each agent the estate room as its record, as if it
            // had been provisioned into it.
            sink.RoomOf = roomOf ?? (_ => sink.FallbackRoom);
            return sink;
        }

        // ---- ParseInnerReply (pure) ----------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ParseInnerReply_ParsesAllFields_IncludingDeferred()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(entries: 2, muteEntries: 1, skipped: 0, deferred: 3));
            Assert.True(r.Present);
            Assert.False(r.Malformed);
            Assert.Equal("applied", r.Status);
            Assert.Equal(2, r.Entries);
            Assert.Equal(1, r.MuteEntries);
            Assert.Equal(0, r.Skipped);
            Assert.Equal(3, r.DeferredListeners);
        }

        [Fact(Timeout = 60000)]
        public void ParseInnerReply_OldMixer_AbsentFields_DefaultZero_NotMalformed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Pre-mute / pre-deferral mixer: response with only slvoice+entries+skipped, no mute_entries,
            // no deferred_listeners. Must parse cleanly to zeros (no-info), NOT be flagged malformed.
            const string body = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"op\":\"add\",\"room\":1,\"entries\":1,\"skipped\":0}}";
            var r = JanusPeerCtlBatchSink.ParseInnerReply(body);
            Assert.True(r.Present);
            Assert.False(r.Malformed);
            Assert.Equal(0, r.MuteEntries);
            {
                var assertedValue = r.DeferredListeners;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "absent deferred_listeners reads as zero (no info)");
            }
        }

        [Fact(Timeout = 60000)]
        public void ParseInnerReply_NoResponseObject_IsMalformed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply("{\"janus\":\"success\",\"transaction\":\"t\"}");
            Assert.False(r.Present);
            Assert.True(r.Malformed);
        }

        [Fact(Timeout = 60000)]
        public void ParseInnerReply_EmptyOrNullBody_IsAbsentDefault()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r1 = JanusPeerCtlBatchSink.ParseInnerReply(string.Empty);
            Assert.False(r1.Present);
            Assert.False(r1.Malformed, "an empty body is absent, not malformed (no new log)");
            var r2 = JanusPeerCtlBatchSink.ParseInnerReply(null);
            Assert.False(r2.Present);
            Assert.False(r2.Malformed);
        }

        [Fact(Timeout = 60000)]
        public void ParseInnerReply_NonAppliedStatus_IsCaptured()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(slvoice: "error"));
            Assert.True(r.Present);
            Assert.Equal("error", r.Status);
            Assert.False(r.Malformed, "a present status is not malformed; it is just not \"applied\"");
        }

        // ---- ClassifyReply (pure severity policy) --------------------------------------------

        [Fact(Timeout = 60000)]
        public void Classify_DeferredOnly_IsInfoNotWarn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(deferred: 4));
            (bool warn, bool info) = JanusPeerCtlBatchSink.ClassifyReply(in r);
            Assert.False(warn, "the deferral self-heal is not a fault");
            Assert.True(info, "deferred_listeners>0 is INFO");
        }

        [Fact(Timeout = 60000)]
        public void Classify_Skipped_IsWarn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(skipped: 2));
            Assert.True(JanusPeerCtlBatchSink.ClassifyReply(in r).Warn, "skipped>0 is real loss -> WARN");
        }

        [Fact(Timeout = 60000)]
        public void Classify_NonApplied_IsWarn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(slvoice: "error"));
            Assert.True(JanusPeerCtlBatchSink.ClassifyReply(in r).Warn, "a non-applied status -> WARN");
        }

        [Fact(Timeout = 60000)]
        public void Classify_Malformed_IsWarn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply("{\"janus\":\"success\"}");
            Assert.True(JanusPeerCtlBatchSink.ClassifyReply(in r).Warn, "a malformed inner reply -> WARN");
        }

        [Fact(Timeout = 60000)]
        public void Classify_QuietApplied_LogsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(Body(skipped: 0));
            (bool warn, bool info) = JanusPeerCtlBatchSink.ClassifyReply(in r);
            Assert.False(warn);
            Assert.False(info, "an applied, all-zero reply logs nothing (quiet steady state)");
        }

        [Fact(Timeout = 60000)]
        public void Classify_AbsentReply_LogsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = JanusPeerCtlBatchSink.ParseInnerReply(string.Empty);
            (bool warn, bool info) = JanusPeerCtlBatchSink.ClassifyReply(in r);
            Assert.False(warn);
            Assert.False(info, "an absent reply (old mixer) logs nothing -- identical to a mixer that sends no inner stats");
        }

        // ---- Sink surfacing (LastSendStats) --------------------------------------------------

        [Fact(Timeout = 60000)]
        public async Task Sink_SurfacesDeferredListeners_OnLastSendStats()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sink = SinkReturning(_ => (AdminSendResult.Ok, Body(deferred: 3)));
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.Equal(PeerCtlSendResult.Ok, res);
            Assert.Equal(3, sink.LastSendStats.DeferredListeners);
            Assert.Equal(1, sink.LastSendStats.RepliesParsed);
            Assert.Equal(0, sink.LastSendStats.Anomalies);
        }

        [Fact(Timeout = 60000)]
        public async Task Sink_SkippedInner_SurfacedAsAnomaly_ResultStillOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sink = SinkReturning(_ => (AdminSendResult.Ok, Body(skipped: 2)));
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            {
                var assertedValue = res;
                TestAssert.WithMessage(() => Assert.Equal(PeerCtlSendResult.Ok, assertedValue), "skipped is a mixer detail, not a transport failure");
            }
            Assert.Equal(2, sink.LastSendStats.Skipped);
            Assert.Equal(1, sink.LastSendStats.Anomalies);
        }

        [Fact(Timeout = 60000)]
        public async Task Sink_MalformedInner_ReportedButResultStaysOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The transport succeeded (janus:"success"); a malformed inner reply is reported (anomaly),
            // NOT converted into a transport/protocol failure -- doing so would wrongly trip the sender's
            // latch / staleness guard (a behaviour change). This is the deliberate call.
            var sink = SinkReturning(_ => (AdminSendResult.Ok, "{\"janus\":\"success\"}"));
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.Equal(PeerCtlSendResult.Ok, res);
            Assert.Equal(1, sink.LastSendStats.Anomalies);
        }

        [Fact(Timeout = 60000)]
        public async Task Sink_AbsentInner_ZeroStats_NoInfo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sink = SinkReturning(_ => (AdminSendResult.Ok, string.Empty));   // old mixer / empty body
            PeerCtlSendResult res = await sink.SendAsync(VisOp.Add, Excl(Id(1), Id(2)));
            Assert.Equal(PeerCtlSendResult.Ok, res);
            Assert.Equal(0, sink.LastSendStats.RepliesParsed);
            Assert.Equal(0, sink.LastSendStats.DeferredListeners);
            Assert.Equal(0, sink.LastSendStats.Anomalies);
        }

        [Fact(Timeout = 60000)]
        public async Task Sink_MultiRoom_AggregatesDeferredAcrossRooms()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.Equal(PeerCtlSendResult.Ok, res);
            {
                var assertedValue = sink.LastSendStats.DeferredListeners;
                TestAssert.WithMessage(() => Assert.Equal(7, assertedValue), "summed across the two rooms");
            }
            Assert.Equal(2, sink.LastSendStats.RepliesParsed);
        }
    }
}
