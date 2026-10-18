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
 * Vehicle dynamics ported from InWorldz Halcyon (InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs); Halcyon's notice above applies.
 * No code from OpenSim's BulletSim vehicle code is in this file; the OpenSim notice above stays with the
 * file's header.
 * Adapted 2026 by Legion Builds
 */

using System;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// Internal limits to various vehicle parameters.
    /// Ported from Halcyon VehicleLimits, where they were tuned for PhysX. The limits a
    /// script's vehicle parameters are held to are per-region settings (VehicleSettings),
    /// with these constants as their defaults.
    /// </summary>
    public static class VehicleLimits
    {
        // The interaction between these limits are nonintuitive. Test all cases before settling on any changes.

        public const float MinPhysicsTimestep     = 0.0156f;
        public const float MinPhysicsForce        = 0.001f;
        public const int   NumTimeStepsPerRayCast  = 8;
        public const int   NumTimeStepsPerWindChk  = 8;

        // Threshold values below which activity is considered ended or effectively zero.
        // Most of these values are tuned to be above the squirm point.
        public const float ThresholdLinearMotorDeltaV    = 0.005f;
        public const float ThresholdAngularMotorDeltaV   = (float)(Math.PI / 512.0);
        public const float ThresholdLinearFrictionDeltaV  = 0.002f;
        public const float ThresholdAngularFrictionDeltaV = 0.004f;
        public const float ThresholdDeflectionSpeed      = 0.2f;
        public const float ThresholdDeflectionAngle      = (float)(Math.PI / 64.0);
        public const float ThresholdStictionFactor       = 0.002f;
        public const float ThresholdAngularMotorEngaged  = 0.9f;
        public const float ThresholdLinearMotorEngaged   = 1.0f;
        public const float ThresholdLinearMotorUnstuck   = 0.025f;
        public const float ThresholdAttractorAngle       = (float)(Math.PI / 256.0);
        public const float ThresholdOverturnAngle        = (float)(Math.PI * 0.60);
        public const float ThresholdHoverDragHeight      = 0.3f;
        public const float ThresholdDelayFubar           = 0.06f;
        public const float ThresholdBankAngle            = (float)(Math.PI / 128.0);
        public const float ThresholdMouselookAngle       = (float)(Math.PI / 32.0);
        public const float ThresholdInverseCrossover     = -2.010203f;

        // Maximum values for various forces and time scales.
        public const float MaxLegacyLinearVelocity   = 30.0f;
        public const float MaxLegacyAngularVelocity  = (float)Math.PI;
        public const float MaxLinearVelocity         = 200.0f;
        public const float MaxLinearOffset           = 100.0f;
        public const float MaxAngularVelocity        = (float)(Math.PI * 4.0);
        public const float MaxDecayTimescale         = 120.0f;
        public const float MaxAttractTimescale       = 500.0f;
        public const float MaxHoverTimescale         = 300.0f;
        public const float MaxTimescale              = 1000.0f;
        public const float MaxBankingSpeed           = 10.0f;
        public const float MaxGroundPenetration      = -0.5f;
        public const float MinRegionHeight           = -128.0f;
        public const float MaxRegionHeight           = 10000.0f;
        public const float MaxAttractDormancy        = 1.5f;

        // The angular rate limiter (VehicleDynamics.cs:3107-3116): the angular motor direction is scaled by
        // max((deflection timescale + 0.001) / (deflection efficiency + 0.00001), 0.1) when that ratio is below 1.
        public const float AngularLimiterFloor       = 0.1f;
        public const float AngularLimiterTimescaleBias  = 0.001f;
        public const float AngularLimiterEfficiencyBias = 0.00001f;
    }

    /// <summary>
    /// Utility math for vehicle dynamics.
    /// </summary>
    public static class VehicleMath
    {
        /// <summary>
        /// Returns -1 if negative, +1 if zero or positive.
        /// </summary>
        public static int PosNeg(float f)
        {
            int sign = Math.Sign(f);
            if (sign == 0) return 1;
            return sign;
        }
    }
}
