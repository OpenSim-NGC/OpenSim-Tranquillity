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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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
            Assert.True(r.A.Moderate(id, Alice, ModerationOp.VoiceModerated, UUID.Zero, true).Ok);
            Assert.True(r.A.Moderate(id, Alice, ModerationOp.VoiceMute, Bob, false).Ok, "Bob is let speak by name");
            Assert.True(r.A.Moderate(id, Alice, ModerationOp.VoiceMute, Carol, true).Ok);
            Assert.True(r.A.Moderate(id, Alice, ModerationOp.TextMute, Bob, true).Ok);
            Assert.True(r.A.Moderate(id, Alice, ModerationOp.TextMute, Carol, true).Ok);
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
                Assert.False(s.VoiceModerated, why + ": mute-all");
                {
                    var assertedValue = s.VoiceMuted;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), why + ": voice mutes");
                }
                {
                    var assertedValue = s.VoiceAllowed;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), why + ": voices let speak");
                }
                {
                    var assertedValue = s.TextMuted;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), why + ": text mutes");
                }
            }
        }

        [InlineData(Store.InProcess)]
        [InlineData(Store.GridService)]
        [Theory(Timeout = 60000)]
        public void MuteAll_DoesNotOutliveTheCall_AndANewGroupCallStartsUnmuted(Store store)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig(store);
            UUID id = StartGroupCall(r);
            Moderate(r, id);

            LeaveOneByOne(r, id);

            AssertNoModeration(r, id, "the call is over");
            r.A.Start(NonSpatialSessionType.Group, Carol, Group, Group, RegionA);
            r.B.Accept(Group, Bob, RegionB, "vs-bob-2");
            NonSpatialVoiceSession again = r.B.Get(id, Bob);
            Assert.False(again.IsVoiceMuted(Bob), "the new call is not muted");
            Assert.False(again.IsVoiceMuted(Carol));
            Assert.False(again.VoiceModerated);
            Assert.False(again.IsTextMuted(Bob));
        }

        [InlineData(Store.InProcess)]
        [InlineData(Store.GridService)]
        [Theory(Timeout = 60000)]
        public void MuteAll_DoesNotOutliveAConference(Store store)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig(store);
            UUID id = StartConference(r);
            Moderate(r, id);

            LeaveOneByOne(r, id);

            AssertNoModeration(r, id, "the conference is over");
        }

        [InlineData(Store.InProcess)]
        [InlineData(Store.GridService)]
        [Theory(Timeout = 60000)]
        public void AMembersVoiceAndTextMutes_AreClearedWithTheCall_AndTheGenerationMoves(Store store)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
                Assert.False(s.IsVoiceMuted(Bob));
                Assert.False(s.IsTextMuted(Bob));
                Assert.True(s.ModerationGeneration > before, "a reader sees that the record changed");
            }
        }

        [Fact(Timeout = 60000)]
        public void ModerationStaysWhileAnySeatIsHeld()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig(Store.InProcess);
            UUID id = StartGroupCall(r);
            Moderate(r, id);

            r.A.Depart(id, Alice, DepartureReason.VoiceTeardown);
            r.B.Depart(id, Bob, DepartureReason.VoiceTeardown);

            NonSpatialVoiceSession s = r.A.Get(id);
            {
                var assertedValue = s.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "Carol is still in the call");
            }
            Assert.True(s.VoiceModerated);
            Assert.True(s.IsTextMuted(Carol));
            Assert.True(s.IsVoiceMuted(Carol));
        }

        [Fact(Timeout = 60000)]
        public void ARemovalThatEmptiesTheCall_ClearsItsMutes_AndKeepsTheRemoval()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig(Store.InProcess);
            r.A.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            r.A.Moderate(Group, Bob, ModerationOp.VoiceModerated, UUID.Zero, true);
            r.A.Moderate(Group, Bob, ModerationOp.TextMute, Carol, true);

            r.A.Moderate(Group, Bob, ModerationOp.Remove, Alice, true, "x", TimeSpan.FromMinutes(30));

            NonSpatialVoiceSession s = r.A.Get(Group);
            Assert.Equal(0, s.SeatsHeld);
            Assert.False(s.VoiceModerated);
            Assert.Empty(s.TextMuted);
            Assert.True(s.IsRemoved(Alice, r.Clock.Now), "the removal keeps its lifetime");
        }

        public enum Release { VoiceTeardown, ChatLeave, PresenceLost, LeaseExpired, RegionStarted }

        [InlineData(Store.InProcess,Release.VoiceTeardown)]
        [InlineData(Store.InProcess,Release.ChatLeave)]
        [InlineData(Store.InProcess,Release.PresenceLost)]
        [InlineData(Store.GridService,Release.VoiceTeardown)]
        [InlineData(Store.GridService,Release.ChatLeave)]
        [InlineData(Store.GridService,Release.PresenceLost)]
        [InlineData(Store.GridService,Release.LeaseExpired)]
        [InlineData(Store.GridService,Release.RegionStarted)]
        [Theory(Timeout = 60000)]
        public void EveryWayTheLastSeatIsReleased_EndsTheModeration(Store store, Release how)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
                    Assert.Equal(2, ((GridNonSpatialSessions)r.A).DepartRegion(RegionA));
                    Assert.Equal(1, ((GridNonSpatialSessions)r.B).DepartRegion(RegionB));
                    break;
            }

            AssertNoModeration(r, id, how.ToString());
            r.A.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            r.A.Accept(Group, Carol, RegionA, "vs-carol-2");
            NonSpatialVoiceSession again = r.A.Get(id, Alice);
            Assert.False(again.IsVoiceMuted(Carol), "the next call starts unmuted");
            Assert.False(again.IsTextMuted(Carol));
        }

        [InlineData(Store.InProcess)]
        [InlineData(Store.GridService)]
        [Theory(Timeout = 60000)]
        public void ARemovedMember_IsStillRefusedAfterTheCallEmptiesAndRestarts_WithinTheRemovalsLifetime(Store store)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig(store);
            UUID id = StartGroupCall(r);
            Moderate(r, id);
            r.A.Moderate(id, Alice, ModerationOp.Remove, Bob, true, "x", TimeSpan.FromMinutes(30));
            r.A.Moderate(id, Alice, ModerationOp.Remove, Carol, true, "until the call ends", null);

            r.A.Depart(id, Alice, DepartureReason.VoiceTeardown);   // the last seat: the call is over
            r.Clock.Now += TimeSpan.FromMinutes(10);

            AssertNoModeration(r, id, "the call is over");
            NonSpatialVoiceSession kept = r.B.Get(id, Bob);
            {
                var assertedValue = kept;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the record is kept for the removal that has time left");
            }
            Assert.True(kept.IsRemoved(Bob, r.Clock.Now));
            Assert.False(kept.IsRemoved(Carol, r.Clock.Now), "the removal that lasts as long as the call ended with it");

            r.B.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionB);
            {
                var assertedValue = r.B.Accept(Group, Bob, RegionB, "vs-bob-2").Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.Removed, assertedValue), "Bob is refused the new call");
            }
            {
                var assertedValue = r.A.Accept(Group, Bob, RegionA, "vs-bob-3").Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.Removed, assertedValue), "by every region server");
            }
            Assert.True(r.A.Accept(Group, Carol, RegionA, "vs-carol-2").Ok, "Carol may join");

            r.Clock.Now += TimeSpan.FromMinutes(21);
            Assert.True(r.B.Accept(Group, Bob, RegionB, "vs-bob-4").Ok, "the removal has run out");
        }

        [Fact(Timeout = 60000)]
        public void AcrossTwoRegionServers_OneServiceHoldsOneRecord_ThatTheLastLeaverEnds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Rig r = NewRig(Store.GridService);
            UUID id = StartGroupCall(r);
            Moderate(r, id);
            r.A.Moderate(id, Alice, ModerationOp.Remove, Carol, true, "x", TimeSpan.FromMinutes(30));

            r.A.Depart(id, Alice, DepartureReason.VoiceTeardown);
            Assert.True(r.B.Get(id, Bob).VoiceModerated, "Bob, in the other region server, is still in the call");
            r.B.Depart(id, Bob, DepartureReason.VoiceTeardown);   // the last seat is in the other region server

            NonSpatialVoiceSession seenByA = r.A.Get(id, Alice);
            Assert.False(seenByA.VoiceModerated);
            Assert.False(seenByA.IsTextMuted(Bob));
            Assert.True(seenByA.IsRemoved(Carol, r.Clock.Now), "the removal's lifetime is the service's, not either server's");

            r.B.Start(NonSpatialSessionType.Group, Bob, Group, Group, RegionB);
            NonSpatialVoiceSession seenByBoth = r.A.Get(id, Alice);
            Assert.False(seenByBoth.IsVoiceMuted(Bob));
            Assert.False(seenByBoth.IsTextMuted(Bob));
            Assert.Equal(SessionOutcome.Removed, r.A.Accept(Group, Carol, RegionA, "vs-c").Decision);
        }
    }
}
