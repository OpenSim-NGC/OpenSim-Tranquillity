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
    [TestFixture]
    public class PositionToleranceTests
    {
        private static readonly PositionSettings Defaults = new PositionSettings().Normalised();

        [Test]
        public void StandingAvatar_GetsTheSpeedFloorAndTheMargin()
        {
            PositionAllowance a = PositionTolerance.For(isChild: false, speedCmS: 0, engineMaxCmS: 100000, Defaults);
            Assert.That(a.SpeedCmS, Is.EqualTo(800));
            Assert.That(a.ToleranceCm, Is.EqualTo(500));
        }

        [Test]
        public void WalkingAvatar_StaysAtTheFloorBecauseTwiceItsSpeedIsBelowIt()
        {
            double walking = PositionTolerance.SpeedCmS(new Vector3(3.2f, 0, 0), null);
            PositionAllowance a = PositionTolerance.For(false, walking, 100000, Defaults);
            Assert.That(walking, Is.EqualTo(320).Within(0.5));
            Assert.That(a.SpeedCmS, Is.EqualTo(800));
        }

        [Test]
        public void RunningAvatar_GetsTwiceItsMeasuredSpeed()
        {
            double running = PositionTolerance.SpeedCmS(new Vector3(0, 6f, 0), null);
            PositionAllowance a = PositionTolerance.For(false, running, 100000, Defaults);
            Assert.That(a.SpeedCmS, Is.EqualTo(1200));
            Assert.That(a.ToleranceCm, Is.EqualTo(500));
        }

        [Test]
        public void FastVehicle_GetsAnAllowanceThatScalesWithItsSpeed()
        {
            double vehicle = PositionTolerance.SpeedCmS(Vector3.Zero, new Vector3(30f, 0, 0));
            PositionAllowance a = PositionTolerance.For(false, vehicle, 100000, Defaults);
            Assert.That(vehicle, Is.EqualTo(3000).Within(0.5));
            Assert.That(a.SpeedCmS, Is.EqualTo(6000));
        }

        [Test]
        public void SeatedAvatar_UsesTheFasterOfItsOwnVelocityAndTheSeatsVelocity()
        {
            Assert.That(PositionTolerance.SpeedCmS(new Vector3(0, 0, 2f), new Vector3(12f, 0, 0)), Is.EqualTo(1200).Within(0.5));
            Assert.That(PositionTolerance.SpeedCmS(new Vector3(0, 0, 20f), new Vector3(1f, 0, 0)), Is.EqualTo(2000).Within(0.5));
        }

        [Test]
        public void Allowance_IsCappedAtThePhysicsEnginesMaximum()
        {
            PositionAllowance a = PositionTolerance.For(false, speedCmS: 50000, engineMaxCmS: 4000, Defaults);
            Assert.That(a.SpeedCmS, Is.EqualTo(4000));
        }

        [Test]
        public void EngineThatDeclaresNoMaximum_FallsBackToTheConfiguredCeiling()
        {
            PositionAllowance a = PositionTolerance.For(false, speedCmS: 90000, engineMaxCmS: 0, Defaults);
            Assert.That(a.SpeedCmS, Is.EqualTo(PositionSettings.DefaultFallbackSpeedMaxCmS));
        }

        [Test]
        public void ChildAgentStanding_GetsTheMarginPlusWhatCoreMayLeaveUnreported()
        {
            // margin 500 + 10 m of movement before core reports + 2 s at the 800 cm/s floor = 3100 cm.
            PositionAllowance a = PositionTolerance.For(isChild: true, speedCmS: 0, engineMaxCmS: 100000, Defaults);
            Assert.That(a.SpeedCmS, Is.EqualTo(800));
            Assert.That(a.ToleranceCm, Is.EqualTo(500 + PositionTolerance.ChildMoveSlackCm + 1600));
        }

        [Test]
        public void ChildAgentMoving_GetsAWiderToleranceThanOneStanding()
        {
            PositionAllowance still = PositionTolerance.For(true, 0, 100000, Defaults);
            PositionAllowance moving = PositionTolerance.For(true, 1500, 100000, Defaults);
            Assert.That(moving.SpeedCmS, Is.EqualTo(3000));
            Assert.That(moving.ToleranceCm, Is.EqualTo(500 + 1000 + 6000));
            Assert.That(moving.ToleranceCm, Is.GreaterThan(still.ToleranceCm));
        }

        [Test]
        public void RootAgent_IsNeverGivenTheChildSlack()
        {
            Assert.That(PositionTolerance.For(false, 1500, 100000, Defaults).ToleranceCm, Is.EqualTo(Defaults.MarginCm));
        }

        [TestCase(double.NaN)]
        [TestCase(-5.0)]
        public void UnusableSpeed_IsTreatedAsStanding(double speed)
        {
            Assert.That(PositionTolerance.For(false, speed, 100000, Defaults).SpeedCmS, Is.EqualTo(Defaults.SpeedFloorCmS));
        }

        [Test]
        public void ConfiguredMarginAndFloor_AreHonoured()
        {
            var s = new PositionSettings { MarginCm = 900, SpeedFloorCmS = 1500 }.Normalised();
            PositionAllowance a = PositionTolerance.For(false, 0, 100000, s);
            Assert.That(a.SpeedCmS, Is.EqualTo(1500));
            Assert.That(a.ToleranceCm, Is.EqualTo(900));
        }
    }
}
