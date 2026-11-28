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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialCapTests
    {
        [Fact(Timeout = 60000)]
        public void TheMirroredMixerCap_MatchesTheMixersOwnErrorCodeConstant()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // WebRtcJanusService.cs is the codebase's existing mirror of janus_slvoice.c:112.
            Assert.Equal(WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE, NonSpatialCaps.MixerRoomFullErrorCode);
        }

        [Fact(Timeout = 60000)]
        public void EveryCapTheEngineCanHandOut_IsOneTheMixerWillHonour()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(NonSpatialCaps.CapsAgree());
            Assert.Equal(50, NonSpatialCaps.DefaultConferenceCap);
            Assert.True(NonSpatialCaps.DefaultConferenceCap <= NonSpatialCaps.MixerRoomCap);
        }

        [Fact(Timeout = 60000)]
        public void ARequestAboveTheMixerCap_IsClampedToIt_NotGranted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(NonSpatialCaps.MixerRoomCap, NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 500));
        }

        [Fact(Timeout = 60000)]
        public void ARequestBelowTheDefault_IsHonoured_AndNeverDropsBelowTwo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(8, NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 8));
            Assert.Equal(2, NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 1));
            Assert.Equal(NonSpatialCaps.DefaultConferenceCap, NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, 0));
        }

        [Fact(Timeout = 60000)]
        public void P2P_IsAlwaysTwo_WhateverIsAskedFor()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(2, NonSpatialCaps.Effective(NonSpatialSessionType.P2P, 50));
            Assert.Equal(2, NonSpatialCaps.Effective(NonSpatialSessionType.P2P, 0));
        }

        [Fact(Timeout = 60000)]
        public void AConferenceAdmitsExactlyFiftySeats_AndRefusesTheFiftyFirst()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;   // creator = seat 1

            for (int i = 0; i < NonSpatialCaps.DefaultConferenceCap - 1; i++)
            {
                UUID a = UUID.Random();
                Assert.True(e.Invite(id, Alice, a).Ok, "invite " + i);
                Assert.True(e.Accept(id, a, RegionA).Ok, "accept " + i);
            }
            Assert.Equal(NonSpatialCaps.DefaultConferenceCap, e.Store.Get(id).SeatsHeld);
            Assert.True(e.Store.Get(id).IsFull);

            UUID late = UUID.Random();
            SessionOutcome invite = e.Invite(id, Alice, late);
            Assert.False(invite.Ok, "inviting into a full room is refused now, not disappointed later");
            Assert.Equal(SessionOutcome.Capacity, invite.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ACapacityRefusalMapsToTheSameObservableAsTheMixers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The mixer's chain is 495 -> HTTP 409 -> the viewer's ERROR_CHANNEL_FULL. The engine's
            // early gate must land on the same 409 so one failure mode reaches the user, not two.
            Assert.Equal(409, NonSpatialCaps.CapacityHttpStatus);
        }

        [Fact(Timeout = 60000)]
        public void ASeatFreedByAnyLifecycle_IsImmediatelyReusable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, requestedCap: 2).Session.SessionId;
            // Both are invited while there is still room; invitations hold no seat, so both stand.
            Assert.True(e.Invite(id, Alice, Bob).Ok);
            Assert.True(e.Invite(id, Alice, Carol).Ok);
            Assert.True(e.Accept(id, Bob, RegionA).Ok);   // seats: Alice + Bob = full

            {
                var assertedValue = e.Accept(id, Carol, RegionA).Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.Capacity, assertedValue), "a standing invitation does not entitle you to a seat that is gone");
            }

            e.Depart(id, Bob, DepartureReason.ChatLeave);
            Assert.True(e.Accept(id, Carol, RegionA).Ok, "the freed seat is reusable at once");
        }

        [Fact(Timeout = 60000)]
        public void ReInvitingSomeoneWhoAlreadyHoldsASeat_IsNotACapacityRefusal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(clock);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, requestedCap: 2).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            Assert.True(e.Store.Get(id).IsFull);

            SessionOutcome again = e.Invite(id, Alice, Bob);

            Assert.True(again.Ok);
            Assert.Equal("invite-idempotent-seated", again.Decision);
        }
    }
}
