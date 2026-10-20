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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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
            => Assert.That(r.Calls.Record(Alice, Bob, RegionA, out _), Is.Not.Null);

        [Test]
        public void ACallWhileTheStoreIsUp_StillGetsItsCredential()
        {
            Rig r = NewRig();
            Record(r);

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void ACallForASessionTheStoreSaysDoesNotExist_Answers404()
        {
            Rig r = NewRig();

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound), "the store answered: there is no such call");
            Assert.That(r.Breaker.State, Is.EqualTo(BreakerState.Closed), "and an answer is not a failure");
        }

        [Test]
        public void ACallForAnEndedSession_Answers404()
        {
            Rig r = NewRig();
            Record(r);
            r.Calls.Decline(CallId, Bob, out _);

            Assert.That(CallAs(r, Alice, CallId).Status, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public void ACallWhileTheStoreGivesNoAnswer_Answers503()
        {
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void ACallWhileTheStoreTimesOutOrRefusesTheConnection_Answers503()
        {
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;
            r.Service.FailWith = new TimeoutException("the session service did not answer");

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void ACallWhileTheBreakerIsOpen_Answers503_WithoutTouchingTheStore()
        {
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;
            CallAs(r, Alice, CallId);                               // the failure that opens it
            Assert.That(r.Breaker.State, Is.EqualTo(BreakerState.Open));
            r.Service.Up = true;                                    // back, but the breaker has not tried yet
            int before = r.Service.Calls;

            ChatSessionOutcome o = CallAs(r, Alice, CallId);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(r.Service.Calls, Is.EqualTo(before), "refused at once, without a request");
        }

        [Test]
        public void TheStoreGoingDownBetweenTheLookAndTheCredential_Answers503()
        {
            Rig r = NewRig();
            Record(r);
            r.Service.DownOnly = "issuetoken";

            Assert.That(CallAs(r, Alice, CallId).Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AStrangersCallWhileTheStoreIsDown_Answers503_NotAGuessAtForbidden()
        {
            Rig r = NewRig();
            Record(r);
            r.Service.Up = false;

            Assert.That(CallAs(r, Carol, CallId).Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AStrangersCallWhileTheStoreIsUp_StillAnswers403()
        {
            Rig r = NewRig();
            Record(r);

            Assert.That(CallAs(r, Carol, CallId).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public void ACallInProcessIsNeverUnavailable()
        {
            NonSpatialVoiceSessionEngine engine = NewEngine();
            A2ACalls calls = new A2ACalls(() => engine);

            Assert.That(ChatSessionRequestLogic.Decide(Call(CallId), Alice, calls).Status, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }
}
