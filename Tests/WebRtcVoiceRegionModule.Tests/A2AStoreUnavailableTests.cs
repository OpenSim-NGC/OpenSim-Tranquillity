/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A "call" for an avatar-to-avatar call, when the calls live in the grid session store. A store that cannot be
 * reached (no answer, a timeout, a refused connection, an open breaker) is not a store that says the call does not
 * exist: the first answers 503, as "start p2p voice" does, and only the second answers 404.
 */
using System;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class A2AStoreUnavailableTests
    {
        private static readonly UUID CallId = A2ACalls.ComputeSessionId(Alice, Bob);

        private sealed class Rig
        {
            public readonly FakeClock Clock = new FakeClock();
            public GridStoreResilienceTests.FlakyService Service;
            public SessionCircuitBreaker Breaker;
            public A2ACalls Calls;
        }

        private static Rig NewRig()
        {
            Rig r = new Rig();
            r.Service = new GridStoreResilienceTests.FlakyService(GridStoreResilienceTests.NewReal(r.Clock));
            r.Breaker = new SessionCircuitBreaker(1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), r.Clock.Get);
            GridNonSpatialSessions region = GridStoreResilienceTests.NewRegion(new GuardedVoiceSessionService(r.Service, r.Breaker));
            r.Calls = new A2ACalls(() => region);
            return r;
        }

        private static OSDMap Call(UUID sessionId) => new OSDMap
        {
            ["method"] = OSD.FromString("call"),
            ["session-id"] = OSD.FromUUID(sessionId),
        };

        private static ChatSessionOutcome CallAs(Rig r, UUID agent, UUID sessionId)
            => ChatSessionRequestLogic.Decide(Call(sessionId), agent, r.Calls);

        private static void Record(Rig r)
            => Assert.NotNull(r.Calls.Record(Alice, Bob, RegionA, out _));

        [Fact(Timeout = 60000)]
        public void ACallWhileTheStoreIsUp_StillGetsItsCredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.Equal(HttpStatusCode.OK, o.Status);
        }

        [Fact(Timeout = 60000)]
        public void ACallForASessionTheStoreSaysDoesNotExist_Answers404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.NotFound, assertedValue), "the store answered: there is no such call");
            }
            {
                var assertedValue = r.Breaker.State;
                TestAssert.WithMessage(() => Assert.Equal(BreakerState.Closed, assertedValue), "and an answer is not a failure");
            }
        }

        [Fact(Timeout = 60000)]
        public void ACallForAnEndedSession_Answers404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);
            r.Calls.Decline(CallId, Bob, out _);

            Assert.Equal(HttpStatusCode.NotFound, CallAs(r, Alice, CallId).Status);
        }

        [Fact(Timeout = 60000)]
        public void ACallWhileTheStoreGivesNoAnswer_Answers503()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, o.Status);
        }

        [Fact(Timeout = 60000)]
        public void ACallWhileTheStoreTimesOutOrRefusesTheConnection_Answers503()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;
            r.Service.FailWith = new TimeoutException("the session service did not answer");

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, o.Status);
        }

        [Fact(Timeout = 60000)]
        public void ACallWhileTheBreakerIsOpen_Answers503_WithoutTouchingTheStore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;
            CallAs(r, Alice, CallId);                               // the failure that opens it
            Assert.Equal(BreakerState.Open, r.Breaker.State);
            r.Service.Up = true;                                    // back, but the breaker has not tried yet
            int before = r.Service.Calls;

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, o.Status);
            {
                var assertedValue = r.Service.Calls;
                TestAssert.WithMessage(() => Assert.Equal(before, assertedValue), "refused at once, without a request");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheStoreGoingDownBetweenTheLookAndTheCredential_Answers503()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);
            r.Service.DownOnly = "issuetoken";

            Assert.Equal(HttpStatusCode.ServiceUnavailable, CallAs(r, Alice, CallId).Status);
        }

        [Fact(Timeout = 60000)]
        public void AStrangersCallWhileTheStoreIsDown_Answers503_NotAGuessAtForbidden()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, CallAs(r, Carol, CallId).Status);
        }

        [Fact(Timeout = 60000)]
        public void AStrangersCallWhileTheStoreIsUp_StillAnswers403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig();
            Record(r);

            Assert.Equal(HttpStatusCode.Forbidden, CallAs(r, Carol, CallId).Status);
        }

        [Fact(Timeout = 60000)]
        public void ACallInProcessIsNeverUnavailable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine engine = NewEngine();
            A2ACalls calls = new A2ACalls(() => engine);

            Assert.Equal(HttpStatusCode.NotFound, ChatSessionRequestLogic.Decide(Call(CallId), Alice, calls).Status);
        }
    }
}
