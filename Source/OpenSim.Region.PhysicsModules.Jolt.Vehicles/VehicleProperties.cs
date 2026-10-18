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
 *
 * The quotations from http://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial
 * are Copyright (c) 2009 Linden Research, Inc and are used under their license
 * of Creative Commons Attribution-Share Alike 3.0
 * (http://creativecommons.org/licenses/by-sa/3.0/).
 */

/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * Vehicle dynamics ported from InWorldz Halcyon (InWorldz.PhysxPhysics/Vehicle/VehicleProperties.cs),
 * through OpenSim's BulletSim vehicle code.
 * The code came to us through BulletSim's port of the Halcyon vehicle code, so the OpenSim notice
 * above and Halcyon's notice both apply.
 * Adapted 2026 by Legion Builds
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    // =====================================================================
    // Float parameter keys — maps to Halcyon FloatParams enum.
    // We use our own enum rather than the OpenSim Vehicle enum because
    // the controller keeps per-axis vector timescales and the additional
    // parameters Halcyon defined, which are not in standard LSL.
    // =====================================================================
    public enum VehFloatParam
    {
        AngularDeflectionEfficiency,
        AngularDeflectionTimescale,
        BankingEfficiency,
        BankingMix,
        BankingTimescale,
        Buoyancy,
        HoverEfficiency,
        HoverHeight,
        HoverTimescale,
        LinearDeflectionEfficiency,
        LinearDeflectionTimescale,
        VerticalAttractionEfficiency,
        VerticalAttractionTimescale,
        // InWorldz extensions (stubbed for future use)
        InvertedBankingModifier,
        MouselookAltitude,
        MouselookAzimuth,
        BankingAzimuth,
        DisableMotorsAbove,
        DisableMotorsAfter,
    }

    // =====================================================================
    // Vector parameter keys — the controller keeps per-axis vectors
    // for timescales, which gives much better vehicle behavior than BSDynamics'
    // scalar timescales.
    // =====================================================================
    public enum VehVectorParam
    {
        LinearFrictionTimescale,
        AngularFrictionTimescale,
        LinearMotorDirection,
        AngularMotorDirection,
        LinearMotorOffset,
        LinearMotorTimescale,
        AngularMotorTimescale,
        LinearMotorDecayTimescale,
        AngularMotorDecayTimescale,
        // InWorldz extensions (stubbed)
        LinearWindEfficiency,
        AngularWindEfficiency,
    }

    // =====================================================================
    // Rotation parameter keys
    // =====================================================================
    public enum VehRotationParam
    {
        ReferenceFrame,
    }

    // =====================================================================
    // Vehicle type enum matching Halcyon types.
    // Standard LSL types map directly to Vehicle.TYPE_* values.
    // =====================================================================
    public enum VehicleType
    {
        None = 0,
        Sled = 1,
        Car = 2,
        Boat = 3,
        Airplane = 4,
        Balloon = 5,
        // InWorldz's two extra types, as scripts name them: VEHICLE_TYPE_SAILBOAT 10001 and VEHICLE_TYPE_MOTORCYCLE 10002
        // (Vehicle.TYPE_SAILBOAT, TYPE_MOTORCYCLE). The numbers here are the module's own.
        Motorcycle = 6,
        Sailboat = 7,
    }

    // =====================================================================
    // Vehicle flags — superset of OpenSim VehicleFlag plus Halcyon extensions.
    // Standard flags use the same bit values as OpenSim VehicleFlag.
    // =====================================================================
    [Flags]
    public enum ExtendedVehicleFlags
    {
        None                = 0,
        NoDeflectionUp      = 1,
        LimitRollOnly       = 2,
        HoverWaterOnly      = 4,
        HoverTerrainOnly    = 8,
        HoverGlobalHeight   = 16,
        HoverUpOnly         = 32,
        LimitMotorUp        = 64,
        MouselookSteer      = 128,
        MouselookBank       = 256,
        CameraDecoupled     = 512,
        NoX                 = 1024,
        NoY                 = 2048,
        NoZ                 = 4096,
        LockHoverHeight     = 8192,
        NoDeflection        = 16384,
        LockRotation        = 32768,
        // Halcyon extensions
        LimitMotorDown      = 1 << 16,
        TorqueWorldZ        = 1 << 17,
        MousePointSteer     = 1 << 18,
        MousePointBank      = 1 << 19,
        ReactToWind         = 1 << 20,
        ReactToCurrents     = 1 << 21,
    }

    /// <summary>
    /// Properties that define a vehicle's behavior. Ported from Halcyon VehicleProperties.
    /// Uses dictionaries keyed by enum for flexibility, matching the Halcyon pattern.
    /// </summary>
    public class VehicleProperties
    {
        public VehicleType Type;
        public ExtendedVehicleFlags Flags;

        public Dictionary<VehFloatParam, float> ParamsFloat;
        public Dictionary<VehVectorParam, Vector3> ParamsVec;
        public Dictionary<VehRotationParam, Quaternion> ParamsRot;

        public VehicleDynamicsState Dynamics;

        public VehicleProperties()
        {
            Type = VehicleType.None;
            Flags = ExtendedVehicleFlags.None;
            ParamsFloat = new Dictionary<VehFloatParam, float>();
            ParamsVec = new Dictionary<VehVectorParam, Vector3>();
            ParamsRot = new Dictionary<VehRotationParam, Quaternion>();
            Dynamics = new VehicleDynamicsState();
        }

        /// <summary>
        /// Merge incoming properties (used for region crossing state transfer).
        /// </summary>
        public void Merge(VehicleProperties other)
        {
            Type = other.Type;
            Flags = other.Flags;

            foreach (var item in other.ParamsFloat)
                ParamsFloat[item.Key] = item.Value;
            foreach (var item in other.ParamsVec)
                ParamsVec[item.Key] = item.Value;
            foreach (var item in other.ParamsRot)
                ParamsRot[item.Key] = item.Value;

            Dynamics = other.Dynamics;
        }

        // =================================================================
        // Helper to safely read a float param with a default
        // =================================================================
        public float GetFloat(VehFloatParam key, float defaultVal = 0f)
        {
            return ParamsFloat.TryGetValue(key, out float val) ? val : defaultVal;
        }

        public Vector3 GetVec(VehVectorParam key)
        {
            return ParamsVec.TryGetValue(key, out Vector3 val) ? val : Vector3.Zero;
        }

        public Quaternion GetRot(VehRotationParam key)
        {
            return ParamsRot.TryGetValue(key, out Quaternion val) ? val : Quaternion.Identity;
        }
    }
}
