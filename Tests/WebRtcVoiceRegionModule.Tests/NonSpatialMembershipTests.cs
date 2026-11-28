/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Membership, and departure taken from EACH of the three lifecycles (chat leave, invitation decline,
 * voice teardown), plus the two backstops that make "departure never waits on a POST that does not come" true.
 *
 * One test per lifecycle:
 *   ChatLeave           UDP IM_SESSION_LEAVE          llimview.cpp:2160-2179
 *   InvitationDeclined  decline invitation / p2p      llimview.cpp:3437, :3422
 *   VoiceTeardown       {logout, viewer_session}      A2AProvisionAdmission.cs
 * and the backstops: PresenceLost (root presence closed) and Expired (TTL sweep).
 */
using System;
using System.Linq;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialMembershipTests
    {
        private static (NonSpatialVoiceSessionEngine, UUID) SeatedAdhoc(FakeClock clock, params UUID[] others)
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            SessionOutcome s = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            foreach (UUID o in others)
            {
                e.Invite(s.Session.SessionId, Alice, o);
                e.Accept(s.Session.SessionId, o, RegionA);
            }
            return (e, s.Session.SessionId);
        }

        // ---- states ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Creator_IsSeatedAtStart_AndNeedsNoInvitation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock);
            NonSpatialMember me = e.Store.Get(id).Find(Alice);
            Assert.NotNull(me);
            Assert.Equal(MemberState.Accepted, me.State);
            Assert.Equal(1, e.Store.Get(id).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void AnInvitationHoldsNoSeat_SoAnInviteStormCannotReserveTheRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock);
            for (int i = 0; i < 40; i++)
                e.Invite(id, Alice, UUID.Random());
            {
                var assertedValue = e.Store.Get(id).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "only the creator holds a seat");
            }
        }

        [Fact(Timeout = 60000)]
        public void MarkPresent_PromotesAcceptedWithoutTakingASecondSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            int before = e.Store.Get(id).SeatsHeld;

            SessionOutcome o = e.MarkPresent(id, Bob, RegionA, "vs-bob");

            Assert.True(o.Ok, o.Decision);
            Assert.Equal(MemberState.Present, e.Store.Get(id).Find(Bob).State);
            Assert.Equal("vs-bob", e.Store.Get(id).Find(Bob).ViewerSession);
            Assert.Equal(before, e.Store.Get(id).SeatsHeld);
        }

        // ---- lifecycle 1: chat leave (UDP IM_SESSION_LEAVE) ------------------------------------

        [Fact(Timeout = 60000)]
        public void ChatLeave_ReleasesTheSeat_WithNoCapPostAndNoVoiceTeardown()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            Assert.Equal(2, e.Store.Get(id).SeatsHeld);

            SessionOutcome o = e.Depart(id, Bob, DepartureReason.ChatLeave);

            Assert.True(o.Ok, o.Decision);
            Assert.Equal("departed", o.Decision);
            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.Equal(MemberState.Departed, m.State);
            Assert.Equal(DepartureReason.ChatLeave, m.Departure);
            Assert.Equal(1, e.Store.Get(id).SeatsHeld);
        }

        // ---- lifecycle 2: invitation decline ---------------------------------------------------

        [Fact(Timeout = 60000)]
        public void InvitationDecline_IsTerminalForTheInvitation_AndNeverTouchedASeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            int seats = e.Store.Get(id).SeatsHeld;

            SessionOutcome o = e.Decline(id, Bob);

            Assert.True(o.Ok, o.Decision);
            Assert.Equal("declined", o.Decision);
            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.Equal(MemberState.Declined, m.State);
            Assert.Equal(DepartureReason.InvitationDeclined, m.Departure);
            {
                var assertedValue = e.Store.Get(id).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(seats, assertedValue), "an invitation held no seat to release");
            }
        }

        [Fact(Timeout = 60000)]
        public void AFreshInviteReopensADeclinedInvitation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Decline(id, Bob);

            Assert.True(e.Invite(id, Alice, Bob).Ok);
            Assert.Equal(MemberState.Invited, e.Store.Get(id).Find(Bob).State);
            Assert.True(e.Accept(id, Bob, RegionA).Ok);
        }

        // ---- lifecycle 3: voice teardown -------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void VoiceTeardown_ReleasesTheSeat_AndClearsTheViewerSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            e.MarkPresent(id, Bob, RegionA, "vs-bob");

            SessionOutcome o = e.Depart(id, Bob, DepartureReason.VoiceTeardown);

            Assert.True(o.Ok, o.Decision);
            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.Equal(DepartureReason.VoiceTeardown, m.Departure);
            Assert.Null(m.ViewerSession);
            Assert.Equal(1, e.Store.Get(id).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void TheThreeLifecyclesAreIndependent_EachAloneIsEnoughToRelease()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            foreach (DepartureReason r in new[] { DepartureReason.ChatLeave, DepartureReason.InvitationDeclined, DepartureReason.VoiceTeardown })
            {
                FakeClock clock = new FakeClock();
                (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
                Assert.True(e.Depart(id, Bob, r).Ok, "reason " + r);
                {
                    var assertedValue = e.Store.Get(id).SeatsHeld;
                    TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "reason " + r + " did not release the seat");
                }
            }
        }

        // ---- backstops: departure never waits on a POST that does not come ---------------------

        [Fact(Timeout = 60000)]
        public void PresenceLost_RemovesTheAgentFromEverySessionAtOnce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID one = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            UUID two = e.StartAdhoc(Carol, UUID.Random(), RegionA).Session.SessionId;
            foreach (UUID id in new[] { one, two })
            {
                e.Invite(id, e.Store.Get(id).Creator, Bob);
                e.Accept(id, Bob, RegionA);
            }

            var touched = e.DepartAll(Bob, DepartureReason.PresenceLost);

            Assert.Equal(2, touched.Count);
            foreach (UUID id in new[] { one, two })
            {
                Assert.Equal(DepartureReason.PresenceLost, e.Store.Get(id).Find(Bob).Departure);
                Assert.Equal(1, e.Store.Get(id).SeatsHeld);
            }
        }

        [Fact(Timeout = 60000)]
        public void AnUnansweredInvitationExpires_WithNoPostFromAnyone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock, inviteTtl: TimeSpan.FromMinutes(2));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            clock.Now = clock.Now.AddMinutes(3);
            e.Sweep();

            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.Equal(MemberState.Departed, m.State);
            Assert.Equal(DepartureReason.Expired, m.Departure);
        }

        [Fact(Timeout = 60000)]
        public void ASessionWithNoSeatsNoInvitationsAndNobodyInTheChatIsCollected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            e.Depart(id, Alice, DepartureReason.ChatLeave);
            e.Depart(id, Bob, DepartureReason.ChatLeave);

            var removed = e.Sweep();

            Assert.Contains(id, removed);
            Assert.Null(e.Store.Get(id));
        }

        /// <summary>
        /// The chat axis changes what "dead" means, and this is the case that shows it. If only voice
        /// counted, everyone hanging up voice would make the voice session collectable -- which would
        /// sweep a conference out from under members who were still typing in it.
        /// </summary>
        [Fact(Timeout = 60000)]
        public void ASessionWhoseMembersHungUpVoiceButAreStillTypingIsNOTCollected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            e.Depart(id, Alice, DepartureReason.VoiceTeardown);
            e.Depart(id, Bob, DepartureReason.VoiceTeardown);

            NonSpatialVoiceSession s = e.Store.Get(id);
            {
                var assertedValue = s.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no voice seats left");
            }
            {
                var assertedValue = s.ChatMembers();
                TestAssert.WithMessage(() => Assert.NotEmpty(assertedValue), "but the conversation is still open");
            }
            Assert.False(s.IsDead);
            Assert.DoesNotContain(id, e.Sweep());
            Assert.NotNull(e.Store.Get(id));
        }

        [Fact(Timeout = 60000)]
        public void ALiveSessionSurvivesTheSweep()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            clock.Now = clock.Now.AddMinutes(30);
            Assert.DoesNotContain(id, e.Sweep());
            Assert.NotNull(e.Store.Get(id));
        }

        [Fact(Timeout = 60000)]
        public void AnIdleSessionIsCollectedByTheBackstop()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);

            clock.Now = clock.Now.AddHours(9);
            Assert.Contains(id, e.Sweep());
        }

        [Fact(Timeout = 60000)]
        public void DepartingAnUnknownAgentOrSessionIsRefused_NotThrown()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock);
            Assert.Equal(SessionOutcome.NoSuchSession, e.Depart(id, Carol, DepartureReason.ChatLeave).Decision);
            Assert.Equal(SessionOutcome.NoSuchSession, e.Depart(UUID.Random(), Alice, DepartureReason.ChatLeave).Decision);
        }
    }
}
