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
 * The enablement and debugging switches (DoAngularDeflection through DebugRegionChange) are ported from InWorldz Halcyon
 * (InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs, class VehicleLimits: the Do* and Debug* switches), made per-region
 * instance properties instead of statics. The limits take their defaults from VehicleLimits.cs.
 * Adapted 2026 by Legion Builds
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>Which values llSetVehicleType gives a vehicle type.</summary>
    public enum VehiclePresetSet
    {
        /// <summary>Second Life's documented defaults, from the wiki page of each type (VEHICLE_TYPE_CAR and the rest).</summary>
        Documented,

        /// <summary>The InWorldz Halcyon values, for vehicles tuned against them.</summary>
        Legacy,
    }

    /// <summary>Whose vehicle behaviour a region follows where Second Life's documentation and the InWorldz simulator differ.</summary>
    public enum VehicleBehaviour
    {
        /// <summary>What Second Life's wiki documents.</summary>
        SecondLife,

        /// <summary>The InWorldz Halcyon simulator's behaviour where it differs from the documented one (see <see cref="VehicleSettings.Behaviour"/>).</summary>
        InWorldz,
    }

    /// <summary>
    /// The per-region settings every vehicle controller in a region shares: the preset set and the limits a script's
    /// vehicle parameters are held to. The host builds one from its configuration when the region starts. Each default
    /// is the value of the matching constant in VehicleLimits, except the motor decay cap, which is Second Life's
    /// documented 120 s, and the preset set, which is the documented one.
    /// </summary>
    public sealed class VehicleSettings
    {
        /// <summary>The settings a controller has when the host gives it none.</summary>
        public static readonly VehicleSettings Default = new VehicleSettings();

        public VehiclePresetSet Presets { get; init; } = VehiclePresetSet.Documented;

        /// <summary>
        /// Whose behaviour the vehicle code follows where Second Life's pages and the InWorldz Halcyon code differ.
        /// <see cref="VehicleBehaviour.SecondLife"/>, the default of a controller that is given no settings, is what the pages
        /// document; a region's default comes from [Jolt] VehicleBehaviour, which is InWorldz (JoltConfig). <see cref="VehicleBehaviour.InWorldz"/>
        /// turns on the behaviours of Halcyon's vehicle code listed in the description of [Jolt] VehicleBehaviour in
        /// RegionServerDefaults.ini; it does not choose the preset values, which are
        /// <see cref="Presets"/>. The host picks both from its configuration (JoltConfig.ToVehicleSettings).
        /// </summary>
        public VehicleBehaviour Behaviour { get; init; } = VehicleBehaviour.SecondLife;

        /// <summary>Largest linear motor speed a script can set (m/s, each axis), and the cap on the motor's result.</summary>
        public float MaxLinearSpeed { get; init; } = VehicleLimits.MaxLinearVelocity;

        /// <summary>The forward speed (m/s) at which dynamic banking and angular deflection reach full strength; their
        /// share is speed / this, at most 1.</summary>
        public float ReferenceSpeed { get; init; } = VehicleLimits.MaxLegacyLinearVelocity;

        /// <summary>Largest angular motor speed a script can set (rad/s, each axis), and the cap on the angular result.</summary>
        public float MaxAngularSpeed { get; init; } = VehicleLimits.MaxAngularVelocity;

        /// <summary>
        /// The angular rate limiter's floor. Setting the angular motor direction scales it by the ratio
        /// (angular deflection timescale + 0.001) / (angular deflection efficiency + 0.00001) when that is below 1 (a
        /// vehicle that deflects hard and fast is not turned as fast by its motor), but by no less than this share.
        /// 0.1 is the InWorldz figure (VehicleDynamics.cs:3107-3116); 1 turns the limiter off. Second Life documents
        /// no such limiter (its only cap on the angular motor is the 4 pi rad/s magnitude, <see cref="MaxAngularSpeed"/>).
        /// </summary>
        public float AngularLimiterFloor { get; init; } = VehicleLimits.AngularLimiterFloor;

        /// <summary>Shortest timescale a script can set (s).</summary>
        public float MinTimescale { get; init; } = VehicleLimits.MinPhysicsTimestep;

        /// <summary>Longest friction, motor or deflection timescale a script can set (s). A timescale of 1000 s or more
        /// stays "off" for friction and deflection, so with a cap below 1000 a script cannot turn them off.</summary>
        public float MaxTimescale { get; init; } = VehicleLimits.MaxTimescale;

        /// <summary>Longest motor decay timescale a script can set (s): Second Life documents 120.</summary>
        public float MaxDecayTimescale { get; init; } = VehicleLimits.MaxDecayTimescale;

        /// <summary>True when the region's configuration sets <see cref="MaxDecayTimescale"/> itself. Where it does not,
        /// <see cref="VehicleBehaviour.InWorldz"/> holds the decay timescale to InWorldz's 1000 s (see
        /// <see cref="EffectiveMaxDecayTimescale"/>).</summary>
        public bool MaxDecayTimescaleSet { get; init; } = false;

        /// <summary>
        /// The longest motor decay timescale a script can set under this region's behaviour: <see cref="MaxDecayTimescale"/>
        /// where the configuration set it, else 120 s under Second Life's behaviour (its pages' range) and 1000 s under
        /// InWorldz's (the Halcyon code holds the linear and angular motor decay timescales to its longest timescale,
        /// VehicleDynamics.cs:406-421; its 120 s constant is named for them and not used there).
        /// </summary>
        public float EffectiveMaxDecayTimescale
            => MaxDecayTimescaleSet || Behaviour != VehicleBehaviour.InWorldz ? MaxDecayTimescale : VehicleLimits.MaxTimescale;

        /// <summary>A hover timescale at or above this turns hover off (s); also the longest a script can set.</summary>
        public float MaxHoverTimescale { get; init; } = VehicleLimits.MaxHoverTimescale;

        /// <summary>A vertical attraction or banking timescale at or above this turns them off (s); also the longest a
        /// script can set for the attractor.</summary>
        public float MaxAttractTimescale { get; init; } = VehicleLimits.MaxAttractTimescale;

        /// <summary>Largest linear motor offset a script can set (m, each axis).</summary>
        public float MaxMotorOffset { get; init; } = VehicleLimits.MaxLinearOffset;

        /// <summary>The range a script's hover height is held to (m).</summary>
        public float MinHoverHeight { get; init; } = VehicleLimits.MinRegionHeight;
        public float MaxHoverHeight { get; init; } = VehicleLimits.MaxRegionHeight;
        /// <summary>
        /// The sled's slope assist at full declination, as a share of gravity: a sled whose nose points down is pushed
        /// along its nose at this share of g times the square root of how far the nose points down (the sine of its
        /// pitch). 0.045 is what the InWorldz code gave at the 15 ms step it was tuned at (3 g applied as a force
        /// times the step, over that step).
        /// </summary>
        public float SledAssist { get; init; } = DefaultSledAssist;

        public const float DefaultSledAssist = 0.045f;

        /// <summary>
        /// An override for the contact friction of every part of a vehicle body. Null, the default, leaves each part its
        /// own material's friction, which adds to the vehicle's friction timescales while it slides in contact (Havok 4
        /// and later, and Halcyon for every type but the sled); a number replaces the material's.
        /// </summary>
        public float? ContactFriction { get; init; }

        /// <summary>
        /// The contact friction of every part of a sled, whatever its material. 0 by default: Halcyon meant the whole sled to
        /// be frictionless (VehicleDynamics.cs, the sled case of SetVehicleDefaultActions). It wins over
        /// <see cref="ContactFriction"/> for a sled. Each part keeps its own restitution, density and gravity multiplier.
        /// </summary>
        public float SledContactFriction { get; init; } = 0f;

        /// <summary>
        /// The rest speed (m/s) of the rest rule (<see cref="VehicleController.HoldsAtRest"/>): a vehicle with no motor
        /// pulling, moving slower than this, whose steady speed on every axis from its present pose is also slower
        /// than this, is held still so that its body can sleep. 0 turns the rule off.
        /// </summary>
        public float RestSpeed { get; init; } = DefaultRestSpeed;

        public const float DefaultRestSpeed = 0.1f;

        /// <summary>
        /// Under <see cref="VehicleBehaviour.InWorldz"/>, whether the linear motor's rule that its target replaces a lower velocity
        /// ("the target ensures forward progress", VehicleDynamics.cs:2585-2589) also acts on an axis the motor has never had a
        /// direction on. False, the default, leaves such an axis its own velocity: the rule is there to get a driven axis going
        /// against friction (and it goes on acting on an axis that was driven, after the key goes up, which is how a car glides to
        /// a stop). On an axis never driven it is a one-sided pull (the sign of a zero direction is positive, so a velocity below
        /// the target is pulled up to it and one above is only braked) toward whatever velocity the ramp last saw there, a bounce,
        /// say, which pushed a car that had left a crest down the slope to twice the motor's speed. True is Halcyon's code as written.
        /// </summary>
        public bool MotorTargetOnUndrivenAxes { get; init; } = false;

        /// <summary>
        /// Whether a vehicle's step cuts the velocity spikes the engine's contacts put into it (the cut Halcyon's vehicle code
        /// made, see the "Spike mitigation" region of VehicleController). Null, the default, follows the behaviour: on under
        /// <see cref="VehicleBehaviour.InWorldz"/>, off under <see cref="VehicleBehaviour.SecondLife"/>. True or false forces it.
        /// </summary>
        public bool? SpikeMitigation { get; init; } = null;

        /// <summary>True when the spike cut acts in this region's vehicles.</summary>
        public bool SpikeMitigationOn => SpikeMitigation ?? Behaviour == VehicleBehaviour.InWorldz;

        // Enablement switches, useful for debugging specific features. A region's settings, so that switching one off
        // in one region does not change the vehicles of another. All are true by default.
        public bool DoAngularDeflection { get; init; } = true;
        public bool DoLinearDeflection { get; init; } = true;
        public bool DoMotors { get; init; } = true;
        public bool DoAngularFriction { get; init; } = true;
        public bool DoLinearFriction { get; init; } = true;
        public bool DoVerticalAttractor { get; init; } = true;
        public bool DoBanking { get; init; } = true;

        // Debugging switches: all false by default. Stored for the region; no vehicle code reads one yet.
        public bool DebugPrintParams { get; init; } = false;
        public bool DebugTimestep { get; init; } = false;
        public bool DebugSpikeDetection { get; init; } = false;
        public bool DebugVehicleChange { get; init; } = false;
        public bool DebugAngular { get; init; } = false;
        public bool DebugBanking { get; init; } = false;
        public bool DebugBlendedZ { get; init; } = false;
        public bool DebugLinear { get; init; } = false;
        public bool DebugAttractor { get; init; } = false;
        public bool DebugRayCast { get; init; } = false;
        public bool DebugWind { get; init; } = false;
        public bool DebugAngularFriction { get; init; } = false;
        public bool DebugLinearFriction { get; init; } = false;
        public bool DebugAngularMotor { get; init; } = false;
        public bool DebugLinearMotor { get; init; } = false;
        public bool DebugDeflection { get; init; } = false;
        public bool DebugRegionChange { get; init; } = false;
    }
}
