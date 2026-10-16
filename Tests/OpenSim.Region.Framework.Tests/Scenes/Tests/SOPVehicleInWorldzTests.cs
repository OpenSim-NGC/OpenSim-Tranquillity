/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The saved vehicle record (SOPVehicle) holds InWorldz's two extra vehicle types (VEHICLE_TYPE_SAILBOAT 10001 and
    /// VEHICLE_TYPE_MOTORCYCLE 10002) and its six extra float parameters (11001 to 11006), so that a vehicle set up by a
    /// script comes back from the record with the same values: after a region restart, a rez, a copy or a crossing.
    /// The reference is Halcyon's VehicleDynamics.cs (presets :1806-1844 and :1886-1924, parameter limits :239-296 and
    /// :84-85) and FloatParams.cs.
    /// </summary>
    public class SOPVehicleInWorldzTests : OpenSimTestCase
    {
        private const int MouselookAzimuth = 11001;
        private const int MouselookAltitude = 11002;
        private const int BankingAzimuth = 11003;
        private const int DisableMotorsAbove = 11004;
        private const int DisableMotorsAfter = 11005;
        private const int InvertedBankingModifier = 11006;

        private const float Pi = (float)System.Math.PI;
        private const float Tolerance = 1e-5f;

        private static SOPVehicle Record(Vehicle type)
        {
            var v = new SOPVehicle();
            v.ProcessTypeChange(type);
            return v;
        }

        private static SOPVehicle Record(int type) => Record((Vehicle)type);

        private static SOPVehicle RoundTrip(SOPVehicle v) => SOPVehicle.FromXml2(v.ToXml2());

        // ---- the six parameters: stored, held to Halcyon's ranges ----

        [Theory]
        // MOUSELOOK_AZIMUTH and _ALTITUDE: pi/32 (the mouselook threshold, VehicleDynamics.cs:85) to pi (:264-269)
        [InlineData(MouselookAzimuth, 1.0f, 1.0f)]
        [InlineData(MouselookAzimuth, 0.0f, Pi / 32f)]
        [InlineData(MouselookAzimuth, -4.0f, Pi / 32f)]
        [InlineData(MouselookAzimuth, 9.0f, Pi)]
        [InlineData(MouselookAltitude, 1.0f, 1.0f)]
        [InlineData(MouselookAltitude, 0.01f, Pi / 32f)]
        [InlineData(MouselookAltitude, 9.0f, Pi)]
        // BANKING_AZIMUTH: pi/128 (VehicleDynamics.cs:84) to pi (:270-271)
        [InlineData(BankingAzimuth, 2.0f, 2.0f)]
        [InlineData(BankingAzimuth, 0.0f, Pi / 128f)]
        [InlineData(BankingAzimuth, 9.0f, Pi)]
        // INVERTED_BANKING_MODIFIER: -10 to 10 (:255-256)
        [InlineData(InvertedBankingModifier, 0.5f, 0.5f)]
        [InlineData(InvertedBankingModifier, -50.0f, -10.0f)]
        [InlineData(InvertedBankingModifier, 50.0f, 10.0f)]
        // DISABLE_MOTORS_ABOVE and _AFTER: not limited (:294)
        [InlineData(DisableMotorsAbove, 3.5f, 3.5f)]
        [InlineData(DisableMotorsAbove, -7.0f, -7.0f)]
        [InlineData(DisableMotorsAfter, 12.0f, 12.0f)]
        [InlineData(DisableMotorsAfter, 0.0f, 0.0f)]
        public void AFloatParameterIsStoredHeldToItsRange(int param, float sent, float stored)
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);

            v.ProcessFloatVehicleParam((Vehicle)param, sent);

            Assert.Equal(stored, Read(v.vd, param), Tolerance);
        }

        private static float Read(VehicleData vd, int param) => param switch
        {
            MouselookAzimuth => vd.m_mouselookAzimuth,
            MouselookAltitude => vd.m_mouselookAltitude,
            BankingAzimuth => vd.m_bankingAzimuth,
            DisableMotorsAbove => vd.m_disableMotorsAbove,
            DisableMotorsAfter => vd.m_disableMotorsAfter,
            InvertedBankingModifier => vd.m_invertedBankingModifier,
            _ => throw new System.ArgumentOutOfRangeException(nameof(param)),
        };

        [Theory]
        [InlineData(MouselookAzimuth)]
        [InlineData(MouselookAltitude)]
        [InlineData(BankingAzimuth)]
        [InlineData(DisableMotorsAbove)]
        [InlineData(DisableMotorsAfter)]
        [InlineData(InvertedBankingModifier)]
        public void ANotANumberIsNotStored(int param)
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_MOTORCYCLE);
            float before = Read(v.vd, param);

            v.ProcessFloatVehicleParam((Vehicle)param, float.NaN);

            Assert.Equal(before, Read(v.vd, param));
        }

        [Theory]
        [InlineData(MouselookAzimuth)]
        [InlineData(MouselookAltitude)]
        [InlineData(BankingAzimuth)]
        [InlineData(DisableMotorsAbove)]
        [InlineData(DisableMotorsAfter)]
        [InlineData(InvertedBankingModifier)]
        public void AVectorOrRotationCallWithAFloatParameterChangesNothing(int param)
        {
            TestHelpers.InMethod();
            SOPVehicle v = Record(Vehicle.TYPE_CAR);
            VehicleData before = v.vd;

            v.ProcessVectorVehicleParam((Vehicle)param, new Vector3(1, 2, 3));
            v.ProcessRotationVehicleParam((Vehicle)param, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f));

            Assert.Equal(before, v.vd);
        }

        [Fact]
        public void AScriptsCallsReachTheRecordOfTheRootPart()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.SetVehicleType((int)Vehicle.TYPE_CAR);

            so.RootPart.SetVehicleFloatParam(MouselookAzimuth, 0.9f);
            so.RootPart.SetVehicleFloatParam(DisableMotorsAfter, 4f);

            VehicleData vd = so.RootPart.VehicleParams.vd;
            Assert.Equal(0.9f, vd.m_mouselookAzimuth, Tolerance);
            Assert.Equal(4f, vd.m_disableMotorsAfter, Tolerance);
        }

        // ---- saved with the object, copied with it, handed to the engine ----

        private static SOPVehicle WithSetValues()
        {
            SOPVehicle v = Record(Vehicle.TYPE_MOTORCYCLE);
            v.ProcessFloatVehicleParam((Vehicle)MouselookAzimuth, 0.7f);
            v.ProcessFloatVehicleParam((Vehicle)MouselookAltitude, 0.6f);
            v.ProcessFloatVehicleParam((Vehicle)BankingAzimuth, 1.25f);
            v.ProcessFloatVehicleParam((Vehicle)DisableMotorsAbove, 3.5f);
            v.ProcessFloatVehicleParam((Vehicle)DisableMotorsAfter, 6f);
            v.ProcessFloatVehicleParam((Vehicle)InvertedBankingModifier, -3f);
            return v;
        }

        [Fact]
        public void TheRecordSavesAndLoadsTheSixValues()
        {
            TestHelpers.InMethod();
            SOPVehicle saved = WithSetValues();

            SOPVehicle loaded = RoundTrip(saved);

            Assert.NotNull(loaded);
            Assert.Equal(saved.vd, loaded.vd);
            Assert.Equal(0.7f, loaded.vd.m_mouselookAzimuth, Tolerance);
            Assert.Equal(-3f, loaded.vd.m_invertedBankingModifier, Tolerance);
        }

        [Fact]
        public void AnObjectSavedAndLoadedKeepsTheSixValues()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithSetValues();

            SceneObjectGroup copy = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(so));

            Assert.Equal(so.RootPart.VehicleParams.vd, copy.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void ACopiedObjectHasTheSixValuesInItsOwnRecord()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithSetValues();

            SceneObjectGroup copy = so.Copy(true);
            copy.RootPart.SetVehicleFloatParam(DisableMotorsAbove, 9f);

            Assert.Equal(3.5f, so.RootPart.VehicleParams.vd.m_disableMotorsAbove, Tolerance);
            Assert.Equal(9f, copy.RootPart.VehicleParams.vd.m_disableMotorsAbove, Tolerance);
            Assert.Equal(0.7f, copy.RootPart.VehicleParams.vd.m_mouselookAzimuth, Tolerance);
        }

        /// <summary>Records every parameter an engine is handed through the base PhysicsActor.SetVehicle.</summary>
        private sealed class RecordingActor : NullPhysicsActor
        {
            public readonly System.Collections.Generic.Dictionary<int, float> Floats = new();
            public override void VehicleFloatParam(int param, float value) => Floats[param] = value;
        }

        [Fact]
        public void TheEngineIsHandedTheSixValuesWithTheRestOfTheRecord()
        {
            TestHelpers.InMethod();
            var actor = new RecordingActor();

            WithSetValues().SetVehicle(actor);

            Assert.Equal(0.7f, actor.Floats[MouselookAzimuth], Tolerance);
            Assert.Equal(0.6f, actor.Floats[MouselookAltitude], Tolerance);
            Assert.Equal(1.25f, actor.Floats[BankingAzimuth], Tolerance);
            Assert.Equal(3.5f, actor.Floats[DisableMotorsAbove], Tolerance);
            Assert.Equal(6f, actor.Floats[DisableMotorsAfter], Tolerance);
            Assert.Equal(-3f, actor.Floats[InvertedBankingModifier], Tolerance);
        }

        // ---- the two types ----

        [Fact]
        public void TheMotorcycleHasHalcyonsPresetValues()
        {
            TestHelpers.InMethod();

            VehicleData vd = Record(10002).vd;

            Assert.Equal((Vehicle)10002, vd.m_type);
            // VehicleDynamics.cs:1806-1844. The motor timescales and decays are per axis there; the record holds one
            // value for each, the first axis' (the forward one).
            Assert.Equal(new Vector3(100f, 0.1f, 10f), vd.m_linearFrictionTimescale);
            Assert.Equal(new Vector3(3f, 0.2f, 10f), vd.m_angularFrictionTimescale);
            Assert.Equal(new Vector3(0f, 0f, -0.1f), vd.m_linearMotorOffset);
            Assert.Equal(0.5f, vd.m_linearMotorTimescale);
            Assert.Equal(0.1f, vd.m_angularMotorTimescale);
            Assert.Equal(10f, vd.m_linearMotorDecayTimescale);
            Assert.Equal(0.2f, vd.m_angularMotorDecayTimescale);
            Assert.Equal(0f, vd.m_VhoverHeight);
            Assert.Equal(0f, vd.m_VhoverEfficiency);
            Assert.Equal(1000f, vd.m_VhoverTimescale);
            Assert.Equal(0f, vd.m_VehicleBuoyancy);
            Assert.Equal(1f, vd.m_linearDeflectionEfficiency);
            Assert.Equal(2f, vd.m_linearDeflectionTimescale);
            Assert.Equal(0.8f, vd.m_angularDeflectionEfficiency);
            Assert.Equal(2f, vd.m_angularDeflectionTimescale);
            Assert.Equal(1f, vd.m_verticalAttractionEfficiency);
            Assert.Equal(1f, vd.m_verticalAttractionTimescale);
            Assert.Equal(0.95f, vd.m_bankingEfficiency);
            Assert.Equal(0.5f, vd.m_bankingMix);
            Assert.Equal(0.1f, vd.m_bankingTimescale);
            Assert.Equal(-0.5f, vd.m_invertedBankingModifier);
            Assert.Equal(Pi / 4f, vd.m_mouselookAzimuth, Tolerance);
            Assert.Equal(Pi / 4f, vd.m_mouselookAltitude, Tolerance);
            Assert.Equal(Pi / 2f, vd.m_bankingAzimuth, Tolerance);
            Assert.Equal(1.5f, vd.m_disableMotorsAbove);
            Assert.Equal(2.5f, vd.m_disableMotorsAfter);
            Assert.Equal(Quaternion.Identity, vd.m_referenceFrame);
            Assert.Equal(
                VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.HOVER_UP_ONLY | VehicleFlag.LIMIT_MOTOR_UP | VehicleFlag.LIMIT_MOTOR_DOWN
                | VehicleFlag.LIMIT_ROLL_ONLY | VehicleFlag.TORQUE_WORLD_Z,
                vd.m_flags);
        }

        [Fact]
        public void TheSailboatHasHalcyonsPresetValues()
        {
            TestHelpers.InMethod();

            VehicleData vd = Record(10001).vd;

            Assert.Equal((Vehicle)10001, vd.m_type);
            // VehicleDynamics.cs:1886-1924, motor values as for the motorcycle.
            Assert.Equal(new Vector3(200f, 0.5f, 3f), vd.m_linearFrictionTimescale);
            Assert.Equal(new Vector3(10f, 1f, 0.2f), vd.m_angularFrictionTimescale);
            Assert.Equal(Vector3.Zero, vd.m_linearMotorOffset);
            Assert.Equal(1f, vd.m_linearMotorTimescale);
            Assert.Equal(2f, vd.m_angularMotorTimescale);
            Assert.Equal(1f, vd.m_linearMotorDecayTimescale);
            Assert.Equal(0.3f, vd.m_angularMotorDecayTimescale);
            Assert.Equal(0.0001f, vd.m_VhoverHeight);
            Assert.Equal(0.8f, vd.m_VhoverEfficiency);
            Assert.Equal(0.5f, vd.m_VhoverTimescale);
            Assert.Equal(0f, vd.m_VehicleBuoyancy);
            Assert.Equal(0.5f, vd.m_linearDeflectionEfficiency);
            Assert.Equal(3f, vd.m_linearDeflectionTimescale);
            Assert.Equal(0.5f, vd.m_angularDeflectionEfficiency);
            Assert.Equal(5f, vd.m_angularDeflectionTimescale);
            Assert.Equal(0.5f, vd.m_verticalAttractionEfficiency);
            Assert.Equal(0.3f, vd.m_verticalAttractionTimescale);
            Assert.Equal(0.8f, vd.m_bankingEfficiency);
            Assert.Equal(0.5f, vd.m_bankingMix);
            Assert.Equal(0.5f, vd.m_bankingTimescale);
            Assert.Equal(-0.2f, vd.m_invertedBankingModifier);
            Assert.Equal(Pi / 4f, vd.m_mouselookAzimuth, Tolerance);
            Assert.Equal(Pi / 4f, vd.m_mouselookAltitude, Tolerance);
            Assert.Equal(Pi / 2f, vd.m_bankingAzimuth, Tolerance);
            Assert.Equal(0f, vd.m_disableMotorsAbove);
            Assert.Equal(0f, vd.m_disableMotorsAfter);
            Assert.Equal(Quaternion.Identity, vd.m_referenceFrame);
            Assert.Equal(
                VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.LIMIT_MOTOR_UP | VehicleFlag.LIMIT_MOTOR_DOWN
                | VehicleFlag.REACT_TO_WIND | VehicleFlag.REACT_TO_CURRENTS | VehicleFlag.TORQUE_WORLD_Z,
                vd.m_flags);
        }

        [Theory]
        [InlineData(10001)]
        [InlineData(10002)]
        public void AVehicleOfEitherTypeComesBackFromItsRecordWithItsOwnValues(int type)
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.SetVehicleType(type);

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(so));

            Assert.Equal(type, back.RootPart.VehicleType);
            Assert.Equal(Record(type).vd, back.RootPart.VehicleParams.vd);
            // and not the values of a vehicle with no type, which is what the record held for a type it had no case for
            Assert.NotEqual(Record(Vehicle.TYPE_NONE).vd.m_linearFrictionTimescale, back.RootPart.VehicleParams.vd.m_linearFrictionTimescale);
        }

        [Theory]
        [InlineData(Vehicle.TYPE_NONE, 0f, 0f)]
        [InlineData(Vehicle.TYPE_SLED, 0f, 0f)]
        [InlineData(Vehicle.TYPE_CAR, 0.75f, 2.5f)]
        [InlineData(Vehicle.TYPE_BOAT, 0f, 0f)]
        [InlineData(Vehicle.TYPE_AIRPLANE, 0f, 0f)]
        [InlineData(Vehicle.TYPE_BALLOON, 0f, 0f)]
        public void TheOlderTypesHaveHalcyonsDefaultsForTheSixValues(Vehicle type, float above, float after)
        {
            TestHelpers.InMethod();

            VehicleData vd = Record(type).vd;

            Assert.Equal(Pi / 4f, vd.m_mouselookAzimuth, Tolerance);
            Assert.Equal(Pi / 4f, vd.m_mouselookAltitude, Tolerance);
            Assert.Equal(Pi / 2f, vd.m_bankingAzimuth, Tolerance);
            Assert.Equal(1f, vd.m_invertedBankingModifier);
            Assert.Equal(above, vd.m_disableMotorsAbove);
            Assert.Equal(after, vd.m_disableMotorsAfter);
        }

        // ---- records saved before this change, and records with fields this code does not know ----

        private static readonly Regex NewElements = new(
            "<(MLAZ|MLALT|BAZ|DMABOVE|DMAFTER|IBMOD)>[^<]*</\\1>", RegexOptions.Compiled);

        /// <summary>A record as the code before this change wrote it: the same XML without the six new elements.</summary>
        private static string OldFormat(SOPVehicle v)
        {
            string xml = NewElements.Replace(v.ToXml2(), string.Empty);
            Assert.DoesNotMatch("MLAZ|MLALT|BAZ|DMABOVE|DMAFTER|IBMOD", xml);
            return xml;
        }

        [Fact]
        public void ARecordSavedBeforeLoadsWithTheDefaultsOfItsType()
        {
            TestHelpers.InMethod();
            SOPVehicle car = Record(Vehicle.TYPE_CAR);
            car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 2f);

            SOPVehicle loaded = SOPVehicle.FromXml2(OldFormat(car));

            Assert.NotNull(loaded);
            Assert.Equal(car.vd, loaded.vd);   // everything the record held, and the car's own six
            Assert.Equal(0.75f, loaded.vd.m_disableMotorsAbove);
            Assert.Equal(Pi / 4f, loaded.vd.m_mouselookAzimuth, Tolerance);
        }

        [Fact]
        public void ARecordSavedBeforeWithOneOfTheNewTypesKeepsItsValuesAndGetsTheDefaultsForTheSix()
        {
            TestHelpers.InMethod();
            // What the older code saved for a vehicle of type 10002: the type and the values of a vehicle with no type.
            SOPVehicle old = Record(Vehicle.TYPE_NONE);
            old.vd.m_type = (Vehicle)10002;

            SOPVehicle loaded = SOPVehicle.FromXml2(OldFormat(old));

            Assert.NotNull(loaded);
            Assert.Equal((Vehicle)10002, loaded.vd.m_type);
            Assert.Equal(old.vd.m_linearFrictionTimescale, loaded.vd.m_linearFrictionTimescale);
            Assert.Equal(-0.5f, loaded.vd.m_invertedBankingModifier);
            Assert.Equal(1.5f, loaded.vd.m_disableMotorsAbove);
        }

        [Fact]
        public void ARecordWithFieldsThisCodeDoesNotKnowLoadsWithoutError()
        {
            TestHelpers.InMethod();
            SOPVehicle v = WithSetValues();
            string xml = v.ToXml2()
                .Replace("<TYPE>", "<NEWFIELD>7</NEWFIELD><NEWBLOCK><A>1</A><B><C>x</C></B></NEWBLOCK><TYPE>")
                .Replace("</Vehicle>", "<TRAILING>1.5</TRAILING></Vehicle>");

            SOPVehicle loaded = SOPVehicle.FromXml2(xml);

            Assert.NotNull(loaded);
            Assert.Equal(v.vd, loaded.vd);
        }

        [Fact]
        public void AnObjectWhoseVehicleHasUnknownFieldsLoadsWithoutError()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.VehicleParams = WithSetValues();
            string xml = SceneObjectSerializer.ToXml2Format(so).Replace("<TYPE>", "<NEWFIELD>7</NEWFIELD><TYPE>");
            Assert.Contains("<NEWFIELD>", xml);

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(xml);

            Assert.NotNull(back.RootPart.VehicleParams);
            Assert.Equal(so.RootPart.VehicleParams.vd, back.RootPart.VehicleParams.vd);
        }
    }
}
