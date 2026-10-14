/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The saved vehicle record (SOPVehicle) keeps a value for each axis of the four motor timescales (the linear and the
    /// angular motor's timescale and decay timescale, which llSetVehicleVectorParam takes as vectors), beside the single value
    /// each has always had, and keeps a decay timescale as it was sent (a physics engine applies its own limit). A record
    /// that holds no per-axis value is written and read exactly as before, so records saved by an older build load unchanged
    /// and an older build loads these records (it skips the elements it does not know).
    /// </summary>
    public class SOPVehicleMotorAxesTests : OpenSimTestCase
    {
        private const float Tolerance = 1e-5f;

        private static readonly Vector3 LinearTs = new(0.5f, 2f, 8f);
        private static readonly Vector3 LinearDecay = new(600f, 30f, 5f);
        private static readonly Vector3 AngularTs = new(0.1f, 0.4f, 3f);
        private static readonly Vector3 AngularDecay = new(1000f, 0.5f, 250f);

        private static SOPVehicle Record(Vehicle type)
        {
            var v = new SOPVehicle();
            v.ProcessTypeChange(type);
            return v;
        }

        private static SOPVehicle WithAxes()
        {
            SOPVehicle v = Record(Vehicle.TYPE_CAR);
            v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, LinearTs);
            v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, LinearDecay);
            v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, AngularTs);
            v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, AngularDecay);
            return v;
        }

        private static void Same(Vector3 expected, Vector3? actual)
        {
            Assert.True(actual.HasValue, "no per-axis value");
            Assert.Equal(expected, actual.Value);
        }

        private static SOPVehicle RoundTrip(SOPVehicle v) => SOPVehicle.FromXml2(v.ToXml2());

        private static readonly Regex AxisElements = new(
            "<(LMTIMEV|LMDTIMEV|AMTIMEV|AMDTIMEV)>.*?</\\1>", RegexOptions.Compiled | RegexOptions.Singleline);

        private static void AssertAxes(VehicleData vd)
        {
            Same(LinearTs, vd.m_linearMotorTimescaleAxes);
            Same(LinearDecay, vd.m_linearMotorDecayTimescaleAxes);
            Same(AngularTs, vd.m_angularMotorTimescaleAxes);
            Same(AngularDecay, vd.m_angularMotorDecayTimescaleAxes);
        }

        // ---- a record with no per-axis value is what it always was ----

        [Theory]
        [InlineData(Vehicle.TYPE_NONE)]
        [InlineData(Vehicle.TYPE_CAR)]
        [InlineData(Vehicle.TYPE_AIRPLANE)]
        [InlineData(Vehicle.TYPE_BALLOON)]
        [InlineData(Vehicle.TYPE_MOTORCYCLE)]
        public void AnOldRecordRoundTripsUnchanged(Vehicle type)
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(type);
            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 3f);
            v.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 7f);

            string xml = v.ToXml2();
            SOPVehicle back = SOPVehicle.FromXml2(xml);

            Assert.DoesNotMatch("LMTIMEV|LMDTIMEV|AMTIMEV|AMDTIMEV", xml);   // the text is the one an older build wrote
            Assert.Equal(v.vd, back.vd);
            Assert.Null(back.vd.m_linearMotorTimescaleAxes);
            Assert.Null(back.vd.m_angularMotorDecayTimescaleAxes);
            Assert.Equal(xml, back.ToXml2());
        }

        // The record as an older build wrote it, element by element (this text has none of the elements added since, and one
        // of the older builds' 120 s decay).
        private const string OldBuildRecord =
            "<Vehicle><TYPE>2</TYPE><FLAGS>1050</FLAGS>" +
            "<LMDIR><X>0</X><Y>0</Y><Z>0</Z></LMDIR><LMFTIME><X>100</X><Y>2</Y><Z>1000</Z></LMFTIME>" +
            "<LMDTIME>60</LMDTIME><LMTIME>1</LMTIME><LMOFF><X>0</X><Y>0</Y><Z>0</Z></LMOFF>" +
            "<AMDIR><X>0</X><Y>0</Y><Z>0</Z></AMDIR><AMTIME>1</AMTIME><AMDTIME>120</AMDTIME>" +
            "<AMFTIME><X>1000</X><Y>1000</Y><Z>1000</Z></AMFTIME>" +
            "<ADEFF>0</ADEFF><ADTIME>10</ADTIME><LDEFF>1</LDEFF><LDTIME>2</LDTIME>" +
            "<BEFF>-0.2</BEFF><BMIX>1</BMIX><BTIME>1</BTIME><HHEI>0</HHEI><HEFF>0</HEFF><HTIME>1000</HTIME><VBUO>0</VBUO>" +
            "<VAEFF>1</VAEFF><VATIME>10</VATIME>" +
            "<REF_FRAME><X>0</X><Y>0</Y><Z>0</Z><W>1</W></REF_FRAME></Vehicle>";

        [Fact]
        public void XmlWrittenByAnOldBuildReadsAsItDid()
        {
            TestHelpers.InMethod();

            SOPVehicle v = SOPVehicle.FromXml2(OldBuildRecord);

            Assert.NotNull(v);
            VehicleData vd = v.vd;
            Assert.Equal(Vehicle.TYPE_CAR, vd.m_type);
            Assert.Equal(1f, vd.m_linearMotorTimescale);
            Assert.Equal(60f, vd.m_linearMotorDecayTimescale);
            Assert.Equal(1f, vd.m_angularMotorTimescale);
            Assert.Equal(120f, vd.m_angularMotorDecayTimescale);
            Assert.Equal(new Vector3(100f, 2f, 1000f), vd.m_linearFrictionTimescale);
            // Nothing per axis: each axis is the single value
            Assert.Null(vd.m_linearMotorTimescaleAxes);
            Assert.Null(vd.m_linearMotorDecayTimescaleAxes);
            Assert.Null(vd.m_angularMotorTimescaleAxes);
            Assert.Null(vd.m_angularMotorDecayTimescaleAxes);
        }

        [Fact]
        public void ARecordOfAnOldBuildInAnObjectLoadsAsItDid()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = SOPVehicle.FromXml2(OldBuildRecord);
            string xml = SceneObjectSerializer.ToXml2Format(so);

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(xml);

            Assert.Equal(so.RootPart.VehicleParams.vd, back.RootPart.VehicleParams.vd);
            Assert.Null(back.RootPart.VehicleParams.vd.m_linearMotorTimescaleAxes);
        }

        [Fact]
        public void ABuildThatDoesNotKnowTheAxesReadsTheSingleValuesItAlwaysDid()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithAxes();
            // What an older build sees: the same text without the four elements it skips.
            string older = AxisElements.Replace(v.ToXml2(), string.Empty);
            Assert.DoesNotMatch("LMTIMEV|LMDTIMEV|AMTIMEV|AMDTIMEV", older);

            SOPVehicle loaded = SOPVehicle.FromXml2(older);

            Assert.NotNull(loaded);
            Assert.Equal(v.vd.m_linearMotorTimescale, loaded.vd.m_linearMotorTimescale);
            Assert.Equal(v.vd.m_linearMotorDecayTimescale, loaded.vd.m_linearMotorDecayTimescale);
            Assert.Equal(v.vd.m_angularMotorTimescale, loaded.vd.m_angularMotorTimescale);
            Assert.Equal(v.vd.m_angularMotorDecayTimescale, loaded.vd.m_angularMotorDecayTimescale);
        }

        // ---- the single values are not touched by a per-axis value ----

        [Fact]
        public void APerAxisValueLeavesTheSingleValueAsItWas()
        {
            TestHelpers.InMethod();
            SOPVehicle plain = Record(Vehicle.TYPE_CAR);

            SOPVehicle v = WithAxes();

            Assert.Equal(plain.vd.m_linearMotorTimescale, v.vd.m_linearMotorTimescale);
            Assert.Equal(plain.vd.m_linearMotorDecayTimescale, v.vd.m_linearMotorDecayTimescale);
            Assert.Equal(plain.vd.m_angularMotorTimescale, v.vd.m_angularMotorTimescale);
            Assert.Equal(plain.vd.m_angularMotorDecayTimescale, v.vd.m_angularMotorDecayTimescale);
            AssertAxes(v.vd);
        }

        [Fact]
        public void ASingleValueSetsAllThreeAxesAndSoReplacesAPerAxisOne()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithAxes();

            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 4f);
            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 9f);
            v.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 5f);
            v.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 6f);

            Assert.Null(v.vd.m_linearMotorTimescaleAxes);
            Assert.Null(v.vd.m_linearMotorDecayTimescaleAxes);
            Assert.Null(v.vd.m_angularMotorTimescaleAxes);
            Assert.Null(v.vd.m_angularMotorDecayTimescaleAxes);
            Assert.Equal(4f, v.vd.m_linearMotorTimescale);
            Assert.Equal(6f, v.vd.m_angularMotorDecayTimescale);
        }

        [Fact]
        public void ATypeChangeStartsWithNoPerAxisValues()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithAxes();

            v.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);

            Assert.Null(v.vd.m_linearMotorTimescaleAxes);
            Assert.Null(v.vd.m_angularMotorDecayTimescaleAxes);
        }

        [Fact]
        public void EachAxisIsHeldToAtLeastOneStepAndANotANumberIsNotStored()
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);

            v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, new Vector3(0.001f, -3f, 5000f));
            Same(new Vector3(0.01f, 0.01f, 5000f), v.vd.m_linearMotorTimescaleAxes);

            v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, new Vector3(float.NaN, 1f, 1f));
            v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, new Vector3(1f, float.PositiveInfinity, 1f));
            Same(new Vector3(0.01f, 0.01f, 5000f), v.vd.m_linearMotorTimescaleAxes);
            Assert.Null(v.vd.m_angularMotorTimescaleAxes);
        }

        // ---- a decay is kept as sent ----

        [Fact]
        public void ADecayOf600IsKeptAsSent()
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);

            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 600f);
            v.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 600f);

            Assert.Equal(600f, v.vd.m_linearMotorDecayTimescale);
            Assert.Equal(600f, v.vd.m_angularMotorDecayTimescale);
            SOPVehicle back = RoundTrip(v);
            Assert.Equal(600f, back.vd.m_linearMotorDecayTimescale);
            Assert.Equal(600f, back.vd.m_angularMotorDecayTimescale);
        }

        [Fact]
        public void ADecayBelowOneStepIsStillHeldToOneStep()
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);

            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0f);

            Assert.Equal(0.01f, v.vd.m_linearMotorDecayTimescale);
        }

        // ---- kept through every way a vehicle is saved and moved ----

        [Fact]
        public void TheRecordKeepsThePerAxisValuesAndTheDecay()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithAxes();
            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 600f);   // the single decay, as the DB column holds it

            SOPVehicle back = RoundTrip(v);

            Assert.Equal(v.vd, back.vd);
            Assert.Equal(600f, back.vd.m_linearMotorDecayTimescale);
            // A single value after the vectors replaced the linear decay's axes; the other three are still there
            Assert.Null(back.vd.m_linearMotorDecayTimescaleAxes);
            Same(LinearTs, back.vd.m_linearMotorTimescaleAxes);
            Same(AngularDecay, back.vd.m_angularMotorDecayTimescaleAxes);
        }

        [Fact]
        public void ARegionSaveKeepsThePerAxisValues()
        {
            // The region's database holds the record as its XML text in one column (ToXml2 on save, FromXml2 on load).
            TestHelpers.InMethod();
            SOPVehicle v = WithAxes();

            string column = v.ToXml2();
            SOPVehicle loaded = SOPVehicle.FromXml2(column);

            AssertAxes(loaded.vd);
            Assert.Equal(v.vd, loaded.vd);
        }

        [Fact]
        public void AnObjectInInventoryKeepsThePerAxisValues()
        {
            // Taking an object into inventory saves it with ToOriginalXmlFormat; rezzing it reads it back.
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithAxes();

            SceneObjectGroup rezzed = SceneObjectSerializer.FromOriginalXmlFormat(SceneObjectSerializer.ToOriginalXmlFormat(so));

            Assert.NotNull(rezzed.RootPart.VehicleParams);
            AssertAxes(rezzed.RootPart.VehicleParams.vd);
            Assert.Equal(so.RootPart.VehicleParams.vd, rezzed.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void AnObjectSavedWithItsRegionKeepsThePerAxisValues()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithAxes();

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(so));

            AssertAxes(back.RootPart.VehicleParams.vd);
            Assert.Equal(so.RootPart.VehicleParams.vd, back.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void AnObjectCrossingIntoAnotherRegionKeepsThePerAxisValues()
        {
            // A crossing sends the object as its XML2 text (SceneObjectGroup.ToXml2) and the other region reads it
            // (SceneObjectSerializer.FromXml2Format).
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithAxes();
            so.RootPart.VehicleParams.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 600f);

            SceneObjectGroup arrived = SceneObjectSerializer.FromXml2Format(so.ToXml2());

            VehicleData vd = arrived.RootPart.VehicleParams.vd;
            Same(LinearTs, vd.m_linearMotorTimescaleAxes);
            Same(LinearDecay, vd.m_linearMotorDecayTimescaleAxes);
            Same(AngularTs, vd.m_angularMotorTimescaleAxes);
            Assert.Null(vd.m_angularMotorDecayTimescaleAxes);   // replaced by the single 600 s
            Assert.Equal(600f, vd.m_angularMotorDecayTimescale);
        }

        [Fact]
        public void ACopiedObjectHasThePerAxisValuesInItsOwnRecord()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithAxes();

            SceneObjectGroup copy = so.Copy(true);
            copy.RootPart.SetVehicleVectorParam((int)Vehicle.LINEAR_MOTOR_TIMESCALE, new Vector3(9f, 9f, 9f));

            Same(LinearTs, so.RootPart.VehicleParams.vd.m_linearMotorTimescaleAxes);
            Same(new Vector3(9f, 9f, 9f), copy.RootPart.VehicleParams.vd.m_linearMotorTimescaleAxes);
            Same(AngularTs, copy.RootPart.VehicleParams.vd.m_angularMotorTimescaleAxes);
        }

        [Fact]
        public void AScriptsVectorCallReachesTheRecordOfTheRootPart()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.SetVehicleType((int)Vehicle.TYPE_CAR);

            so.RootPart.SetVehicleVectorParam((int)Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, AngularDecay);

            Same(AngularDecay, so.RootPart.VehicleParams.vd.m_angularMotorDecayTimescaleAxes);
        }

        // ---- what an engine that knows only the single values is handed ----

        private sealed class RecordingActor : NullPhysicsActor
        {
            public readonly System.Collections.Generic.Dictionary<int, float> Floats = new();
            public readonly System.Collections.Generic.Dictionary<int, Vector3> Vectors = new();
            public override void VehicleFloatParam(int param, float value) => Floats[param] = value;
            public override void VehicleVectorParam(int param, Vector3 value) => Vectors[param] = value;
        }

        [Fact]
        public void AnEngineWithTheBaseSetVehicleIsHandedTheSingleValuesAsBefore()
        {
            TestHelpers.InMethod();
            SOPVehicle plain = Record(Vehicle.TYPE_CAR);
            SOPVehicle axes = WithAxes();
            var a = new RecordingActor();
            var b = new RecordingActor();

            plain.SetVehicle(a);
            axes.SetVehicle(b);

            Assert.Equal(a.Floats[(int)Vehicle.LINEAR_MOTOR_TIMESCALE], b.Floats[(int)Vehicle.LINEAR_MOTOR_TIMESCALE]);
            Assert.Equal(a.Floats[(int)Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE], b.Floats[(int)Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE]);
            Assert.Equal(a.Floats[(int)Vehicle.ANGULAR_MOTOR_TIMESCALE], b.Floats[(int)Vehicle.ANGULAR_MOTOR_TIMESCALE]);
            Assert.Equal(a.Floats[(int)Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE], b.Floats[(int)Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE]);
            Assert.False(b.Vectors.ContainsKey((int)Vehicle.LINEAR_MOTOR_TIMESCALE));
        }

        [Fact]
        public void TheEngineIsHandedTheRecordsDecayWhole()
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);
            v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 600f);
            var actor = new RecordingActor();

            v.SetVehicle(actor);

            Assert.Equal(600f, actor.Floats[(int)Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE], Tolerance);
        }
    }
}
