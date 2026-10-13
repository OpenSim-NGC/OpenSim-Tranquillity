/*
 * Copyright (c) Contributors, http://opensimulator.org/
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

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using System.Text;
using System.Xml;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.Framework.Scenes;

public class SOPVehicle
{
    public VehicleData vd;

    public Vehicle Type
    {
        get { return vd.m_type; }
    }

    // Smallest values Halcyon accepts for the mouselook and banking ranges (VehicleDynamics.cs:84-85).
    private const float MouselookThreshold = MathF.PI / 32f;
    private const float BankThreshold = MathF.PI / 128f;

    public SOPVehicle()
    {
        vd = new VehicleData();
        ProcessTypeChange(Vehicle.TYPE_NONE); // is needed?
    }

    public void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
    {
        float len;
        float timestep = 0.01f;
        switch (pParam)
        {
            case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                if (pValue < 0f) pValue = 0f;
                if (pValue > 1f) pValue = 1f;
                vd.m_angularDeflectionEfficiency = pValue;
                break;
            case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_angularDeflectionTimescale = pValue;
                break;
            case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                else if (pValue > 120) pValue = 120;
                vd.m_angularMotorDecayTimescale = pValue;
                break;
            case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_angularMotorTimescale = pValue;
                break;
            case Vehicle.BANKING_EFFICIENCY:
                if (pValue < -1f) pValue = -1f;
                if (pValue > 1f) pValue = 1f;
                vd.m_bankingEfficiency = pValue;
                break;
            case Vehicle.BANKING_MIX:
                if (pValue < 0f) pValue = 0f;
                if (pValue > 1f) pValue = 1f;
                vd.m_bankingMix = pValue;
                break;
            case Vehicle.BANKING_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_bankingTimescale = pValue;
                break;
            case Vehicle.BUOYANCY:
                if (pValue < -1f) pValue = -1f;
                if (pValue > 1f) pValue = 1f;
                vd.m_VehicleBuoyancy = pValue;
                break;
            case Vehicle.HOVER_EFFICIENCY:
                if (pValue < 0f) pValue = 0f;
                if (pValue > 1f) pValue = 1f;
                vd.m_VhoverEfficiency = pValue;
                break;
            case Vehicle.HOVER_HEIGHT:
                vd.m_VhoverHeight = pValue;
                break;
            case Vehicle.HOVER_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_VhoverTimescale = pValue;
                break;
            case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                if (pValue < 0f) pValue = 0f;
                if (pValue > 1f) pValue = 1f;
                vd.m_linearDeflectionEfficiency = pValue;
                break;
            case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_linearDeflectionTimescale = pValue;
                break;
            case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                else if (pValue > 120) pValue = 120;
                vd.m_linearMotorDecayTimescale = pValue;
                break;
            case Vehicle.LINEAR_MOTOR_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_linearMotorTimescale = pValue;
                break;
            case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                if (pValue < 0f) pValue = 0f;
                if (pValue > 1f) pValue = 1f;
                vd.m_verticalAttractionEfficiency = pValue;
                break;
            case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_verticalAttractionTimescale = pValue;
                break;

            // InWorldz's parameters, held to the ranges Halcyon holds them to (VehicleDynamics.cs:239-296; the
            // thresholds are :84-85). A value that is not a number is not stored.
            case Vehicle.MOUSELOOK_AZIMUTH:
                if (float.IsNaN(pValue)) break;
                vd.m_mouselookAzimuth = Math.Clamp(pValue, MouselookThreshold, MathF.PI);
                break;
            case Vehicle.MOUSELOOK_ALTITUDE:
                if (float.IsNaN(pValue)) break;
                vd.m_mouselookAltitude = Math.Clamp(pValue, MouselookThreshold, MathF.PI);
                break;
            case Vehicle.BANKING_AZIMUTH:
                if (float.IsNaN(pValue)) break;
                vd.m_bankingAzimuth = Math.Clamp(pValue, BankThreshold, MathF.PI);
                break;
            case Vehicle.DISABLE_MOTORS_ABOVE:
                if (float.IsNaN(pValue)) break;
                vd.m_disableMotorsAbove = pValue;
                break;
            case Vehicle.DISABLE_MOTORS_AFTER:
                if (float.IsNaN(pValue)) break;
                vd.m_disableMotorsAfter = pValue;
                break;
            case Vehicle.INVERTED_BANKING_MODIFIER:
                if (float.IsNaN(pValue)) break;
                vd.m_invertedBankingModifier = Math.Clamp(pValue, -10f, 10f);
                break;

            // These are vector properties but the engine lets you use a single float value to
            // set all of the components to the same value
            case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_angularFrictionTimescale = new Vector3(pValue, pValue, pValue);
                break;
            case Vehicle.ANGULAR_MOTOR_DIRECTION:
                vd.m_angularMotorDirection = new Vector3(pValue, pValue, pValue);
                len = vd.m_angularMotorDirection.Length();
                if (len > 12.566f)
                    vd.m_angularMotorDirection *= (12.566f / len);
                break;
            case Vehicle.LINEAR_FRICTION_TIMESCALE:
                if (pValue < timestep) pValue = timestep;
                vd.m_linearFrictionTimescale = new Vector3(pValue, pValue, pValue);
                break;
            case Vehicle.LINEAR_MOTOR_DIRECTION:
                vd.m_linearMotorDirection = new Vector3(pValue, pValue, pValue);
                len = vd.m_linearMotorDirection.Length();
                if (len > 30.0f)
                    vd.m_linearMotorDirection *= (30.0f / len);
                break;
            case Vehicle.LINEAR_MOTOR_OFFSET:
                vd.m_linearMotorOffset = new Vector3(pValue, pValue, pValue);
                len = vd.m_linearMotorOffset.Length();
                if (len > 100.0f)
                    vd.m_linearMotorOffset *= (100.0f / len);
                break;
        }
    }//end ProcessFloatVehicleParam

    public void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
    {
        float len;
        float timestep = 0.01f;
        switch (pParam)
        {
            case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                if (pValue.X < timestep) pValue.X = timestep;
                if (pValue.Y < timestep) pValue.Y = timestep;
                if (pValue.Z < timestep) pValue.Z = timestep;

                vd.m_angularFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                break;
            case Vehicle.ANGULAR_MOTOR_DIRECTION:
                vd.m_angularMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                // Limit requested angular speed to 2 rps= 4 pi rads/sec
                len = vd.m_angularMotorDirection.Length();
                if (len > 12.566f)
                    vd.m_angularMotorDirection *= (12.566f / len);
                break;
            case Vehicle.LINEAR_FRICTION_TIMESCALE:
                if (pValue.X < timestep) pValue.X = timestep;
                if (pValue.Y < timestep) pValue.Y = timestep;
                if (pValue.Z < timestep) pValue.Z = timestep;
                vd.m_linearFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                break;
            case Vehicle.LINEAR_MOTOR_DIRECTION:
                vd.m_linearMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                len = vd.m_linearMotorDirection.Length();
                if (len > 30.0f)
                    vd.m_linearMotorDirection *= (30.0f / len);
                break;
            case Vehicle.LINEAR_MOTOR_OFFSET:
                vd.m_linearMotorOffset = new Vector3(pValue.X, pValue.Y, pValue.Z);
                len = vd.m_linearMotorOffset.Length();
                if (len > 100.0f)
                    vd.m_linearMotorOffset *= (100.0f / len);
                break;
        }
    }//end ProcessVectorVehicleParam

    public void ProcessRotationVehicleParam(Vehicle pParam, Quaternion pValue)
    {
        switch (pParam)
        {
            case Vehicle.REFERENCE_FRAME:
                vd.m_referenceFrame = pValue;
                break;
        }
    }//end ProcessRotationVehicleParam

    public void ProcessVehicleFlags(int pParam, bool remove)
    {
        if (remove)
        {
            vd.m_flags &= ~((VehicleFlag)pParam);
        }
        else
        {
            vd.m_flags |= (VehicleFlag)pParam;
        }
    }//end ProcessVehicleFlags

    public void ProcessTypeChange(Vehicle pType)
    {
        vd.m_linearMotorDirection = Vector3.Zero;
        vd.m_angularMotorDirection = Vector3.Zero;
        vd.m_linearMotorOffset = Vector3.Zero;
        vd.m_referenceFrame = Quaternion.Identity;

        // The defaults of InWorldz's parameters (VehicleDynamics.cs:1710-1720); the types below that differ set theirs.
        vd.m_mouselookAzimuth = MathF.PI / 4f;
        vd.m_mouselookAltitude = MathF.PI / 4f;
        vd.m_bankingAzimuth = MathF.PI / 2f;
        vd.m_invertedBankingModifier = 1f;
        vd.m_disableMotorsAbove = 0f;
        vd.m_disableMotorsAfter = 0f;

        // Set Defaults For Type
        vd.m_type = pType;
        switch (pType)
        {
            case Vehicle.TYPE_NONE:
                vd.m_linearFrictionTimescale = new Vector3(1000, 1000, 1000);
                vd.m_angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                vd.m_linearMotorTimescale = 1000;
                vd.m_linearMotorDecayTimescale = 120;
                vd.m_angularMotorTimescale = 1000;
                vd.m_angularMotorDecayTimescale = 1000;
                vd.m_VhoverHeight = 0;
                vd.m_VhoverEfficiency = 1;
                vd.m_VhoverTimescale = 1000;
                vd.m_VehicleBuoyancy = 0;
                vd.m_linearDeflectionEfficiency = 0;
                vd.m_linearDeflectionTimescale = 1000;
                vd.m_angularDeflectionEfficiency = 0;
                vd.m_angularDeflectionTimescale = 1000;
                vd.m_bankingEfficiency = 0;
                vd.m_bankingMix = 1;
                vd.m_bankingTimescale = 1000;
                vd.m_verticalAttractionEfficiency = 0;
                vd.m_verticalAttractionTimescale = 1000;

                vd.m_flags = (VehicleFlag)0;
                break;

            case Vehicle.TYPE_SLED:
                vd.m_linearFrictionTimescale = new Vector3(30, 1, 1000);
                vd.m_angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                vd.m_linearMotorTimescale = 1000;
                vd.m_linearMotorDecayTimescale = 120;
                vd.m_angularMotorTimescale = 1000;
                vd.m_angularMotorDecayTimescale = 120;
                vd.m_VhoverHeight = 0;
                vd.m_VhoverEfficiency = 1;
                vd.m_VhoverTimescale = 10;
                vd.m_VehicleBuoyancy = 0;
                vd.m_linearDeflectionEfficiency = 1;
                vd.m_linearDeflectionTimescale = 1;
                vd.m_angularDeflectionEfficiency = 0;
                vd.m_angularDeflectionTimescale = 1000;
                vd.m_bankingEfficiency = 0;
                vd.m_bankingMix = 1;
                vd.m_bankingTimescale = 10;
                vd.m_flags &=
                     ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY |
                       VehicleFlag.HOVER_GLOBAL_HEIGHT | VehicleFlag.HOVER_UP_ONLY);
                vd.m_flags |= (VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY | VehicleFlag.LIMIT_MOTOR_UP);
                break;
            case Vehicle.TYPE_CAR:
                vd.m_linearFrictionTimescale = new Vector3(100, 2, 1000);
                vd.m_angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                vd.m_linearMotorTimescale = 1;
                vd.m_linearMotorDecayTimescale = 60;
                vd.m_angularMotorTimescale = 1;
                vd.m_angularMotorDecayTimescale = 0.8f;
                vd.m_VhoverHeight = 0;
                vd.m_VhoverEfficiency = 0;
                vd.m_VhoverTimescale = 1000;
                vd.m_VehicleBuoyancy = 0;
                vd.m_linearDeflectionEfficiency = 1;
                vd.m_linearDeflectionTimescale = 2;
                vd.m_angularDeflectionEfficiency = 0;
                vd.m_angularDeflectionTimescale = 10;
                vd.m_verticalAttractionEfficiency = 1f;
                vd.m_verticalAttractionTimescale = 10f;
                vd.m_bankingEfficiency = -0.2f;
                vd.m_bankingMix = 1;
                vd.m_bankingTimescale = 1;
                vd.m_disableMotorsAbove = 0.75f;   // VehicleDynamics.cs:1797-1798
                vd.m_disableMotorsAfter = 2.5f;
                vd.m_flags &= ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY | VehicleFlag.HOVER_GLOBAL_HEIGHT);
                vd.m_flags |= (VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY |
                            VehicleFlag.LIMIT_MOTOR_UP | VehicleFlag.HOVER_UP_ONLY);
                break;
            case Vehicle.TYPE_BOAT:
                vd.m_linearFrictionTimescale = new Vector3(10, 3, 2);
                vd.m_angularFrictionTimescale = new Vector3(10, 10, 10);
                vd.m_linearMotorTimescale = 5;
                vd.m_linearMotorDecayTimescale = 60;
                vd.m_angularMotorTimescale = 4;
                vd.m_angularMotorDecayTimescale = 4;
                vd.m_VhoverHeight = 0;
                vd.m_VhoverEfficiency = 0.5f;
                vd.m_VhoverTimescale = 2;
                vd.m_VehicleBuoyancy = 1;
                vd.m_linearDeflectionEfficiency = 0.5f;
                vd.m_linearDeflectionTimescale = 3;
                vd.m_angularDeflectionEfficiency = 0.5f;
                vd.m_angularDeflectionTimescale = 5;
                vd.m_verticalAttractionEfficiency = 0.5f;
                vd.m_verticalAttractionTimescale = 5f;
                vd.m_bankingEfficiency = -0.3f;
                vd.m_bankingMix = 0.8f;
                vd.m_bankingTimescale = 1;
                vd.m_flags &= ~(VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_GLOBAL_HEIGHT |
                        VehicleFlag.HOVER_UP_ONLY |
                        VehicleFlag.LIMIT_ROLL_ONLY);
                vd.m_flags |= (VehicleFlag.NO_DEFLECTION_UP |
                            VehicleFlag.LIMIT_MOTOR_UP |
                            VehicleFlag.HOVER_WATER_ONLY);
                break;
            case Vehicle.TYPE_AIRPLANE:
                vd.m_linearFrictionTimescale = new Vector3(200, 10, 5);
                vd.m_angularFrictionTimescale = new Vector3(20, 20, 20);
                vd.m_linearMotorTimescale = 2;
                vd.m_linearMotorDecayTimescale = 60;
                vd.m_angularMotorTimescale = 4;
                vd.m_angularMotorDecayTimescale = 8;
                vd.m_VhoverHeight = 0;
                vd.m_VhoverEfficiency = 0.5f;
                vd.m_VhoverTimescale = 1000;
                vd.m_VehicleBuoyancy = 0;
                vd.m_linearDeflectionEfficiency = 0.5f;
                vd.m_linearDeflectionTimescale = 0.5f;
                vd.m_angularDeflectionEfficiency = 1;
                vd.m_angularDeflectionTimescale = 2;
                vd.m_verticalAttractionEfficiency = 0.9f;
                vd.m_verticalAttractionTimescale = 2f;
                vd.m_bankingEfficiency = 1;
                vd.m_bankingMix = 0.7f;
                vd.m_bankingTimescale = 2;
                vd.m_flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                    VehicleFlag.HOVER_TERRAIN_ONLY |
                    VehicleFlag.HOVER_GLOBAL_HEIGHT |
                    VehicleFlag.HOVER_UP_ONLY |
                    VehicleFlag.NO_DEFLECTION_UP |
                    VehicleFlag.LIMIT_MOTOR_UP);
                vd.m_flags |= (VehicleFlag.LIMIT_ROLL_ONLY);
                break;
            case Vehicle.TYPE_BALLOON:
                vd.m_linearFrictionTimescale = new Vector3(5, 5, 5);
                vd.m_angularFrictionTimescale = new Vector3(10, 10, 10);
                vd.m_linearMotorTimescale = 5;
                vd.m_linearMotorDecayTimescale = 60;
                vd.m_angularMotorTimescale = 6;
                vd.m_angularMotorDecayTimescale = 10;
                vd.m_VhoverHeight = 5;
                vd.m_VhoverEfficiency = 0.8f;
                vd.m_VhoverTimescale = 10;
                vd.m_VehicleBuoyancy = 1;
                vd.m_linearDeflectionEfficiency = 0;
                vd.m_linearDeflectionTimescale = 5;
                vd.m_angularDeflectionEfficiency = 0;
                vd.m_angularDeflectionTimescale = 5;
                vd.m_verticalAttractionEfficiency = 0f;
                vd.m_verticalAttractionTimescale = 1000f;
                vd.m_bankingEfficiency = 0;
                vd.m_bankingMix = 0.7f;
                vd.m_bankingTimescale = 5;
                vd.m_flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                    VehicleFlag.HOVER_TERRAIN_ONLY |
                    VehicleFlag.HOVER_UP_ONLY |
                    VehicleFlag.NO_DEFLECTION_UP |
                    VehicleFlag.LIMIT_MOTOR_UP);
                vd.m_flags |= (VehicleFlag.LIMIT_ROLL_ONLY |
                    VehicleFlag.HOVER_GLOBAL_HEIGHT);
                break;

            // InWorldz's two types, with Halcyon's values (VehicleDynamics.cs:1806-1844 and :1886-1924). There the motor
            // timescales and decays are one value per axis; the record holds one value each, taken from the first axis.
            case Vehicle.TYPE_MOTORCYCLE:
                vd.m_linearFrictionTimescale = new Vector3(100f, 0.1f, 10f);
                vd.m_angularFrictionTimescale = new Vector3(3f, 0.2f, 10f);
                vd.m_linearMotorOffset = new Vector3(0f, 0f, -0.1f);
                vd.m_linearMotorTimescale = 0.5f;
                vd.m_linearMotorDecayTimescale = 10f;
                vd.m_angularMotorTimescale = 0.1f;
                vd.m_angularMotorDecayTimescale = 0.2f;
                vd.m_VhoverHeight = 0;
                vd.m_VhoverEfficiency = 0;
                vd.m_VhoverTimescale = 1000;
                vd.m_VehicleBuoyancy = 0;
                vd.m_linearDeflectionEfficiency = 1;
                vd.m_linearDeflectionTimescale = 2;
                vd.m_angularDeflectionEfficiency = 0.8f;
                vd.m_angularDeflectionTimescale = 2;
                vd.m_verticalAttractionEfficiency = 1;
                vd.m_verticalAttractionTimescale = 1;
                vd.m_bankingEfficiency = 0.95f;
                vd.m_bankingMix = 0.5f;
                vd.m_bankingTimescale = 0.1f;
                vd.m_invertedBankingModifier = -0.5f;
                vd.m_disableMotorsAbove = 1.5f;
                vd.m_disableMotorsAfter = 2.5f;
                vd.m_flags = VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.HOVER_UP_ONLY | VehicleFlag.LIMIT_MOTOR_UP |
                    VehicleFlag.LIMIT_MOTOR_DOWN | VehicleFlag.LIMIT_ROLL_ONLY | VehicleFlag.TORQUE_WORLD_Z;
                break;
            case Vehicle.TYPE_SAILBOAT:
                vd.m_linearFrictionTimescale = new Vector3(200f, 0.5f, 3f);
                vd.m_angularFrictionTimescale = new Vector3(10f, 1f, 0.2f);
                vd.m_linearMotorOffset = Vector3.Zero;
                vd.m_linearMotorTimescale = 1f;
                vd.m_linearMotorDecayTimescale = 1f;
                vd.m_angularMotorTimescale = 2f;
                vd.m_angularMotorDecayTimescale = 0.3f;
                vd.m_VhoverHeight = 0.0001f;
                vd.m_VhoverEfficiency = 0.8f;
                vd.m_VhoverTimescale = 0.5f;
                vd.m_VehicleBuoyancy = 0;
                vd.m_linearDeflectionEfficiency = 0.5f;
                vd.m_linearDeflectionTimescale = 3;
                vd.m_angularDeflectionEfficiency = 0.5f;
                vd.m_angularDeflectionTimescale = 5;
                vd.m_verticalAttractionEfficiency = 0.5f;
                vd.m_verticalAttractionTimescale = 0.3f;
                vd.m_bankingEfficiency = 0.8f;
                vd.m_bankingMix = 0.5f;
                vd.m_bankingTimescale = 0.5f;
                vd.m_invertedBankingModifier = -0.2f;
                vd.m_flags = VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.LIMIT_MOTOR_UP |
                    VehicleFlag.LIMIT_MOTOR_DOWN | VehicleFlag.REACT_TO_WIND | VehicleFlag.REACT_TO_CURRENTS |
                    VehicleFlag.TORQUE_WORLD_Z;
                break;
        }
    }
    public void SetVehicle(PhysicsActor ph)
    {
        if (ph == null)
            return;
        ph.SetVehicle(vd);
    }

    public bool CameraDecoupled
    {
        get
        {
            if((vd.m_flags & VehicleFlag.CAMERA_DECOUPLED) != 0)
                return true;
            return false;
        }
    }

    private XmlTextWriter writer;

    private void XWint(string name, int i)
    {
        writer.WriteElementString(name, i.ToString());
    }

    private void XWfloat(string name, float f)
    {
        writer.WriteElementString(name, f.ToString(Culture.FormatProvider));
    }

    private void XWVector(string name, Vector3 vec)
    {
        writer.WriteStartElement(name);
        writer.WriteElementString("X", vec.X.ToString(Culture.FormatProvider));
        writer.WriteElementString("Y", vec.Y.ToString(Culture.FormatProvider));
        writer.WriteElementString("Z", vec.Z.ToString(Culture.FormatProvider));
        writer.WriteEndElement();
    }

    private void XWQuat(string name, Quaternion quat)
    {
        writer.WriteStartElement(name);
        writer.WriteElementString("X", quat.X.ToString(Culture.FormatProvider));
        writer.WriteElementString("Y", quat.Y.ToString(Culture.FormatProvider));
        writer.WriteElementString("Z", quat.Z.ToString(Culture.FormatProvider));
        writer.WriteElementString("W", quat.W.ToString(Culture.FormatProvider));
        writer.WriteEndElement();
    }

    public void ToXml2(XmlTextWriter twriter)
    {
        writer = twriter;
        writer.WriteStartElement("Vehicle");

        XWint("TYPE", (int)vd.m_type);
        XWint("FLAGS", (int)vd.m_flags);

        // Linear properties
        XWVector("LMDIR", vd.m_linearMotorDirection);
        XWVector("LMFTIME", vd.m_linearFrictionTimescale);
        XWfloat("LMDTIME", vd.m_linearMotorDecayTimescale);
        XWfloat("LMTIME", vd.m_linearMotorTimescale);
        XWVector("LMOFF", vd.m_linearMotorOffset);

        //Angular properties
        XWVector("AMDIR", vd.m_angularMotorDirection);
        XWfloat("AMTIME", vd.m_angularMotorTimescale);
        XWfloat("AMDTIME", vd.m_angularMotorDecayTimescale);
        XWVector("AMFTIME", vd.m_angularFrictionTimescale);

        //Deflection properties
        XWfloat("ADEFF", vd.m_angularDeflectionEfficiency);
        XWfloat("ADTIME", vd.m_angularDeflectionTimescale);
        XWfloat("LDEFF", vd.m_linearDeflectionEfficiency);
        XWfloat("LDTIME", vd.m_linearDeflectionTimescale);

        //Banking properties
        XWfloat("BEFF", vd.m_bankingEfficiency);
        XWfloat("BMIX", vd.m_bankingMix);
        XWfloat("BTIME", vd.m_bankingTimescale);

        //Hover and Buoyancy properties
        XWfloat("HHEI", vd.m_VhoverHeight);
        XWfloat("HEFF", vd.m_VhoverEfficiency);
        XWfloat("HTIME", vd.m_VhoverTimescale);
        XWfloat("VBUO", vd.m_VehicleBuoyancy);

        //Attractor properties
        XWfloat("VAEFF", vd.m_verticalAttractionEfficiency);
        XWfloat("VATIME", vd.m_verticalAttractionTimescale);

        // InWorldz parameters; a simulator that does not know them skips these elements when it loads the record
        XWfloat("MLAZ", vd.m_mouselookAzimuth);
        XWfloat("MLALT", vd.m_mouselookAltitude);
        XWfloat("BAZ", vd.m_bankingAzimuth);
        XWfloat("DMABOVE", vd.m_disableMotorsAbove);
        XWfloat("DMAFTER", vd.m_disableMotorsAfter);
        XWfloat("IBMOD", vd.m_invertedBankingModifier);

        XWQuat("REF_FRAME", vd.m_referenceFrame);

        writer.WriteEndElement();
        writer = null;
    }



    XmlReader reader;

    // which of the six InWorldz elements the record being read held (one bit each, in the order they are written)
    private int m_xmlSeen;

    private int XRint()
    {
        return reader.ReadElementContentAsInt();
    }

    private float XRfloat()
    {
        return reader.ReadElementContentAsFloat();
    }

    public Vector3 XRvector()
    {
        Vector3 vec;
        reader.ReadStartElement();
        vec.X = reader.ReadElementContentAsFloat();
        vec.Y = reader.ReadElementContentAsFloat();
        vec.Z = reader.ReadElementContentAsFloat();
        reader.ReadEndElement();
        return vec;
    }

    public Quaternion XRquat()
    {
        Quaternion q;
        reader.ReadStartElement();
        q.X = reader.ReadElementContentAsFloat();
        q.Y = reader.ReadElementContentAsFloat();
        q.Z = reader.ReadElementContentAsFloat();
        q.W = reader.ReadElementContentAsFloat();
        reader.ReadEndElement();
        return q;
    }

    public static bool EReadProcessors(
        Dictionary<string, Action> processors,
        XmlReader xtr)
    {
        bool errors = false;

        string nodeName = string.Empty;
        while (xtr.NodeType != XmlNodeType.EndElement)
        {
            nodeName = xtr.Name;

            // m_log.LogDebug("[ExternalRepresentationUtils]: Processing: {0}", nodeName);

            Action p = null;
            if (processors.TryGetValue(xtr.Name, out p))
            {
                // m_log.LogDebug("[ExternalRepresentationUtils]: Found {0} processor, nodeName);

                try
                {
                    p();
                }
                catch
                {
                    errors = true;
                    if (xtr.NodeType == XmlNodeType.EndElement)
                        xtr.Read();
                }
            }
            else
            {
                // m_log.LogDebug("[LandDataSerializer]: caught unknown element {0}", nodeName);
                xtr.ReadOuterXml(); // ignore
            }
        }

        return errors;
    }


    public string ToXml2()
    {
        using (StringWriter sw = new StringWriter())
        {
            using (XmlTextWriter xwriter = new XmlTextWriter(sw))
            {
                ToXml2(xwriter);
            }

            return sw.ToString();
        }
    }

    public static SOPVehicle FromXml2(string text)
    {
        if (text.Length == 0)
            return null;

        UTF8Encoding enc = new UTF8Encoding();
        MemoryStream ms = new MemoryStream(enc.GetBytes(text));
        XmlTextReader xreader = new XmlTextReader(ms);
        xreader.DtdProcessing = DtdProcessing.Ignore;

        SOPVehicle v = new SOPVehicle();
        bool error;

        v.FromXml2(xreader, out error);

        xreader.Close();

        if (error)
        {
            v = null;
            return null;
        }
        return v;
    }

    public static SOPVehicle FromXml2(XmlReader reader)
    {
        SOPVehicle vehicle = new SOPVehicle();

        bool errors = false;

        vehicle.FromXml2(reader, out errors);
        if (errors)
            return null;

        return vehicle;
    }

    private void FromXml2(XmlReader _reader, out bool errors)
    {
        errors = false;
        reader = _reader;

        Dictionary<string, Action> m_VehicleXmlProcessors
        = new Dictionary<string, Action>();

        m_VehicleXmlProcessors.Add("TYPE", ProcessXR_type);
        m_VehicleXmlProcessors.Add("FLAGS", ProcessXR_flags);

        // Linear properties
        m_VehicleXmlProcessors.Add("LMDIR", ProcessXR_linearMotorDirection);
        m_VehicleXmlProcessors.Add("LMFTIME", ProcessXR_linearFrictionTimescale);
        m_VehicleXmlProcessors.Add("LMDTIME", ProcessXR_linearMotorDecayTimescale);
        m_VehicleXmlProcessors.Add("LMTIME", ProcessXR_linearMotorTimescale);
        m_VehicleXmlProcessors.Add("LMOFF", ProcessXR_linearMotorOffset);

        //Angular properties
        m_VehicleXmlProcessors.Add("AMDIR", ProcessXR_angularMotorDirection);
        m_VehicleXmlProcessors.Add("AMTIME", ProcessXR_angularMotorTimescale);
        m_VehicleXmlProcessors.Add("AMDTIME", ProcessXR_angularMotorDecayTimescale);
        m_VehicleXmlProcessors.Add("AMFTIME", ProcessXR_angularFrictionTimescale);

        //Deflection properties
        m_VehicleXmlProcessors.Add("ADEFF", ProcessXR_angularDeflectionEfficiency);
        m_VehicleXmlProcessors.Add("ADTIME", ProcessXR_angularDeflectionTimescale);
        m_VehicleXmlProcessors.Add("LDEFF", ProcessXR_linearDeflectionEfficiency);
        m_VehicleXmlProcessors.Add("LDTIME", ProcessXR_linearDeflectionTimescale);

        //Banking properties
        m_VehicleXmlProcessors.Add("BEFF", ProcessXR_bankingEfficiency);
        m_VehicleXmlProcessors.Add("BMIX", ProcessXR_bankingMix);
        m_VehicleXmlProcessors.Add("BTIME", ProcessXR_bankingTimescale);

        //Hover and Buoyancy properties
        m_VehicleXmlProcessors.Add("HHEI", ProcessXR_VhoverHeight);
        m_VehicleXmlProcessors.Add("HEFF", ProcessXR_VhoverEfficiency);
        m_VehicleXmlProcessors.Add("HTIME", ProcessXR_VhoverTimescale);

        m_VehicleXmlProcessors.Add("VBUO", ProcessXR_VehicleBuoyancy);

        //Attractor properties
        m_VehicleXmlProcessors.Add("VAEFF", ProcessXR_verticalAttractionEfficiency);
        m_VehicleXmlProcessors.Add("VATIME", ProcessXR_verticalAttractionTimescale);

        m_VehicleXmlProcessors.Add("REF_FRAME", ProcessXR_referenceFrame);

        // InWorldz parameters, absent from a record saved before they existed
        m_VehicleXmlProcessors.Add("MLAZ", () => { vd.m_mouselookAzimuth = XRfloat(); m_xmlSeen |= 1; });
        m_VehicleXmlProcessors.Add("MLALT", () => { vd.m_mouselookAltitude = XRfloat(); m_xmlSeen |= 2; });
        m_VehicleXmlProcessors.Add("BAZ", () => { vd.m_bankingAzimuth = XRfloat(); m_xmlSeen |= 4; });
        m_VehicleXmlProcessors.Add("DMABOVE", () => { vd.m_disableMotorsAbove = XRfloat(); m_xmlSeen |= 8; });
        m_VehicleXmlProcessors.Add("DMAFTER", () => { vd.m_disableMotorsAfter = XRfloat(); m_xmlSeen |= 16; });
        m_VehicleXmlProcessors.Add("IBMOD", () => { vd.m_invertedBankingModifier = XRfloat(); m_xmlSeen |= 32; });

        vd = new VehicleData();
        m_xmlSeen = 0;

        reader.ReadStartElement("Vehicle", String.Empty);

        errors = EReadProcessors(
            m_VehicleXmlProcessors,
            reader);

        // A value the record does not hold is its type's default
        if (m_xmlSeen != 63)
        {
            SOPVehicle preset = new SOPVehicle();
            preset.ProcessTypeChange(vd.m_type);
            if ((m_xmlSeen & 1) == 0) vd.m_mouselookAzimuth = preset.vd.m_mouselookAzimuth;
            if ((m_xmlSeen & 2) == 0) vd.m_mouselookAltitude = preset.vd.m_mouselookAltitude;
            if ((m_xmlSeen & 4) == 0) vd.m_bankingAzimuth = preset.vd.m_bankingAzimuth;
            if ((m_xmlSeen & 8) == 0) vd.m_disableMotorsAbove = preset.vd.m_disableMotorsAbove;
            if ((m_xmlSeen & 16) == 0) vd.m_disableMotorsAfter = preset.vd.m_disableMotorsAfter;
            if ((m_xmlSeen & 32) == 0) vd.m_invertedBankingModifier = preset.vd.m_invertedBankingModifier;
        }

        reader.ReadEndElement();
        reader = null;
    }

    private void ProcessXR_type()
    {
        vd.m_type = (Vehicle)XRint();
    }
    private void ProcessXR_flags()
    {
        vd.m_flags = (VehicleFlag)XRint();
    }
    // Linear properties
    private void ProcessXR_linearMotorDirection()
    {
        vd.m_linearMotorDirection = XRvector();
    }

    private void ProcessXR_linearFrictionTimescale()
    {
        vd.m_linearFrictionTimescale = XRvector();
    }

    private void ProcessXR_linearMotorDecayTimescale()
    {
        vd.m_linearMotorDecayTimescale = XRfloat();
    }
    private void ProcessXR_linearMotorTimescale()
    {
        vd.m_linearMotorTimescale = XRfloat();
    }
    private void ProcessXR_linearMotorOffset()
    {
        vd.m_linearMotorOffset = XRvector();
    }


    //Angular properties
    private void ProcessXR_angularMotorDirection()
    {
        vd.m_angularMotorDirection = XRvector();
    }
    private void ProcessXR_angularMotorTimescale()
    {
        vd.m_angularMotorTimescale = XRfloat();
    }
    private void ProcessXR_angularMotorDecayTimescale()
    {
        vd.m_angularMotorDecayTimescale = XRfloat();
    }
    private void ProcessXR_angularFrictionTimescale()
    {
        vd.m_angularFrictionTimescale = XRvector();
    }

    //Deflection properties
    private void ProcessXR_angularDeflectionEfficiency()
    {
        vd.m_angularDeflectionEfficiency = XRfloat();
    }
    private void ProcessXR_angularDeflectionTimescale()
    {
        vd.m_angularDeflectionTimescale = XRfloat();
    }
    private void ProcessXR_linearDeflectionEfficiency()
    {
        vd.m_linearDeflectionEfficiency = XRfloat();
    }
    private void ProcessXR_linearDeflectionTimescale()
    {
        vd.m_linearDeflectionTimescale = XRfloat();
    }

    //Banking properties
    private void ProcessXR_bankingEfficiency()
    {
        vd.m_bankingEfficiency = XRfloat();
    }
    private void ProcessXR_bankingMix()
    {
        vd.m_bankingMix = XRfloat();
    }
    private void ProcessXR_bankingTimescale()
    {
        vd.m_bankingTimescale = XRfloat();
    }

    //Hover and Buoyancy properties
    private void ProcessXR_VhoverHeight()
    {
        vd.m_VhoverHeight = XRfloat();
    }
    private void ProcessXR_VhoverEfficiency()
    {
        vd.m_VhoverEfficiency = XRfloat();
    }
    private void ProcessXR_VhoverTimescale()
    {
        vd.m_VhoverTimescale = XRfloat();
    }

    private void ProcessXR_VehicleBuoyancy()
    {
        vd.m_VehicleBuoyancy = XRfloat();
    }

    //Attractor properties
    private void ProcessXR_verticalAttractionEfficiency()
    {
        vd.m_verticalAttractionEfficiency = XRfloat();
    }
    private void ProcessXR_verticalAttractionTimescale()
    {
        vd.m_verticalAttractionTimescale = XRfloat();
    }

    private void ProcessXR_referenceFrame()
    {
        vd.m_referenceFrame = XRquat();
    }
}
