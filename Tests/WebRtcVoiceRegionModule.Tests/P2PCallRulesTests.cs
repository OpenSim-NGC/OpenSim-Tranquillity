/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The rules the engine applies to a call between two people, where they differ from a conference or a group:
 * the invitation's and the call's lifetimes, what ends a call, and what a call is not counted against.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class P2PCallRulesTests
    {
        private static readonly UUID CallId = A2ACalls.ComputeSessionId(Alice, Bob);

        private static OSDMap CallBody() => new OSDMap
        {
            ["method"] = OSD.FromString("call"),
            ["session-id"] = OSD.FromUUID(CallId),
        };

        private static A2ARegistry New(FakeClock clock = null)
            => clock == null ? new A2ARegistry() : new A2ARegistry(NonSpatialVoiceSessionEngine.DefaultInviteTtl, clock.Get);

        [Test]
        public void TheCalleeLeavingBeforeAnswering_ChangesNothing()
        {
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);

            Assert.That(reg.Engine.DepartAll(Bob, DepartureReason.PresenceLost), Has.Count.EqualTo(1), "the departure is accepted");

            A2ASession call = reg.TryGet(CallId);
            Assert.That(call.State, Is.EqualTo(A2ASessionState.Invited), "the invitation is still open");
            Assert.That(reg.MarkProvisioned(CallId, Bob, "vs-bob").State, Is.EqualTo(A2ASessionState.Active), "and Bob can still answer it");
        }

        [Test]
        public void ThatTheCalleeWasRung_IsKeptForTheLifeOfTheCall_EvenWhenTheCallerHangsUp()
        {
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkInviteSent(CallId);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");

            reg.MarkGone(Alice, "vs-alice");

            Assert.That(reg.TryGet(CallId).InviteSent, Is.True);
            ChatSessionOutcome again = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(again.Invite, Is.Null, "no second ring for the same invitation");
            Assert.That(again.Instrument, Does.Contain("suppressed-already-sent"));
        }

        [Test]
        public void APartyWhoHungUp_CanBeGivenTheCredentialAgain()
        {
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            string token = reg.IssueToken(CallId, Alice).Token;
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkGone(Alice, "vs-alice");

            Assert.That(reg.IssueToken(CallId, Alice).Token, Is.EqualTo(token));
        }

        [Test]
        public void AStartNamingTheSamePeopleAfterTheCallEnded_MakesANewCall()
        {
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            string first = reg.IssueToken(CallId, Alice).Token;
            reg.Decline(CallId, Bob, out _);
            Assert.That(reg.TryGet(CallId), Is.Null);

            A2ASession again = reg.Record(Bob, Alice, out bool created);

            Assert.That(created, Is.True);
            Assert.That(again.Caller, Is.EqualTo(Bob), "the one who started it this time");
            Assert.That(again.Token, Is.Null);
            Assert.That(reg.IssueToken(CallId, Bob).Token, Is.Not.EqualTo(first));
        }

        [Test]
        public void TheSweep_EndsALapsedInvitation_AndCountsWhatItReleased()
        {
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Record(Alice, Bob, out _);
            clock.Now += NonSpatialVoiceSessionEngine.DefaultInviteTtl + TimeSpan.FromSeconds(1);

            NonSpatialVoiceSessionEngine.SweepSummary summary = reg.Engine.SweepWithSummary();

            Assert.That(summary.InvitationsExpired, Is.EqualTo(1));
            Assert.That(summary.SeatsReleased, Is.EqualTo(1), "the caller's seat");
            Assert.That(summary.Removed, Is.EqualTo(new[] { CallId }));
            Assert.That(reg.Engine.Store.All(), Is.Empty);
        }

        [Test]
        public void TheSweep_KeepsAnAnsweredCallIdleForTheWholeBackstop_WhileAPartyIsPresent()
        {
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Engine.IsAgentPresent = _ => true;
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            for (int hour = 1; hour <= 30; hour++)
            {
                clock.Now += TimeSpan.FromHours(1);
                reg.Engine.Sweep();
            }

            Assert.That(reg.TryGet(CallId), Is.Not.Null, "someone in the call is still here, so it is not idle");
            Assert.That(reg.Engine.Store.Get(CallId).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void ALookUp_KeepsAnAnsweredCallIdleForTheWholeBackstop_WhileAPartyIsPresent()
        {
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            HashSet<UUID> here = new() { Bob };
            reg.Engine.IsAgentPresent = here.Contains;
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            clock.Now += NonSpatialVoiceSessionEngine.DefaultIdleTtl + TimeSpan.FromHours(1);

            Assert.That(reg.TryGet(CallId), Is.Not.Null, "Bob is still here; Alice's viewer being gone does not end it");
        }

        [Test]
        public void TheSweep_EndsAnAnsweredCallIdleForTheWholeBackstop_WhenBothPartiesHaveGone_AndReleasesTheirSeats()
        {
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Engine.IsAgentPresent = _ => false;
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            clock.Now += NonSpatialVoiceSessionEngine.DefaultIdleTtl + TimeSpan.FromMinutes(1);
            NonSpatialVoiceSessionEngine.SweepSummary summary = reg.Engine.SweepWithSummary();

            Assert.That(reg.TryGet(CallId), Is.Null, "nobody is left, so the idle time applies");
            Assert.That(summary.SeatsReleased, Is.EqualTo(2));
            Assert.That(summary.Removed, Is.EqualTo(new[] { CallId }));
        }

        [Test]
        public void APresenceCheckThatThrows_KeepsTheCall_AsItDoesForAGroup()
        {
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Engine.IsAgentPresent = _ => throw new InvalidOperationException("scene lookup failed");
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            clock.Now += NonSpatialVoiceSessionEngine.DefaultIdleTtl + TimeSpan.FromMinutes(1);
            reg.Engine.Sweep();

            Assert.That(reg.TryGet(CallId), Is.Not.Null);
        }

        [Test]
        public void ACallIsNotCountedAgainstTheCaps_AndDoesNotCountConferencesOut()
        {
            NonSpatialVoiceSessionEngine engine = NewEngine(limits: new NonSpatialLimits { MaxNonSpatialSessions = 2, MaxSessionsPerAgent = 2 });
            A2ACalls calls = new A2ACalls(() => engine);

            for (int i = 0; i < 6; i++)
                Assert.That(calls.Record(Alice, UUID.Random(), RegionA, out _), Is.Not.Null);

            Assert.That(engine.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);
            Assert.That(engine.StartAdhoc(Alice, UUID.Random(), RegionA).Ok, Is.True);
            Assert.That(engine.StartAdhoc(Alice, UUID.Random(), RegionA).Decision, Is.EqualTo(SessionOutcome.GlobalSessionCap),
                "the caps still bind conferences");
        }

        [Test]
        public void ACallIdIsNeverTakenForAGroupOrConference()
        {
            NonSpatialVoiceSessionEngine engine = NewEngine();
            A2ACalls calls = new A2ACalls(() => engine);
            UUID group = engine.Start(NonSpatialSessionType.Group, Alice, NonSpatialIdentityTests.Group, NonSpatialIdentityTests.Group, RegionA).Session.SessionId;

            Assert.That(calls.TryGet(group, Alice), Is.Null, "a group session is not a call");
            Assert.That(calls.IssueToken(group, Alice), Is.Null);
            Assert.That(engine.Get(group).Token, Is.Null, "and asking for a call's credential did not mint the group's");
        }
    }
}
