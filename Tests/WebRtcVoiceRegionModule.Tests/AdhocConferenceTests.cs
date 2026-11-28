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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void StartConferenceProducesTheEventQueueReplyWithBothIds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            UUID temp = UUID.Random();

            bool handled = AdhocVoiceChatSession.TryHandleStartConference(
                StartConference(temp, Bob, Carol), Alice, e, RegionA, enabled: true, out ChatSessionOutcome o);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            {
                var assertedValue = o.Reply;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "no Reply means no event-queue event and the viewer times out");
            }
            {
                var assertedValue = o.Reply.TempSessionId;
                TestAssert.WithMessage(() => Assert.Equal(temp, assertedValue), "the viewer keys the reply by the id it sent");
            }
            Assert.NotEqual(UUID.Zero, o.Reply.SessionId);
            {
                var assertedValue = o.Reply.SessionId;
                TestAssert.WithMessage(() => Assert.NotEqual(temp, assertedValue), "ADHOC is the one type that genuinely re-keys: the server owns the authoritative id");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheEngineHoldsTheAdhocSessionWithTheInviteesFromParams()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            UUID temp = UUID.Random();

            AdhocVoiceChatSession.TryHandleStartConference(
                StartConference(temp, Bob, Carol), Alice, e, RegionA, enabled: true, out ChatSessionOutcome o);

            NonSpatialVoiceSession s = e.Store.Get(o.Reply.SessionId);
            Assert.NotNull(s);
            Assert.Equal(NonSpatialSessionType.Adhoc, s.Type);
            Assert.True(s.RequiresRekey);
            Assert.Equal(temp, s.TempSessionId);
            Assert.Equal(Alice, s.Creator);
            Assert.True((s.Find(Alice)?.HoldsSeat), "the creator takes its seat at start");
            Assert.Equal(MemberState.Invited, s.Find(Bob)?.State);
            Assert.Equal(MemberState.Invited, s.Find(Carol)?.State);
            {
                var assertedValue = s.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "an invitation holds no seat");
            }
            Assert.StartsWith("nsv1:adhoc:", s.RoomKey);
        }

        [Fact(Timeout = 60000)]
        public void ARetriedStartFindsTheSameSessionAndSendsTheSameIds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer mints its temp id once, at session creation, and re-POSTs with the same one
            // after a timeout. A second session would mean a second room and a second reply.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID temp = UUID.Random();

            AdhocVoiceChatSession.TryHandleStartConference(StartConference(temp, Bob), Alice, e, RegionA, true, out ChatSessionOutcome a);
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(temp, Bob), Alice, e, RegionA, true, out ChatSessionOutcome b);

            Assert.Equal(a.Reply.SessionId, b.Reply.SessionId);
            Assert.Equal(temp, b.Reply.TempSessionId);
            Assert.Contains(AdhocVoiceChatSession.DecisionIdempotent, b.Instrument);
            {
                var assertedValue = e.Store.All().Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one session, one room, one reply");
            }
        }

        [Fact(Timeout = 60000)]
        public void TwoDifferentConferencesFromOneCreatorAreTwoSessions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(UUID.Random(), Bob), Alice, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(UUID.Random(), Carol), Alice, e, RegionA, true, out _);
            {
                var assertedValue = e.Store.All().Count;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "idempotency is per temp id, not per creator");
            }
        }

        // ---- params parsing ------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ParamsIsAnArrayForAConference_AndAScalarIsStillAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // startConferenceCoro sends an LLSD ARRAY (llimview.cpp:2484-2487); "start p2p voice"
            // sends a scalar. Accepting both costs two lines and avoids an empty conference.
            OSDArray arr = new OSDArray { OSD.FromUUID(Bob), OSD.FromUUID(Carol) };
            Assert.Equivalent(new[] { Bob, Carol }, AdhocVoiceChatSession.ParseInvitees(new OSDMap { ["params"] = arr }), strict: true);
            Assert.Equivalent(new[] { Bob }, AdhocVoiceChatSession.ParseInvitees(new OSDMap { ["params"] = OSD.FromUUID(Bob) }), strict: true);
            Assert.Empty(AdhocVoiceChatSession.ParseInvitees(new OSDMap()));
            Assert.Empty(AdhocVoiceChatSession.ParseInvitees(null));
        }

        [Fact(Timeout = 60000)]
        public void DuplicateAndZeroInviteesAreDropped()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDArray arr = new OSDArray { OSD.FromUUID(Bob), OSD.FromUUID(Bob), OSD.FromUUID(UUID.Zero) };
            Assert.Equivalent(new[] { Bob }, AdhocVoiceChatSession.ParseInvitees(new OSDMap { ["params"] = arr }), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AConferenceWithNoInviteesStillOpens()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer can open a conference and invite afterwards; refusing here would break that.
            NonSpatialVoiceSessionEngine e = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(StartConference(UUID.Random()), Alice, e, RegionA, true, out ChatSessionOutcome o);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.Equal(1, e.Store.Get(o.Reply.SessionId).SeatsHeld);
        }

        // ---- the arm takes only what is its own -----------------------------------------------------

        [Fact(Timeout = 60000)]
        public void NothingIsTakenWhenAdhocVoiceIsDisabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            Assert.False(AdhocVoiceChatSession.TryHandleStartConference(
                        StartConference(UUID.Random(), Bob), Alice, e, RegionA, enabled: false, out ChatSessionOutcome o));
            Assert.Null(o);
            {
                var assertedValue = e.Store.All();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and no session is created");
            }
        }

        [Fact(Timeout = 60000)]
        public void OnlyStartConferenceIsTaken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            foreach (string m in new[] { "call", "start p2p voice", "accept invitation", "decline invitation", "fetch history", "invite" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(UUID.Random()) };
                Assert.False(AdhocVoiceChatSession.TryHandleStartConference(body, Alice, e, RegionA, true, out _), m);
            }
        }

        [Fact(Timeout = 60000)]
        public void AStartConferenceWithNoSessionIdIsNotOurs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            OSDMap body = new OSDMap { ["method"] = OSD.FromString("start conference") };
            Assert.False(AdhocVoiceChatSession.TryHandleStartConference(body, Alice, e, RegionA, true, out _));
        }

        [Fact(Timeout = 60000)]
        public void TheAdhocRoomKeyIsDistinctFromTheGroupAndP2PKeysForTheSameId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            string adhoc = NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Adhoc, id);
            Assert.NotEqual(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, id), adhoc);
            Assert.NotEqual(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.P2P, id), adhoc);
            Assert.False(UUID.TryParse(adhoc, out _), "still never mistakable for an A2A channel");
        }
    }
}
