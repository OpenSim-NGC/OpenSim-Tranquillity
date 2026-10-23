/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Avatar-to-avatar calls in the grid session store. Two regions share one session service (the engine running in
 * the grid service, reached through GridNonSpatialSessions): the caller is on one region and the callee on the
 * other. A call is one session in the store, both parties hold a seat in it, and hanging up gives the seats back.
 */
using System;
using System.Collections.Generic;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2AGridCallTests
    {
        private static readonly UUID CallId = A2ACalls.ComputeSessionId(Alice, Bob);

        private sealed class Grid
        {
            public readonly FakeClock Clock = new FakeClock();
            public VoiceSessionService Service;
            public GridNonSpatialSessions SessionsA, SessionsB;
            public A2ACalls A, B;   // the calls as region A and region B see them

            public NonSpatialVoiceSession Stored() => ((INonSpatialSessions)SessionsA).Get(CallId);
        }

        private static Grid NewGrid()
        {
            Grid g = new Grid();
            g.Service = VoiceSessionServiceTests.NewService(g.Clock);
            g.SessionsA = VoiceSessionServiceTests.NewRegion(g.Service);
            g.SessionsB = VoiceSessionServiceTests.NewRegion(g.Service);
            g.A = new A2ACalls(() => g.SessionsA);
            g.B = new A2ACalls(() => g.SessionsB);
            return g;
        }

        /// <summary>Alice on region A starts and calls, then both join: Alice on region A, Bob on region B.</summary>
        private static string FormCall(Grid g)
        {
            Assert.That(g.A.Record(Alice, Bob, RegionA, out bool created), Is.Not.Null);
            Assert.That(created, Is.True);
            string token = g.A.IssueToken(CallId, Alice).Token;
            Assert.That(g.A.MarkProvisioned(CallId, Alice, RegionA, "vs-alice"), Is.Not.Null);
            Assert.That(g.B.MarkProvisioned(CallId, Bob, RegionB, "vs-bob"), Is.Not.Null);
            return token;
        }

        [Test]
        public void ACall_IsOneSessionInTheStore_WithBothEndsSeated()
        {
            Grid g = NewGrid();

            string token = FormCall(g);

            Assert.That(g.Service.SessionCount, Is.EqualTo(1), "one call, not one record per region");
            NonSpatialVoiceSession stored = g.Stored();
            Assert.That(stored.Type, Is.EqualTo(NonSpatialSessionType.P2P));
            Assert.That(stored.SeatsHeld, Is.EqualTo(2), "both ends seated");
            Assert.That(stored.Find(Alice).OriginRegion, Is.EqualTo(RegionA));
            Assert.That(stored.Find(Bob).OriginRegion, Is.EqualTo(RegionB));
            Assert.That(stored.Find(Alice).ViewerSession, Is.EqualTo("vs-alice"));
            Assert.That(stored.Find(Bob).ViewerSession, Is.EqualTo("vs-bob"));

            // Region B sees the call region A started, with the credential, and the call is active there too.
            A2ASession seenByB = g.B.TryGet(CallId, Bob);
            Assert.That(seenByB.State, Is.EqualTo(A2ASessionState.Active));
            Assert.That(seenByB.Token, Is.EqualTo(token));
            Assert.That(seenByB.CallerProvisioned && seenByB.CalleeProvisioned, Is.True);
        }

        [Test]
        public void TheCalleesRegionAdmitsTheCredentialTheCallersRegionIssued()
        {
            Grid g = NewGrid();
            g.A.Record(Alice, Bob, RegionA, out _);
            string token = g.A.IssueToken(CallId, Alice).Token;

            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(CallId.ToString()),
                ["credentials"] = OSD.FromString(token),
            };

            Assert.That(A2AProvisionAdmission.Decide(body, Bob, g.B).Kind, Is.EqualTo(ProvisionKind.Multiagent));
            Assert.That(A2AProvisionAdmission.Decide(body, Carol, g.B).Decision, Is.EqualTo(A2AProvisionAdmission.DecisionNotParty));
            body["credentials"] = OSD.FromString(new string('0', 64));
            Assert.That(A2AProvisionAdmission.Decide(body, Bob, g.B).Decision, Is.EqualTo(A2AProvisionAdmission.DecisionBadToken));
        }

        [Test]
        public void HangingUp_ReleasesBothSeats_AndTheCallEnds()
        {
            Grid g = NewGrid();
            FormCall(g);

            IReadOnlyList<NonSpatialVoiceSession> first = g.SessionsA.DepartByViewerSession(Alice, "vs-alice", DepartureReason.VoiceTeardown);
            Assert.That(g.A.Ended(first), Is.Empty, "Bob is still in the call");
            Assert.That(g.Stored().SeatsHeld, Is.EqualTo(1), "Alice's seat is back");
            Assert.That(g.Stored().Find(Bob).HoldsSeat, Is.True);

            IReadOnlyList<NonSpatialVoiceSession> second = g.SessionsB.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);

            List<A2ASession> ended = g.B.Ended(second);
            Assert.That(ended, Has.Count.EqualTo(1));
            Assert.That(ended[0].SessionId, Is.EqualTo(CallId));
            Assert.That(g.B.TryGet(CallId, Bob), Is.Null, "the call is over for both regions");
            Assert.That(g.A.TryGet(CallId, Alice), Is.Null);
            Assert.That(g.Stored().SeatsHeld, Is.EqualTo(0), "both seats are back");
            g.Service.Sweep();
            Assert.That(g.Service.SessionCount, Is.EqualTo(0), "and the store lets the call go");
        }

        [Test]
        public void AfterHangUp_ANewCallHasANewCredential()
        {
            Grid g = NewGrid();
            string first = FormCall(g);
            g.SessionsA.DepartByViewerSession(Alice, "vs-alice", DepartureReason.VoiceTeardown);
            g.SessionsB.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);

            Assert.That(g.B.Record(Bob, Alice, RegionB, out bool created), Is.Not.Null);
            string second = g.B.IssueToken(CallId, Bob).Token;

            Assert.That(created, Is.True);
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(g.Service.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void ARegionClosingTheCallersClient_EndsTheCallOnlyWhenTheOtherPartyIsGoneToo()
        {
            Grid g = NewGrid();
            FormCall(g);

            Assert.That(g.A.Ended(g.SessionsA.DepartAll(Alice, DepartureReason.PresenceLost)), Is.Empty);
            Assert.That(g.B.TryGet(CallId, Bob).CalleeProvisioned, Is.True, "Bob is still joined, as region B sees it");

            Assert.That(g.B.Ended(g.SessionsB.DepartAll(Bob, DepartureReason.PresenceLost)), Has.Count.EqualTo(1));
            Assert.That(g.Stored().SeatsHeld, Is.EqualTo(0));
        }

        [Test]
        public void AnUnansweredInvitation_LapsesInTheServiceAfterTheInviteTtl_FreeingTheCallersSeat()
        {
            Grid g = NewGrid();
            g.A.Record(Alice, Bob, RegionA, out _);
            g.A.IssueToken(CallId, Alice);
            Assert.That(g.Stored().SeatsHeld, Is.EqualTo(1), "the caller is seated from the start");

            g.Clock.Now += NonSpatialVoiceSessionEngine.DefaultInviteTtl - TimeSpan.FromSeconds(1);
            Assert.That(g.B.TryGet(CallId, Bob), Is.Not.Null, "still within the invitation's life");

            g.Clock.Now += TimeSpan.FromSeconds(2);
            Assert.That(g.B.TryGet(CallId, Bob), Is.Null, "past it, the call is gone on every region");
            Assert.That(g.A.IssueToken(CallId, Alice), Is.Null);
            Assert.That(g.Stored().SeatsHeld, Is.EqualTo(0));
            g.Service.Sweep();
            Assert.That(g.Service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ACallIsOutsideTheSessionCaps()
        {
            FakeClock clock = new FakeClock();
            VoiceSessionService service = VoiceSessionServiceTests.NewService(clock,
                new NonSpatialLimits { MaxNonSpatialSessions = 1, MaxSessionsPerAgent = 1 });
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(service);
            A2ACalls calls = new A2ACalls(() => region);

            for (int i = 0; i < 5; i++)
                Assert.That(calls.Record(Alice, UUID.Random(), RegionA, out _), Is.Not.Null, "call " + i);

            Assert.That(region.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True, "and they do not use up the room conferences have");
        }

        [Test]
        public void WithNoStore_NothingIsRecorded_AndTheViewerIsToldTheServiceIsUnavailable()
        {
            A2ACalls calls = new A2ACalls(() => null);

            Assert.That(calls.Record(Alice, Bob, RegionA, out bool created), Is.Null);
            Assert.That(created, Is.False);
            Assert.That(calls.TryGet(CallId, Alice), Is.Null);

            OSDMap start = new OSDMap
            {
                ["method"] = OSD.FromString("start p2p voice"),
                ["session-id"] = OSD.FromUUID(CallId),
                ["params"] = OSD.FromUUID(Bob),
            };
            Assert.That(ChatSessionRequestLogic.Decide(start, Alice, calls).Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }
    }
}
