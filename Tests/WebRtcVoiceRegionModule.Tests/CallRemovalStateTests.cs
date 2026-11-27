/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A member removed from a call: the record of it (engine and copy), what it frees, how it ends, and every way back
 * into the call it closes. A removal lasts until the call ends, or for the lifetime it was given.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class CallRemovalStateTests
    {
        private static readonly UUID Group = NonSpatialIdentityTests.Group;

        private static (NonSpatialVoiceSessionEngine Engine, FakeClock Clock) GroupCall()
        {
            FakeClock clock = new();
            NonSpatialVoiceSessionEngine e = NewEngine(clock);
            e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            e.Accept(Group, Bob, RegionA, "vs-bob");
            e.MarkPresent(Group, Bob, RegionA, "vs-bob");
            e.Accept(Group, Carol, RegionA);
            return (e, clock);
        }

        private static SessionOutcome RemoveBob(NonSpatialVoiceSessionEngine e, TimeSpan? lifetime = null, string reason = "test")
            => e.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, reason, lifetime);

        [Fact(Timeout = 60000)]
        public void ARemovalFreesTheSeat_AndRecordsWhoWhenAndWhy()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();

            SessionOutcome o = RemoveBob(e, reason: "talking over the speaker");

            Assert.True(o.Ok);
            Assert.Equal(SessionOutcome.ModerationChanged, o.Decision);
            NonSpatialVoiceSession s = e.Get(Group);
            NonSpatialMember bob = s.Find(Bob);
            Assert.False(bob.HoldsSeat);
            Assert.Equal(MemberState.Departed, bob.State);
            Assert.Equal(DepartureReason.Removed, bob.Departure);
            {
                var assertedValue = bob.ViewerSession;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the viewer session the seat held is released");
            }
            CallRemoval r = s.RemovalOf(Bob);
            Assert.Equal(Alice, r.By);
            Assert.Equal(clock.Now, r.AtUtc);
            Assert.Equal("talking over the speaker", r.Reason);
            {
                var assertedValue = r.ExpiresUtc;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "with no lifetime it lasts until the call ends");
            }
            Assert.True(s.IsRemoved(Bob, clock.Now));
            Assert.False(s.IsRemoved(Carol, clock.Now), "nobody else");
            Assert.Equal(2, s.SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void RemovingTwice_ChangesNothing_AndDoesNotExtendTheRemoval()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromMinutes(30));
            int generation = e.Get(Group).ModerationGeneration;

            clock.Now += TimeSpan.FromMinutes(10);
            SessionOutcome again = RemoveBob(e, TimeSpan.FromMinutes(30));

            Assert.Equal(SessionOutcome.ModerationUnchanged, again.Decision);
            Assert.Equal(generation, e.Get(Group).ModerationGeneration);
            Assert.Equal(clock.Now - TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(30), e.Get(Group).RemovalOf(Bob).ExpiresUtc);
        }

        [Fact(Timeout = 60000)]
        public void ARemovedMemberIsRefusedEveryWayBackIn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            RemoveBob(e);

            {
                var assertedValue = e.Accept(Group, Bob, RegionA).Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.Removed, assertedValue), "call and accept invitation");
            }
            {
                var assertedValue = e.MarkPresent(Group, Bob, RegionA, "vs-new").Decision;
                TestAssert.WithMessage(() => Assert.Equal(SessionOutcome.Removed, assertedValue), "an admitted provision");
            }
            Assert.False(e.Get(Group).Find(Bob).HoldsSeat);
            Assert.True(e.IsRemoved(e.Get(Group), Bob));
        }

        [Fact(Timeout = 60000)]
        public void TheCreatorRemoved_IsNotSeatedAgainByTheirOwnStart()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            e.Moderate(Group, Bob, ModerationOp.Remove, Alice, true, "x", null);

            SessionOutcome start = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);

            Assert.True(start.Ok, "a start finds the call");
            Assert.False(e.Get(Group).Find(Alice).HoldsSeat, "and does not hand the creator their seat back");
        }

        [Fact(Timeout = 60000)]
        public void ARemovalWithALifetime_EndsAtTheMinute_NotBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromMinutes(30));

            clock.Now += TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59);
            Assert.Equal(SessionOutcome.Removed, e.Accept(Group, Bob, RegionA).Decision);

            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(e.Accept(Group, Bob, RegionA).Ok, "the removal has run out");
            Assert.True(e.Get(Group).Find(Bob).HoldsSeat);
        }

        [Fact(Timeout = 60000)]
        public void ARemovalWithNoLifetime_EndsWithTheCall_AndNotBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            RemoveBob(e);
            e.Depart(Group, Carol, DepartureReason.VoiceTeardown);
            Assert.True(e.Get(Group).IsRemoved(Bob, DateTime.UtcNow), "Alice still holds a seat: the call is on");
            Assert.Equal(SessionOutcome.Removed, e.Accept(Group, Bob, RegionA).Decision);

            e.Depart(Group, Alice, DepartureReason.VoiceTeardown);   // the last seat: the call ends

            Assert.Empty(e.Get(Group).Removed);
            Assert.True(e.Accept(Group, Bob, RegionA).Ok, "a new call has no removals");
        }

        [Fact(Timeout = 60000)]
        public void ARemovalWithALifetime_OutlastsTheCall_AndKeepsTheGroupsRecord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromHours(2));
            e.Depart(Group, Carol, DepartureReason.PresenceLost);
            e.Depart(Group, Alice, DepartureReason.PresenceLost);
            {
                var assertedValue = e.Get(Group).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the call is over");
            }

            Assert.False(e.Get(Group).IsDead, "the record is kept for the removal");
            Assert.Equal(SessionOutcome.Removed, e.Accept(Group, Bob, RegionA).Decision);
            clock.Now += TimeSpan.FromHours(2);
            Assert.True(e.Accept(Group, Bob, RegionA).Ok);
        }

        [Fact(Timeout = 60000)]
        public void ReadmitLiftsItAtOnce_AndDoesNotSeatThemAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            RemoveBob(e);

            SessionOutcome back = e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);
            SessionOutcome again = e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);

            Assert.Equal(SessionOutcome.ModerationChanged, back.Decision);
            Assert.Equal(SessionOutcome.ModerationUnchanged, again.Decision);
            Assert.False(e.Get(Group).Find(Bob).HoldsSeat, "they come back by calling, as anyone does");
            Assert.True(e.Accept(Group, Bob, RegionA).Ok);
        }

        [Fact(Timeout = 60000)]
        public void TheGenerationMovesOncePerEffectiveChange()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            int g0 = e.Get(Group).ModerationGeneration;

            RemoveBob(e);
            RemoveBob(e);
            e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);
            e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);

            Assert.Equal(g0 + 2, e.Get(Group).ModerationGeneration);
        }

        [Fact(Timeout = 60000)]
        public void AGroupRemovalLeavesTheGroupsText_AConferenceRemovalLeavesTheConferenceText()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine g, _) = GroupCall();
            RemoveBob(g);
            Assert.True(g.Get(Group).Find(Bob).InChat, "the group's chat is the group's");
            Assert.Contains(Bob, g.Get(Group).ChatMembers());

            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome conf = e.StartAdhoc(Alice, UUID.Random(), RegionA, new[] { Bob, Carol });
            UUID id = conf.Session.SessionId;
            e.Accept(id, Bob, RegionA);
            Assert.Contains(Bob, e.Get(id).ChatMembers());

            e.Moderate(id, Alice, ModerationOp.Remove, Bob, true, "x", null);

            Assert.False(e.Get(id).Find(Bob).InChat);
            Assert.DoesNotContain(Bob, e.Get(id).ChatMembers());
            Assert.Equal(SessionOutcome.Removed, e.Accept(id, Bob, RegionA).Decision);
            Assert.Equal(SessionOutcome.Removed, e.Invite(id, Alice, Bob).Decision);
        }

        [Fact(Timeout = 60000)]
        public void ARemovedInviteeIsNotRungAgain_AndTheirOpenInvitationLapses()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome conf = e.StartAdhoc(Alice, UUID.Random(), RegionA, new[] { Bob, Carol });
            UUID id = conf.Session.SessionId;
            {
                var assertedValue = AdhocVoiceInvite.Targets(e.Get(id), Alice);
                TestAssert.WithMessage(() => Assert.Equivalent(new[] { Bob, Carol }, assertedValue, strict: true), "precondition: both would be rung");
            }
            e.Moderate(id, Alice, ModerationOp.Remove, Bob, true, "x", null);

            List<UUID> rung = AdhocVoiceInvite.Targets(e.Get(id), Alice);

            Assert.Equal(new[] { Carol }, rung);
        }

        [Fact(Timeout = 60000)]
        public void ACallBetweenTwoPeopleHasNoRemoval()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome p2p = e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);

            Assert.False(e.Moderate(p2p.Session.SessionId, Alice, ModerationOp.Remove, Bob, true, "x", null).Ok);
        }

        [Fact(Timeout = 60000)]
        public async Task SixtyFourAcceptsRacingOneRemoval_NeverLeaveTheMemberSeated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            for (int round = 0; round < 40; round++)
            {
                (NonSpatialVoiceSessionEngine e, _) = GroupCall();
                int refused = 0, accepted = 0;
                using ManualResetEventSlim go = new(false);
                List<Task> tasks = new();
                for (int i = 0; i < 64; i++)
                    tasks.Add(Task.Run(() =>
                    {
                        go.Wait(TestContext.Current.CancellationToken);
                        SessionOutcome o = e.Accept(Group, Bob, RegionA, "vs-race");
                        if (o.Ok) Interlocked.Increment(ref accepted); else Interlocked.Increment(ref refused);
                    }, TestContext.Current.CancellationToken));
                tasks.Add(Task.Run(() => { go.Wait(TestContext.Current.CancellationToken); RemoveBob(e); }, TestContext.Current.CancellationToken));
                go.Set();
                await Task.WhenAll(tasks);

                // The member's seat is gone whatever the order: an accept after the removal is refused, and one before
                // it had its seat taken by the same mutation that recorded the removal.
                NonSpatialVoiceSession s = e.Get(Group);
                Assert.False(s.Find(Bob).HoldsSeat, $"round {round}: accepted={accepted} refused={refused}");
                Assert.True(s.IsRemoved(Bob, DateTime.UtcNow));
                Assert.Equal(SessionOutcome.Removed, e.Accept(Group, Bob, RegionA).Decision);
            }
        }

        [Fact(Timeout = 60000)]
        public void TheListOfRemovalsIsBounded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            for (int i = 0; i < NonSpatialVoiceSession.MaxModerationEntries; i++)
                Assert.True(e.Moderate(Group, Alice, ModerationOp.Remove, UUID.Random(), true, "x", null).Ok);

            SessionOutcome over = e.Moderate(Group, Alice, ModerationOp.Remove, UUID.Random(), true, "x", null);

            Assert.Equal(SessionOutcome.ModerationRefused, over.Decision);
        }

        // ---- the copy of the record ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheCopyCarriesRemovals_WithTheirEndTimes_AndAnOldCopyReadsAsNone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromMinutes(45), "loud");
            e.Moderate(Group, Alice, ModerationOp.Remove, Carol, true, "other", null);

            SessionData data = SessionWire.ToData(e.Get(Group));
            NonSpatialVoiceSession back = SessionWire.FromData(SessionReply.FromJson(new SessionReply { Session = data }.ToJson()).Session);

            Assert.True(back.IsRemoved(Bob, clock.Now));
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(45), back.RemovalOf(Bob).ExpiresUtc);
            Assert.Equal("loud", back.RemovalOf(Bob).Reason);
            Assert.Equal(Alice, back.RemovalOf(Bob).By);
            Assert.Null(back.RemovalOf(Carol).ExpiresUtc);
            Assert.Equal(e.Get(Group).ModerationGeneration, back.ModerationGeneration);

            data.Removed = null;
            {
                var assertedValue = SessionWire.FromData(data).Removed;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a copy from a service without removals reads as none");
            }
        }

        // ---- through the grid service -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ThroughTheGridService_ARemovalIsSeenByAnotherRegion_AtOnce_AndRefusesItsAccept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            b.Accept(Group, Bob, RegionB, "vs-bob");

            SessionOutcome o = a.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, "x", null);

            Assert.Equal(SessionOutcome.ModerationChanged, o.Decision);
            NonSpatialVoiceSession seen = b.Get(Group, Bob);
            Assert.True(b.IsRemoved(seen, Bob));
            Assert.Equal(SessionOutcome.Removed, b.Accept(Group, Bob, RegionB).Decision);
            Assert.Equal(SessionOutcome.ModerationChanged, a.Moderate(Group, Alice, ModerationOp.Remove, Bob, false).Decision);
            Assert.True(b.Accept(Group, Bob, RegionB).Ok);
        }

        [Fact(Timeout = 60000)]
        public void ThroughTheGridService_TheRegionHoldingTheSeatIsToldItWasRemoved_WhenItRenews()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            b.Accept(Group, Bob, RegionB, "vs-bob");
            Assert.Contains(new SeatKey(Group, Bob), b.HeldSeats());

            Assert.True(b.RenewSeats(b.HeldSeats(), out IReadOnlyList<SeatKey> before));
            {
                var assertedValue = before;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing to tear down while the member is in the call");
            }

            a.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, "x", null);
            Assert.True(b.RenewSeats(b.HeldSeats(), out IReadOnlyList<SeatKey> removed));

            Assert.Equal(new[] { new SeatKey(Group, Bob) }, removed);
            {
                var assertedValue = b.HeldSeats();
                TestAssert.WithMessage(() => Assert.DoesNotContain(new SeatKey(Group, Bob), assertedValue), "the region lets go of the seat it was told of");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheRenewerCallsBackForEachSeatTheServiceSaysWasRemoved()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            b.Accept(Group, Bob, RegionB, "vs-bob");
            List<SeatKey> told = new();
            using GridSeatRenewer renewer = new(b, () => new[] { Alice, Bob }, null, seatRemoved: told.Add);

            renewer.RunOnce();
            Assert.Empty(told);
            a.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, "x", null);
            renewer.RunOnce();

            Assert.Equal(new[] { new SeatKey(Group, Bob) }, told);
        }

        [Fact(Timeout = 60000)]
        public void TheServiceAnswersAnUnknownOpWithBadRequest_AndAnOldServiceWouldDoTheSame()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            service.Start((int)NonSpatialSessionType.Group, Alice, Group, Group, RegionA, 0);

            SessionReply unknown = service.Moderate(Group, Alice, 99, Bob, true);
            SessionReply removal = service.Moderate(Group, Alice, (int)ModerationOp.Remove, Bob, true, "x", 10);

            Assert.False(unknown.Ok);
            Assert.Equal("refused-bad-request", unknown.Decision);
            Assert.True(removal.Ok);
            Assert.NotNull(SessionWire.FromData(removal.Session).RemovalOf(Bob).ExpiresUtc);
        }
    }
}
