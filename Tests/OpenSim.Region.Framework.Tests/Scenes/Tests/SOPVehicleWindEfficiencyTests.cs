/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Linq;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The saved vehicle record (SOPVehicle) keeps InWorldz's two wind efficiency vectors (VEHICLE_LINEAR_WIND_EFFICIENCY 12001
    /// and VEHICLE_ANGULAR_WIND_EFFICIENCY 12002) as optional elements. A record that holds neither is written and read
    /// exactly as before, so records saved by an older build load unchanged and an older build loads these records (it skips
    /// the elements it does not know).
    /// </summary>
    public class SOPVehicleWindEfficiencyTests : OpenSimTestCase
    {
        private static readonly Vector3 LinearWind = new(0.5f, -2f, 7.25f);
        private static readonly Vector3 AngularWind = new(0.1f, 0.01f, -3f);

        private static SOPVehicle Record(Vehicle type)
        {
            var v = new SOPVehicle();
            v.ProcessTypeChange(type);
            return v;
        }

        private static SOPVehicle WithWind()
        {
            SOPVehicle v = Record(Vehicle.TYPE_CAR);
            v.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, LinearWind);
            v.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, AngularWind);
            return v;
        }

        private static void Same(Vector3 expected, Vector3? actual)
        {
            Assert.True(actual.HasValue, "no wind efficiency in the record");
            Assert.Equal(expected, actual.Value);
        }

        private static void AssertWind(VehicleData vd)
        {
            Same(LinearWind, vd.m_linearWindEfficiency);
            Same(AngularWind, vd.m_angularWindEfficiency);
        }

        private static SOPVehicle RoundTrip(SOPVehicle v) => SOPVehicle.FromXml2(v.ToXml2());

        private static readonly Regex WindElements = new(
            "<(LWEFF|AWEFF)>.*?</\\1>", RegexOptions.Compiled | RegexOptions.Singleline);

        // ---- a record with no wind efficiency is what it always was ----

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

            string xml = v.ToXml2();
            SOPVehicle back = SOPVehicle.FromXml2(xml);

            Assert.DoesNotMatch("LWEFF|AWEFF", xml);   // the text is the one an older build wrote
            Assert.Equal(v.vd, back.vd);
            Assert.Null(back.vd.m_linearWindEfficiency);
            Assert.Null(back.vd.m_angularWindEfficiency);
            Assert.Equal(xml, back.ToXml2());
        }

        // The record as an older build wrote it (none of the elements added since).
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
            Assert.Equal(Vehicle.TYPE_CAR, v.vd.m_type);
            Assert.Equal(60f, v.vd.m_linearMotorDecayTimescale);
            Assert.Null(v.vd.m_linearWindEfficiency);
            Assert.Null(v.vd.m_angularWindEfficiency);
        }

        [Fact]
        public void ARecordOfAnOldBuildInAnObjectLoadsAsItDid()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = SOPVehicle.FromXml2(OldBuildRecord);

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(so));

            Assert.Equal(so.RootPart.VehicleParams.vd, back.RootPart.VehicleParams.vd);
            Assert.Null(back.RootPart.VehicleParams.vd.m_linearWindEfficiency);
            Assert.Null(back.RootPart.VehicleParams.vd.m_angularWindEfficiency);
        }

        [Fact]
        public void ABuildThatDoesNotKnowTheWindElementsReadsTheRestAsItAlwaysDid()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithWind();
            string xml = v.ToXml2();
            Assert.Matches("<LWEFF>", xml);
            Assert.Matches("<AWEFF>", xml);
            // What an older build sees: the same text without the two elements it skips.
            string older = WindElements.Replace(xml, string.Empty);
            Assert.DoesNotMatch("LWEFF|AWEFF", older);

            SOPVehicle loaded = SOPVehicle.FromXml2(older);

            Assert.NotNull(loaded);
            Assert.Null(loaded.vd.m_linearWindEfficiency);
            Assert.Equal(v.vd.m_type, loaded.vd.m_type);
            Assert.Equal(v.vd.m_flags, loaded.vd.m_flags);
            Assert.Equal(v.vd.m_linearMotorTimescale, loaded.vd.m_linearMotorTimescale);
            Assert.Equal(v.vd.m_mouselookAzimuth, loaded.vd.m_mouselookAzimuth);
        }

        // ---- what a script's call stores ----

        [Fact]
        public void AVectorCallIsStoredAndTheOtherValuesAreNotTouched()
        {
            TestHelpers.InMethod();
            SOPVehicle plain = Record(Vehicle.TYPE_CAR);

            SOPVehicle v = WithWind();

            AssertWind(v.vd);
            Assert.Equal(plain.vd.m_linearMotorTimescale, v.vd.m_linearMotorTimescale);
            Assert.Equal(plain.vd.m_linearFrictionTimescale, v.vd.m_linearFrictionTimescale);
            Assert.Equal(plain.vd.m_mouselookAzimuth, v.vd.m_mouselookAzimuth);
        }

        [Fact]
        public void EachAxisIsHeldToMinusTenToTenAndANotANumberIsNotStored()
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);

            v.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(50f, -50f, 3f));
            Same(new Vector3(10f, -10f, 3f), v.vd.m_linearWindEfficiency);

            v.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(float.NaN, 1f, 1f));
            v.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, new Vector3(1f, float.PositiveInfinity, 1f));
            Same(new Vector3(10f, -10f, 3f), v.vd.m_linearWindEfficiency);
            Assert.Null(v.vd.m_angularWindEfficiency);
        }

        [Fact]
        public void AZeroVectorIsStoredAsASetValue()
        {
            // A script that sets 0 turns the wind off for a preset that has wind; that must survive, not read as "none set".
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);

            v.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, Vector3.Zero);

            SOPVehicle back = RoundTrip(v);
            Same(Vector3.Zero, back.vd.m_linearWindEfficiency);
            Assert.Null(back.vd.m_angularWindEfficiency);
        }

        [Fact]
        public void ATypeChangeStartsWithNoWindEfficiency()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithWind();

            v.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);

            Assert.Null(v.vd.m_linearWindEfficiency);
            Assert.Null(v.vd.m_angularWindEfficiency);
        }

        // ---- kept through every way a vehicle is saved and moved ----

        [Fact]
        public void TheRecordKeepsTheWindEfficiency()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithWind();

            SOPVehicle back = RoundTrip(v);

            AssertWind(back.vd);
            Assert.Equal(v.vd, back.vd);
        }

        [Fact]
        public void OneVectorSetAndTheOtherNotIsKeptThatWay()
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_BOAT);
            v.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, AngularWind);

            SOPVehicle back = RoundTrip(v);

            Assert.Null(back.vd.m_linearWindEfficiency);
            Same(AngularWind, back.vd.m_angularWindEfficiency);
        }

        [Fact]
        public void TheWindEfficiencyDoesNotChangeTheOtherElementsOfTheRecord()
        {
            TestHelpers.InMethod();
            SOPVehicle plain = Record(Vehicle.TYPE_CAR);

            string withWind = WithWind().ToXml2();

            Assert.Equal(plain.ToXml2(), WindElements.Replace(withWind, string.Empty));
        }

        [Fact]
        public void ARegionSaveKeepsTheWindEfficiency()
        {
            // The region's database holds the record as its XML text in one column (ToXml2 on save, FromXml2 on load).
            TestHelpers.InMethod();
            SOPVehicle v = WithWind();

            SOPVehicle loaded = SOPVehicle.FromXml2(v.ToXml2());

            AssertWind(loaded.vd);
            Assert.Equal(v.vd, loaded.vd);
        }

        [Fact]
        public void AnObjectInInventoryKeepsTheWindEfficiency()
        {
            // Taking an object into inventory saves it with ToOriginalXmlFormat; rezzing it reads it back.
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithWind();

            SceneObjectGroup rezzed = SceneObjectSerializer.FromOriginalXmlFormat(SceneObjectSerializer.ToOriginalXmlFormat(so));

            Assert.NotNull(rezzed.RootPart.VehicleParams);
            AssertWind(rezzed.RootPart.VehicleParams.vd);
            Assert.Equal(so.RootPart.VehicleParams.vd, rezzed.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void AnObjectSavedWithItsRegionKeepsTheWindEfficiency()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithWind();

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(so));

            AssertWind(back.RootPart.VehicleParams.vd);
            Assert.Equal(so.RootPart.VehicleParams.vd, back.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void AnObjectCrossingIntoAnotherRegionKeepsTheWindEfficiency()
        {
            // A crossing sends the object as its XML2 text (SceneObjectGroup.ToXml2) and the other region reads it
            // (SceneObjectSerializer.FromXml2Format).
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithWind();

            SceneObjectGroup arrived = SceneObjectSerializer.FromXml2Format(so.ToXml2());

            AssertWind(arrived.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void ACopiedObjectHasTheWindEfficiencyInItsOwnRecord()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithWind();

            SceneObjectGroup copy = so.Copy(true);
            copy.RootPart.SetVehicleVectorParam((int)Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(1f, 1f, 1f));

            Same(LinearWind, so.RootPart.VehicleParams.vd.m_linearWindEfficiency);
            Same(new Vector3(1f, 1f, 1f), copy.RootPart.VehicleParams.vd.m_linearWindEfficiency);
            Same(AngularWind, copy.RootPart.VehicleParams.vd.m_angularWindEfficiency);
        }

        [Fact]
        public void AScriptsVectorCallReachesTheRecordOfTheRootPart()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.SetVehicleType((int)Vehicle.TYPE_BOAT);

            so.RootPart.SetVehicleVectorParam(12002, AngularWind);

            Same(AngularWind, so.RootPart.VehicleParams.vd.m_angularWindEfficiency);
        }

        // ---- what an engine that knows only the older values is handed ----

        private sealed class RecordingActor : NullPhysicsActor
        {
            public readonly System.Collections.Generic.Dictionary<int, Vector3> Vectors = new();
            public override void VehicleVectorParam(int param, Vector3 value) => Vectors[param] = value;
        }

        [Fact]
        public void AnEngineWithTheBaseSetVehicleIsHandedTheSameCallsAsBefore()
        {
            TestHelpers.InMethod();
            SOPVehicle plain = Record(Vehicle.TYPE_CAR);
            SOPVehicle wind = WithWind();
            var a = new RecordingActor();
            var b = new RecordingActor();

            plain.SetVehicle(a);
            wind.SetVehicle(b);

            Assert.Equal(a.Vectors.Keys.OrderBy(k => k), b.Vectors.Keys.OrderBy(k => k));
            Assert.False(b.Vectors.ContainsKey((int)Vehicle.LINEAR_WIND_EFFICIENCY));
            Assert.False(b.Vectors.ContainsKey((int)Vehicle.ANGULAR_WIND_EFFICIENCY));
        }
    }
}
