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
 * Vehicle dynamics ported from InWorldz Halcyon (InWorldz.PhysxPhysics/Vehicle/DynamicsSimulationData.cs); Halcyon's notice above applies.
 * No code from OpenSim's BulletSim vehicle code is in this file; the OpenSim notice above stays with the
 * file's header.
 * Adapted 2026 by Legion Builds
 */

using System;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// Internal dynamics values used by the vehicle simulator.
    /// These are runtime state values that feed forward between simulation frames.
    /// Ported from Halcyon DynamicsSimulationData (protobuf removed).
    /// </summary>
    public class VehicleDynamicsState
    {
        public Vector3 LocalLinearVelocity;
        public Vector3 LocalAngularVelocity;

        // Motor dynamics (in local coordinates)
        public Vector3 LinearTargetVelocity;
        public float LinearDecayIndex;
        public Vector3 AngularTargetVelocity;
        public float AngularDecayIndex;

        public DateTime LastAccessTOD;

        public Vector3 AngularDirection;
        public Vector3 LinearDirection;

        public Vector3 LastPosition;
        public Vector3 ShortTermPositionDelta;

        public float LastVerticalAngle;
        public float VerticalForceAdjust;

        public Vector3 TargetAngularDelta;
        public Vector3 TargetLinearDelta;

        public float Timestep;

        public float BankingDirection;
        public float BankingTargetVelocity;

        public uint LastVerticalFrameNumber;

        // Seconds since the vehicle's tilt last changed by the attractor's threshold (InWorldz's attractor dormancy);
        // infinite until it first does.
        public float AttractorQuietTime;

        public Vector3 WindDirection;
        public Vector3 WaterDirection;

        public VehicleDynamicsState()
        {
            LocalLinearVelocity = Vector3.Zero;
            LocalAngularVelocity = Vector3.Zero;
            LinearTargetVelocity = Vector3.Zero;
            LinearDecayIndex = 0f;
            AngularTargetVelocity = Vector3.Zero;
            AngularDecayIndex = 0f;
            LastAccessTOD = DateTime.Now;
            AngularDirection = Vector3.Zero;
            LinearDirection = Vector3.Zero;
            LastPosition = Vector3.Zero;
            ShortTermPositionDelta = Vector3.Zero;
            LastVerticalAngle = 0f;
            VerticalForceAdjust = 1.0f;
            TargetAngularDelta = Vector3.Zero;
            TargetLinearDelta = Vector3.Zero;
            Timestep = VehicleLimits.MinPhysicsTimestep;
            BankingDirection = 0f;
            BankingTargetVelocity = 0f;
            LastVerticalFrameNumber = 0;
            AttractorQuietTime = float.PositiveInfinity;
            WindDirection = Vector3.Zero;
            WaterDirection = Vector3.Zero;
        }

        /// <summary>
        /// Reset all dynamics state to defaults. Called on vehicle type change or motor reset.
        /// </summary>
        public void Reset()
        {
            LocalLinearVelocity = Vector3.Zero;
            LocalAngularVelocity = Vector3.Zero;
            LinearTargetVelocity = Vector3.Zero;
            LinearDecayIndex = 0f;
            AngularTargetVelocity = Vector3.Zero;
            AngularDecayIndex = 0f;
            LastAccessTOD = DateTime.Now;
            AngularDirection = Vector3.Zero;
            LinearDirection = Vector3.Zero;
            ShortTermPositionDelta = Vector3.Zero;
            LastVerticalAngle = 0f;
            VerticalForceAdjust = 1.0f;
            TargetAngularDelta = Vector3.Zero;
            TargetLinearDelta = Vector3.Zero;
            Timestep = VehicleLimits.MinPhysicsTimestep;
            BankingDirection = 0f;
            BankingTargetVelocity = 0f;
            LastVerticalFrameNumber = 0;
            AttractorQuietTime = float.PositiveInfinity;
            WindDirection = Vector3.Zero;
            WaterDirection = Vector3.Zero;
        }
    }
}
