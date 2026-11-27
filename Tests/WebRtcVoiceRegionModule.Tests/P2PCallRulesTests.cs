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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void TheCalleeLeavingBeforeAnswering_ChangesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);

            {
                var assertedValue = reg.Engine.DepartAll(Bob, DepartureReason.PresenceLost);
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the departure is accepted");
            }

            A2ASession call = reg.TryGet(CallId);
            {
                var assertedValue = call.State;
                TestAssert.WithMessage(() => Assert.Equal(A2ASessionState.Invited, assertedValue), "the invitation is still open");
            }
            {
                var assertedValue = reg.MarkProvisioned(CallId, Bob, "vs-bob").State;
                TestAssert.WithMessage(() => Assert.Equal(A2ASessionState.Active, assertedValue), "and Bob can still answer it");
            }
        }

        [Fact(Timeout = 60000)]
        public void ThatTheCalleeWasRung_IsKeptForTheLifeOfTheCall_EvenWhenTheCallerHangsUp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkInviteSent(CallId);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");

            reg.MarkGone(Alice, "vs-alice");

            Assert.True(reg.TryGet(CallId).InviteSent);
            ChatSessionOutcome again = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            {
                var assertedValue = again.Invite;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no second ring for the same invitation");
            }
            Assert.Contains("suppressed-already-sent", again.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void APartyWhoHungUp_CanBeGivenTheCredentialAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            string token = reg.IssueToken(CallId, Alice).Token;
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkGone(Alice, "vs-alice");

            Assert.Equal(token, reg.IssueToken(CallId, Alice).Token);
        }

        [Fact(Timeout = 60000)]
        public void AStartNamingTheSamePeopleAfterTheCallEnded_MakesANewCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry reg = New();
            reg.Record(Alice, Bob, out _);
            string first = reg.IssueToken(CallId, Alice).Token;
            reg.Decline(CallId, Bob, out _);
            Assert.Null(reg.TryGet(CallId));

            A2ASession again = reg.Record(Bob, Alice, out bool created);

            Assert.True(created);
            {
                var assertedValue = again.Caller;
                TestAssert.WithMessage(() => Assert.Equal(Bob, assertedValue), "the one who started it this time");
            }
            Assert.Null(again.Token);
            Assert.NotEqual(first, reg.IssueToken(CallId, Bob).Token);
        }

        [Fact(Timeout = 60000)]
        public void TheSweep_EndsALapsedInvitation_AndCountsWhatItReleased()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Record(Alice, Bob, out _);
            clock.Now += NonSpatialVoiceSessionEngine.DefaultInviteTtl + TimeSpan.FromSeconds(1);

            NonSpatialVoiceSessionEngine.SweepSummary summary = reg.Engine.SweepWithSummary();

            Assert.Equal(1, summary.InvitationsExpired);
            {
                var assertedValue = summary.SeatsReleased;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the caller's seat");
            }
            Assert.Equal(new[] { CallId }, summary.Removed);
            Assert.Empty(reg.Engine.Store.All());
        }

        [Fact(Timeout = 60000)]
        public void TheSweep_KeepsAnAnsweredCallIdleForTheWholeBackstop_WhileAPartyIsPresent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            {
                var assertedValue = reg.TryGet(CallId);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "someone in the call is still here, so it is not idle");
            }
            Assert.Equal(2, reg.Engine.Store.Get(CallId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void ALookUp_KeepsAnAnsweredCallIdleForTheWholeBackstop_WhileAPartyIsPresent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            HashSet<UUID> here = new() { Bob };
            reg.Engine.IsAgentPresent = here.Contains;
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            clock.Now += NonSpatialVoiceSessionEngine.DefaultIdleTtl + TimeSpan.FromHours(1);

            {
                var assertedValue = reg.TryGet(CallId);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "Bob is still here; Alice's viewer being gone does not end it");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheSweep_EndsAnAnsweredCallIdleForTheWholeBackstop_WhenBothPartiesHaveGone_AndReleasesTheirSeats()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Engine.IsAgentPresent = _ => false;
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            clock.Now += NonSpatialVoiceSessionEngine.DefaultIdleTtl + TimeSpan.FromMinutes(1);
            NonSpatialVoiceSessionEngine.SweepSummary summary = reg.Engine.SweepWithSummary();

            {
                var assertedValue = reg.TryGet(CallId);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "nobody is left, so the idle time applies");
            }
            Assert.Equal(2, summary.SeatsReleased);
            Assert.Equal(new[] { CallId }, summary.Removed);
        }

        [Fact(Timeout = 60000)]
        public void APresenceCheckThatThrows_KeepsTheCall_AsItDoesForAGroup()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            A2ARegistry reg = New(clock);
            reg.Engine.IsAgentPresent = _ => throw new InvalidOperationException("scene lookup failed");
            reg.Record(Alice, Bob, out _);
            reg.IssueToken(CallId, Alice);
            reg.MarkProvisioned(CallId, Alice, "vs-alice");
            reg.MarkProvisioned(CallId, Bob, "vs-bob");

            clock.Now += NonSpatialVoiceSessionEngine.DefaultIdleTtl + TimeSpan.FromMinutes(1);
            reg.Engine.Sweep();

            Assert.NotNull(reg.TryGet(CallId));
        }

        [Fact(Timeout = 60000)]
        public void ACallIsNotCountedAgainstTheCaps_AndDoesNotCountConferencesOut()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine engine = NewEngine(limits: new NonSpatialLimits { MaxNonSpatialSessions = 2, MaxSessionsPerAgent = 2 });
            A2ACalls calls = new A2ACalls(() => engine);

            for (int i = 0; i < 6; i++)
                Assert.NotNull(calls.Record(Alice, UUID.Random(), RegionA, out _));

            Assert.True(engine.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);
            Assert.True(engine.StartAdhoc(Alice, UUID.Random(), RegionA).Ok);
            {
                var assertedValue = engine.StartAdhoc(Alice, UUID.Random(), RegionA).Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.GlobalSessionCap, assertedValue), "the caps still bind conferences");
            }
        }

        [Fact(Timeout = 60000)]
        public void ACallIdIsNeverTakenForAGroupOrConference()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine engine = NewEngine();
            A2ACalls calls = new A2ACalls(() => engine);
            UUID group = engine.Start(NonSpatialSessionType.Group, Alice, NonSpatialIdentityTests.Group, NonSpatialIdentityTests.Group, RegionA).Session.SessionId;

            {
                var assertedValue = calls.TryGet(group, Alice);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a group session is not a call");
            }
            Assert.Null(calls.IssueToken(group, Alice));
            {
                var assertedValue = engine.Get(group).Token;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "and asking for a call's credential did not mint the group's");
            }
        }
    }
}
