/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * How long a call's moderation lasts. Mute-all, the voice mutes and the text mutes belong to the call: they go
 * when its last seat is released, however it was released, and a call started afterwards begins unmuted. A removal
 * keeps the lifetime it was given: until the call ends, or for the minutes it was asked to last.
 * Each test runs against the engine in this process and against the grid session service.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CallModerationLifetimeTests
    {
        public enum Store { InProcess, GridService }

        private static readonly UUID Group = NonSpatialIdentityTests.Group;
        private static readonly TimeSpan Lease = TimeSpan.FromSeconds(180);

        /// <summary>The record as one region server sees it: A and B are two region servers over one service.</summary>
        private sealed class Rig
        {
            public INonSpatialSessions A;
            public INonSpatialSessions B;
            public VoiceSessionService Service;
            public FakeClock Clock = new FakeClock();
        }

        private static Rig NewRig(Store store)
        {
            Rig r = new Rig();
            if (store == Store.InProcess)
            {
                r.A = r.B = NewEngine(r.Clock);
            }
            else
            {
                r.Service = VoiceSessionServiceTests.NewService(r.Clock, seatLease: Lease);
                r.A = VoiceSessionServiceTests.NewRegion(r.Service);
                r.B = VoiceSessionServiceTests.NewRegion(r.Service);
            }
            return r;
        }

        /// <summary>Alice and Carol sit in region A and Bob in region B.</summary>
        private static UUID StartGroupCall(Rig r)
        {
            r.A.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            r.B.Accept(Group, Bob, RegionB, "vs-bob");
            r.B.MarkPresent(Group, Bob, RegionB, "vs-bob");
            r.A.Accept(Group, Carol, RegionA, "vs-carol");
            return Group;
        }

        private static UUID StartConference(Rig r)
        {
            UUID id = r.A.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol }).Session.SessionId;
            r.B.Accept(id, Bob, RegionB, "vs-bob");
            r.A.Accept(id, Carol, RegionA, "vs-carol");
            return id;
        }

        private static void Moderate(Rig r, UUID id)
        {
            Assert.That(r.A.Moderate(id, Alice, ModerationOp.VoiceModerated, UUID.Zero, true).Ok, Is.True);
            Assert.That(r.A.Moderate(id, Alice, ModerationOp.VoiceMute, Bob, false).Ok, Is.True, "Bob is let speak by name");
            Assert.That(r.A.Moderate(id, Alice, ModerationOp.VoiceMute, Carol, true).Ok, Is.True);
            Assert.That(r.A.Moderate(id, Alice, ModerationOp.TextMute, Bob, true).Ok, Is.True);
            Assert.That(r.A.Moderate(id, Alice, ModerationOp.TextMute, Carol, true).Ok, Is.True);
        }

        private static void LeaveOneByOne(Rig r, UUID id, DepartureReason reason = DepartureReason.VoiceTeardown)
        {
            r.A.Depart(id, Alice, reason);
            r.B.Depart(id, Bob, reason);
            r.A.Depart(id, Carol, reason);
        }

        /// <summary>Nothing a moderator decided is left in the record, if the record is still there.</summary>
        private static void AssertNoModeration(Rig r, UUID id, string why)
        {
            foreach (INonSpatialSessions region in new[] { r.A, r.B })
            {
                NonSpatialVoiceSession s = region.Get(id, Alice);
                if (s == null) continue;
                Assert.That(s.VoiceModerated, Is.False, why + ": mute-all");
                Assert.That(s.VoiceMuted, Is.Empty, why + ": voice mutes");
                Assert.That(s.VoiceAllowed, Is.Empty, why + ": voices let speak");
                Assert.That(s.TextMuted, Is.Empty, why + ": text mutes");
            }
        }

        [TestCase(Store.InProcess)]
        [TestCase(Store.GridService)]
        public void MuteAll_DoesNotOutliveTheCall_AndANewGroupCallStartsUnmuted(Store store)
        {
            Rig r = NewRig(store);
            UUID id = StartGroupCall(r);
            Moderate(r, id);

            LeaveOneByOne(r, id);

            AssertNoModeration(r, id, "the call is over");
            r.A.Start(NonSpatialSessionType.Group, Carol, Group, Group, RegionA);
            r.B.Accept(Group, Bob, RegionB, "vs-bob-2");
            NonSpatialVoiceSession again = r.B.Get(id, Bob);
            Assert.That(again.IsVoiceMuted(Bob), Is.False, "the new call is not muted");
            Assert.That(again.IsVoiceMuted(Carol), Is.False);
            Assert.That(again.VoiceModerated, Is.False);
            Assert.That(again.IsTextMuted(Bob), Is.False);
        }

        [TestCase(Store.InProcess)]
        [TestCase(Store.GridService)]
        public void MuteAll_DoesNotOutliveAConference(Store store)
        {
            Rig r = NewRig(store);
            UUID id = StartConference(r);
            Moderate(r, id);

            LeaveOneByOne(r, id);

            AssertNoModeration(r, id, "the conference is over");
        }

        [TestCase(Store.InProcess)]
        [TestCase(Store.GridService)]
        public void AMembersVoiceAndTextMutes_AreClearedWithTheCall_AndTheGenerationMoves(Store store)
        {
            Rig r = NewRig(store);
            UUID id = StartGroupCall(r);
            r.A.Moderate(id, Alice, ModerationOp.VoiceMute, Bob, true);
            r.A.Moderate(id, Alice, ModerationOp.TextMute, Bob, true);
            int before = r.A.Get(id).ModerationGeneration;

            LeaveOneByOne(r, id);

            AssertNoModeration(r, id, "the call is over");
            NonSpatialVoiceSession s = r.A.Get(id, Alice);
            if (s != null)
            {
                Assert.That(s.IsVoiceMuted(Bob), Is.False);
                Assert.That(s.IsTextMuted(Bob), Is.False);
                Assert.That(s.ModerationGeneration, Is.GreaterThan(before), "a reader sees that the record changed");
            }
        }

        [Test]
        public void ModerationStaysWhileAnySeatIsHeld()
        {
            Rig r = NewRig(Store.InProcess);
            UUID id = StartGroupCall(r);
            Moderate(r, id);

            r.A.Depart(id, Alice, DepartureReason.VoiceTeardown);
            r.B.Depart(id, Bob, DepartureReason.VoiceTeardown);

            NonSpatialVoiceSession s = r.A.Get(id);
            Assert.That(s.SeatsHeld, Is.EqualTo(1), "Carol is still in the call");
            Assert.That(s.VoiceModerated, Is.True);
            Assert.That(s.IsTextMuted(Carol), Is.True);
            Assert.That(s.IsVoiceMuted(Carol), Is.True);
        }

        [Test]
        public void ARemovalThatEmptiesTheCall_ClearsItsMutes_AndKeepsTheRemoval()
        {
            Rig r = NewRig(Store.InProcess);
            r.A.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            r.A.Moderate(Group, Bob, ModerationOp.VoiceModerated, UUID.Zero, true);
            r.A.Moderate(Group, Bob, ModerationOp.TextMute, Carol, true);

            r.A.Moderate(Group, Bob, ModerationOp.Remove, Alice, true, "x", TimeSpan.FromMinutes(30));

            NonSpatialVoiceSession s = r.A.Get(Group);
            Assert.That(s.SeatsHeld, Is.EqualTo(0));
            Assert.That(s.VoiceModerated, Is.False);
            Assert.That(s.TextMuted, Is.Empty);
            Assert.That(s.IsRemoved(Alice, r.Clock.Now), Is.True, "the removal keeps its lifetime");
        }

        public enum Release { VoiceTeardown, ChatLeave, PresenceLost, LeaseExpired, RegionStarted }

        [TestCase(Store.InProcess, Release.VoiceTeardown)]
        [TestCase(Store.InProcess, Release.ChatLeave)]
        [TestCase(Store.InProcess, Release.PresenceLost)]
        [TestCase(Store.GridService, Release.VoiceTeardown)]
        [TestCase(Store.GridService, Release.ChatLeave)]
        [TestCase(Store.GridService, Release.PresenceLost)]
        [TestCase(Store.GridService, Release.LeaseExpired)]
        [TestCase(Store.GridService, Release.RegionStarted)]
        public void EveryWayTheLastSeatIsReleased_EndsTheModeration(Store store, Release how)
        {
            Rig r = NewRig(store);
            UUID id = StartGroupCall(r);
            Moderate(r, id);

            switch (how)
            {
                case Release.VoiceTeardown: LeaveOneByOne(r, id); break;
                case Release.ChatLeave: LeaveOneByOne(r, id, DepartureReason.ChatLeave); break;
                case Release.PresenceLost:
                    r.A.DepartAll(Alice, DepartureReason.PresenceLost);
                    r.B.DepartAll(Bob, DepartureReason.PresenceLost);
                    r.A.DepartAll(Carol, DepartureReason.PresenceLost);
                    break;
                case Release.LeaseExpired:
                    r.Clock.Now += Lease + TimeSpan.FromSeconds(1);
                    r.Service.Sweep();
                    break;
                case Release.RegionStarted:
                    Assert.That(((GridNonSpatialSessions)r.A).DepartRegion(RegionA), Is.EqualTo(2));
                    Assert.That(((GridNonSpatialSessions)r.B).DepartRegion(RegionB), Is.EqualTo(1));
                    break;
            }

            AssertNoModeration(r, id, how.ToString());
            r.A.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            r.A.Accept(Group, Carol, RegionA, "vs-carol-2");
            NonSpatialVoiceSession again = r.A.Get(id, Alice);
            Assert.That(again.IsVoiceMuted(Carol), Is.False, "the next call starts unmuted");
            Assert.That(again.IsTextMuted(Carol), Is.False);
        }

        [TestCase(Store.InProcess)]
        [TestCase(Store.GridService)]
        public void ARemovedMember_IsStillRefusedAfterTheCallEmptiesAndRestarts_WithinTheRemovalsLifetime(Store store)
        {
            Rig r = NewRig(store);
            UUID id = StartGroupCall(r);
            Moderate(r, id);
            r.A.Moderate(id, Alice, ModerationOp.Remove, Bob, true, "x", TimeSpan.FromMinutes(30));
            r.A.Moderate(id, Alice, ModerationOp.Remove, Carol, true, "until the call ends", null);

            r.A.Depart(id, Alice, DepartureReason.VoiceTeardown);   // the last seat: the call is over
            r.Clock.Now += TimeSpan.FromMinutes(10);

            AssertNoModeration(r, id, "the call is over");
            NonSpatialVoiceSession kept = r.B.Get(id, Bob);
            Assert.That(kept, Is.Not.Null, "the record is kept for the removal that has time left");
            Assert.That(kept.IsRemoved(Bob, r.Clock.Now), Is.True);
            Assert.That(kept.IsRemoved(Carol, r.Clock.Now), Is.False, "the removal that lasts as long as the call ended with it");

            r.B.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionB);
            Assert.That(r.B.Accept(Group, Bob, RegionB, "vs-bob-2").Decision, Is.EqualTo(SessionOutcome.Removed), "Bob is refused the new call");
            Assert.That(r.A.Accept(Group, Bob, RegionA, "vs-bob-3").Decision, Is.EqualTo(SessionOutcome.Removed), "by every region server");
            Assert.That(r.A.Accept(Group, Carol, RegionA, "vs-carol-2").Ok, Is.True, "Carol may join");

            r.Clock.Now += TimeSpan.FromMinutes(21);
            Assert.That(r.B.Accept(Group, Bob, RegionB, "vs-bob-4").Ok, Is.True, "the removal has run out");
        }

        [Test]
        public void AcrossTwoRegionServers_OneServiceHoldsOneRecord_ThatTheLastLeaverEnds()
        {
            Rig r = NewRig(Store.GridService);
            UUID id = StartGroupCall(r);
            Moderate(r, id);
            r.A.Moderate(id, Alice, ModerationOp.Remove, Carol, true, "x", TimeSpan.FromMinutes(30));

            r.A.Depart(id, Alice, DepartureReason.VoiceTeardown);
            Assert.That(r.B.Get(id, Bob).VoiceModerated, Is.True, "Bob, in the other region server, is still in the call");
            r.B.Depart(id, Bob, DepartureReason.VoiceTeardown);   // the last seat is in the other region server

            NonSpatialVoiceSession seenByA = r.A.Get(id, Alice);
            Assert.That(seenByA.VoiceModerated, Is.False);
            Assert.That(seenByA.IsTextMuted(Bob), Is.False);
            Assert.That(seenByA.IsRemoved(Carol, r.Clock.Now), Is.True, "the removal's lifetime is the service's, not either server's");

            r.B.Start(NonSpatialSessionType.Group, Bob, Group, Group, RegionB);
            NonSpatialVoiceSession seenByBoth = r.A.Get(id, Alice);
            Assert.That(seenByBoth.IsVoiceMuted(Bob), Is.False);
            Assert.That(seenByBoth.IsTextMuted(Bob), Is.False);
            Assert.That(r.A.Accept(Group, Carol, RegionA, "vs-c").Decision, Is.EqualTo(SessionOutcome.Removed));
        }
    }
}
