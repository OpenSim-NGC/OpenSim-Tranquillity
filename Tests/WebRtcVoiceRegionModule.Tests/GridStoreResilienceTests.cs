/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What a region does when the grid call session service does not answer: a circuit breaker that
 * refuses calls at once instead of letting each one wait out its timeout, and a bounded queue that
 * keeps departures and delivers them when the service is back. Time is a test clock throughout.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class GridStoreResilienceTests
    {
        private static readonly UUID GroupId = NonSpatialIdentityTests.Group;

        /// <summary>A real service that can be made to stop answering, and counts the calls that reach it.</summary>
        internal sealed class FlakyService : IVoiceSessionService
        {
            public readonly VoiceSessionService Inner;
            public bool Up = true;
            /// <summary>When set, only the operation of this name goes unanswered; the rest are answered.</summary>
            public string DownOnly;
            /// <summary>When set, a call that would go unanswered throws this instead, as a timeout or a refused connection does.</summary>
            public Exception FailWith;
            public int Calls;
            public readonly List<string> Delivered = new List<string>();

            public FlakyService(VoiceSessionService inner) { Inner = inner; }

            private SessionReply Pass(string what, Func<SessionReply> call)
            {
                Calls++;
                if (!Up || what == DownOnly)
                {
                    if (FailWith != null) throw FailWith;
                    return null;
                }
                Delivered.Add(what);
                return call();
            }

            public SessionReply Start(int type, UUID creator, UUID otherOrOwner, UUID tempSessionId, UUID originRegion, int requestedCap) => Pass("start", () => Inner.Start(type, creator, otherOrOwner, tempSessionId, originRegion, requestedCap));
            public SessionReply StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion, IReadOnlyList<UUID> invitees, int requestedCap) => Pass("startadhoc", () => Inner.StartAdhoc(creator, tempSessionId, originRegion, invitees, requestedCap));
            public SessionReply CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyList<UUID> asked) => Pass("checkinvitecount", () => Inner.CheckInviteCount(sessionId, inviter, asked));
            public SessionReply Invite(UUID sessionId, UUID inviter, UUID invitee) => Pass("invite", () => Inner.Invite(sessionId, inviter, invitee));
            public SessionReply Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession) => Pass("accept", () => Inner.Accept(sessionId, agent, originRegion, viewerSession));
            public SessionReply MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession) => Pass("markpresent", () => Inner.MarkPresent(sessionId, agent, originRegion, viewerSession));
            public SessionReply Decline(UUID sessionId, UUID agent) => Pass("decline", () => Inner.Decline(sessionId, agent));
            public SessionReply Depart(UUID sessionId, UUID agent, int reason) => Pass("depart:" + agent, () => Inner.Depart(sessionId, agent, reason));
            public SessionReply LeaveChat(UUID sessionId, UUID agent) => Pass("leavechat:" + agent, () => Inner.LeaveChat(sessionId, agent));
            public SessionReply DepartByViewerSession(UUID agent, string viewerSession, int reason) => Pass("departbyviewer:" + agent, () => Inner.DepartByViewerSession(agent, viewerSession, reason));
            public SessionReply DepartAll(UUID agent, int reason) => Pass("departall:" + agent, () => Inner.DepartAll(agent, reason));
            public SessionReply DepartRegion(UUID region) => Pass("departregion", () => Inner.DepartRegion(region));
            public SessionReply RenewSeats(IReadOnlyList<SeatKey> seats) => Pass("renewseats", () => Inner.RenewSeats(seats));
            public SessionReply MarkInvited(UUID sessionId, IReadOnlyList<UUID> agents) => Pass("markinvited", () => Inner.MarkInvited(sessionId, agents));
            public SessionReply VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, int type) => Pass("verifyring", () => Inner.VerifyRing(sessionId, target, claimedCaller, type));
            public SessionReply CarrierKey(UUID sessionId, UUID agent) => Pass("carrierkey", () => Inner.CarrierKey(sessionId, agent));
            public SessionReply Moderate(UUID sessionId, UUID by, int op, UUID target, bool on, string reason = null, int minutes = 0) => Pass("moderate", () => Inner.Moderate(sessionId, by, op, target, on, reason, minutes));
            public SessionReply IssueToken(UUID sessionId, UUID agent) => Pass("issuetoken", () => Inner.IssueToken(sessionId, agent));
            public SessionReply TakeConferenceRequest(UUID agent) => Pass("takeconferencerequest", () => Inner.TakeConferenceRequest(agent));
            public SessionReply Get(UUID sessionId, UUID agent = default) => Pass("get", () => Inner.Get(sessionId, agent));
            public SessionReply GetByRoomKey(string roomKey, UUID agent = default) => Pass("getbyroomkey", () => Inner.GetByRoomKey(roomKey, agent));
        }

        internal static FakeClock Clock() => new FakeClock();

        internal static VoiceSessionService NewReal(FakeClock clock) => new VoiceSessionService("example-grid", clock: clock.Get);

        private static SessionCircuitBreaker NewBreaker(FakeClock clock, int failures = 5, int windowSeconds = 10, int openSeconds = 5)
            => new SessionCircuitBreaker(failures, TimeSpan.FromSeconds(windowSeconds), TimeSpan.FromSeconds(openSeconds), clock.Get);

        internal static DepartOutbox NewOutbox(IVoiceSessionService service, FakeClock clock, int max = 256, int ageSeconds = 60)
            => new DepartOutbox(service, max, TimeSpan.FromSeconds(ageSeconds), clock.Get);

        internal static GridNonSpatialSessions NewRegion(IVoiceSessionService service, DepartOutbox outbox = null, FakeClock clock = null)
            => new GridNonSpatialSessions(service, new INonSpatialAdmission[]
            {
                new P2PAdmission(),
                new AdhocAdmission(),
                new GroupAdmission((agent, group) => true, (agent, group) => true),
            }, clock == null ? null : clock.Get, outbox);

        internal static bool Holds(VoiceSessionService service, UUID session, UUID agent)
            => service.Get(session).Session.Members.Any(m => m.Agent == agent.ToString()
                && (m.State == (int)MemberState.Accepted || m.State == (int)MemberState.Present));

        // ---- the circuit breaker ---------------------------------------------------------------------

        [Test]
        public void TheBreakerOpensAfterRepeatedFailures_AndNotBefore()
        {
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);

            for (int i = 0; i < 4; i++)
            {
                Assert.That(breaker.TryEnter(), Is.True);
                breaker.RecordFailure();
                clock.Now += TimeSpan.FromSeconds(1);
            }
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Closed), "four failures are not enough");
            Assert.That(breaker.TryEnter(), Is.True);

            breaker.RecordFailure();
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Open));
            Assert.That(breaker.TryEnter(), Is.False);
        }

        [Test]
        public void FailuresSpreadOutOverTimeNeverOpenTheBreaker()
        {
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);

            for (int i = 0; i < 40; i++)
            {
                Assert.That(breaker.TryEnter(), Is.True, "failure " + i);
                breaker.RecordFailure();
                clock.Now += TimeSpan.FromSeconds(3);   // five inside ten seconds is never reached
            }
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Closed));
        }

        [Test]
        public void AnAnswerClearsTheCountOfFailures()
        {
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);

            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 4; i++) breaker.RecordFailure();
                breaker.RecordSuccess();
            }
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Closed), "failures separated by an answer are not repeated failures");
        }

        [Test]
        public void WhileOpenTheServiceIsNotCalledAtAll_AndTheRegionIsToldAtOnce()
        {
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            SessionCircuitBreaker breaker = NewBreaker(clock);
            GridNonSpatialSessions region = NewRegion(new GuardedVoiceSessionService(flaky, breaker));

            for (int i = 0; i < 5; i++)
                Assert.That(region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA).Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Open));
            Assert.That(flaky.Calls, Is.EqualTo(5), "each failure waited on the service");

            for (int i = 0; i < 50; i++)
            {
                SessionOutcome refused = region.Accept(GroupId, Bob, RegionA);
                Assert.That(refused.Ok, Is.False);
                Assert.That(refused.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
                Assert.That(region.Get(GroupId), Is.Null);
            }
            Assert.That(flaky.Calls, Is.EqualTo(5), "fifty more requests, none of them reached the service");
        }

        [Test]
        public void AfterTheOpenPeriodOneRequestIsLetThrough_AndAnAnswerClosesTheBreaker()
        {
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            SessionCircuitBreaker breaker = NewBreaker(clock);
            List<BreakerState> seen = new List<BreakerState>();
            breaker.Changed += seen.Add;
            GridNonSpatialSessions region = NewRegion(new GuardedVoiceSessionService(flaky, breaker));
            for (int i = 0; i < 5; i++) region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Open));

            clock.Now += TimeSpan.FromSeconds(4);
            Assert.That(region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA).Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(flaky.Calls, Is.EqualTo(5), "still inside the open period");

            clock.Now += TimeSpan.FromSeconds(1);
            flaky.Up = true;
            SessionOutcome probe = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.That(probe.Ok, Is.True, probe.Decision);
            Assert.That(flaky.Calls, Is.EqualTo(6), "exactly one request was let through");
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Closed), "the service answered");
            Assert.That(region.Accept(GroupId, Bob, RegionA).Ok, Is.True, "and traffic flows again");
            Assert.That(seen, Is.EqualTo(new[] { BreakerState.Open, BreakerState.HalfOpen, BreakerState.Closed }));
        }

        [Test]
        public void OnlyOneProbeRunsWhileHalfOpen_AndAFailedProbeOpensItAgainForTheFullPeriod()
        {
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);
            for (int i = 0; i < 5; i++) breaker.RecordFailure();
            clock.Now += TimeSpan.FromSeconds(5);

            Assert.That(breaker.TryEnter(), Is.True, "the probe");
            Assert.That(breaker.State, Is.EqualTo(BreakerState.HalfOpen));
            Assert.That(breaker.TryEnter(), Is.False, "the others wait for the probe");

            breaker.RecordFailure();
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Open));
            clock.Now += TimeSpan.FromSeconds(4);
            Assert.That(breaker.TryEnter(), Is.False, "the period starts over from the failed probe");
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.That(breaker.TryEnter(), Is.True);
        }

        // ---- the retry queue for departs -------------------------------------------------------------

        [Test]
        public void DepartsThatFailWhileTheServiceIsDownAreRetriedAndArriveWhenItIsBack()
        {
            FakeClock clock = Clock();
            VoiceSessionService real = NewReal(clock);
            FlakyService flaky = new FlakyService(real);
            DepartOutbox outbox = NewOutbox(flaky, clock);
            GridNonSpatialSessions region = NewRegion(flaky, outbox, clock);
            UUID dave = UUID.Random(), erin = UUID.Random();
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            region.Accept(GroupId, Bob, RegionA, "viewer-bob");
            region.Accept(GroupId, Carol, RegionA, "viewer-carol");
            region.Accept(GroupId, dave, RegionA, "viewer-dave");
            region.Accept(GroupId, erin, RegionA, "viewer-erin");

            flaky.Up = false;
            Assert.That(region.Depart(GroupId, Bob, DepartureReason.VoiceTeardown).Decision, Is.EqualTo(SessionOutcome.StoreUnavailable), "the caller is told as before");
            region.LeaveChat(GroupId, Carol);
            region.DepartByViewerSession(dave, "viewer-dave", DepartureReason.VoiceTeardown);
            region.DepartAll(erin, DepartureReason.PresenceLost);
            Assert.That(outbox.Count, Is.EqualTo(4));
            Assert.That(Holds(real, GroupId, Bob), Is.True, "nothing has arrived yet");

            Assert.That(outbox.RunOnce(), Is.EqualTo(0), "still down: nothing delivered");
            Assert.That(outbox.Count, Is.EqualTo(4), "and nothing lost");

            flaky.Up = true;
            flaky.Delivered.Clear();
            Assert.That(outbox.RunOnce(), Is.EqualTo(4));
            Assert.That(outbox.Count, Is.EqualTo(0));
            Assert.That(flaky.Delivered, Is.EqualTo(new[] { "depart:" + Bob, "leavechat:" + Carol, "departbyviewer:" + dave, "departall:" + erin }),
                        "each delivered once, oldest first");
            Assert.That(Holds(real, GroupId, Bob), Is.False, "the depart arrived");
            Assert.That(Holds(real, GroupId, dave), Is.False);
            Assert.That(Holds(real, GroupId, erin), Is.False);
        }

        [Test]
        public void ARetryWhileTheServiceIsStillDownTriesOnceAndStops()
        {
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            DepartOutbox outbox = NewOutbox(flaky, clock);
            for (int i = 0; i < 20; i++) outbox.EnqueueDepart(GroupId, UUID.Random(), (int)DepartureReason.VoiceTeardown);

            outbox.RunOnce();

            Assert.That(flaky.Calls, Is.EqualTo(1), "one attempt to find out, not twenty");
            Assert.That(outbox.Count, Is.EqualTo(20));
        }

        [Test]
        public void TheQueueIsBounded_AndKeepsTheNewest()
        {
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock));
            DepartOutbox outbox = NewOutbox(flaky, clock, max: 3);
            UUID[] agents = Enumerable.Range(0, 5).Select(_ => UUID.Random()).ToArray();

            foreach (UUID a in agents) outbox.EnqueueDepart(GroupId, a, (int)DepartureReason.VoiceTeardown);

            Assert.That(outbox.Count, Is.EqualTo(3));
            Assert.That(outbox.Dropped, Is.EqualTo(2));
            outbox.RunOnce();
            Assert.That(flaky.Delivered, Is.EqualTo(agents.Skip(2).Select(a => "depart:" + a)), "the two oldest were the ones given up, in order");
        }

        [Test]
        public void ADepartOlderThanTheLimitIsDroppedNotDelivered()
        {
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock));
            DepartOutbox outbox = NewOutbox(flaky, clock);
            outbox.EnqueueDepart(GroupId, Bob, (int)DepartureReason.VoiceTeardown);
            clock.Now += TimeSpan.FromSeconds(61);
            outbox.EnqueueDepart(GroupId, Carol, (int)DepartureReason.VoiceTeardown);

            outbox.RunOnce();

            Assert.That(flaky.Delivered, Is.EqualTo(new[] { "depart:" + Carol }), "the seat lease covers the one that aged out");
            Assert.That(outbox.Dropped, Is.EqualTo(1));
            Assert.That(outbox.Count, Is.EqualTo(0));
        }

        [Test]
        public void JoiningAgainDropsTheQueuedDepartForThatSeat()
        {
            FakeClock clock = Clock();
            VoiceSessionService real = NewReal(clock);
            FlakyService flaky = new FlakyService(real);
            DepartOutbox outbox = NewOutbox(flaky, clock);
            GridNonSpatialSessions region = NewRegion(flaky, outbox, clock);
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            region.Accept(GroupId, Bob, RegionA, "viewer-bob");
            UUID other = UUID.Random();

            flaky.Up = false;
            region.Depart(GroupId, Bob, DepartureReason.VoiceTeardown);
            outbox.EnqueueDepart(GroupId, other, (int)DepartureReason.VoiceTeardown);
            Assert.That(outbox.Count, Is.EqualTo(2));

            flaky.Up = true;
            Assert.That(region.Accept(GroupId, Bob, RegionA, "viewer-bob").Ok, Is.True, "Bob comes back before the queue is worked");
            Assert.That(outbox.Count, Is.EqualTo(1), "his old depart is gone, the other agent's stays");

            outbox.RunOnce();
            Assert.That(Holds(real, GroupId, Bob), Is.True, "the late depart did not remove the seat he took again");
        }

        [Test]
        public void OnlyDepartsAreQueued()
        {
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            DepartOutbox outbox = NewOutbox(flaky, clock);
            GridNonSpatialSessions region = NewRegion(flaky, outbox, clock);

            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            region.Accept(GroupId, Bob, RegionA);
            region.MarkPresent(GroupId, Bob, RegionA);
            region.Decline(GroupId, Bob);
            region.Invite(GroupId, Alice, Bob);

            Assert.That(outbox.Count, Is.EqualTo(0), "a call that could not start or join is refused, not replayed later");
        }

        [Test]
        public void TheQueueEmptiesThroughTheBreakerOnceItClosesAgain()
        {
            FakeClock clock = Clock();
            VoiceSessionService real = NewReal(clock);
            FlakyService flaky = new FlakyService(real);
            SessionCircuitBreaker breaker = NewBreaker(clock);
            GuardedVoiceSessionService guarded = new GuardedVoiceSessionService(flaky, breaker);
            DepartOutbox outbox = NewOutbox(guarded, clock);
            GridNonSpatialSessions region = NewRegion(guarded, outbox, clock);
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            UUID[] agents = Enumerable.Range(0, 6).Select(_ => UUID.Random()).ToArray();
            foreach (UUID a in agents) region.Accept(GroupId, a, RegionA, "v-" + a);

            flaky.Up = false;
            foreach (UUID a in agents) region.Depart(GroupId, a, DepartureReason.VoiceTeardown);
            Assert.That(breaker.State, Is.EqualTo(BreakerState.Open), "five failures in a row");
            Assert.That(outbox.Count, Is.EqualTo(6), "all six kept, the sixth refused by the open breaker");

            int calls = flaky.Calls;
            Assert.That(outbox.RunOnce(), Is.EqualTo(0));
            Assert.That(flaky.Calls, Is.EqualTo(calls), "an open breaker keeps the retry off the service too");

            clock.Now += TimeSpan.FromSeconds(5);
            flaky.Up = true;
            Assert.That(outbox.RunOnce(), Is.EqualTo(6), "the first retry is the probe, and it answers");
            foreach (UUID a in agents) Assert.That(Holds(real, GroupId, a), Is.False);
        }

        // ---- the settings ----------------------------------------------------------------------------

        [Test]
        public void TheNewKeysHaveTheDesignsDefaults_AndUnusableValuesFallBackToThem()
        {
            IConfig voice = new IniConfigSource().AddConfig("WebRtcVoice");
            SessionStoreSettings d = SessionStoreSettings.Read(voice);
            Assert.That(new[] { d.OutboxMax, d.OutboxSeconds, d.BreakerFailures, d.BreakerWindowSeconds, d.BreakerOpenSeconds },
                        Is.EqualTo(new[] { 256, 60, 5, 10, 5 }));

            voice.Set("SessionStoreOutboxMax", "32");
            voice.Set("SessionStoreOutboxSeconds", "20");
            voice.Set("SessionStoreBreakerFailures", "3");
            voice.Set("SessionStoreBreakerWindowSeconds", "30");
            voice.Set("SessionStoreBreakerOpenSeconds", "15");
            SessionStoreSettings set = SessionStoreSettings.Read(voice);
            Assert.That(new[] { set.OutboxMax, set.OutboxSeconds, set.BreakerFailures, set.BreakerWindowSeconds, set.BreakerOpenSeconds },
                        Is.EqualTo(new[] { 32, 20, 3, 30, 15 }));

            foreach (string key in new[] { "SessionStoreOutboxMax", "SessionStoreOutboxSeconds", "SessionStoreBreakerFailures",
                                           "SessionStoreBreakerWindowSeconds", "SessionStoreBreakerOpenSeconds" })
                voice.Set(key, "0");
            SessionStoreSettings zero = SessionStoreSettings.Read(voice);
            Assert.That(new[] { zero.OutboxMax, zero.OutboxSeconds, zero.BreakerFailures, zero.BreakerWindowSeconds, zero.BreakerOpenSeconds },
                        Is.EqualTo(new[] { 256, 60, 5, 10, 5 }), "zero or less is not a usable value");
        }
    }
}
