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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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
            => Assert.That(columns[listener], Is.EquivalentTo(sources), "column of " + listener);

        // ---- the columns ----------------------------------------------------------------------------------

        [Test]
        public void AMutedMemberIsInEveryonesColumn_ItsOwnIncluded()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Bob, true);

            var columns = CallMuteColumns.Build(s, AliceModerates);

            Assert.That(columns.Keys, Is.EquivalentTo(new[] { Alice, Bob, Carol }));
            AssertColumns(columns, Alice, Bob);
            AssertColumns(columns, Bob, Bob);
            AssertColumns(columns, Carol, Bob);
        }

        [Test]
        public void WhenNobodyIsMutedEveryColumnIsEmpty_SoARepeatClearsWhatWasSet()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);

            var columns = CallMuteColumns.Build(s, AliceModerates);

            Assert.That(columns.Values.All(c => c.Count == 0), Is.True);
            Assert.That(columns.Count, Is.EqualTo(3));
        }

        [Test]
        public void MuteAllSilencesEveryoneButTheModerator_AndALetSpeakMemberIsLeftOut()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);
            e.Moderate(Group, Alice, ModerationOp.VoiceMute, Carol, false);

            var columns = CallMuteColumns.Build(s, AliceModerates);

            AssertColumns(columns, Alice, Bob);
            AssertColumns(columns, Bob, Bob);
            AssertColumns(columns, Carol, Bob);
        }

        [Test]
        public void OnlyMembersHoldingASeatHaveAColumn()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            NonSpatialVoiceSession s = ThreeInRoom(e);
            e.Depart(Group, Carol, DepartureReason.VoiceTeardown);
            e.Moderate(Group, Alice, ModerationOp.VoiceModerated, UUID.Zero, true);

            var columns = CallMuteColumns.Build(e.Get(Group), AliceModerates);

            Assert.That(columns.Keys, Is.EquivalentTo(new[] { Alice, Bob }));
            AssertColumns(columns, Alice, Bob);
        }

        // ---- reading the mixer's answer --------------------------------------------------------------------

        private static JanusPeerCtlBatchSink.SlvoiceReply Reply(string status, int muteEntries, string reason = null, bool present = true)
            => new JanusPeerCtlBatchSink.SlvoiceReply { Present = present, Status = status, MuteEntries = muteEntries, Reason = reason };

        [Test]
        public void TheMixersAnswerIsReadHonestly()
        {
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("applied", 3), 3), Is.EqualTo(ParticipantMuteResult.Applied));
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("applied", 0), 3), Is.EqualTo(ParticipantMuteResult.Unsupported),
                        "a mixer that parsed no mute entries has no mute channel");
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.Ok, default, 3), Is.EqualTo(ParticipantMuteResult.Unsupported),
                        "an old mixer answers nothing about it");
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("error", 0, "unknown_room"), 3), Is.EqualTo(ParticipantMuteResult.NotInRoom));
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.Ok, Reply("error", 0, "bad_request"), 3), Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.TransportError, default, 3), Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.ProtocolError, default, 3), Is.EqualTo(ParticipantMuteResult.Failed));
            Assert.That(CallMuteColumns.Classify(PeerCtlSendResult.Ok, default, 0), Is.EqualTo(ParticipantMuteResult.Applied), "no one to tell");
        }

        // ---- the batch to one room ---------------------------------------------------------------------------

        [Test]
        public async Task TheBatchGoesToTheNamedRoomOnly_NotToTheEstateRoomOrWhereTheTableSaysAnAgentIs()
        {
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

            Assert.That(sent, Has.Count.EqualTo(1));
            OSDMap body = sent[0];
            Assert.That(body["request"].AsString(), Is.EqualTo("peer_ctl_batch"));
            Assert.That(body["op"].AsString(), Is.EqualTo("replace"));
            Assert.That(body["room"].AsInteger(), Is.EqualTo(777));
            Assert.That(((OSDMap)body["excl"]).Count, Is.EqualTo(0));
            OSDMap muteMap = (OSDMap)body["mute"];
            Assert.That(muteMap.Keys, Is.EquivalentTo(new[] { listener.ToString(), source.ToString() }));
            Assert.That(((OSDArray)muteMap[listener.ToString()]).Select(o => o.AsString()), Is.EqualTo(new[] { source.ToString() }));
            Assert.That(body.ContainsKey("room_epoch"), Is.False, "a call's room is not under the visibility authority");
            Assert.That(result, Is.EqualTo(PeerCtlSendResult.Ok));
            Assert.That(reply.MuteEntries, Is.EqualTo(2));
        }

        [Test]
        public async Task AnEmptyColumnIsSentAsAnEmptyList_ItClearsThatListener()
        {
            List<OSDMap> sent = new();
            JanusPeerCtlBatchSink sink = new JanusPeerCtlBatchSink("http://gateway.example.org:7188/admin", "token", TimeSpan.FromSeconds(1),
                UUID.Random(), "Test Region", 1, req => { sent.Add(req); return Task.FromResult((AdminSendResult.Ok, "{}")); });
            UUID listener = UUID.Random();

            await sink.SendToRoomAsync(5, VisOp.Replace, null, new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = new List<UUID>() });

            Assert.That(((OSDArray)((OSDMap)sent[0]["mute"])[listener.ToString()]).Count, Is.EqualTo(0));
        }
    }
}
