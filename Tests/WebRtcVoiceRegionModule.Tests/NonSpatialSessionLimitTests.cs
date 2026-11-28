/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Ceilings on the number of non-spatial sessions: one for the whole process and one per agent that
 * started them. A start beyond either is refused with its own decision, and the warning that goes
 * with it is limited to once per agent per minute.
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
    public class NonSpatialSessionLimitTests
    {
        private static NonSpatialLimits Limits(int global, int perAgent)
            => new NonSpatialLimits { MaxNonSpatialSessions = global, MaxSessionsPerAgent = perAgent };

        private static UUID Agent(int n) => new UUID(new Guid(n, 0, 0, new byte[8]));

        [Fact(Timeout = 60000)]
        public void TheDefaultsAreFinite()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(5000, NonSpatialLimits.DefaultMaxNonSpatialSessions);
            Assert.Equal(20, NonSpatialLimits.DefaultMaxSessionsPerAgent);
            Assert.Equal(5000, NonSpatialLimits.Default.MaxNonSpatialSessions);
            Assert.Equal(20, NonSpatialLimits.Default.MaxSessionsPerAgent);
        }

        [Fact(Timeout = 60000)]
        public void AStartBeyondTheProcessCapIsRefused_WithItsOwnDecision()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 3, perAgent: 20));
            for (int i = 1; i <= 3; i++)
                Assert.True(e.StartAdhoc(Agent(i), UUID.Random(), RegionA).Ok, "start " + i);

            SessionOutcome over = e.StartAdhoc(Agent(4), UUID.Random(), RegionA);

            Assert.False(over.Ok);
            Assert.Equal(SessionOutcome.GlobalSessionCap, over.Decision);
            {
                var assertedValue = e.Store.All().Count;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "the refused start created nothing");
            }
        }

        [Fact(Timeout = 60000)]
        public void AStartBeyondTheAgentCapIsRefused_AndOtherAgentsAreNotAffected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 2));
            Assert.True(e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);
            Assert.True(e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);

            SessionOutcome over = e.StartAdhoc(Alice, UUID.Random(), RegionA);

            Assert.False(over.Ok);
            Assert.Equal(SessionOutcome.AgentSessionCap, over.Decision);
            Assert.True(e.StartAdhoc(Bob, UUID.Random(), RegionA).Ok, "the cap is per agent");
        }

        [Fact(Timeout = 60000)]
        public void ARetriedStartAtTheCapStillFindsItsSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 1));
            UUID temp = UUID.Random();
            SessionOutcome first = e.StartAdhoc(Alice, temp, RegionA);

            SessionOutcome retry = e.StartAdhoc(Alice, temp, RegionA);

            Assert.True(retry.Ok, retry.Decision);
            Assert.Equal(first.Session.SessionId, retry.Session.SessionId);
        }

        [Fact(Timeout = 60000)]
        public void OnlyTheLiveSessionsTheAgentStartedCount()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits(global: 100, perAgent: 1));
            UUID bobs = e.StartAdhoc(Bob, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(bobs, Bob, Alice);   // being invited into someone else's call is not starting one

            SessionOutcome mine = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.True(mine.Ok, mine.Decision);

            // Once the session has ended and been collected the agent may start another.
            e.Depart(mine.Session.SessionId, Alice, DepartureReason.ChatLeave);
            e.Sweep();
            Assert.True(e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);
        }

        [Fact(Timeout = 60000)]
        public void TheCapsCoverGroupSessionsToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 1));
            Assert.True(e.Start(NonSpatialSessionType.Group, Alice, UUID.Random(), UUID.Zero, RegionA).Ok);

            SessionOutcome over = e.Start(NonSpatialSessionType.Group, Alice, UUID.Random(), UUID.Zero, RegionA);

            Assert.False(over.Ok);
            Assert.Equal(SessionOutcome.AgentSessionCap, over.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ConcurrentStartsNeverExceedTheCap()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 5));
            int ok = 0;
            System.Threading.Tasks.Parallel.For(0, 40, _ =>
            {
                if (e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok) System.Threading.Interlocked.Increment(ref ok);
            });
            Assert.Equal(5, ok);
            Assert.Equal(5, e.Store.All().Count);
        }

        [Fact(Timeout = 60000)]
        public void TheRefusalWarningIsLimitedToOncePerAgentPerMinute()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits(global: 100, perAgent: 1));
            List<(UUID Agent, string Decision, string Detail)> lines = new();
            e.LimitRefused += (agent, decision, detail) => lines.Add((agent, decision, detail));
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            e.StartAdhoc(Bob, UUID.Random(), RegionA);

            for (int i = 0; i < 5; i++) e.StartAdhoc(Alice, UUID.Random(), RegionA);
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "five refusals, one warning");
            }
            Assert.Equal(Alice, lines[0].Agent);
            Assert.Equal(SessionOutcome.AgentSessionCap, lines[0].Decision);

            e.StartAdhoc(Bob, UUID.Random(), RegionA);
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "another agent has its own allowance");
            }

            clock.Now = clock.Now.AddSeconds(59);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "still inside the minute");
            }

            clock.Now = clock.Now.AddSeconds(2);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue.Count()), "a new minute, a new warning");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheRefusalGateForgetsAgentsUnseenForTenMinutes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalLogGate gate = new RefusalLogGate();
            DateTime t0 = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            gate.ShouldLog(Alice, t0);
            gate.ShouldLog(Bob, t0.AddSeconds(630));

            int dropped = gate.Prune(t0.AddMinutes(11), NonSpatialVoiceSessionEngine.TransientStateMaxAge);

            {
                var assertedValue = dropped;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "Alice was last seen 11 minutes ago, Bob half a minute ago");
            }
            Assert.False(gate.ShouldLog(Bob, t0.AddMinutes(11)), "Bob is still remembered");
        }

        [Fact(Timeout = 60000)]
        public void TheConferenceHandlerAnswersACapRefusalWithForbiddenAndItsDecisionWord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 1));
            OSDMap Start(UUID temp) => new OSDMap
            {
                ["method"] = "start conference",
                ["session-id"] = temp,
                ["params"] = new OSDArray { OSD.FromUUID(Bob) },
            };
            Assert.True(AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random()), Alice, e, RegionA, true, out _));

            Assert.True(AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random()), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome));

            Assert.Equal(HttpStatusCode.Forbidden, outcome.Status);
            Assert.Null(outcome.Reply);
            Assert.Contains("decision=adhoc-refused-" + SessionOutcome.AgentSessionCap, outcome.Instrument);
        }
    }
}
