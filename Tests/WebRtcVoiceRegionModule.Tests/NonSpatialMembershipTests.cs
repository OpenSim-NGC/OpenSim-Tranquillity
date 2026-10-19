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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void Creator_IsSeatedAtStart_AndNeedsNoInvitation()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock);
            NonSpatialMember me = e.Store.Get(id).Find(Alice);
            Assert.That(me, Is.Not.Null);
            Assert.That(me.State, Is.EqualTo(MemberState.Accepted));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void AnInvitationHoldsNoSeat_SoAnInviteStormCannotReserveTheRoom()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock);
            for (int i = 0; i < 40; i++)
                e.Invite(id, Alice, UUID.Random());
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1), "only the creator holds a seat");
        }

        [Test]
        public void MarkPresent_PromotesAcceptedWithoutTakingASecondSeat()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            int before = e.Store.Get(id).SeatsHeld;

            SessionOutcome o = e.MarkPresent(id, Bob, RegionA, "vs-bob");

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(e.Store.Get(id).Find(Bob).State, Is.EqualTo(MemberState.Present));
            Assert.That(e.Store.Get(id).Find(Bob).ViewerSession, Is.EqualTo("vs-bob"));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(before));
        }

        // ---- lifecycle 1: chat leave (UDP IM_SESSION_LEAVE) ------------------------------------

        [Test]
        public void ChatLeave_ReleasesTheSeat_WithNoCapPostAndNoVoiceTeardown()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(2));

            SessionOutcome o = e.Depart(id, Bob, DepartureReason.ChatLeave);

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(o.Decision, Is.EqualTo("departed"));
            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.That(m.State, Is.EqualTo(MemberState.Departed));
            Assert.That(m.Departure, Is.EqualTo(DepartureReason.ChatLeave));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));
        }

        // ---- lifecycle 2: invitation decline ---------------------------------------------------

        [Test]
        public void InvitationDecline_IsTerminalForTheInvitation_AndNeverTouchedASeat()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            int seats = e.Store.Get(id).SeatsHeld;

            SessionOutcome o = e.Decline(id, Bob);

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(o.Decision, Is.EqualTo("declined"));
            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.That(m.State, Is.EqualTo(MemberState.Declined));
            Assert.That(m.Departure, Is.EqualTo(DepartureReason.InvitationDeclined));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(seats), "an invitation held no seat to release");
        }

        [Test]
        public void AFreshInviteReopensADeclinedInvitation()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Decline(id, Bob);

            Assert.That(e.Invite(id, Alice, Bob).Ok, Is.True);
            Assert.That(e.Store.Get(id).Find(Bob).State, Is.EqualTo(MemberState.Invited));
            Assert.That(e.Accept(id, Bob, RegionA).Ok, Is.True);
        }

        // ---- lifecycle 3: voice teardown -------------------------------------------------------

        [Test]
        public void VoiceTeardown_ReleasesTheSeat_AndClearsTheViewerSession()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            e.MarkPresent(id, Bob, RegionA, "vs-bob");

            SessionOutcome o = e.Depart(id, Bob, DepartureReason.VoiceTeardown);

            Assert.That(o.Ok, Is.True, o.Decision);
            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.That(m.Departure, Is.EqualTo(DepartureReason.VoiceTeardown));
            Assert.That(m.ViewerSession, Is.Null);
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void TheThreeLifecyclesAreIndependent_EachAloneIsEnoughToRelease()
        {
            foreach (DepartureReason r in new[] { DepartureReason.ChatLeave, DepartureReason.InvitationDeclined, DepartureReason.VoiceTeardown })
            {
                FakeClock clock = new FakeClock();
                (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
                Assert.That(e.Depart(id, Bob, r).Ok, Is.True, "reason " + r);
                Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1), "reason " + r + " did not release the seat");
            }
        }

        // ---- backstops: departure never waits on a POST that does not come ---------------------

        [Test]
        public void PresenceLost_RemovesTheAgentFromEverySessionAtOnce()
        {
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

            Assert.That(touched.Count, Is.EqualTo(2));
            foreach (UUID id in new[] { one, two })
            {
                Assert.That(e.Store.Get(id).Find(Bob).Departure, Is.EqualTo(DepartureReason.PresenceLost));
                Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));
            }
        }

        [Test]
        public void AnUnansweredInvitationExpires_WithNoPostFromAnyone()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock, inviteTtl: TimeSpan.FromMinutes(2));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            clock.Now = clock.Now.AddMinutes(3);
            e.Sweep();

            NonSpatialMember m = e.Store.Get(id).Find(Bob);
            Assert.That(m.State, Is.EqualTo(MemberState.Departed));
            Assert.That(m.Departure, Is.EqualTo(DepartureReason.Expired));
        }

        [Test]
        public void ASessionWithNoSeatsNoInvitationsAndNobodyInTheChatIsCollected()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            e.Depart(id, Alice, DepartureReason.ChatLeave);
            e.Depart(id, Bob, DepartureReason.ChatLeave);

            var removed = e.Sweep();

            Assert.That(removed, Does.Contain(id));
            Assert.That(e.Store.Get(id), Is.Null);
        }

        /// <summary>
        /// The chat axis changes what "dead" means, and this is the case that shows it. If only voice
        /// counted, everyone hanging up voice would make the voice session collectable -- which would
        /// sweep a conference out from under members who were still typing in it.
        /// </summary>
        [Test]
        public void ASessionWhoseMembersHungUpVoiceButAreStillTypingIsNOTCollected()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            e.Depart(id, Alice, DepartureReason.VoiceTeardown);
            e.Depart(id, Bob, DepartureReason.VoiceTeardown);

            NonSpatialVoiceSession s = e.Store.Get(id);
            Assert.That(s.SeatsHeld, Is.EqualTo(0), "no voice seats left");
            Assert.That(s.ChatMembers(), Is.Not.Empty, "but the conversation is still open");
            Assert.That(s.IsDead, Is.False);
            Assert.That(e.Sweep(), Does.Not.Contain(id));
            Assert.That(e.Store.Get(id), Is.Not.Null);
        }

        [Test]
        public void ALiveSessionSurvivesTheSweep()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock, Bob);
            clock.Now = clock.Now.AddMinutes(30);
            Assert.That(e.Sweep(), Does.Not.Contain(id));
            Assert.That(e.Store.Get(id), Is.Not.Null);
        }

        [Test]
        public void AnIdleSessionIsCollectedByTheBackstop()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);

            clock.Now = clock.Now.AddHours(9);
            Assert.That(e.Sweep(), Does.Contain(id));
        }

        [Test]
        public void DepartingAnUnknownAgentOrSessionIsRefused_NotThrown()
        {
            FakeClock clock = new FakeClock();
            (NonSpatialVoiceSessionEngine e, UUID id) = SeatedAdhoc(clock);
            Assert.That(e.Depart(id, Carol, DepartureReason.ChatLeave).Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
            Assert.That(e.Depart(UUID.Random(), Alice, DepartureReason.ChatLeave).Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
        }
    }
}
