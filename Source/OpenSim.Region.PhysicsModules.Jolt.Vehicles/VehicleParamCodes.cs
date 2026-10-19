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
 * The enum is copied from OpenSim's VehicleConstants.cs (the first notice above); the
 * InWorldz extension codes follow the InWorldz Halcyon vehicle code (the second notice).
 * Adapted 2026 by Legion Builds
 */

/*
 * Backend-agnostic vehicle controller.
 *
 * The LSL vehicle parameter wire codes, copied from OpenSim's
 * OpenSim.Region.PhysicsModules.SharedBase.VehicleConstants so this assembly does not link
 * OpenSim. The values are the Second Life protocol constants and are frozen; keeping the enum
 * NAME "Vehicle" lets the controller's switches read the same as the InWorldz Halcyon vehicle
 * code they came from. Hosts cast their int params to this enum.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    public enum Vehicle : int
    {
        TYPE_NONE = 0,
        TYPE_SLED = 1,
        TYPE_CAR = 2,
        TYPE_BOAT = 3,
        TYPE_AIRPLANE = 4,
        TYPE_BALLOON = 5,
        // InWorldz's two extra vehicle types (VehicleType.cs:53-54 of the Halcyon source); Second Life has no such types.
        TYPE_SAILBOAT = 10001,
        TYPE_MOTORCYCLE = 10002,
        LINEAR_FRICTION_TIMESCALE = 16,
        ANGULAR_FRICTION_TIMESCALE = 17,
        LINEAR_MOTOR_DIRECTION = 18,
        ANGULAR_MOTOR_DIRECTION = 19,
        LINEAR_MOTOR_OFFSET = 20,
        HOVER_HEIGHT = 24,
        HOVER_EFFICIENCY = 25,
        HOVER_TIMESCALE = 26,
        BUOYANCY = 27,
        LINEAR_DEFLECTION_EFFICIENCY = 28,
        LINEAR_DEFLECTION_TIMESCALE = 29,
        LINEAR_MOTOR_TIMESCALE = 30,
        LINEAR_MOTOR_DECAY_TIMESCALE = 31,
        ANGULAR_DEFLECTION_EFFICIENCY = 32,
        ANGULAR_DEFLECTION_TIMESCALE = 33,
        ANGULAR_MOTOR_TIMESCALE = 34,
        ANGULAR_MOTOR_DECAY_TIMESCALE = 35,
        VERTICAL_ATTRACTION_EFFICIENCY = 36,
        VERTICAL_ATTRACTION_TIMESCALE = 37,
        BANKING_EFFICIENCY = 38,
        BANKING_MIX = 39,
        BANKING_TIMESCALE = 40,
        REFERENCE_FRAME = 44,
        BLOCK_EXIT = 45,
        ROLL_FRAME = 46,
        // InWorldz's float parameters (FloatParams.cs of the Halcyon source): VEHICLE_MOUSELOOK_AZIMUTH and _ALTITUDE (the
        // range the camera's turn is a share of), VEHICLE_BANKING_AZIMUTH (the roll's range for banking),
        // VEHICLE_DISABLE_MOTORS_ABOVE and _AFTER (the height and delay of the in-air motor disable), and
        // VEHICLE_INVERTED_BANKING_MODIFIER. Second Life has none of them.
        MOUSELOOK_AZIMUTH = 11001,
        MOUSELOOK_ALTITUDE = 11002,
        BANKING_AZIMUTH = 11003,
        DISABLE_MOTORS_ABOVE = 11004,
        DISABLE_MOTORS_AFTER = 11005,
        INVERTED_BANKING_MODIFIER = 11006,
        // InWorldz's two wind vector parameters (VectorParams.cs:59-60 of the Halcyon source): how much of the wind, or of the
        // water's current, at the vehicle's position pushes it along each of its axes (linear) and rolls and pitches it
        // (angular). Second Life has neither.
        LINEAR_WIND_EFFICIENCY = 12001,
        ANGULAR_WIND_EFFICIENCY = 12002
    }
}
