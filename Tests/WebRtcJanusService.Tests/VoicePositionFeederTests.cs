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

        [Fact(Timeout = 60000)]
        public void ANewParticipant_IsSentOnceInAFullSnapshot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            IReadOnlyList<PositionMessage> first = Build(f, 0, AllIn(7), Root(A, 100));

            Assert.Single(first);
            Assert.Equal(7, first[0].Room);
            Assert.True(first[0].Full);
            Assert.Single(first[0].Entries);
            Assert.Equal(A, first[0].Entries[0].Id);
            Assert.Equal(100, first[0].Entries[0].XCm);

            {
                var assertedValue = Build(f, 100, AllIn(7), Root(A, 100));
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing new, nothing moved");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnUnmovedParticipant_IsResentOnlyAtTheKeepAlive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 100));

            Assert.Empty(Build(f, 1999, AllIn(7), Root(A, 100)));
            IReadOnlyList<PositionMessage> keepAlive = Build(f, 2000, AllIn(7), Root(A, 100));
            Assert.Single(keepAlive);
            Assert.True(keepAlive[0].Full);
            Assert.Equal(A, keepAlive[0].Entries[0].Id);
        }

        [Fact(Timeout = 60000)]
        public void AParticipantThatMovedFarEnough_IsSentAlone_AndOneThatBarelyMovedIsNot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0), Root(B, 5000));

            {
                var assertedValue = Build(f, 100, AllIn(7), Root(A, 99), Root(B, 5000));
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "99 cm is under the 100 cm threshold");
            }

            IReadOnlyList<PositionMessage> moved = Build(f, 200, AllIn(7), Root(A, 100), Root(B, 5000));
            Assert.Single(moved);
            Assert.False(moved[0].Full);
            Assert.Equal(new[] { A }, moved[0].Entries.Select(e => e.Id));
        }

        [Fact(Timeout = 60000)]
        public void MovementIsMeasuredFromTheLastPointSent_NotFromTheLastTick()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0));
            Assert.Empty(Build(f, 100, AllIn(7), Root(A, 60)));
            {
                var assertedValue = Build(f, 200, AllIn(7), Root(A, 120));
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "120 cm from the point last sent");
            }
        }

        [Fact(Timeout = 60000)]
        public void ADepartedParticipant_IsAbsentFromTheNextFullSnapshot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0), Root(B, 5000));

            IReadOnlyList<PositionMessage> next = Build(f, 2000, AllIn(7), Root(A, 0));
            Assert.True(next[0].Full);
            Assert.Equal(new[] { A }, next[0].Entries.Select(e => e.Id));
        }

        [Fact(Timeout = 60000)]
        public void WhenTheLastParticipantLeaves_TheRoomGetsOneEmptyFullSnapshotAndThenNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Root(A, 0));

            IReadOnlyList<PositionMessage> gone = Build(f, 100, AllIn(7));
            Assert.Single(gone);
            Assert.True(gone[0].Full);
            Assert.Empty(gone[0].Entries);

            Assert.Empty(Build(f, 2000, AllIn(7)));
        }

        [Fact(Timeout = 60000)]
        public void TwoRooms_ProduceTwoRequests()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Func<UUID, int?> roomOf = id => id == A ? 7 : 8;
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, roomOf, Root(A, 0), Root(B, 100));

            Assert.Equal(new[] { 7, 8 }, msgs.Select(m => m.Room));
            Assert.Equal(A, msgs[0].Entries.Single().Id);
            Assert.Equal(B, msgs[1].Entries.Single().Id);
        }

        [Fact(Timeout = 60000)]
        public void AParticipantWithNoRoom_IsLeftOutAndCounted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, id => id == A ? 7 : (int?)null, Root(A, 0), Root(B, 100));

            Assert.Equal(new[] { A }, msgs.Single().Entries.Select(e => e.Id));
            Assert.Equal(1, f.Unplaced);
        }

        [Fact(Timeout = 60000)]
        public void SequenceNumbers_StartAboveTheBaseAndOnlyEverRisePerRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            long s1 = Build(f, 0, AllIn(7), Root(A, 0))[0].Seq;
            long s2 = Build(f, 2000, AllIn(7), Root(A, 0))[0].Seq;
            long s3 = Build(f, 2200, AllIn(7), Root(A, 500))[0].Seq;

            Assert.Equal(SeqBase + 1, s1);
            Assert.True(s2 > s1);
            Assert.True(s3 > s2);
        }

        [Fact(Timeout = 60000)]
        public void ARoomThatEmptiesAndRefills_KeepsCountingUp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            long first = Build(f, 0, AllIn(7), Root(A, 0))[0].Seq;
            long empty = Build(f, 100, AllIn(7))[0].Seq;
            long refilled = Build(f, 200, AllIn(7), Root(B, 0))[0].Seq;
            Assert.True(empty > first);
            Assert.True(refilled > empty);
        }

        [Fact(Timeout = 60000)]
        public void ALargeRoom_IsSplitSoNoRequestExceedsTheEntryLimit_AndOnlyTheFirstReplacesTheRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            PositionView[] crowd = Enumerable.Range(0, 130).Select(i => Root(UUID.Random(), i * 1000)).ToArray();
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, AllIn(7), crowd);

            Assert.Equal(2, msgs.Count());
            Assert.Equal(new[] { PositionBatchSerializer.MaxEntries, 130 - PositionBatchSerializer.MaxEntries }, msgs.Select(m => m.Entries.Count));
            Assert.Equal(new[] { true, false }, msgs.Select(m => m.Full));
            Assert.True(msgs[1].Seq > msgs[0].Seq);
        }

        [Fact(Timeout = 60000)]
        public void ARootAgent_CarriesTheSimsOwnSpeed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            PositionEntry e = Build(f, 0, AllIn(7), Root(A, 0, speed: 3000))[0].Entries[0];
            Assert.Equal(6000, e.SpeedCmS);
            Assert.Equal(500, e.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void TheEnginesMaximum_CapsTheAllowanceThatIsSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            PositionEntry e = f.Build(0, new[] { Root(A, 0, speed: 90000) }, AllIn(7), 4000)[0].Entries[0];
            Assert.Equal(4000, e.SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void AChildThatHasNotMoved_IsGivenTheStandingChildAllowance()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            PositionEntry e = Build(f, 0, AllIn(7), Child(A, -4000))[0].Entries[0];
            Assert.Equal(800, e.SpeedCmS);
            Assert.Equal(500 + PositionTolerance.ChildMoveSlackCm + 1600, e.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void AChildThatMoved10mInTwoSeconds_HasItsSpeedEstimatedFromThoseTwoReports()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Child(A, -4000));
            // 1000 cm in 2000 ms = 500 cm/s, doubled = 1000 cm/s.
            IReadOnlyList<PositionMessage> moved = Build(f, 2000, AllIn(7), Child(A, -3000));

            PositionEntry e = moved.Single().Entries.Single();
            Assert.Equal(1000, e.SpeedCmS);
            Assert.Equal(500 + 1000 + 2000, e.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void AChildThatStopsReporting_IsTreatedAsStandingAgainAfterTwiceTheReportInterval()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            Build(f, 0, AllIn(7), Child(A, -4000));
            Build(f, 2000, AllIn(7), Child(A, -3000));

            // Silent for just over 2 x 2 s: the keep-alive snapshot now carries the floor again.
            PositionEntry e = Build(f, 6001, AllIn(7), Child(A, -3000)).Single().Entries.Single();
            Assert.Equal(800, e.SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void AMovingChildAndARootAgent_AreTreatedDifferentlyInTheSameRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoicePositionFeeder f = NewFeeder();
            IReadOnlyList<PositionMessage> msgs = Build(f, 0, AllIn(7), Root(A, 0), Child(B, -4000));
            PositionEntry root = msgs[0].Entries.Single(x => x.Id == A);
            PositionEntry child = msgs[0].Entries.Single(x => x.Id == B);
            Assert.True(child.ToleranceCm > root.ToleranceCm);
        }
    }
}
