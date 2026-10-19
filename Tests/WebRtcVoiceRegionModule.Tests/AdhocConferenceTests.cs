/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The "start conference" request and the event-queue ChatterBoxSessionStartReply.
 *
 * The bug being fixed is the one a viewer user sees in-world: "Unable to start a new chat session with
 * Multi-person chat", followed by a timeout message. We answered 200-and-no-body, and a
 * body would not have helped -- startConferenceCoro reads nothing from the response
 * (llimview.cpp:576-624). The id has to arrive on the event queue.
 *
 * Both the handler arm and the end-to-end path through the real ProvisionVoiceAccountRequest-style
 * handler are covered: the arm decides, and GroupProvisionHandlerTests' pattern proves the module
 * applies it.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class AdhocConferenceTests
    {
        private const string Grid = "example-grid";

        private static NonSpatialVoiceSessionEngine Engine()
            => new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(),
                                             new GroupAdmission((a, g) => true, (a, g) => true) },
                Grid);

        private static OSDMap StartConference(UUID temp, params UUID[] invitees)
        {
            OSDArray arr = new OSDArray();
            foreach (UUID i in invitees) arr.Add(OSD.FromUUID(i));
            return new OSDMap
            {
                ["method"] = OSD.FromString("start conference"),
                ["session-id"] = OSD.FromUUID(temp),
                ["params"] = arr,
                ["alt_params"] = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") },
            };
        }

        // ---- the reply, which is the whole point --------------------------------------------------

        [Test]
        public void StartConferenceProducesTheEventQueueReplyWithBothIds()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            UUID temp = UUID.Random();

            bool handled = AdhocVoiceChatSession.TryHandleStartConference(
                StartConference(temp, Bob, Carol), Alice, e, RegionA, enabled: true, out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            Assert.That(o.Reply, Is.Not.Null, "no Reply means no event-queue event and the viewer times out");
            Assert.That(o.Reply.TempSessionId, Is.EqualTo(temp), "the viewer keys the reply by the id it sent");
            Assert.That(o.Reply.SessionId, Is.Not.EqualTo(UUID.Zero));
            Assert.That(o.Reply.SessionId, Is.Not.EqualTo(temp),
                        "ADHOC is the one type that genuinely re-keys: the server owns the authoritative id");
        }

        [Test]
        public void TheEngineHoldsTheAdhocSessionWithTheInviteesFromParams()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            UUID temp = UUID.Random();

            AdhocVoiceChatSession.TryHandleStartConference(
                StartConference(temp, Bob, Carol), Alice, e, RegionA, enabled: true, out ChatSessionOutcome o);

            NonSpatialVoiceSession s = e.Store.Get(o.Reply.SessionId);
            Assert.That(s, Is.Not.Null);
            Assert.That(s.Type, Is.EqualTo(NonSpatialSessionType.Adhoc));
            Assert.That(s.RequiresRekey, Is.True);
            Assert.That(s.TempSessionId, Is.EqualTo(temp));
            Assert.That(s.Creator, Is.EqualTo(Alice));
            Assert.That(s.Find(Alice)?.HoldsSeat, Is.True, "the creator takes its seat at start");
            Assert.That(s.Find(Bob)?.State, Is.EqualTo(MemberState.Invited));
            Assert.That(s.Find(Carol)?.State, Is.EqualTo(MemberState.Invited));
            Assert.That(s.SeatsHeld, Is.EqualTo(1), "an invitation holds no seat");
            Assert.That(s.RoomKey, Does.StartWith("nsv1:adhoc:"));
        }

        [Test]
        public void ARetriedStartFindsTheSameSessionAndSendsTheSameIds()
        {
            // The viewer mints its temp id once, at session creation, and re-POSTs with the same one
            // after a timeout. A second session would mean a second room and a second reply.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID temp = UUID.Random();

            AdhocVoiceChatSession.TryHandleStartConference(StartConference(temp, Bob), Alice, e, RegionA, true, out ChatSessionOutcome a);
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(temp, Bob), Alice, e, RegionA, true, out ChatSessionOutcome b);

            Assert.That(b.Reply.SessionId, Is.EqualTo(a.Reply.SessionId));
            Assert.That(b.Reply.TempSessionId, Is.EqualTo(temp));
            Assert.That(b.Instrument, Does.Contain(AdhocVoiceChatSession.DecisionIdempotent));
            Assert.That(e.Store.All().Count, Is.EqualTo(1), "one session, one room, one reply");
        }

        [Test]
        public void TwoDifferentConferencesFromOneCreatorAreTwoSessions()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(UUID.Random(), Bob), Alice, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(UUID.Random(), Carol), Alice, e, RegionA, true, out _);
            Assert.That(e.Store.All().Count, Is.EqualTo(2), "idempotency is per temp id, not per creator");
        }

        // ---- params parsing ------------------------------------------------------------------------

        [Test]
        public void ParamsIsAnArrayForAConference_AndAScalarIsStillAccepted()
        {
            // startConferenceCoro sends an LLSD ARRAY (llimview.cpp:2484-2487); "start p2p voice"
            // sends a scalar. Accepting both costs two lines and avoids an empty conference.
            OSDArray arr = new OSDArray { OSD.FromUUID(Bob), OSD.FromUUID(Carol) };
            Assert.That(AdhocVoiceChatSession.ParseInvitees(new OSDMap { ["params"] = arr }),
                        Is.EquivalentTo(new[] { Bob, Carol }));
            Assert.That(AdhocVoiceChatSession.ParseInvitees(new OSDMap { ["params"] = OSD.FromUUID(Bob) }),
                        Is.EquivalentTo(new[] { Bob }));
            Assert.That(AdhocVoiceChatSession.ParseInvitees(new OSDMap()), Is.Empty);
            Assert.That(AdhocVoiceChatSession.ParseInvitees(null), Is.Empty);
        }

        [Test]
        public void DuplicateAndZeroInviteesAreDropped()
        {
            OSDArray arr = new OSDArray { OSD.FromUUID(Bob), OSD.FromUUID(Bob), OSD.FromUUID(UUID.Zero) };
            Assert.That(AdhocVoiceChatSession.ParseInvitees(new OSDMap { ["params"] = arr }),
                        Is.EquivalentTo(new[] { Bob }));
        }

        [Test]
        public void AConferenceWithNoInviteesStillOpens()
        {
            // The viewer can open a conference and invite afterwards; refusing here would break that.
            NonSpatialVoiceSessionEngine e = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(UUID.Random()), Alice, e, RegionA, true, out ChatSessionOutcome o);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(e.Store.Get(o.Reply.SessionId).SeatsHeld, Is.EqualTo(1));
        }

        // ---- the arm takes only what is its own -----------------------------------------------------

        [Test]
        public void NothingIsTakenWhenAdhocVoiceIsDisabled()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(
                        StartConference(UUID.Random(), Bob), Alice, e, RegionA, enabled: false, out ChatSessionOutcome o), Is.False);
            Assert.That(o, Is.Null);
            Assert.That(e.Store.All(), Is.Empty, "and no session is created");
        }

        [Test]
        public void OnlyStartConferenceIsTaken()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            foreach (string m in new[] { "call", "start p2p voice", "accept invitation", "decline invitation", "fetch history", "invite" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(UUID.Random()) };
                Assert.That(AdhocVoiceChatSession.TryHandleStartConference(body, Alice, e, RegionA, true, out _), Is.False, m);
            }
        }

        [Test]
        public void AStartConferenceWithNoSessionIdIsNotOurs()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            OSDMap body = new OSDMap { ["method"] = OSD.FromString("start conference") };
            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(body, Alice, e, RegionA, true, out _), Is.False);
        }

        [Test]
        public void TheAdhocRoomKeyIsDistinctFromTheGroupAndP2PKeysForTheSameId()
        {
            UUID id = UUID.Random();
            string adhoc = NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Adhoc, id);
            Assert.That(adhoc, Is.Not.EqualTo(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, id)));
            Assert.That(adhoc, Is.Not.EqualTo(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.P2P, id)));
            Assert.That(UUID.TryParse(adhoc, out _), Is.False, "still never mistakable for an A2A channel");
        }
    }
}
