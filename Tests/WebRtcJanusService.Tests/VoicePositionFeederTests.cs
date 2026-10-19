/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the position feed decides to send each tick: a new participant once, an unmoved one only at the
 * keep-alive, a moved one when it has moved far enough, a departed one not at all, and one request per
 * room. A child agent's speed is estimated from its reports, since the region does not keep its velocity.
 */

using OpenMetaverse;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoicePositionFeederTests
    {
        private const long SeqBase = 1000;
        private static readonly UUID A = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly UUID B = new UUID("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private static readonly UUID C = new UUID("cccccccc-cccc-cccc-cccc-cccccccccccc");

        private static PositionView Root(UUID id, int x, int y = 0, int z = 0, double speed = 0)
            => new PositionView(id, false, x, y, z, speed);

        private static PositionView Child(UUID id, int x, int y = 0, int z = 0)
            => new PositionView(id, true, x, y, z, 0);

        private static VoicePositionFeeder NewFeeder(PositionSettings settings = null)
            => new VoicePositionFeeder(settings ?? new PositionSettings(), SeqBase);

        private static Func<UUID, int?> AllIn(int room) => _ => room;

        private static IReadOnlyList<PositionMessage> Build(VoicePositionFeeder f, long now, Func<UUID, int?> roomOf, params PositionView[] views)
            => f.Build(now, views, roomOf, 100000);

        [Test]
        public void ANewParticipant_IsSentOnceInAFullSnapshot()
        {
            VoicePositionFeeder f = NewFeeder();
            IReadOnlyList<PositionMessage> first = Build(f, 0, AllIn(7), Root(A, 100));

            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(first[0].Room, Is.EqualTo(7));
            Assert.That(first[0].Full, Is.True);
            Assert.That(first[0].Entries, Has.Count.EqualTo(1));
            Assert.That(first[0].Entries[0].Id, Is.EqualTo(A));
            Assert.That(first[0].Entries[0].XCm, Is.EqualTo(100));

            Assert.That(Build(f, 100, AllIn(7), Root(A, 100)), Is.Empty, "nothing new, nothing moved");
        }

        [Test]
        public void AnUnmovedParticipant_IsResentOnlyAtTheKeepAlive()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 100));

            Assert.That(Build(f, 999, AllIn(7), Root(A, 100)), Is.Empty);
            IReadOnlyList<PositionMessage> keepAlive = Build(f, 1000, AllIn(7), Root(A, 100));
            Assert.That(keepAlive, Has.Count.EqualTo(1));
            Assert.That(keepAlive[0].Full, Is.True);
            Assert.That(keepAlive[0].Entries[0].Id, Is.EqualTo(A));
        }

        [Test]
        public void AParticipantThatMovedFarEnough_IsSentAlone_AndOneThatBarelyMovedIsNot()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0), Root(B, 5000));

            Assert.That(Build(f, 100, AllIn(7), Root(A, 99), Root(B, 5000)), Is.Empty, "99 cm is under the 100 cm threshold");

            IReadOnlyList<PositionMessage> moved = Build(f, 200, AllIn(7), Root(A, 100), Root(B, 5000));
            Assert.That(moved, Has.Count.EqualTo(1));
            Assert.That(moved[0].Full, Is.False);
            Assert.That(moved[0].Entries.Select(e => e.Id), Is.EqualTo(new[] { A }));
        }

        [Test]
        public void MovementIsMeasuredFromTheLastPointSent_NotFromTheLastTick()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0));
            Assert.That(Build(f, 100, AllIn(7), Root(A, 60)), Is.Empty);
            Assert.That(Build(f, 200, AllIn(7), Root(A, 120)), Has.Count.EqualTo(1), "120 cm from the point last sent");
        }

        [Test]
        public void ADepartedParticipant_IsAbsentFromTheNextFullSnapshot()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0), Root(B, 5000));

            IReadOnlyList<PositionMessage> next = Build(f, 1000, AllIn(7), Root(A, 0));
            Assert.That(next[0].Full, Is.True);
            Assert.That(next[0].Entries.Select(e => e.Id), Is.EqualTo(new[] { A }));
        }

        [Test]
        public void WhenTheLastParticipantLeaves_TheRoomGetsOneEmptyFullSnapshotAndThenNothing()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0));

            IReadOnlyList<PositionMessage> gone = Build(f, 100, AllIn(7));
            Assert.That(gone, Has.Count.EqualTo(1));
            Assert.That(gone[0].Full, Is.True);
            Assert.That(gone[0].Entries, Is.Empty);

            Assert.That(Build(f, 2000, AllIn(7)), Is.Empty);
        }

        [Test]
        public void TwoRooms_ProduceTwoRequests()
        {
            VoicePositionFeeder f = NewFeeder();
            Func<UUID, int?> roomOf = id => id == A ? 7 : 8;
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, roomOf, Root(A, 0), Root(B, 100));

            Assert.That(msgs.Select(m => m.Room), Is.EqualTo(new[] { 7, 8 }));
            Assert.That(msgs[0].Entries.Single().Id, Is.EqualTo(A));
            Assert.That(msgs[1].Entries.Single().Id, Is.EqualTo(B));
        }

        [Test]
        public void AParticipantWithNoRoom_IsLeftOutAndCounted()
        {
            VoicePositionFeeder f = NewFeeder();
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, id => id == A ? 7 : (int?)null, Root(A, 0), Root(B, 100));

            Assert.That(msgs.Single().Entries.Select(e => e.Id), Is.EqualTo(new[] { A }));
            Assert.That(f.Unplaced, Is.EqualTo(1));
        }

        [Test]
        public void SequenceNumbers_StartAboveTheBaseAndOnlyEverRisePerRoom()
        {
            VoicePositionFeeder f = NewFeeder();
            long s1 = Build(f, 0, AllIn(7), Root(A, 0))[0].Seq;
            long s2 = Build(f, 1000, AllIn(7), Root(A, 0))[0].Seq;
            long s3 = Build(f, 1200, AllIn(7), Root(A, 500))[0].Seq;

            Assert.That(s1, Is.EqualTo(SeqBase + 1));
            Assert.That(s2, Is.GreaterThan(s1));
            Assert.That(s3, Is.GreaterThan(s2));
        }

        [Test]
        public void ARoomThatEmptiesAndRefills_KeepsCountingUp()
        {
            VoicePositionFeeder f = NewFeeder();
            long first = Build(f, 0, AllIn(7), Root(A, 0))[0].Seq;
            long empty = Build(f, 100, AllIn(7))[0].Seq;
            long refilled = Build(f, 200, AllIn(7), Root(B, 0))[0].Seq;
            Assert.That(empty, Is.GreaterThan(first));
            Assert.That(refilled, Is.GreaterThan(empty));
        }

        [Test]
        public void ALargeRoom_IsSplitSoNoRequestExceedsTheEntryLimit_AndOnlyTheFirstReplacesTheRoom()
        {
            VoicePositionFeeder f = NewFeeder();
            PositionView[] crowd = Enumerable.Range(0, 130).Select(i => Root(UUID.Random(), i * 1000)).ToArray();
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, AllIn(7), crowd);

            Assert.That(msgs, Has.Count.EqualTo(2));
            Assert.That(msgs.Select(m => m.Entries.Count), Is.EqualTo(new[] { PositionBatchSerializer.MaxEntries, 130 - PositionBatchSerializer.MaxEntries }));
            Assert.That(msgs.Select(m => m.Full), Is.EqualTo(new[] { true, false }));
            Assert.That(msgs[1].Seq, Is.GreaterThan(msgs[0].Seq));
        }

        [Test]
        public void ARootAgent_CarriesTheSimsOwnSpeed()
        {
            VoicePositionFeeder f = NewFeeder();
            PositionEntry e = Build(f, 0, AllIn(7), Root(A, 0, speed: 3000))[0].Entries[0];
            Assert.That(e.SpeedCmS, Is.EqualTo(6000));
            Assert.That(e.ToleranceCm, Is.EqualTo(500));
        }

        [Test]
        public void TheEnginesMaximum_CapsTheAllowanceThatIsSent()
        {
            VoicePositionFeeder f = NewFeeder();
            PositionEntry e = f.Build(0, new[] { Root(A, 0, speed: 90000) }, AllIn(7), 4000)[0].Entries[0];
            Assert.That(e.SpeedCmS, Is.EqualTo(4000));
        }

        [Test]
        public void AChildThatHasNotMoved_IsGivenTheStandingChildAllowance()
        {
            VoicePositionFeeder f = NewFeeder();
            PositionEntry e = Build(f, 0, AllIn(7), Child(A, -4000))[0].Entries[0];
            Assert.That(e.SpeedCmS, Is.EqualTo(800));
            Assert.That(e.ToleranceCm, Is.EqualTo(500 + PositionTolerance.ChildMoveSlackCm + 1600));
        }

        [Test]
        public void AChildThatMoved10mInTwoSeconds_HasItsSpeedEstimatedFromThoseTwoReports()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Child(A, -4000));
            // 1000 cm in 2000 ms = 500 cm/s, doubled = 1000 cm/s.
            IReadOnlyList<PositionMessage> moved = Build(f, 2000, AllIn(7), Child(A, -3000));

            PositionEntry e = moved.Single().Entries.Single();
            Assert.That(e.SpeedCmS, Is.EqualTo(1000));
            Assert.That(e.ToleranceCm, Is.EqualTo(500 + 1000 + 2000));
        }

        [Test]
        public void AChildThatStopsReporting_IsTreatedAsStandingAgainAfterTwiceTheReportInterval()
        {
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Child(A, -4000));
            Build(f, 2000, AllIn(7), Child(A, -3000));

            // Silent for just over 2 x 2 s: the keep-alive snapshot now carries the floor again.
            PositionEntry e = Build(f, 6001, AllIn(7), Child(A, -3000)).Single().Entries.Single();
            Assert.That(e.SpeedCmS, Is.EqualTo(800));
        }

        [Test]
        public void AMovingChildAndARootAgent_AreTreatedDifferentlyInTheSameRoom()
        {
            VoicePositionFeeder f = NewFeeder();
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, AllIn(7), Root(A, 0), Child(B, -4000));
            PositionEntry root = msgs[0].Entries.Single(x => x.Id == A);
            PositionEntry child = msgs[0].Entries.Single(x => x.Id == B);
            Assert.That(child.ToleranceCm, Is.GreaterThan(root.ToleranceCm));
        }
    }
}
