/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * How a call's voice mutes reach the mixer: the columns built from the record, the batch sent to one named
 * room, and how the mixer's answer is read; and the text check on a conference and on group chat.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class CallMuteEnforcementTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;

        private static NonSpatialVoiceSession ThreeInRoom(NonSpatialVoiceSessionEngine e)
        {
            e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            e.Accept(Group, Bob, RegionA);
            e.Accept(Group, Carol, RegionA);
            return e.Get(Group);
        }

        private static Func<UUID, bool> AliceModerates => a => a == Alice;

        private static void AssertColumns(Dictionary<UUID, IReadOnlyCollection<UUID>> columns, UUID listener, params UUID[] sources)
            => TestAssert.WithMessage(() => { Assert.Equivalent(sources, columns[listener], strict: true); }, "column of " + listener);

        // ---- the columns ----------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AMutedMemberIsInEveryonesColumn_ItsOwnIncluded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, true);

            var columns = CallMuteColumns.Build(s, AliceModerates);

            Assert.Equivalent(new[] { Alice, Bob, Carol }, columns.Keys, strict: true);
            AssertColumns(columns, Alice, Bob);
            AssertColumns(columns, Bob, Bob);
            AssertColumns(columns, Carol, Bob);
        }

        [Fact(Timeout = 60000)]
        public void WhenNobodyIsMutedEveryColumnIsEmpty_SoARepeatClearsWhatWasSet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);

            var columns = CallMuteColumns.Build(s, AliceModerates);

            Assert.True(columns.Values.All(c => c.Count == 0));
            Assert.Equal(3, columns.Count);
        }

        [Fact(Timeout = 60000)]
        public void MuteAllSilencesEveryoneButTheModerator_AndALetSpeakMemberIsLeftOut()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Carol, false);

            var columns = CallMuteColumns.Build(s, AliceModerates);

            AssertColumns(columns, Alice, Bob);
            AssertColumns(columns, Bob, Bob);
            AssertColumns(columns, Carol, Bob);
        }

        [Fact(Timeout = 60000)]
        public void OnlyMembersHoldingASeatHaveAColumn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);
            e.Depart(Group, Carol, DepartureReason.VoiceTeardown);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);

            var columns = CallMuteColumns.Build(e.Get(Group), AliceModerates);

            Assert.Equivalent(new[] { Alice, Bob }, columns.Keys, strict: true);
            AssertColumns(columns, Alice, Bob);
        }

        // ---- reading the mixer's answer --------------------------------------------------------------------

        private static JanusPeerCtlBatchSink.SlvoiceReply Reply(string status, int muteEntries, string reason = null, bool present = true)
            => new JanusPeerCtlBatchSink.SlvoiceReply { Present = present, Status = status, MuteEntries = muteEntries, Reason = reason };

        [Fact(Timeout = 60000)]
        public void TheMixersAnswerIsReadHonestly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ParticipantMuteResult.Applied, CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("applied", 3), 3));
            {
                var assertedValue = CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("applied", 0), 3);
                TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Unsupported, assertedValue), "a mixer that parsed no mute entries has no mute channel");
            }
            {
                var assertedValue = CallMuteColumns.Classify(PeerCtlSendResult.Ok, default, 3);
                TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Unsupported, assertedValue), "an old mixer answers nothing about it");
            }
            Assert.Equal(ParticipantMuteResult.NotInRoom, CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("error", 0, "unknown_room"), 3));
            Assert.Equal(ParticipantMuteResult.Failed, CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("error", 0, "bad_request"), 3));
            Assert.Equal(ParticipantMuteResult.Failed, CallMuteColumns.Classify(PeerCtlSendResult.TransportError, default, 3));
            Assert.Equal(ParticipantMuteResult.Failed, CallMuteColumns.Classify(PeerCtlSendResult.ProtocolError, default, 3));
            {
                var assertedValue = CallMuteColumns.Classify(PeerCtlSendResult.Ok, default, 0);
                TestAssert.WithMessage(() => Assert.Equal(ParticipantMuteResult.Applied, assertedValue), "no one to tell");
            }
        }

        // ---- the batch to one room ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public async Task TheBatchGoesToTheNamedRoomOnly_NotToTheEstateRoomOrWhereTheTableSaysAnAgentIs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<OSDMap> sent = new();
            JanusPeerCtlBatchSink sink = new JanusPeerCtlBatchSink("http://gateway.example.org:7188/admin", "token", TimeSpan.FromSeconds(1),
                UUID.Random(), "Test Region", 1,
                req =>
                {
                    sent.Add(req);
                    return Task.FromResult((AdminSendResult.Ok, "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"mute_entries\":2}}"));
                });
            sink.RoomOf = _ => 4242;   // where the visibility table would put every agent
            UUID listener = new UUID("11111111-1111-4111-8111-111111111111");
            UUID source = new UUID("22222222-2222-4222-8222-222222222222");
            var mute = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = new List<UUID> { source }, [source] = new List<UUID> { source } };

            (PeerCtlSendResult result, JanusPeerCtlBatchSink.SlvoiceReply reply) = await sink.SendToRoomAsync(777, VisOp.Replace, null, mute);

            Assert.Single(sent);
            OSDMap body = sent[0];
            Assert.Equal("peer_ctl_batch", body["request"].AsString());
            Assert.Equal("replace", body["op"].AsString());
            Assert.Equal(777, body["room"].AsInteger());
            Assert.Empty(((OSDMap)body["excl"]));
            OSDMap muteMap = (OSDMap)body["mute"];
            Assert.Equivalent(new[] { listener.ToString(), source.ToString() }, muteMap.Keys, strict: true);
            Assert.Equal(new[] { source.ToString() }, ((OSDArray)muteMap[listener.ToString()]).Select(o => o.AsString()));
            Assert.False(body.ContainsKey("room_epoch"), "a call's room is not under the visibility authority");
            Assert.Equal(PeerCtlSendResult.Ok, result);
            Assert.Equal(2, reply.MuteEntries);
        }

        [Fact(Timeout = 60000)]
        public async Task AnEmptyColumnIsSentAsAnEmptyList_ItClearsThatListener()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<OSDMap> sent = new();
            JanusPeerCtlBatchSink sink = new JanusPeerCtlBatchSink("http://gateway.example.org:7188/admin", "token", TimeSpan.FromSeconds(1),
                UUID.Random(), "Test Region", 1, req => { sent.Add(req); return Task.FromResult((AdminSendResult.Ok, "{}")); });
            UUID listener = UUID.Random();

            await sink.SendToRoomAsync(5, VisOp.Replace, null, new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = new List<UUID>() });

            Assert.Empty(((OSDArray)((OSDMap)sent[0]["mute"])[listener.ToString()]));
        }
    }
}
