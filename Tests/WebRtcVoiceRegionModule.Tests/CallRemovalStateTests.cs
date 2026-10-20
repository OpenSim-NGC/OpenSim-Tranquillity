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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void ARemovalFreesTheSeat_AndRecordsWhoWhenAndWhy()
        {
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();

            SessionOutcome o = RemoveBob(e, reason: "talking over the speaker");

            Assert.That(o.Ok, Is.True);
            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            NonSpatialVoiceSession s = e.Get(Group);
            NonSpatialMember bob = s.Find(Bob);
            Assert.That(bob.HoldsSeat, Is.False);
            Assert.That(bob.State, Is.EqualTo(MemberState.Departed));
            Assert.That(bob.Departure, Is.EqualTo(DepartureReason.Removed));
            Assert.That(bob.ViewerSession, Is.Null, "the viewer session the seat held is released");
            CallRemoval r = s.RemovalOf(Bob);
            Assert.That(r.By, Is.EqualTo(Alice));
            Assert.That(r.AtUtc, Is.EqualTo(clock.Now));
            Assert.That(r.Reason, Is.EqualTo("talking over the speaker"));
            Assert.That(r.ExpiresUtc, Is.Null, "with no lifetime it lasts until the call ends");
            Assert.That(s.IsRemoved(Bob, clock.Now), Is.True);
            Assert.That(s.IsRemoved(Carol, clock.Now), Is.False, "nobody else");
            Assert.That(s.SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void RemovingTwice_ChangesNothing_AndDoesNotExtendTheRemoval()
        {
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromMinutes(30));
            int generation = e.Get(Group).ModerationGeneration;

            clock.Now += TimeSpan.FromMinutes(10);
            SessionOutcome again = RemoveBob(e, TimeSpan.FromMinutes(30));

            Assert.That(again.Decision, Is.EqualTo(SessionOutcome.ModerationUnchanged));
            Assert.That(e.Get(Group).ModerationGeneration, Is.EqualTo(generation));
            Assert.That(e.Get(Group).RemovalOf(Bob).ExpiresUtc, Is.EqualTo(clock.Now - TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(30)));
        }

        [Test]
        public void ARemovedMemberIsRefusedEveryWayBackIn()
        {
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            RemoveBob(e);

            Assert.That(e.Accept(Group, Bob, RegionA).Decision, Is.EqualTo(SessionOutcome.Removed), "call and accept invitation");
            Assert.That(e.MarkPresent(Group, Bob, RegionA, "vs-new").Decision, Is.EqualTo(SessionOutcome.Removed), "an admitted provision");
            Assert.That(e.Get(Group).Find(Bob).HoldsSeat, Is.False);
            Assert.That(e.IsRemoved(e.Get(Group), Bob), Is.True);
        }

        [Test]
        public void TheCreatorRemoved_IsNotSeatedAgainByTheirOwnStart()
        {
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            e.Moderate(Group, Bob, ModerationOp.Remove, Alice, true, "x", null);

            SessionOutcome start = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);

            Assert.That(start.Ok, Is.True, "a start finds the call");
            Assert.That(e.Get(Group).Find(Alice).HoldsSeat, Is.False, "and does not hand the creator their seat back");
        }

        [Test]
        public void ARemovalWithALifetime_EndsAtTheMinute_NotBefore()
        {
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromMinutes(30));

            clock.Now += TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59);
            Assert.That(e.Accept(Group, Bob, RegionA).Decision, Is.EqualTo(SessionOutcome.Removed));

            clock.Now += TimeSpan.FromSeconds(1);
            Assert.That(e.Accept(Group, Bob, RegionA).Ok, Is.True, "the removal has run out");
            Assert.That(e.Get(Group).Find(Bob).HoldsSeat, Is.True);
        }

        [Test]
        public void ARemovalWithNoLifetime_EndsWithTheCall_AndNotBefore()
        {
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            RemoveBob(e);
            e.Depart(Group, Carol, DepartureReason.VoiceTeardown);
            Assert.That(e.Get(Group).IsRemoved(Bob, DateTime.UtcNow), Is.True, "Alice still holds a seat: the call is on");
            Assert.That(e.Accept(Group, Bob, RegionA).Decision, Is.EqualTo(SessionOutcome.Removed));

            e.Depart(Group, Alice, DepartureReason.VoiceTeardown);   // the last seat: the call ends

            Assert.That(e.Get(Group).Removed, Is.Empty);
            Assert.That(e.Accept(Group, Bob, RegionA).Ok, Is.True, "a new call has no removals");
        }

        [Test]
        public void ARemovalWithALifetime_OutlastsTheCall_AndKeepsTheGroupsRecord()
        {
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromHours(2));
            e.Depart(Group, Carol, DepartureReason.PresenceLost);
            e.Depart(Group, Alice, DepartureReason.PresenceLost);
            Assert.That(e.Get(Group).SeatsHeld, Is.EqualTo(0), "the call is over");

            Assert.That(e.Get(Group).IsDead, Is.False, "the record is kept for the removal");
            Assert.That(e.Accept(Group, Bob, RegionA).Decision, Is.EqualTo(SessionOutcome.Removed));
            clock.Now += TimeSpan.FromHours(2);
            Assert.That(e.Accept(Group, Bob, RegionA).Ok, Is.True);
        }

        [Test]
        public void ReadmitLiftsItAtOnce_AndDoesNotSeatThemAgain()
        {
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            RemoveBob(e);

            SessionOutcome back = e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);
            SessionOutcome again = e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);

            Assert.That(back.Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            Assert.That(again.Decision, Is.EqualTo(SessionOutcome.ModerationUnchanged));
            Assert.That(e.Get(Group).Find(Bob).HoldsSeat, Is.False, "they come back by calling, as anyone does");
            Assert.That(e.Accept(Group, Bob, RegionA).Ok, Is.True);
        }

        [Test]
        public void TheGenerationMovesOncePerEffectiveChange()
        {
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            int g0 = e.Get(Group).ModerationGeneration;

            RemoveBob(e);
            RemoveBob(e);
            e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);
            e.Moderate(Group, Alice, ModerationOp.Remove, Bob, false);

            Assert.That(e.Get(Group).ModerationGeneration, Is.EqualTo(g0 + 2));
        }

        [Test]
        public void AGroupRemovalLeavesTheGroupsText_AConferenceRemovalLeavesTheConferenceText()
        {
            (NonSpatialVoiceSessionEngine g, _) = GroupCall();
            RemoveBob(g);
            Assert.That(g.Get(Group).Find(Bob).InChat, Is.True, "the group's chat is the group's");
            Assert.That(g.Get(Group).ChatMembers(), Does.Contain(Bob));

            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome conf = e.StartAdhoc(Alice, UUID.Random(), RegionA, new[] { Bob, Carol });
            UUID id = conf.Session.SessionId;
            e.Accept(id, Bob, RegionA);
            Assert.That(e.Get(id).ChatMembers(), Does.Contain(Bob));

            e.Moderate(id, Alice, ModerationOp.Remove, Bob, true, "x", null);

            Assert.That(e.Get(id).Find(Bob).InChat, Is.False);
            Assert.That(e.Get(id).ChatMembers(), Does.Not.Contain(Bob));
            Assert.That(e.Accept(id, Bob, RegionA).Decision, Is.EqualTo(SessionOutcome.Removed));
            Assert.That(e.Invite(id, Alice, Bob).Decision, Is.EqualTo(SessionOutcome.Removed));
        }

        [Test]
        public void ARemovedInviteeIsNotRungAgain_AndTheirOpenInvitationLapses()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome conf = e.StartAdhoc(Alice, UUID.Random(), RegionA, new[] { Bob, Carol });
            UUID id = conf.Session.SessionId;
            Assert.That(AdhocVoiceInvite.Targets(e.Get(id), Alice), Is.EquivalentTo(new[] { Bob, Carol }), "precondition: both would be rung");
            e.Moderate(id, Alice, ModerationOp.Remove, Bob, true, "x", null);

            List<UUID> rung = AdhocVoiceInvite.Targets(e.Get(id), Alice);

            Assert.That(rung, Is.EqualTo(new[] { Carol }));
        }

        [Test]
        public void ACallBetweenTwoPeopleHasNoRemoval()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome p2p = e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Random(), RegionA);

            Assert.That(e.Moderate(p2p.Session.SessionId, Alice, ModerationOp.Remove, Bob, true, "x", null).Ok, Is.False);
        }

        [Test]
        public void SixtyFourAcceptsRacingOneRemoval_NeverLeaveTheMemberSeated()
        {
            for (int round = 0; round < 40; round++)
            {
                (NonSpatialVoiceSessionEngine e, _) = GroupCall();
                int refused = 0, accepted = 0;
                using ManualResetEventSlim go = new(false);
                List<Task> tasks = new();
                for (int i = 0; i < 64; i++)
                    tasks.Add(Task.Run(() =>
                    {
                        go.Wait();
                        SessionOutcome o = e.Accept(Group, Bob, RegionA, "vs-race");
                        if (o.Ok) Interlocked.Increment(ref accepted); else Interlocked.Increment(ref refused);
                    }));
                tasks.Add(Task.Run(() => { go.Wait(); RemoveBob(e); }));
                go.Set();
                Task.WaitAll(tasks.ToArray());

                // The member's seat is gone whatever the order: an accept after the removal is refused, and one before
                // it had its seat taken by the same mutation that recorded the removal.
                NonSpatialVoiceSession s = e.Get(Group);
                Assert.That(s.Find(Bob).HoldsSeat, Is.False, $"round {round}: accepted={accepted} refused={refused}");
                Assert.That(s.IsRemoved(Bob, DateTime.UtcNow), Is.True);
                Assert.That(e.Accept(Group, Bob, RegionA).Decision, Is.EqualTo(SessionOutcome.Removed));
            }
        }

        [Test]
        public void TheListOfRemovalsIsBounded()
        {
            (NonSpatialVoiceSessionEngine e, _) = GroupCall();
            for (int i = 0; i < NonSpatialVoiceSession.MaxModerationEntries; i++)
                Assert.That(e.Moderate(Group, Alice, ModerationOp.Remove, UUID.Random(), true, "x", null).Ok, Is.True);

            SessionOutcome over = e.Moderate(Group, Alice, ModerationOp.Remove, UUID.Random(), true, "x", null);

            Assert.That(over.Decision, Is.EqualTo(SessionOutcome.ModerationRefused));
        }

        // ---- the copy of the record ---------------------------------------------------------------------

        [Test]
        public void TheCopyCarriesRemovals_WithTheirEndTimes_AndAnOldCopyReadsAsNone()
        {
            (NonSpatialVoiceSessionEngine e, FakeClock clock) = GroupCall();
            RemoveBob(e, TimeSpan.FromMinutes(45), "loud");
            e.Moderate(Group, Alice, ModerationOp.Remove, Carol, true, "other", null);

            SessionData data = SessionWire.ToData(e.Get(Group));
            NonSpatialVoiceSession back = SessionWire.FromData(SessionReply.FromJson(new SessionReply { Session = data }.ToJson()).Session);

            Assert.That(back.IsRemoved(Bob, clock.Now), Is.True);
            Assert.That(back.RemovalOf(Bob).ExpiresUtc, Is.EqualTo(clock.Now + TimeSpan.FromMinutes(45)));
            Assert.That(back.RemovalOf(Bob).Reason, Is.EqualTo("loud"));
            Assert.That(back.RemovalOf(Bob).By, Is.EqualTo(Alice));
            Assert.That(back.RemovalOf(Carol).ExpiresUtc, Is.Null);
            Assert.That(back.ModerationGeneration, Is.EqualTo(e.Get(Group).ModerationGeneration));

            data.Removed = null;
            Assert.That(SessionWire.FromData(data).Removed, Is.Empty, "a copy from a service without removals reads as none");
        }

        // ---- through the grid service -----------------------------------------------------------------------

        [Test]
        public void ThroughTheGridService_ARemovalIsSeenByAnotherRegion_AtOnce_AndRefusesItsAccept()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            b.Accept(Group, Bob, RegionB, "vs-bob");

            SessionOutcome o = a.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, "x", null);

            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            NonSpatialVoiceSession seen = b.Get(Group, Bob);
            Assert.That(b.IsRemoved(seen, Bob), Is.True);
            Assert.That(b.Accept(Group, Bob, RegionB).Decision, Is.EqualTo(SessionOutcome.Removed));
            Assert.That(a.Moderate(Group, Alice, ModerationOp.Remove, Bob, false).Decision, Is.EqualTo(SessionOutcome.ModerationChanged));
            Assert.That(b.Accept(Group, Bob, RegionB).Ok, Is.True);
        }

        [Test]
        public void ThroughTheGridService_TheRegionHoldingTheSeatIsToldItWasRemoved_WhenItRenews()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            b.Accept(Group, Bob, RegionB, "vs-bob");
            Assert.That(b.HeldSeats(), Does.Contain(new SeatKey(Group, Bob)));

            Assert.That(b.RenewSeats(b.HeldSeats(), out IReadOnlyList<SeatKey> before), Is.True);
            Assert.That(before, Is.Empty, "nothing to tear down while the member is in the call");

            a.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, "x", null);
            Assert.That(b.RenewSeats(b.HeldSeats(), out IReadOnlyList<SeatKey> removed), Is.True);

            Assert.That(removed, Is.EqualTo(new[] { new SeatKey(Group, Bob) }));
            Assert.That(b.HeldSeats(), Does.Not.Contain(new SeatKey(Group, Bob)), "the region lets go of the seat it was told of");
        }

        [Test]
        public void TheRenewerCallsBackForEachSeatTheServiceSaysWasRemoved()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            GridNonSpatialSessions a = VoiceSessionServiceTests.NewRegion(service);
            GridNonSpatialSessions b = VoiceSessionServiceTests.NewRegion(service);
            a.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            b.Accept(Group, Bob, RegionB, "vs-bob");
            List<SeatKey> told = new();
            using GridSeatRenewer renewer = new(b, () => new[] { Alice, Bob }, null, seatRemoved: told.Add);

            renewer.RunOnce();
            Assert.That(told, Is.Empty);
            a.Moderate(Group, Alice, ModerationOp.Remove, Bob, true, "x", null);
            renewer.RunOnce();

            Assert.That(told, Is.EqualTo(new[] { new SeatKey(Group, Bob) }));
        }

        [Test]
        public void TheServiceAnswersAnUnknownOpWithBadRequest_AndAnOldServiceWouldDoTheSame()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            service.Start((int)NonSpatialSessionType.Group, Alice, Group, Group, RegionA, 0);

            SessionReply unknown = service.Moderate(Group, Alice, 99, Bob, true);
            SessionReply removal = service.Moderate(Group, Alice, (int)ModerationOp.Remove, Bob, true, "x", 10);

            Assert.That(unknown.Ok, Is.False);
            Assert.That(unknown.Decision, Is.EqualTo("refused-bad-request"));
            Assert.That(removal.Ok, Is.True);
            Assert.That(SessionWire.FromData(removal.Session).RemovalOf(Bob).ExpiresUtc, Is.Not.Null);
        }
    }
}
