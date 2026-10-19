/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The 50-participant cap, RECONCILED with the mixer's SLV_MAX_MIX rather than added
 * beside it. The invariant these tests protect is that the engine can never admit a seat the mixer
 * will then refuse, and that a capacity refusal produces the same observable either way.
 */
using System;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialCapTests
    {
        [Test]
        public void TheMirroredMixerCap_MatchesTheMixersOwnErrorCodeConstant()
        {
            // WebRtcJanusService.cs is the codebase's existing mirror of janus_slvoice.c:112.
            Assert.That(NonSpatialCaps.MixerRoomFullErrorCode, Is.EqualTo(WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE));
        }

        [Test]
        public void EveryCapTheEngineCanHandOut_IsOneTheMixerWillHonour()
        {
            Assert.That(NonSpatialCaps.CapsAgree(), Is.True);
            Assert.That(NonSpatialCaps.DefaultConferenceCap, Is.EqualTo(50));
            Assert.That(NonSpatialCaps.DefaultConferenceCap, Is.LessThanOrEqualTo(NonSpatialCaps.MixerRoomCap));
        }

        [Test]
        public void ARequestAboveTheMixerCap_IsClampedToIt_NotGranted()
        {
            Assert.That(NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 500), Is.EqualTo(NonSpatialCaps.MixerRoomCap));
        }

        [Test]
        public void ARequestBelowTheDefault_IsHonoured_AndNeverDropsBelowTwo()
        {
            Assert.That(NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 8), Is.EqualTo(8));
            Assert.That(NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 1), Is.EqualTo(2));
            Assert.That(NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 0), Is.EqualTo(NonSpatialCaps.DefaultConferenceCap));
        }

        [Test]
        public void P2P_IsAlwaysTwo_WhateverIsAskedFor()
        {
            Assert.That(NonSpatialCaps.Effective(NonSpatialSessionType.P2P, 50), Is.EqualTo(2));
            Assert.That(NonSpatialCaps.Effective(NonSpatialSessionType.P2P, 0), Is.EqualTo(2));
        }

        [Test]
        public void AConferenceAdmitsExactlyFiftySeats_AndRefusesTheFiftyFirst()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;   // creator = seat 1

            for (int i = 0; i < NonSpatialCaps.DefaultConferenceCap - 1; i++)
            {
                UUID a = UUID.Random();
                Assert.That(e.Invite(id, Alice, a).Ok, Is.True, "invite " + i);
                Assert.That(e.Accept(id, a, RegionA).Ok, Is.True, "accept " + i);
            }
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap));
            Assert.That(e.Store.Get(id).IsFull, Is.True);

            UUID late = UUID.Random();
            SessionOutcome invite = e.Invite(id, Alice, late);
            Assert.That(invite.Ok, Is.False, "inviting into a full room is refused now, not disappointed later");
            Assert.That(invite.Decision, Is.EqualTo(SessionOutcome.Capacity));
        }

        [Test]
        public void ACapacityRefusalMapsToTheSameObservableAsTheMixers()
        {
            // The mixer's chain is 495 -> HTTP 409 -> the viewer's ERROR_CHANNEL_FULL. The engine's
            // early gate must land on the same 409 so one failure mode reaches the user, not two.
            Assert.That(NonSpatialCaps.CapacityHttpStatus, Is.EqualTo(409));
        }

        [Test]
        public void ASeatFreedByAnyLifecycle_IsImmediatelyReusable()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, requestedCap: 2).Session.SessionId;
            // Both are invited while there is still room; invitations hold no seat, so both stand.
            Assert.That(e.Invite(id, Alice, Bob).Ok, Is.True);
            Assert.That(e.Invite(id, Alice, Carol).Ok, Is.True);
            Assert.That(e.Accept(id, Bob, RegionA).Ok, Is.True);   // seats: Alice + Bob = full

            Assert.That(e.Accept(id, Carol, RegionA).Decision, Is.EqualTo(SessionOutcome.Capacity),
                        "a standing invitation does not entitle you to a seat that is gone");

            e.Depart(id, Bob, DepartureReason.ChatLeave);
            Assert.That(e.Accept(id, Carol, RegionA).Ok, Is.True, "the freed seat is reusable at once");
        }

        [Test]
        public void ReInvitingSomeoneWhoAlreadyHoldsASeat_IsNotACapacityRefusal()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, requestedCap: 2).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            Assert.That(e.Store.Get(id).IsFull, Is.True);

            SessionOutcome again = e.Invite(id, Alice, Bob);

            Assert.That(again.Ok, Is.True);
            Assert.That(again.Decision, Is.EqualTo("invite-idempotent-seated"));
        }
    }
}
