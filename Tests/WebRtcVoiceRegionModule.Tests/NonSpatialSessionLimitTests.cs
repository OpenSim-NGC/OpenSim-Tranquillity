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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialSessionLimitTests
    {
        private static NonSpatialLimits Limits(int global, int perAgent)
            => new NonSpatialLimits { MaxNonSpatialSessions = global, MaxSessionsPerAgent = perAgent };

        private static UUID Agent(int n) => new UUID(new Guid(n, 0, 0, new byte[8]));

        [Test]
        public void TheDefaultsAreFinite()
        {
            Assert.That(NonSpatialLimits.DefaultMaxNonSpatialSessions, Is.EqualTo(5000));
            Assert.That(NonSpatialLimits.DefaultMaxSessionsPerAgent, Is.EqualTo(20));
            Assert.That(NonSpatialLimits.Default.MaxNonSpatialSessions, Is.EqualTo(5000));
            Assert.That(NonSpatialLimits.Default.MaxSessionsPerAgent, Is.EqualTo(20));
        }

        [Test]
        public void AStartBeyondTheProcessCapIsRefused_WithItsOwnDecision()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 3, perAgent: 20));
            for (int i = 1; i <= 3; i++)
                Assert.That(e.StartAdhoc(Agent(i), UUID.Random(), RegionA).Ok, Is.True, "start " + i);

            SessionOutcome over = e.StartAdhoc(Agent(4), UUID.Random(), RegionA);

            Assert.That(over.Ok, Is.False);
            Assert.That(over.Decision, Is.EqualTo(SessionOutcome.GlobalSessionCap));
            Assert.That(e.Store.All().Count, Is.EqualTo(3), "the refused start created nothing");
        }

        [Test]
        public void AStartBeyondTheAgentCapIsRefused_AndOtherAgentsAreNotAffected()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 2));
            Assert.That(e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);
            Assert.That(e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);

            SessionOutcome over = e.StartAdhoc(Alice, UUID.Random(), RegionA);

            Assert.That(over.Ok, Is.False);
            Assert.That(over.Decision, Is.EqualTo(SessionOutcome.AgentSessionCap));
            Assert.That(e.StartAdhoc(Bob, UUID.Random(), RegionA).Ok, Is.True, "the cap is per agent");
        }

        [Test]
        public void ARetriedStartAtTheCapStillFindsItsSession()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 1));
            UUID temp = UUID.Random();
            SessionOutcome first = e.StartAdhoc(Alice, temp, RegionA);

            SessionOutcome retry = e.StartAdhoc(Alice, temp, RegionA);

            Assert.That(retry.Ok, Is.True, retry.Decision);
            Assert.That(retry.Session.SessionId, Is.EqualTo(first.Session.SessionId));
        }

        [Test]
        public void OnlyTheLiveSessionsTheAgentStartedCount()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits(global: 100, perAgent: 1));
            UUID bobs = e.StartAdhoc(Bob, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(bobs, Bob, Alice);   // being invited into someone else's call is not starting one

            SessionOutcome mine = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.That(mine.Ok, Is.True, mine.Decision);

            // Once the session has ended and been collected the agent may start another.
            e.Depart(mine.Session.SessionId, Alice, DepartureReason.ChatLeave);
            e.Sweep();
            Assert.That(e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);
        }

        [Test]
        public void TheCapsCoverGroupSessionsToo()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 1));
            Assert.That(e.Start(NonSpatialSessionType.Group, Alice, UUID.Random(), UUID.Zero, RegionA).Ok, Is.True);

            SessionOutcome over = e.Start(NonSpatialSessionType.Group, Alice, UUID.Random(), UUID.Zero, RegionA);

            Assert.That(over.Ok, Is.False);
            Assert.That(over.Decision, Is.EqualTo(SessionOutcome.AgentSessionCap));
        }

        [Test]
        public void ConcurrentStartsNeverExceedTheCap()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 5));
            int ok = 0;
            System.Threading.Tasks.Parallel.For(0, 40, _ =>
            {
                if (e.StartAdhoc(Alice, UUID.Random(), RegionA).Ok) System.Threading.Interlocked.Increment(ref ok);
            });
            Assert.That(ok, Is.EqualTo(5));
            Assert.That(e.Store.All().Count, Is.EqualTo(5));
        }

        [Test]
        public void TheRefusalWarningIsLimitedToOncePerAgentPerMinute()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, limits: Limits(global: 100, perAgent: 1));
            List<(UUID Agent, string Decision, string Detail)> lines = new();
            e.LimitRefused += (agent, decision, detail) => lines.Add((agent, decision, detail));
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            e.StartAdhoc(Bob, UUID.Random(), RegionA);

            for (int i = 0; i < 5; i++) e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.That(lines, Has.Count.EqualTo(1), "five refusals, one warning");
            Assert.That(lines[0].Agent, Is.EqualTo(Alice));
            Assert.That(lines[0].Decision, Is.EqualTo(SessionOutcome.AgentSessionCap));

            e.StartAdhoc(Bob, UUID.Random(), RegionA);
            Assert.That(lines, Has.Count.EqualTo(2), "another agent has its own allowance");

            clock.Now = clock.Now.AddSeconds(59);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.That(lines, Has.Count.EqualTo(2), "still inside the minute");

            clock.Now = clock.Now.AddSeconds(2);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.That(lines, Has.Count.EqualTo(3), "a new minute, a new warning");
        }

        [Test]
        public void TheRefusalGateForgetsAgentsUnseenForTenMinutes()
        {
            RefusalLogGate gate = new RefusalLogGate();
            DateTime t0 = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            gate.ShouldLog(Alice, t0);
            gate.ShouldLog(Bob, t0.AddSeconds(630));

            int dropped = gate.Prune(t0.AddMinutes(11), NonSpatialVoiceSessionEngine.TransientStateMaxAge);

            Assert.That(dropped, Is.EqualTo(1), "Alice was last seen 11 minutes ago, Bob half a minute ago");
            Assert.That(gate.ShouldLog(Bob, t0.AddMinutes(11)), Is.False, "Bob is still remembered");
        }

        [Test]
        public void TheConferenceHandlerAnswersACapRefusalWithForbiddenAndItsDecisionWord()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(limits: Limits(global: 100, perAgent: 1));
            OSDMap Start(UUID temp) => new OSDMap
            {
                ["method"] = "start conference",
                ["session-id"] = temp,
                ["params"] = new OSDArray { OSD.FromUUID(Bob) },
            };
            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random()), Alice, e, RegionA, true, out _), Is.True);

            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random()), Alice, e, RegionA, true,
                out ChatSessionOutcome outcome), Is.True);

            Assert.That(outcome.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(outcome.Reply, Is.Null);
            Assert.That(outcome.Instrument, Does.Contain("decision=adhoc-refused-" + SessionOutcome.AgentSessionCap));
        }
    }
}
