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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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
            Assert.NotNull(g.A.Record(Alice, Bob, RegionA, out bool created));
            Assert.True(created);
            string token = g.A.IssueToken(CallId, Alice).Token;
            Assert.NotNull(g.A.MarkProvisioned(CallId, Alice, RegionA, "vs-alice"));
            Assert.NotNull(g.B.MarkProvisioned(CallId, Bob, RegionB, "vs-bob"));
            return token;
        }

        [Fact(Timeout = 60000)]
        public void ACall_IsOneSessionInTheStore_WithBothEndsSeated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            string token = FormCall(g);

            {
                var assertedValue = g.Service.SessionCount;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one call, not one record per region");
            }
            NonSpatialVoiceSession stored = g.Stored();
            Assert.Equal(NonSpatialSessionType.P2P, stored.Type);
            {
                var assertedValue = stored.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "both ends seated");
            }
            Assert.Equal(RegionA, stored.Find(Alice).OriginRegion);
            Assert.Equal(RegionB, stored.Find(Bob).OriginRegion);
            Assert.Equal("vs-alice", stored.Find(Alice).ViewerSession);
            Assert.Equal("vs-bob", stored.Find(Bob).ViewerSession);

            // Region B sees the call region A started, with the credential, and the call is active there too.
            A2ASession seenByB = g.B.TryGet(CallId, Bob);
            Assert.Equal(A2ASessionState.Active, seenByB.State);
            Assert.Equal(token, seenByB.Token);
            Assert.True(seenByB.CallerProvisioned && seenByB.CalleeProvisioned);
        }

        [Fact(Timeout = 60000)]
        public void TheCalleesRegionAdmitsTheCredentialTheCallersRegionIssued()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            g.A.Record(Alice, Bob, RegionA, out _);
            string token = g.A.IssueToken(CallId, Alice).Token;

            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(CallId.ToString()),
                ["credentials"] = OSD.FromString(token),
            };

            Assert.Equal(ProvisionKind.Multiagent, A2AProvisionAdmission.Decide(body, Bob, g.B).Kind);
            Assert.Equal(A2AProvisionAdmission.DecisionNotParty, A2AProvisionAdmission.Decide(body, Carol, g.B).Decision);
            body["credentials"] = OSD.FromString(new string('0', 64));
            Assert.Equal(A2AProvisionAdmission.DecisionBadToken, A2AProvisionAdmission.Decide(body, Bob, g.B).Decision);
        }

        [Fact(Timeout = 60000)]
        public void HangingUp_ReleasesBothSeats_AndTheCallEnds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            FormCall(g);

            IReadOnlyList<NonSpatialVoiceSession> first = g.SessionsA.DepartByViewerSession(Alice, "vs-alice", DepartureReason.VoiceTeardown);
            {
                var assertedValue = g.A.Ended(first);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "Bob is still in the call");
            }
            {
                var assertedValue = g.Stored().SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "Alice's seat is back");
            }
            Assert.True(g.Stored().Find(Bob).HoldsSeat);

            IReadOnlyList<NonSpatialVoiceSession> second = g.SessionsB.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);

            List<A2ASession> ended = g.B.Ended(second);
            Assert.Single(ended);
            Assert.Equal(CallId, ended[0].SessionId);
            {
                var assertedValue = g.B.TryGet(CallId, Bob);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the call is over for both regions");
            }
            Assert.Null(g.A.TryGet(CallId, Alice));
            {
                var assertedValue = g.Stored().SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "both seats are back");
            }
            g.Service.Sweep();
            {
                var assertedValue = g.Service.SessionCount;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and the store lets the call go");
            }
        }

        [Fact(Timeout = 60000)]
        public void AfterHangUp_ANewCallHasANewCredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            string first = FormCall(g);
            g.SessionsA.DepartByViewerSession(Alice, "vs-alice", DepartureReason.VoiceTeardown);
            g.SessionsB.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);

            Assert.NotNull(g.B.Record(Bob, Alice, RegionB, out bool created));
            string second = g.B.IssueToken(CallId, Bob).Token;

            Assert.True(created);
            Assert.NotEqual(first, second);
            Assert.Equal(1, g.Service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void ARegionClosingTheCallersClient_EndsTheCallOnlyWhenTheOtherPartyIsGoneToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            FormCall(g);

            Assert.Empty(g.A.Ended(g.SessionsA.DepartAll(Alice, DepartureReason.PresenceLost)));
            Assert.True(g.B.TryGet(CallId, Bob).CalleeProvisioned, "Bob is still joined, as region B sees it");

            Assert.Single(g.B.Ended(g.SessionsB.DepartAll(Bob, DepartureReason.PresenceLost)));
            Assert.Equal(0, g.Stored().SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void AnUnansweredInvitation_LapsesInTheServiceAfterTheInviteTtl_FreeingTheCallersSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            g.A.Record(Alice, Bob, RegionA, out _);
            g.A.IssueToken(CallId, Alice);
            {
                var assertedValue = g.Stored().SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the caller is seated from the start");
            }

            g.Clock.Now += NonSpatialVoiceSessionEngine.DefaultInviteTtl - TimeSpan.FromSeconds(1);
            {
                var assertedValue = g.B.TryGet(CallId, Bob);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "still within the invitation's life");
            }

            g.Clock.Now += TimeSpan.FromSeconds(2);
            {
                var assertedValue = g.B.TryGet(CallId, Bob);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "past it, the call is gone on every region");
            }
            Assert.Null(g.A.IssueToken(CallId, Alice));
            Assert.Equal(0, g.Stored().SeatsHeld);
            g.Service.Sweep();
            Assert.Equal(0, g.Service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void ACallIsOutsideTheSessionCaps()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            VoiceSessionService service = VoiceSessionServiceTests.NewService(clock,
                new NonSpatialLimits { MaxNonSpatialSessions = 1, MaxSessionsPerAgent = 1 });
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(service);
            A2ACalls calls = new A2ACalls(() => region);

            for (int i = 0; i < 5; i++)
                {
                    var assertedValue = calls.Record(Alice, UUID.Random(), RegionA, out _);
                    TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "call " + i);
                }

            Assert.True(region.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, "and they do not use up the room conferences have");
        }

        [Fact(Timeout = 60000)]
        public void WithNoStore_NothingIsRecorded_AndTheViewerIsToldTheServiceIsUnavailable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ACalls calls = new A2ACalls(() => null);

            Assert.Null(calls.Record(Alice, Bob, RegionA, out bool created));
            Assert.False(created);
            Assert.Null(calls.TryGet(CallId, Alice));

            OSDMap start = new OSDMap
            {
                ["method"] = OSD.FromString("start p2p voice"),
                ["session-id"] = OSD.FromUUID(CallId),
                ["params"] = OSD.FromUUID(Bob),
            };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ChatSessionRequestLogic.Decide(start, Alice, calls).Status);
        }
    }
}
