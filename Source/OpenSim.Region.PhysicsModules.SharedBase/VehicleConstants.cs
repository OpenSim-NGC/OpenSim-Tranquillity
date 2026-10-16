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

namespace OpenSim.Region.PhysicsModules.SharedBase;

public enum Vehicle : int
{
    /// <summary>
    /// Turns off Vehicle Support
    /// </summary>
    TYPE_NONE = 0,

    /// <summary>
    /// No Angular motor, High Left right friction, No Hover, Linear Deflection 1, no angular deflection
    /// no vertical attractor, No banking, Identity rotation frame
    /// </summary>
    TYPE_SLED = 1,

    /// <summary>
    /// Needs Motors to be driven by timer or control events  High left/right friction, No angular friction
    /// Linear Motor wins in a second, decays in 60 seconds.  Angular motor wins in a second, decays in 8/10ths of a second
    /// linear deflection 2 seconds
    /// Vertical Attractor locked UP
    /// </summary>
    TYPE_CAR = 2,
    TYPE_BOAT = 3,
    TYPE_AIRPLANE = 4,
    TYPE_BALLOON = 5,

    /// <summary>InWorldz's VEHICLE_TYPE_SAILBOAT; Second Life has no such type.</summary>
    TYPE_SAILBOAT = 10001,

    /// <summary>InWorldz's VEHICLE_TYPE_MOTORCYCLE; Second Life has no such type.</summary>
    TYPE_MOTORCYCLE = 10002,
    LINEAR_FRICTION_TIMESCALE = 16,
    /// <summary>
    /// vector of timescales for exponential decay of angular velocity about three axis
    /// </summary>
    ANGULAR_FRICTION_TIMESCALE = 17,
    /// <summary>
    /// linear velocity vehicle will try for
    /// </summary>
    LINEAR_MOTOR_DIRECTION = 18,

    /// <summary>
    /// Offset from center of mass where linear motor forces are added
    /// </summary>
    LINEAR_MOTOR_OFFSET = 20,
    /// <summary>
    /// angular velocity that vehicle will try for
    /// </summary>
    ANGULAR_MOTOR_DIRECTION = 19,
    HOVER_HEIGHT = 24,
    HOVER_EFFICIENCY = 25,
    HOVER_TIMESCALE = 26,
    BUOYANCY = 27,
    LINEAR_DEFLECTION_EFFICIENCY = 28,
    LINEAR_DEFLECTION_TIMESCALE = 29,
    LINEAR_MOTOR_TIMESCALE = 30,
    LINEAR_MOTOR_DECAY_TIMESCALE = 31,

    /// <summary>
    /// slide between 0 and 1
    /// </summary>
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

    // InWorldz's float parameters (Halcyon FloatParams.cs). Second Life has none of them.
    /// <summary>The range of camera turn, in radians, that mouselook steering counts as a full turn.</summary>
    MOUSELOOK_AZIMUTH = 11001,
    /// <summary>The range of camera pitch, in radians, that mouselook steering counts as a full pitch.</summary>
    MOUSELOOK_ALTITUDE = 11002,
    /// <summary>The range of roll, in radians, that banking counts as full.</summary>
    BANKING_AZIMUTH = 11003,
    /// <summary>The height above the ground, in metres, beyond which the motors are disabled after DISABLE_MOTORS_AFTER.</summary>
    DISABLE_MOTORS_ABOVE = 11004,
    /// <summary>The time, in seconds, a vehicle may stay above DISABLE_MOTORS_ABOVE before its motors are disabled.</summary>
    DISABLE_MOTORS_AFTER = 11005,
    /// <summary>Multiplier of the banking efficiency while the vehicle is upside down.</summary>
    INVERTED_BANKING_MODIFIER = 11006
}

[Flags]
public enum VehicleFlag
{
    NO_DEFLECTION_UP = 1,
    LIMIT_ROLL_ONLY = 2,
    HOVER_WATER_ONLY = 4,
    HOVER_TERRAIN_ONLY = 8,
    HOVER_GLOBAL_HEIGHT = 16,
    HOVER_UP_ONLY = 32,
    LIMIT_MOTOR_UP = 64,
    MOUSELOOK_STEER = 128,
    MOUSELOOK_BANK = 256,
    CAMERA_DECOUPLED = 512,
    NO_X = 1024,
    NO_Y = 2048,
    NO_Z = 4096,
    LOCK_HOVER_HEIGHT = 8192,
    NO_DEFLECTION = 16392,
    LOCK_ROTATION = 32784,

    // InWorldz's flags, with the numbers its scripts send (Halcyon VehicleFlags.cs).
    REACT_TO_CURRENTS = 0x10000,
    REACT_TO_WIND = 0x20000,
    LIMIT_MOTOR_DOWN = 0x40000,
    TORQUE_WORLD_Z = 0x80000,
    MOUSEPOINT_STEER = 0x100000,
    MOUSEPOINT_BANK = 0x200000
}

public struct VehicleData
{
    public Vehicle m_type;
    public VehicleFlag m_flags;

    // Linear properties
    public Vector3 m_linearMotorDirection;
    public Vector3 m_linearFrictionTimescale;
    public float m_linearMotorDecayTimescale;
    public float m_linearMotorTimescale;
    public Vector3 m_linearMotorOffset;

    //Angular properties
    public Vector3 m_angularMotorDirection;
    public float m_angularMotorTimescale;
    public float m_angularMotorDecayTimescale;
    public Vector3 m_angularFrictionTimescale;

    //Deflection properties
    public float m_angularDeflectionEfficiency;
    public float m_angularDeflectionTimescale;
    public float m_linearDeflectionEfficiency;
    public float m_linearDeflectionTimescale;

    //Banking properties
    public float m_bankingEfficiency;
    public float m_bankingMix;
    public float m_bankingTimescale;

    //Hover and Buoyancy properties
    public float m_VhoverHeight;
    public float m_VhoverEfficiency;
    public float m_VhoverTimescale;
    public float m_VehicleBuoyancy;

    //Attractor properties
    public float m_verticalAttractionEfficiency;
    public float m_verticalAttractionTimescale;

    // Axis
    public Quaternion m_referenceFrame;

    // InWorldz's float parameters 11001 to 11006
    public float m_mouselookAzimuth;
    public float m_mouselookAltitude;
    public float m_bankingAzimuth;
    public float m_disableMotorsAbove;
    public float m_disableMotorsAfter;
    public float m_invertedBankingModifier;
}
