/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A vehicle record can carry the running state of its two motors (the decay clock, the direction and the ramp velocity)
    /// across a region border. The state is written only when present, so every record saved outside a crossing is the text
    /// an older build wrote, and it is forgotten once a physics engine has taken it up.
    /// </summary>
    public class SOPVehicleRunningStateTests : OpenSimTestCase
    {
        private sealed class CountingActor : NullPhysicsActor
        {
            public int SetVehicleCalls;
            public override void SetVehicle(object pvdata) => SetVehicleCalls++;
        }

        private static SOPVehicle CarWithRunningMotors()
        {
            var v = new SOPVehicle();
            v.ProcessTypeChange(Vehicle.TYPE_CAR);
            v.vd.m_linearMotorRunAge = 1.5f;
            v.vd.m_linearMotorRunDirection = new Vector3(1f, 0f, 0f);
            v.vd.m_linearMotorRunTarget = new Vector3(4f, 0.5f, 0f);
            v.vd.m_angularMotorRunAge = 0.25f;
            v.vd.m_angularMotorRunDirection = new Vector3(0f, 0f, 1f);
            v.vd.m_angularMotorRunTarget = new Vector3(0f, 0f, 0.75f);
            return v;
        }

        [Fact]
        public void ARecordWithNoRunningStateIsWrittenAsBefore()
        {
            TestHelpers.InMethod();
            var v = new SOPVehicle();
            v.ProcessTypeChange(Vehicle.TYPE_CAR);

            string xml = v.ToXml2();

            Assert.DoesNotMatch("LMRAGE|LMRDIR|LMRTGT|AMRAGE|AMRDIR|AMRTGT", xml);
            Assert.Null(SOPVehicle.FromXml2(xml).vd.m_linearMotorRunAge);
        }

        [Fact]
        public void TheRunningStateOfBothMotorsRoundTrips()
        {
            TestHelpers.InMethod();
            SOPVehicle v = CarWithRunningMotors();

            SOPVehicle back = SOPVehicle.FromXml2(v.ToXml2());

            Assert.Equal(v.vd, back.vd);
            Assert.Equal(1.5f, back.vd.m_linearMotorRunAge);
            Assert.Equal(new Vector3(4f, 0.5f, 0f), back.vd.m_linearMotorRunTarget);
            Assert.Equal(new Vector3(0f, 0f, 1f), back.vd.m_angularMotorRunDirection);
        }

        [Fact]
        public void OnlyTheMotorThatIsRunningIsWritten()
        {
            TestHelpers.InMethod();
            SOPVehicle v = CarWithRunningMotors();
            v.vd.m_angularMotorRunAge = null;
            v.vd.m_angularMotorRunDirection = null;
            v.vd.m_angularMotorRunTarget = null;

            string xml = v.ToXml2();

            Assert.Matches("LMRAGE", xml);
            Assert.DoesNotMatch("AMRAGE|AMRDIR|AMRTGT", xml);
            Assert.Null(SOPVehicle.FromXml2(xml).vd.m_angularMotorRunTarget);
        }

        [Fact]
        public void HandingTheRecordToAnEngineForgetsTheRunningState()
        {
            TestHelpers.InMethod();
            SOPVehicle v = CarWithRunningMotors();
            var actor = new CountingActor();

            v.SetVehicle(actor);

            Assert.Equal(1, actor.SetVehicleCalls);
            Assert.Null(v.vd.m_linearMotorRunAge);
            Assert.Null(v.vd.m_linearMotorRunTarget);
            Assert.Null(v.vd.m_angularMotorRunDirection);
            Assert.DoesNotMatch("LMRAGE|AMRAGE", v.ToXml2());
        }

        [Fact]
        public void AFailedCrossingForgetsTheRunningState()
        {
            TestHelpers.InMethod();
            SOPVehicle v = CarWithRunningMotors();

            v.ClearRunningState();

            Assert.Null(v.vd.m_linearMotorRunDirection);
            Assert.Null(v.vd.m_angularMotorRunAge);
        }
    }
}
