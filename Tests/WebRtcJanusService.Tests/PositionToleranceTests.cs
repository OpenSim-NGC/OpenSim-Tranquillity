/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The speed allowance and tolerance the sim sends with each position: a standing avatar, a walking and
 * a running one, one in a fast vehicle, one seated on a moving object, and a child agent in a
 * neighbouring region (whose position the region only gets in coarse steps).
 */

using OpenMetaverse;

namespace osWebRtcVoice.Tests
{
    public class PositionToleranceTests
    {
        private static readonly PositionSettings Defaults = new PositionSettings().Normalised();

        [Fact(Timeout = 60000)]
        public void StandingAvatar_GetsTheSpeedFloorAndTheMargin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PositionAllowance a = PositionTolerance.For(isChild: false, speedCmS: 0, engineMaxCmS: 100000, Defaults);
            Assert.Equal(800, a.SpeedCmS);
            Assert.Equal(500, a.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void WalkingAvatar_StaysAtTheFloorBecauseTwiceItsSpeedIsBelowIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            double walking = PositionTolerance.SpeedCmS(new Vector3(3.2f, 0, 0), null);
            PositionAllowance a = PositionTolerance.For(false, walking, 100000, Defaults);
            Assert.InRange(walking, (320) - (0.5), (320) + (0.5));
            Assert.Equal(800, a.SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void RunningAvatar_GetsTwiceItsMeasuredSpeed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            double running = PositionTolerance.SpeedCmS(new Vector3(0, 6f, 0), null);
            PositionAllowance a = PositionTolerance.For(false, running, 100000, Defaults);
            Assert.Equal(1200, a.SpeedCmS);
            Assert.Equal(500, a.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void FastVehicle_GetsAnAllowanceThatScalesWithItsSpeed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            double vehicle = PositionTolerance.SpeedCmS(Vector3.Zero, new Vector3(30f, 0, 0));
            PositionAllowance a = PositionTolerance.For(false, vehicle, 100000, Defaults);
            Assert.InRange(vehicle, (3000) - (0.5), (3000) + (0.5));
            Assert.Equal(6000, a.SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void SeatedAvatar_UsesTheFasterOfItsOwnVelocityAndTheSeatsVelocity()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.InRange(PositionTolerance.SpeedCmS(new Vector3(0, 0, 2f), new Vector3(12f, 0, 0)), (1200) - (0.5), (1200) + (0.5));
            Assert.InRange(PositionTolerance.SpeedCmS(new Vector3(0, 0, 20f), new Vector3(1f, 0, 0)), (2000) - (0.5), (2000) + (0.5));
        }

        [Fact(Timeout = 60000)]
        public void Allowance_IsCappedAtThePhysicsEnginesMaximum()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PositionAllowance a = PositionTolerance.For(false, speedCmS: 50000, engineMaxCmS: 4000, Defaults);
            Assert.Equal(4000, a.SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void EngineThatDeclaresNoMaximum_FallsBackToTheConfiguredCeiling()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PositionAllowance a = PositionTolerance.For(false, speedCmS: 90000, engineMaxCmS: 0, Defaults);
            Assert.Equal(PositionSettings.DefaultFallbackSpeedMaxCmS, a.SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void ChildAgentStanding_GetsTheMarginPlusWhatCoreMayLeaveUnreported()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // margin 500 + 10 m of movement before core reports + 2 s at the 800 cm/s floor = 3100 cm.
            PositionAllowance a = PositionTolerance.For(isChild: true, speedCmS: 0, engineMaxCmS: 100000, Defaults);
            Assert.Equal(800, a.SpeedCmS);
            Assert.Equal(500 + PositionTolerance.ChildMoveSlackCm + 1600, a.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void ChildAgentMoving_GetsAWiderToleranceThanOneStanding()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PositionAllowance still = PositionTolerance.For(true, 0, 100000, Defaults);
            PositionAllowance moving = PositionTolerance.For(true, 1500, 100000, Defaults);
            Assert.Equal(3000, moving.SpeedCmS);
            Assert.Equal(500 + 1000 + 6000, moving.ToleranceCm);
            Assert.True(moving.ToleranceCm > still.ToleranceCm);
        }

        [Fact(Timeout = 60000)]
        public void RootAgent_IsNeverGivenTheChildSlack()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Defaults.MarginCm, PositionTolerance.For(false, 1500, 100000, Defaults).ToleranceCm);
        }

        [InlineData(double.NaN)]
        [InlineData(-5.0)]
        [Theory(Timeout = 60000)]
        public void UnusableSpeed_IsTreatedAsStanding(double speed)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Defaults.SpeedFloorCmS, PositionTolerance.For(false, speed, 100000, Defaults).SpeedCmS);
        }

        [Fact(Timeout = 60000)]
        public void ConfiguredMarginAndFloor_AreHonoured()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new PositionSettings { MarginCm = 900, SpeedFloorCmS = 1500 }.Normalised();
            PositionAllowance a = PositionTolerance.For(false, 0, 100000, s);
            Assert.Equal(1500, a.SpeedCmS);
            Assert.Equal(900, a.ToleranceCm);
        }
    }
}
