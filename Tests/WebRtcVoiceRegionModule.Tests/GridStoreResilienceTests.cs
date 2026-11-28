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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void TheBreakerOpensAfterRepeatedFailures_AndNotBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);

            for (int i = 0; i < 4; i++)
            {
                Assert.True(breaker.TryEnter());
                breaker.RecordFailure();
                clock.Now += TimeSpan.FromSeconds(1);
            }
            {
                var assertedValue = breaker.State;
                TestAssert.WithMessage(() => Assert.Equal(BreakerState.Closed, assertedValue), "four failures are not enough");
            }
            Assert.True(breaker.TryEnter());

            breaker.RecordFailure();
            Assert.Equal(BreakerState.Open, breaker.State);
            Assert.False(breaker.TryEnter());
        }

        [Fact(Timeout = 60000)]
        public void FailuresSpreadOutOverTimeNeverOpenTheBreaker()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);

            for (int i = 0; i < 40; i++)
            {
                Assert.True(breaker.TryEnter(), "failure " + i);
                breaker.RecordFailure();
                clock.Now += TimeSpan.FromSeconds(3);   // five inside ten seconds is never reached
            }
            Assert.Equal(BreakerState.Closed, breaker.State);
        }

        [Fact(Timeout = 60000)]
        public void AnAnswerClearsTheCountOfFailures()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);

            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 4; i++) breaker.RecordFailure();
                breaker.RecordSuccess();
            }
            {
                var assertedValue = breaker.State;
                TestAssert.WithMessage(() => Assert.Equal(BreakerState.Closed, assertedValue), "failures separated by an answer are not repeated failures");
            }
        }

        [Fact(Timeout = 60000)]
        public void WhileOpenTheServiceIsNotCalledAtAll_AndTheRegionIsToldAtOnce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            SessionCircuitBreaker breaker = NewBreaker(clock);
            GridNonSpatialSessions region = NewRegion(new GuardedVoiceSessionService(flaky, breaker));

            for (int i = 0; i < 5; i++)
                Assert.Equal(SessionOutcome.StoreUnavailable, region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA).Decision);
            Assert.Equal(BreakerState.Open, breaker.State);
            {
                var assertedValue = flaky.Calls;
                TestAssert.WithMessage(() => Assert.Equal(5, assertedValue), "each failure waited on the service");
            }

            for (int i = 0; i < 50; i++)
            {
                SessionOutcome refused = region.Accept(GroupId, Bob, RegionA);
                Assert.False(refused.Ok);
                Assert.Equal(SessionOutcome.StoreUnavailable, refused.Decision);
                Assert.Null(region.Get(GroupId));
            }
            {
                var assertedValue = flaky.Calls;
                TestAssert.WithMessage(() => Assert.Equal(5, assertedValue), "fifty more requests, none of them reached the service");
            }
        }

        [Fact(Timeout = 60000)]
        public void AfterTheOpenPeriodOneRequestIsLetThrough_AndAnAnswerClosesTheBreaker()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            SessionCircuitBreaker breaker = NewBreaker(clock);
            List<BreakerState> seen = new List<BreakerState>();
            breaker.Changed += seen.Add;
            GridNonSpatialSessions region = NewRegion(new GuardedVoiceSessionService(flaky, breaker));
            for (int i = 0; i < 5; i++) region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Assert.Equal(BreakerState.Open, breaker.State);

            clock.Now += TimeSpan.FromSeconds(4);
            Assert.Equal(SessionOutcome.StoreUnavailable, region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA).Decision);
            {
                var assertedValue = flaky.Calls;
                TestAssert.WithMessage(() => Assert.Equal(5, assertedValue), "still inside the open period");
            }

            clock.Now += TimeSpan.FromSeconds(1);
            flaky.Up = true;
            SessionOutcome probe = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.True(probe.Ok, probe.Decision);
            {
                var assertedValue = flaky.Calls;
                TestAssert.WithMessage(() => Assert.Equal(6, assertedValue), "exactly one request was let through");
            }
            {
                var assertedValue = breaker.State;
                TestAssert.WithMessage(() => Assert.Equal(BreakerState.Closed, assertedValue), "the service answered");
            }
            Assert.True(region.Accept(GroupId, Bob, RegionA).Ok, "and traffic flows again");
            Assert.Equal(new[] { BreakerState.Open, BreakerState.HalfOpen, BreakerState.Closed }, seen);
        }

        [Fact(Timeout = 60000)]
        public void OnlyOneProbeRunsWhileHalfOpen_AndAFailedProbeOpensItAgainForTheFullPeriod()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            SessionCircuitBreaker breaker = NewBreaker(clock);
            for (int i = 0; i < 5; i++) breaker.RecordFailure();
            clock.Now += TimeSpan.FromSeconds(5);

            Assert.True(breaker.TryEnter(), "the probe");
            Assert.Equal(BreakerState.HalfOpen, breaker.State);
            Assert.False(breaker.TryEnter(), "the others wait for the probe");

            breaker.RecordFailure();
            Assert.Equal(BreakerState.Open, breaker.State);
            clock.Now += TimeSpan.FromSeconds(4);
            Assert.False(breaker.TryEnter(), "the period starts over from the failed probe");
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(breaker.TryEnter());
        }

        // ---- the retry queue for departs -------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void DepartsThatFailWhileTheServiceIsDownAreRetriedAndArriveWhenItIsBack()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            {
                var assertedValue = region.Depart(GroupId, Bob, DepartureReason.VoiceTeardown).Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.StoreUnavailable, assertedValue), "the caller is told as before");
            }
            region.LeaveChat(GroupId, Carol);
            region.DepartByViewerSession(dave, "viewer-dave", DepartureReason.VoiceTeardown);
            region.DepartAll(erin, DepartureReason.PresenceLost);
            Assert.Equal(4, outbox.Count);
            Assert.True(Holds(real, GroupId, Bob), "nothing has arrived yet");

            {
                var assertedValue = outbox.RunOnce();
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "still down: nothing delivered");
            }
            {
                var assertedValue = outbox.Count;
                TestAssert.WithMessage(() => Assert.Equal(4, assertedValue), "and nothing lost");
            }

            flaky.Up = true;
            flaky.Delivered.Clear();
            Assert.Equal(4, outbox.RunOnce());
            Assert.Equal(0, outbox.Count);
            {
                var assertedValue = flaky.Delivered;
                TestAssert.WithMessage(() => Assert.Equal(new[] { "depart:" + Bob, "leavechat:" + Carol, "departbyviewer:" + dave, "departall:" + erin }, assertedValue), "each delivered once, oldest first");
            }
            Assert.False(Holds(real, GroupId, Bob), "the depart arrived");
            Assert.False(Holds(real, GroupId, dave));
            Assert.False(Holds(real, GroupId, erin));
        }

        [Fact(Timeout = 60000)]
        public void ARetryWhileTheServiceIsStillDownTriesOnceAndStops()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock)) { Up = false };
            DepartOutbox outbox = NewOutbox(flaky, clock);
            for (int i = 0; i < 20; i++) outbox.EnqueueDepart(GroupId, UUID.Random(), (int)DepartureReason.VoiceTeardown);

            outbox.RunOnce();

            {
                var assertedValue = flaky.Calls;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one attempt to find out, not twenty");
            }
            Assert.Equal(20, outbox.Count);
        }

        [Fact(Timeout = 60000)]
        public void TheQueueIsBounded_AndKeepsTheNewest()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock));
            DepartOutbox outbox = NewOutbox(flaky, clock, max: 3);
            UUID[] agents = Enumerable.Range(0, 5).Select(_ => UUID.Random()).ToArray();

            foreach (UUID a in agents) outbox.EnqueueDepart(GroupId, a, (int)DepartureReason.VoiceTeardown);

            Assert.Equal(3, outbox.Count);
            Assert.Equal(2, outbox.Dropped);
            outbox.RunOnce();
            {
                var assertedValue = flaky.Delivered;
                TestAssert.WithMessage(() => Assert.Equal(agents.Skip(2).Select(a => "depart:" + a), assertedValue), "the two oldest were the ones given up, in order");
            }
        }

        [Fact(Timeout = 60000)]
        public void ADepartOlderThanTheLimitIsDroppedNotDelivered()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            FlakyService flaky = new FlakyService(NewReal(clock));
            DepartOutbox outbox = NewOutbox(flaky, clock);
            outbox.EnqueueDepart(GroupId, Bob, (int)DepartureReason.VoiceTeardown);
            clock.Now += TimeSpan.FromSeconds(61);
            outbox.EnqueueDepart(GroupId, Carol, (int)DepartureReason.VoiceTeardown);

            outbox.RunOnce();

            {
                var assertedValue = flaky.Delivered;
                TestAssert.WithMessage(() => Assert.Equal(new[] { "depart:" + Carol }, assertedValue), "the seat lease covers the one that aged out");
            }
            Assert.Equal(1, outbox.Dropped);
            Assert.Equal(0, outbox.Count);
        }

        [Fact(Timeout = 60000)]
        public void JoiningAgainDropsTheQueuedDepartForThatSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.Equal(2, outbox.Count);

            flaky.Up = true;
            Assert.True(region.Accept(GroupId, Bob, RegionA, "viewer-bob").Ok, "Bob comes back before the queue is worked");
            {
                var assertedValue = outbox.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "his old depart is gone, the other agent's stays");
            }

            outbox.RunOnce();
            Assert.True(Holds(real, GroupId, Bob), "the late depart did not remove the seat he took again");
        }

        [Fact(Timeout = 60000)]
        public void OnlyDepartsAreQueued()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            {
                var assertedValue = outbox.Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a call that could not start or join is refused, not replayed later");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheQueueEmptiesThroughTheBreakerOnceItClosesAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            {
                var assertedValue = breaker.State;
                TestAssert.WithMessage(() => Assert.Equal(BreakerState.Open, assertedValue), "five failures in a row");
            }
            {
                var assertedValue = outbox.Count;
                TestAssert.WithMessage(() => Assert.Equal(6, assertedValue), "all six kept, the sixth refused by the open breaker");
            }

            int calls = flaky.Calls;
            Assert.Equal(0, outbox.RunOnce());
            {
                var assertedValue = flaky.Calls;
                TestAssert.WithMessage(() => Assert.Equal(calls, assertedValue), "an open breaker keeps the retry off the service too");
            }

            clock.Now += TimeSpan.FromSeconds(5);
            flaky.Up = true;
            {
                var assertedValue = outbox.RunOnce();
                TestAssert.WithMessage(() => Assert.Equal(6, assertedValue), "the first retry is the probe, and it answers");
            }
            foreach (UUID a in agents) Assert.False(Holds(real, GroupId, a));
        }

        // ---- the settings ----------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheNewKeysHaveTheDesignsDefaults_AndUnusableValuesFallBackToThem()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig voice = new IniConfigSource().AddConfig("WebRtcVoice");
            SessionStoreSettings d = SessionStoreSettings.Read(voice);
            Assert.Equal(new[] { 256, 60, 5, 10, 5 }, new[] { d.OutboxMax, d.OutboxSeconds, d.BreakerFailures, d.BreakerWindowSeconds, d.BreakerOpenSeconds });

            voice.Set("SessionStoreOutboxMax", "32");
            voice.Set("SessionStoreOutboxSeconds", "20");
            voice.Set("SessionStoreBreakerFailures", "3");
            voice.Set("SessionStoreBreakerWindowSeconds", "30");
            voice.Set("SessionStoreBreakerOpenSeconds", "15");
            SessionStoreSettings set = SessionStoreSettings.Read(voice);
            Assert.Equal(new[] { 32, 20, 3, 30, 15 }, new[] { set.OutboxMax, set.OutboxSeconds, set.BreakerFailures, set.BreakerWindowSeconds, set.BreakerOpenSeconds });

            foreach (string key in new[] { "SessionStoreOutboxMax", "SessionStoreOutboxSeconds", "SessionStoreBreakerFailures",
                                           "SessionStoreBreakerWindowSeconds", "SessionStoreBreakerOpenSeconds" })
                voice.Set(key, "0");
            SessionStoreSettings zero = SessionStoreSettings.Read(voice);
            {
                var assertedValue = new[]
                {
                    zero.OutboxMax,
                    zero.OutboxSeconds,
                    zero.BreakerFailures,
                    zero.BreakerWindowSeconds,
                    zero.BreakerOpenSeconds
                };
                TestAssert.WithMessage(() => Assert.Equal(new[] { 256, 60, 5, 10, 5 }, assertedValue), "zero or less is not a usable value");
            }
        }
    }
}
