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
 * Vehicle dynamics ported from InWorldz Halcyon (InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs),
 * through OpenSim's BulletSim vehicle code.
 * The code came to us through BulletSim's port of the Halcyon vehicle code, so the OpenSim notice
 * above and Halcyon's notice both apply.
 * Adapted 2026 by Legion Builds
 *
 * A backend-agnostic vehicle controller. The behaviours Second Life documents (the motors and
 * friction, hover, buoyancy, the vertical attractor, banking and deflection) follow its documented
 * vehicle model (Linden_Vehicle_Tutorial and the llSetVehicle* pages of the SL wiki; each
 * Simulate method names its source). The sled's slope assist is not a documented Second Life
 * behaviour: it is carried over from the InWorldz Halcyon sled movement (SledAssist). The frame
 * structure, the parameter routing and the legacy type presets come from the InWorldz Halcyon
 * vehicle code, as OpenSim's BulletSim vehicle code expresses it; the legacy values are kept
 * behind VehiclePresets = legacy (VehicleSettings). The behaviours of the Halcyon code that differ from the documented
 * model are on only where the region chooses VehicleSettings.Behaviour = InWorldz; the description of
 * [Jolt] VehicleBehaviour in RegionServerDefaults.ini lists them.
 * The physics engine is reached only through IVehicleBody, per this table (BulletSim's names
 * on the left):
 *
 *   ControllingPrim.ForceOrientation            -> _body.Orientation
 *   ControllingPrim.ForceVelocity (get/set)     -> _body.LinearVelocity   (same read-back semantics)
 *   ControllingPrim.ForceRotationalVelocity     -> _body.AngularVelocity  (same read-back semantics)
 *   ControllingPrim.ForcePosition               -> _body.Position
 *   ControllingPrim.TotalMass                   -> _body.Mass
 *   ControllingPrim.Inertia                     -> _body.InertiaDiagonal
 *   ControllingPrim.AddForce(true, f)           -> _body.AddForce(f)      (central force, next step)
 *   ControllingPrim.AddAngularForce(true, t)    -> _body.AddTorque(t)     (torque, next step)
 *   ControllingPrim.ActivateIfPhysical(false)   -> _body.KeepAwake()
 *   ControllingPrim.ComputeGravity(buoy)        -> _body.SetGravityFactor(1 - buoy): the engine applies it
 *                                                  (GravModifier = 1)
 *   BSParam.Gravity                             -> _body.Gravity.Z
 *   BSParam.VehicleGroundGravityFudge           -> GroundGravityFactor (the host's setting; 1 leaves gravity whole)
 *   GetTerrainHeight/GetWaterLevel              -> _body.GetTerrainHeight/_body.GetWaterLevel
 *   m_physicsScene.PE.PushUpdate                -> dropped (Bullet-only activation nudge; the host
 *                                                  disables sleeping on an active vehicle body)
 *   VDetailLog                                  -> dropped (logging only)
 *   BSActor/scene-event plumbing                -> the HOST steps an active controller before each
 *                                                  physics step and applies/restores body params
 *
 * Simulated: linear and angular motors with their timescales and decay, linear and angular
 * friction, hover, buoyancy, the vertical attractor, angular and linear deflection, sled
 * movement and banking-to-yaw, plus the shared frame infrastructure (timestep smoothing, velocity
 * anti-jitter, motor reset, stall detection, ground-penetration fix, the vehicle's gravity share, torque
 * accumulator), and mouselook steering and banking (VEHICLE_FLAG_MOUSELOOK_STEER, _BANK) toward the driver's camera
 * the host supplies (DriverCamera). Not simulated: wind.
 *
 * The motors and friction are solved by VehicleMotorSolver, hover and the vertical attractor by
 * VehicleSpring, each as the exact solution over the step the engine takes.
 *
 * One evaluation-order note: ApplyGravity's ground fudge test is written here as
 * `GroundVehicle && _body.HasCollision` (BulletSim: `HasSomeCollision && IsGroundVehicle`) so
 * the engine query short-circuits away for non-ground vehicles; the result is identical.
 *
 * THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 */

using System;
using OpenMetaverse;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Backend.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Vehicles.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Harness.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Scenarios.Avatar.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Scenarios.Prim.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Scenarios.Vehicle.Tests")]

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// The vehicle controller, backend-agnostic: Second Life's documented vehicle behaviours and the sled's
    /// slope assist from the Halcyon sled movement, on a frame structure derived from the Halcyon vehicle code.
    /// The host owns one instance per vehicle body and calls Step(dt) every frame BEFORE the
    /// physics step while the vehicle is active and physical. A host whose engine splits its step into collision steps
    /// calls <see cref="BeginUpdate"/> before the step and <see cref="StepOnce"/> before each collision step instead, so that
    /// the terms that integrate over time are worked out over the collision step's own length.
    /// </summary>
    /// <summary>
    /// What the vehicle knows of its driver's camera: the world direction it looks along, and whether the driver is in
    /// mouselook (the viewer's AGENT_CONTROL_MOUSELOOK control flag, as the simulator's CameraData.MouseLook).
    /// </summary>
    public readonly record struct DriverView(Vector3 At, bool InMouselook);

    public sealed class VehicleController
    {
        // The neutral physics seam this controller reads/writes through. _hostBody is the host's body, which the
        // Update-rate work (BeginUpdate, Rest, Hold and the script-facing calls) uses. _body is the body the code in front
        // of it reads and writes: the host's, except inside StepOnce, where a host that steps inside the engine's collision
        // steps (Jolt's step listener) hands in its own view of the body for that one step.
        private readonly IVehicleBody _hostBody;
        private IVehicleBody _body;

        // Vehicle properties and runtime state, as the script calls set them (the live set).
        private VehicleProperties _props;

        // The snapshot of the live properties the steps read: the type, flags, parameters and reference frame as they were
        // when BeginUpdate (or a call that asks a question of the vehicle) last took them. Script threads change the live
        // set at any time and never this one, which is written only on the host's thread between engine steps, so what a
        // collision step reads does not change while the engine steps. Its Dynamics is the live one: the running state of the
        // motors, which the steps advance. A script's motor set reaches it through the pending set below.
        private readonly VehicleProperties _sp = new VehicleProperties();
        private volatile bool _paramsDirty = true;
        private static readonly VehFloatParam[] FloatKeys = (VehFloatParam[])Enum.GetValues(typeof(VehFloatParam));
        private static readonly VehVectorParam[] VectorKeys = (VehVectorParam[])Enum.GetValues(typeof(VehVectorParam));
        private static readonly VehRotationParam[] RotationKeys = (VehRotationParam[])Enum.GetValues(typeof(VehRotationParam));

        /// <summary>
        /// Whether a call that changes the motors' running state waits for the next <see cref="BeginUpdate"/> (or question put
        /// to the vehicle) instead of acting at once: a script's motor direction set, a type change (which clears the
        /// running state), <see cref="StopMotors"/> and <see cref="ResumeAfterHold"/>. The host that steps inside the
        /// engine's collision steps sets it, so that a call from a script or scene thread never changes the running state
        /// while the engine steps; the effect is the same, because the state only acts in a step. Calls are applied at the
        /// start of the next Update, the clearing ones before the motor sets (a motor set made after one of them is the
        /// later call, and one made before it was dropped when the call was made).
        /// </summary>
        public bool DeferStateChanges { get; set; }

        // Taken by every call that writes the live properties and by the snapshot copy, so the copy never reads a
        // dictionary a script thread is changing. Lock order: _propsGate, then _pendingGate.
        private readonly object _propsGate = new object();

        private readonly object _pendingGate = new object();
        private Vector3 _pendingLinear, _pendingAngular;
        private bool _hasPendingLinear, _hasPendingAngular;

        // Calls that clear or stop the running state, waiting for the next Update (written under _pendingGate; the first two are
        // also read without it, by the questions below).
        private volatile bool _pendingTypeReset, _pendingStop;
        private bool _pendingResume;
        private MotorRunningState? _pendingRunLinear, _pendingRunAngular;

        // True while any of the three waits (read without the lock, like the motor flags, so the common case, nothing
        // waiting, costs nothing).
        private volatile bool _hasPendingState;

        private void SetLinearMotor(Vector3 direction)
        {
            if (!DeferStateChanges)
            {
                MoveLinear(direction);
                return;
            }
            lock (_pendingGate)
            {
                _pendingLinear = direction;
                _hasPendingLinear = true;
            }
        }

        private void SetAngularMotor(Vector3 direction)
        {
            if (!DeferStateChanges)
            {
                MoveAngular(direction);
                return;
            }
            lock (_pendingGate)
            {
                _pendingAngular = direction;
                _hasPendingAngular = true;
            }
        }

        // Takes the live properties and the pending motor sets into the snapshot. On the host's thread, between engine steps.
        private void RefreshSnapshot()
        {
            if (_hasPendingLinear || _hasPendingAngular || _hasPendingState)
            {
                Vector3 linear, angular;
                bool hasLinear, hasAngular, typeReset, stop, resume;
                MotorRunningState? runLinear, runAngular;
                lock (_pendingGate)
                {
                    runLinear = _pendingRunLinear;
                    runAngular = _pendingRunAngular;
                    _pendingRunLinear = _pendingRunAngular = null;
                    linear = _pendingLinear;
                    angular = _pendingAngular;
                    hasLinear = _hasPendingLinear;
                    hasAngular = _hasPendingAngular;
                    typeReset = _pendingTypeReset;
                    stop = _pendingStop;
                    resume = _pendingResume;
                    _hasPendingLinear = _hasPendingAngular = false;
                    _pendingTypeReset = _pendingStop = _pendingResume = false;
                    _hasPendingState = false;
                }
                if (typeReset)
                    ResetRunningState();
                if (stop)
                    StopMotorsNow();
                if (resume)
                    _props.Dynamics.LastAccessTOD = Clock();
                if (runLinear.HasValue || runAngular.HasValue)
                    ApplyRunningMotors(runLinear, runAngular);
                if (hasLinear)
                    MoveLinear(linear);
                if (hasAngular)
                    MoveAngular(angular);
            }
            if (!_paramsDirty)
                return;
            lock (_propsGate)
                CopySnapshot();
        }

        private void CopySnapshot()
        {
            _paramsDirty = false;
            _sp.Type = _props.Type;
            _sp.Flags = _props.Flags;
            foreach (VehFloatParam key in FloatKeys)
                if (_props.ParamsFloat.TryGetValue(key, out float f))
                    _sp.ParamsFloat[key] = f;
            foreach (VehVectorParam key in VectorKeys)
                if (_props.ParamsVec.TryGetValue(key, out Vector3 v))
                    _sp.ParamsVec[key] = v;
            foreach (VehRotationParam key in RotationKeys)
                if (_props.ParamsRot.TryGetValue(key, out Quaternion q))
                    _sp.ParamsRot[key] = q;
            _sp.Dynamics = _props.Dynamics;
        }

        // A type change clears the running state: the motors off until a script sets one, the clocks and carries back to
        // their start.
        private void ResetRunningState()
        {
            _props.Dynamics.Reset();
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _props.Dynamics.LastAccessTOD = Clock();
            _bankingAirTime = 0;
        }

        // Cached mass
        private float m_vehicleMass;

        /// <summary>The body's mass as <see cref="BeginUpdate"/> read it.</summary>
        public float BodyMass => m_vehicleMass;

        // The clock the motor reset and the spike checks read: the time since the last step or motor set.
        // The wall clock by default; a host that steps faster or slower than real time (a test harness)
        // supplies a simulated one.
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        // Frame counter for periodic operations
        private uint m_frameNum;


        // The share of gravity a ground vehicle (car or sled) gets while it touches something: BulletSim's
        // VehicleGroundGravityFudge, 0.2 there. 1 leaves gravity whole. Set by the host from its configuration.
        public float GroundGravityFactor { get; set; } = 1f;

        // The driver's camera (the simulator's CameraData: CameraAtAxis and MouseLook), for VEHICLE_FLAG_MOUSELOOK_STEER
        // and VEHICLE_FLAG_MOUSELOOK_BANK; null, or no function, when no driver's camera is known. Read on the physics
        // step. Set by the host.
        public Func<DriverView?> DriverCamera { get; set; }

        // The region's vehicle settings (preset set and limits). Set by the host before the first vehicle type is set.
        public VehicleSettings Settings { get; set; } = VehicleSettings.Default;
        private VehicleSettings S => Settings;

        // The region follows InWorldz's vehicle behaviour where [Jolt] VehicleBehaviour says (VehicleSettings.Behaviour; the
        // places are listed in the description of that key in RegionServerDefaults.ini). Each is written so that with Second
        // Life's behaviour the arithmetic is what it was without the choice.
        private bool InWorldz => S.Behaviour == VehicleBehaviour.InWorldz;

        // The lift that brings a vehicle found under the ground back up: m/s, m/s per metre under the ground, and the most.
        private const float GroundLiftBase = 1f, GroundLiftPerMetre = 3f, GroundLiftMax = 10f;
        private bool _groundLifting;

        // =====================================================================
        // Ephemeral per-frame computed values (not persisted)
        // =====================================================================
        private Quaternion _vframe;
        private Quaternion _rotation;
        private Vector3 _worldAngularVel;
        private Vector3 _worldLinearVel;
        private Vector3 _localAngularVel;
        private Vector3 _localLinearVel;

        // The body's velocities as read at the start of the step, before the anti-jitter filter.
        private Vector3 _rawWorldAngularVel;
        private Vector3 _rawWorldLinearVel;
        private Vector3 _rawLocalLinearVel;

        // =====================================================================
        // Stall detection state (per-frame, not persisted)
        // =====================================================================
        private bool _linearMotorStalled;
        private bool _linearMotorStallChecked;

        // =====================================================================
        // Torque accumulator (Halcyon pattern: accumulate per-frame, apply once)
        // =====================================================================
        private Vector3 _accumTorqueVelChange;
        private Vector3 _accumTorqueImpulse;

        // The angular motor's and angular friction's change this step. Applied whole in TorqueFini: the near-zero
        // clean-up there drops a fixed amount per step, which would stop the motor and friction short of their
        // equation by more the shorter the step.
        private Vector3 _motorTorqueVelChange;

        // =====================================================================
        // Constructor
        // =====================================================================
        public VehicleController(IVehicleBody body)
        {
            _hostBody = body;
            _body = body;
            _props = new VehicleProperties();
            SetVehicleDefaults(VehicleType.None);
            RefreshSnapshot();
        }

        // =====================================================================
        // Active state. (IsPhysicallyActive is not checked here: the HOST only steps the
        // controller while the body is physical.)
        // =====================================================================
        public bool IsActive
        {
            get { return (_props.Type != VehicleType.None); }
        }

        /// <summary>
        /// True once a script has set the linear motor, until the motors are reset. A type change or StopMotors that waits for
        /// the next Update (<see cref="DeferStateChanges"/>) already counts: the motors are reported as they will be once it
        /// has landed, so a host asking between Updates is not told of a motor the call has ended. A motor set that waits is
        /// not counted until it lands.
        /// </summary>
        public bool LinearMotorSet => !(_pendingTypeReset || _pendingStop) && !float.IsPositiveInfinity(_props.Dynamics.LinearDecayIndex);

        public bool IsGroundVehicle
        {
            // A motorcycle is a car where InWorldz tells the two apart (VehicleDynamics.cs:2826).
            get { return (_props.Type == VehicleType.Car || _props.Type == VehicleType.Motorcycle || _props.Type == VehicleType.Sled); }
        }

        // The same, for the step: the type as of the last snapshot.
        private bool GroundVehicle
            => _sp.Type == VehicleType.Car || _sp.Type == VehicleType.Motorcycle || _sp.Type == VehicleType.Sled;

        /// <summary>Read a current float vehicle param (preset default + any llSetVehicleFloatParam override).
        /// Introspection for the host / tests - e.g. asserting the boat preset's buoyancy.</summary>
        public float GetFloatParam(VehFloatParam key) => _props.GetFloat(key, 0f);

        /// <summary>Read a current vector vehicle param (preset default + any llSetVehicleVectorParam override).
        /// Introspection for the host / tests - e.g. asserting the car preset's friction/motor timescales.</summary>
        public Vector3 GetVecParam(VehVectorParam key) => _props.GetVec(key);

        /// <summary>The current vehicle flags (preset + any llSetVehicleFlags / llRemoveVehicleFlags). For tests.</summary>
        internal ExtendedVehicleFlags Flags => _props.Flags;

        /// <summary>The current reference frame (llSetVehicleRotationParam VEHICLE_REFERENCE_FRAME). For tests.</summary>
        internal Quaternion ReferenceFrame => _props.GetRot(VehRotationParam.ReferenceFrame);

        /// <summary>True once a script has set the angular motor, until the motors are reset; as <see cref="LinearMotorSet"/>. For tests.</summary>
        internal bool AngularMotorSet => !(_pendingTypeReset || _pendingStop) && !float.IsPositiveInfinity(_props.Dynamics.AngularDecayIndex);

        /// <summary>The seconds since each motor was set, as its decay clock reads (negative while a short motor's decay is
        /// held back under InWorldz's behaviour). For tests.</summary>
        internal float LinearMotorAge => _props.Dynamics.LinearDecayIndex;
        internal float AngularMotorAge => _props.Dynamics.AngularDecayIndex;

        /// <summary>The factor the attractor's strength has grown to while the vehicle is overturned (InWorldz's behaviour;
        /// 1 otherwise). For tests.</summary>
        internal float AttractorEscalation => _props.Dynamics.VerticalForceAdjust;

        #region Vehicle Parameter Setting — routes from LSL Vehicle wire codes to internal enums

        /// <summary>
        /// The vehicle was held still on purpose and is let go (an object held for the avatars that cross with it). The
        /// controller was not stepped while it was held, which is not a gap in its stepping of the kind
        /// <see cref="CheckResetMotors"/> reads as "idle or just rezzed": its motors, their decay and its hover go on from
        /// where they were.
        /// </summary>
        public void ResumeAfterHold()
        {
            if (!DeferStateChanges)
            {
                _props.Dynamics.LastAccessTOD = Clock();
                return;
            }
            lock (_pendingGate)
            {
                _pendingResume = true;
                _hasPendingState = true;
            }
        }

        // =================================================================
        // Process vehicle type change from LSL
        // =================================================================
        public void ProcessTypeChange(Vehicle pType)
        {
            lock (_propsGate)
            {
                ProcessTypeChangeCore(pType);
                _paramsDirty = true;
            }
        }

        private void ProcessTypeChangeCore(Vehicle pType)
        {
            VehicleType newType;
            switch (pType)
            {
                case Vehicle.TYPE_NONE:     newType = VehicleType.None; break;
                case Vehicle.TYPE_SLED:     newType = VehicleType.Sled; break;
                case Vehicle.TYPE_CAR:      newType = VehicleType.Car; break;
                case Vehicle.TYPE_BOAT:     newType = VehicleType.Boat; break;
                case Vehicle.TYPE_AIRPLANE: newType = VehicleType.Airplane; break;
                case Vehicle.TYPE_BALLOON:  newType = VehicleType.Balloon; break;
                case Vehicle.TYPE_SAILBOAT:    newType = VehicleType.Sailboat; break;
                case Vehicle.TYPE_MOTORCYCLE:  newType = VehicleType.Motorcycle; break;
                default:                    newType = VehicleType.None; break;
            }

            // A motor set made before the type change is dropped by it. The running state is cleared now, or at the start
            // of the next Update when the host defers it (DeferStateChanges); the type and the presets are live properties
            // and reach the steps through the snapshot, at the same boundary. No motor runs until a script sets one.
            lock (_pendingGate)
            {
                _hasPendingLinear = _hasPendingAngular = false;
                if (DeferStateChanges)
                {
                    _pendingTypeReset = true;
                    _pendingStop = false;
                    _pendingResume = false;
                    _hasPendingState = true;
                }
            }
            _props.Type = newType;
            SetVehicleDefaults(newType);
            if (!DeferStateChanges)
                ResetRunningState();

            // (No scene-event registration or Refresh here; the host reacts to IsActive instead:
            // per-frame Step drive + body physical params.)
        }

        // =================================================================
        // Process float param from LSL
        // =================================================================
        public void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            lock (_propsGate)
            {
                ProcessFloatVehicleParamCore(pParam, pValue);
                _paramsDirty = true;
            }
        }

        private void ProcessFloatVehicleParamCore(Vehicle pParam, float pValue)
        {
            // ClampF passes NaN through (Math.Max/Min propagate it), so a non-finite script value
            // would be stored and drive the motors. Ignore it; the stored param is unchanged.
            if (!float.IsFinite(pValue))
                return;
            bool hoverWasOn = InWorldz && HoverCommanded();
            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale] = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    // Scalar set → apply to all 3 axes
                    pValue = ClampF(pValue, S.MinTimescale, S.EffectiveMaxDecayTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency] = ClampF(pValue, -1f, 1f);
                    break;
                case Vehicle.BANKING_MIX:
                    _props.ParamsFloat[VehFloatParam.BankingMix] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.BankingTimescale] = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    break;
                case Vehicle.BUOYANCY:
                    // InWorldz holds it to -2..30 here and to the vehicle's gravity multiplier where it is used (Buoyancy;
                    // VehicleDynamics.cs:273-274, 314-323); Second Life's page gives -1 to 1.
                    _props.ParamsFloat[VehFloatParam.Buoyancy] = InWorldz ? ClampF(pValue, -2f, 30f) : ClampF(pValue, -1f, 1f);
                    break;
                // InWorldz's parameters, held to the ranges of the InWorldz simulator's ClampFloatParam
                // (VehicleDynamics.cs:164-218); the disable-motors pair has no range there.
                case Vehicle.MOUSELOOK_AZIMUTH:
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth] = ClampF(pValue, VehicleLimits.ThresholdMouselookAngle, (float)Math.PI);
                    break;
                case Vehicle.MOUSELOOK_ALTITUDE:
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude] = ClampF(pValue, VehicleLimits.ThresholdMouselookAngle, (float)Math.PI);
                    break;
                case Vehicle.BANKING_AZIMUTH:
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth] = ClampF(pValue, VehicleLimits.ThresholdBankAngle, (float)Math.PI);
                    break;
                case Vehicle.DISABLE_MOTORS_ABOVE:
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove] = pValue;
                    break;
                case Vehicle.DISABLE_MOTORS_AFTER:
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter] = pValue;
                    break;
                case Vehicle.INVERTED_BANKING_MODIFIER:
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier] = ClampF(pValue, -10f, 10f);
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.HOVER_HEIGHT:
                    _props.ParamsFloat[VehFloatParam.HoverHeight] = ClampF(pValue, S.MinHoverHeight, S.MaxHoverHeight);
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.HoverTimescale] = ClampF(pValue, S.MinTimescale, S.MaxHoverTimescale);
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale] = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.EffectiveMaxDecayTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency] = ClampF(pValue, 0f, 1f);
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale] = ClampF(pValue, S.MinTimescale, S.MaxAttractTimescale);
                    break;

                // These are vector properties but LSL allows setting them as a single float
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = HeldAngularMotor(new Vector3(pValue, pValue, pValue));
                    if (!ScriptMotorIgnored())
                        SetAngularMotor(_props.ParamsVec[VehVectorParam.AngularMotorDirection]);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue = ClampF(pValue, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue = ClampF(pValue, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = new Vector3(pValue, pValue, pValue);
                    SetLinearMotor(_props.ParamsVec[VehVectorParam.LinearMotorDirection]);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue = ClampF(pValue, -S.MaxMotorOffset, S.MaxMotorOffset);
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset] = new Vector3(pValue, pValue, pValue);
                    break;
            }

            // InWorldz: "Special case for legacy scripts: height<=0 nd eff=0 means no hover". A hover that was on is cleared
            // and its timescale put to the off value (VehicleDynamics.cs:1562-1571).
            if (hoverWasOn && HoverOffByLegacyRule(_props))
                _props.ParamsFloat[VehFloatParam.HoverTimescale] = S.MaxHoverTimescale;
        }

        // Hover is commanded: its timescale is below the off value and the legacy rule does not switch it off.
        private bool HoverCommanded()
            => _props.GetFloat(VehFloatParam.HoverTimescale, 1000f) < S.MaxHoverTimescale && !HoverOffByLegacyRule(_props);

        // InWorldz: a hover height of zero or below with efficiency zero is no hover (VehicleDynamics.cs:1563-1564).
        private static bool HoverOffByLegacyRule(VehicleProperties p)
            => p.GetFloat(VehFloatParam.HoverHeight, 0f) <= 0f && p.GetFloat(VehFloatParam.HoverEfficiency, 0f) == 0f;

        // =================================================================
        // Process vector param from LSL
        // =================================================================
        public void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            lock (_propsGate)
            {
                ProcessVectorVehicleParamCore(pParam, pValue);
                _paramsDirty = true;
            }
        }

        private void ProcessVectorVehicleParamCore(Vehicle pParam, Vector3 pValue)
        {
            if (!float.IsFinite(pValue.X) || !float.IsFinite(pValue.Y) || !float.IsFinite(pValue.Z))
                return;   // non-finite: see ProcessFloatVehicleParam
            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, S.MinTimescale, S.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, S.MinTimescale, S.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale] = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    pValue = HeldAngularMotor(pValue);
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection] = pValue;
                    if (!ScriptMotorIgnored())
                        SetAngularMotor(pValue);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue.X = ClampF(pValue.X, S.MinTimescale, S.MaxTimescale);
                    pValue.Y = ClampF(pValue.Y, S.MinTimescale, S.MaxTimescale);
                    pValue.Z = ClampF(pValue.Z, S.MinTimescale, S.MaxTimescale);
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale] = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue = HeldLinearMotor(pValue);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection] = pValue;
                    SetLinearMotor(pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    pValue.X = ClampF(pValue.X, -S.MaxMotorOffset, S.MaxMotorOffset);
                    pValue.Y = ClampF(pValue.Y, -S.MaxMotorOffset, S.MaxMotorOffset);
                    pValue.Z = ClampF(pValue.Z, -S.MaxMotorOffset, S.MaxMotorOffset);
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset] = pValue;
                    break;
                // InWorldz takes a value for each axis of the four motor timescales (llSetVehicleVectorParam with codes 30,
                // 31, 34 and 35; Halcyon's VectorParams.cs:45-52 and VehicleDynamics.cs:406-427), each held to the same
                // range the float setter gives all three. Second Life documents these as floats and the call is ignored.
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.LinearMotorTimescale] = HeldTimescales(pValue, S.MaxTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.AngularMotorTimescale] = HeldTimescales(pValue, S.MaxTimescale);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale] = HeldTimescales(pValue, S.EffectiveMaxDecayTimescale);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = HeldTimescales(pValue, S.EffectiveMaxDecayTimescale);
                    break;
                // InWorldz's wind parameters (VehicleDynamics.cs:448-459): how much of the wind pushes the vehicle along each of its
                // axes and rolls and pitches it, each axis held to -10..10. Second Life has no such parameters and ignores them.
                case Vehicle.LINEAR_WIND_EFFICIENCY:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.LinearWindEfficiency] = HeldWindEfficiency(pValue);
                    break;
                case Vehicle.ANGULAR_WIND_EFFICIENCY:
                    if (InWorldz)
                        _props.ParamsVec[VehVectorParam.AngularWindEfficiency] = HeldWindEfficiency(pValue);
                    break;
                case Vehicle.BLOCK_EXIT:
                    // Not simulated; ignored, as Halcyon did
                    break;
            }
        }

        private static Vector3 HeldWindEfficiency(Vector3 v)
            => new Vector3(
                ClampF(v.X, -HalcyonVehicleLaws.MaxWindEfficiency, HalcyonVehicleLaws.MaxWindEfficiency),
                ClampF(v.Y, -HalcyonVehicleLaws.MaxWindEfficiency, HalcyonVehicleLaws.MaxWindEfficiency),
                ClampF(v.Z, -HalcyonVehicleLaws.MaxWindEfficiency, HalcyonVehicleLaws.MaxWindEfficiency));

        // =================================================================
        // Process rotation param from LSL
        // =================================================================
        public void ProcessRotationVehicleParam(Vehicle pParam, Quaternion pValue)
        {
            lock (_propsGate)
            {
                ProcessRotationVehicleParamCore(pParam, pValue);
                _paramsDirty = true;
            }
        }

        private void ProcessRotationVehicleParamCore(Vehicle pParam, Quaternion pValue)
        {
            switch (pParam)
            {
                case Vehicle.REFERENCE_FRAME:
                    // A rotation with a NaN or infinite component is not a rotation: refuse it and keep the frame, as
                    // the float and vector parameters do. (Normalize turns a NaN into the identity and an infinity
                    // into NaN, either of which would turn the whole vehicle.)
                    if (!float.IsFinite(pValue.X) || !float.IsFinite(pValue.Y) || !float.IsFinite(pValue.Z) || !float.IsFinite(pValue.W))
                        return;
                    pValue = Quaternion.Normalize(in pValue);
                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = pValue;
                    break;
                case Vehicle.ROLL_FRAME:
                    // Not simulated; ignored, as Halcyon did
                    break;
            }
        }

        // A timescale on each axis, held to the region's shortest timescale and the given longest.
        private Vector3 HeldTimescales(Vector3 v, float longest)
            => new Vector3(ClampF(v.X, S.MinTimescale, longest), ClampF(v.Y, S.MinTimescale, longest), ClampF(v.Z, S.MinTimescale, longest));

        // A motor direction held to the region's limits, each axis.
        private Vector3 HeldLinearMotor(Vector3 v)
            => new Vector3(ClampF(v.X, -S.MaxLinearSpeed, S.MaxLinearSpeed), ClampF(v.Y, -S.MaxLinearSpeed, S.MaxLinearSpeed),
                           ClampF(v.Z, -S.MaxLinearSpeed, S.MaxLinearSpeed));

        // The angular motor direction. Second Life documents the cap on its magnitude ("The magnitude of the angular motor
        // is capped to be no more than two rotations per second (4 * PI radians/sec)"), so the vector is scaled down with
        // its direction kept. The InWorldz simulator holds each axis instead (VehicleDynamics.cs:394-398), which lets a
        // diagonal direction reach the cap times the square root of 3.
        private Vector3 HeldAngularMotor(Vector3 v)
        {
            if (InWorldz)
                return new Vector3(ClampF(v.X, -S.MaxAngularSpeed, S.MaxAngularSpeed), ClampF(v.Y, -S.MaxAngularSpeed, S.MaxAngularSpeed),
                                   ClampF(v.Z, -S.MaxAngularSpeed, S.MaxAngularSpeed));
            // In double, so that a huge component does not overflow the length.
            double length = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z);
            if (!(length > S.MaxAngularSpeed))
                return v;
            float scale = (float)(S.MaxAngularSpeed / length);
            return new Vector3(v.X * scale, v.Y * scale, v.Z * scale);
        }

        // =================================================================
        // Process vehicle flags from LSL
        // =================================================================
        // As on ubODE (ODEDynamics.ProcessVehicleFlags): set these flags, or remove them; remove with -1 removes every
        // flag, and set with -1 sets every flag.
        public void ProcessVehicleFlags(int pParam, bool remove)
        {
            lock (_propsGate)
            {
                ProcessVehicleFlagsCore(pParam, remove);
                _paramsDirty = true;
            }
        }

        private void ProcessVehicleFlagsCore(int pParam, bool remove)
        {
            ExtendedVehicleFlags flags = FromScriptFlags(pParam);

            if (remove)
                _props.Flags &= ~flags;
            else
                _props.Flags |= flags;
        }

        // =================================================================
        // A saved vehicle handed back (the host's PhysicsActor.SetVehicle)
        // =================================================================
        /// <summary>A saved vehicle's flags, in place of every flag it has (its type's).</summary>
        public void RestoreFlags(int flags)
        {
            lock (_propsGate)
            {
                RestoreFlagsCore(flags);
                _paramsDirty = true;
            }
        }

        private void RestoreFlagsCore(int flags)
        {
            _props.Flags = FromScriptFlags(flags);
        }

        // A script's flag number, and the number a saved record keeps, as the engine's flags. The bits 0x1 to 0x8000 are the
        // same in both. The InWorldz extensions are numbered differently: a script sends REACT_TO_CURRENTS 0x10000,
        // REACT_TO_WIND 0x20000, LIMIT_MOTOR_DOWN 0x40000, TORQUE_WORLD_Z 0x80000, MOUSEPOINT_STEER 0x100000 and
        // MOUSEPOINT_BANK 0x200000 (the InWorldz simulator's VehicleFlags.cs), where the engine's enum has them at
        // 1 << 16 to 1 << 21 in another order. A bit above 0x200000 has no flag and is kept where it is, so -1 is still
        // every bit.
        internal static ExtendedVehicleFlags FromScriptFlags(int script)
        {
            ExtendedVehicleFlags flags = (ExtendedVehicleFlags)(script & (0xFFFF | ~0x3FFFFF));
            if ((script & 0x10000) != 0) flags |= ExtendedVehicleFlags.ReactToCurrents;
            if ((script & 0x20000) != 0) flags |= ExtendedVehicleFlags.ReactToWind;
            if ((script & 0x40000) != 0) flags |= ExtendedVehicleFlags.LimitMotorDown;
            if ((script & 0x80000) != 0) flags |= ExtendedVehicleFlags.TorqueWorldZ;
            if ((script & 0x100000) != 0) flags |= ExtendedVehicleFlags.MousePointSteer;
            if ((script & 0x200000) != 0) flags |= ExtendedVehicleFlags.MousePointBank;
            return flags;
        }

        /// <summary>
        /// A saved vehicle's flags over the flags its type's preset gave it: each flag where the record differs from
        /// <paramref name="presetFlags"/> (the flags the record's own preset holds) takes the record's value, and every
        /// other flag keeps what the controller's preset set. Those flags are the ones a script set or removed.
        /// </summary>
        public void RestoreFlagsOverPreset(int flags, int presetFlags)
        {
            lock (_propsGate)
            {
                RestoreFlagsOverPresetCore(flags, presetFlags);
                _paramsDirty = true;
            }
        }

        private void RestoreFlagsOverPresetCore(int flags, int presetFlags)
        {
            ExtendedVehicleFlags record = FromScriptFlags(flags);
            ExtendedVehicleFlags changed = record ^ FromScriptFlags(presetFlags);
            _props.Flags = (_props.Flags & ~changed) | (record & changed);
        }

        /// <summary>Both motors and the banking turn off, their directions kept as settings: a failed region crossing, as
        /// ubODE's ODEDynamics.Stop (motor effect 0), so a vehicle put back inside the edge does not drive out again until a
        /// script sets a motor.</summary>
        public void StopMotors()
        {
            lock (_pendingGate)
            {
                _hasPendingLinear = _hasPendingAngular = false;
                if (DeferStateChanges)
                {
                    _pendingStop = true;
                    _hasPendingState = true;
                    return;
                }
            }
            StopMotorsNow();
        }

        private void StopMotorsNow()
        {
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _props.Dynamics.LinearTargetVelocity = Vector3.Zero;
            _props.Dynamics.AngularTargetVelocity = Vector3.Zero;
            _props.Dynamics.BankingDirection = 0;
            _props.Dynamics.BankingTargetVelocity = 0;
        }

        /// <summary>A saved vehicle's motor directions, held to the region's limits as a script's are, kept as its settings
        /// without starting either motor: the motors run once a script sets them again, as on ubODE, whose
        /// ODEDynamics.DoSetVehicle stores the directions with no motor effect.</summary>
        public void RestoreMotorDirections(Vector3 linear, Vector3 angular)
        {
            lock (_propsGate)
            {
                RestoreMotorDirectionsCore(linear, angular);
                _paramsDirty = true;
            }
        }

        private void RestoreMotorDirectionsCore(Vector3 linear, Vector3 angular)
        {
            if (float.IsFinite(linear.X) && float.IsFinite(linear.Y) && float.IsFinite(linear.Z))
                _props.ParamsVec[VehVectorParam.LinearMotorDirection] = HeldLinearMotor(linear);
            if (float.IsFinite(angular.X) && float.IsFinite(angular.Y) && float.IsFinite(angular.Z))
                _props.ParamsVec[VehVectorParam.AngularMotorDirection] = HeldAngularMotor(angular);
        }

        /// <summary>
        /// The motors' running state, to carry with the vehicle across a region border: for each motor that is running, the
        /// time on its decay clock, the direction it drives toward and the velocity its ramp has reached. A motor that is off
        /// (its clock at or past <see cref="HalcyonVehicleLaws.MotorOffClock"/>) gives null. A motor set just before the call, not
        /// yet taken into the running state, is counted as set at its start.
        /// </summary>
        public void GetRunningMotors(out MotorRunningState? linear, out MotorRunningState? angular)
        {
            Vector3 pendingLinear, pendingAngular;
            bool hasLinear, hasAngular;
            lock (_pendingGate)
            {
                pendingLinear = _pendingLinear;
                pendingAngular = _pendingAngular;
                hasLinear = _hasPendingLinear;
                hasAngular = _hasPendingAngular;
            }
            lock (_propsGate)
            {
                VehicleDynamicsState d = _props.Dynamics;
                linear = hasLinear
                    ? new MotorRunningState(MotorDecayStart(VehVectorParam.LinearMotorTimescale), pendingLinear, d.LinearTargetVelocity)
                    : d.LinearDecayIndex < HalcyonVehicleLaws.MotorOffClock
                        ? new MotorRunningState(d.LinearDecayIndex, d.LinearDirection, d.LinearTargetVelocity)
                        : (MotorRunningState?)null;
                angular = hasAngular
                    ? new MotorRunningState(MotorDecayStart(VehVectorParam.AngularMotorTimescale), pendingAngular, d.AngularTargetVelocity)
                    : d.AngularDecayIndex < HalcyonVehicleLaws.MotorOffClock
                        ? new MotorRunningState(d.AngularDecayIndex, d.AngularDirection, d.AngularTargetVelocity)
                        : (MotorRunningState?)null;
            }
        }

        /// <summary>
        /// A vehicle that arrived over a region border takes up its motors where they were (<see cref="GetRunningMotors"/>):
        /// the decay clock, the direction and the ramp's velocity, each held to the region's limits as a script's are.
        /// Called after the motor directions are restored, which leave the motors off.
        /// </summary>
        public void RestoreRunningMotors(MotorRunningState? linear, MotorRunningState? angular)
        {
            if (!linear.HasValue && !angular.HasValue)
                return;
            if (!DeferStateChanges)
            {
                ApplyRunningMotors(linear, angular);
                return;
            }
            lock (_pendingGate)
            {
                if (linear.HasValue) _pendingRunLinear = linear;
                if (angular.HasValue) _pendingRunAngular = angular;
                _hasPendingState = true;
            }
        }

        private void ApplyRunningMotors(MotorRunningState? linear, MotorRunningState? angular)
        {
            VehicleDynamicsState d = _props.Dynamics;
            if (linear is MotorRunningState l && IsFinite(l) && l.Age < HalcyonVehicleLaws.MotorOffClock)
            {
                d.LinearDirection = HeldLinearMotor(l.Direction);
                d.LinearTargetVelocity = HeldLinearMotor(l.Target);
                // An axis with a direction or a target running down has been driven.
                _linearAxesDriven = new Vector3(
                    d.LinearDirection.X != 0f || d.LinearTargetVelocity.X != 0f ? 1f : 0f,
                    d.LinearDirection.Y != 0f || d.LinearTargetVelocity.Y != 0f ? 1f : 0f,
                    d.LinearDirection.Z != 0f || d.LinearTargetVelocity.Z != 0f ? 1f : 0f);
                d.LinearDecayIndex = l.Age;
                d.TargetLinearDelta = Vector3.Zero;
            }
            if (angular is MotorRunningState a && IsFinite(a) && a.Age < HalcyonVehicleLaws.MotorOffClock)
            {
                d.AngularDirection = HeldAngularMotor(a.Direction);
                d.AngularTargetVelocity = HeldAngularMotor(a.Target);
                d.AngularDecayIndex = a.Age;
                d.TargetAngularDelta = Vector3.Zero;
            }
            d.LastAccessTOD = Clock();
            _hostBody.KeepAwake();
        }

        private static bool IsFinite(MotorRunningState s)
            => float.IsFinite(s.Age) && float.IsFinite(s.Direction.X) && float.IsFinite(s.Direction.Y) && float.IsFinite(s.Direction.Z)
            && float.IsFinite(s.Target.X) && float.IsFinite(s.Target.Y) && float.IsFinite(s.Target.Z);

        #endregion // Vehicle Parameter Setting

        #region Step — Main Simulation Entry Point

        /// <summary>
        /// One whole step for a host that has no collision steps inside its engine step: <see cref="BeginUpdate"/> and then
        /// <see cref="StepOnce"/> over the same time, in the place of Halcyon's VehicleDynamics.Simulate(). A host whose
        /// engine splits a step into collision steps calls BeginUpdate before the engine step and StepOnce before each
        /// collision step instead.
        /// </summary>
        public void Step(float pTimestep)
        {
            if (!IsActive) return;
            BeginUpdate(pTimestep);
            StepOnce(pTimestep, _hostBody);
        }

        /// <summary>
        /// The part of a step that runs once per engine step (Update), on the host's thread, before the engine step: the
        /// terms that are events or read the world (the stale-motor reset, the camera steering, the ground-penetration fix,
        /// the stall check, the motor-height rays, the wind). They write the host body's velocity and the state
        /// <see cref="StepOnce"/> reads, and do not move while the engine steps.
        /// </summary>
        public void BeginUpdate(float pTimestep)
        {
            if (!IsActive) return;
            RefreshSnapshot();
            Vector3 linearBefore = _hostBody.LinearVelocity, angularBefore = _hostBody.AngularVelocity;

            m_frameNum++;
            m_vehicleMass = _hostBody.Mass;
            _stepSeconds = pTimestep;

            // The Halcyon code smoothed the step 0.8/0.2 per step, and filtered a velocity whose sign flipped since the
            // last step; both made the result depend on the step rate, and at a steady rate the smoothed step is the step.
            float timeStep = pTimestep;

            ReadState();

            // -------------------------------------------------------
            // Reset motors if stale (more than 1 second since last access)
            CheckResetMotors(timeStep);

            // Initialize torque accumulator and stall detection
            TorqueInit();
            ClearLinearMotorStalled();

            _sp.Dynamics.Timestep = timeStep;

            // InWorldz's camera steering sets the angular motor from the driver's camera before the motors act.
            if (InWorldz)
                SimulateMouselookInput();

            // -------------------------------------------------------
            // Ground penetration fix: a vehicle whose centre is more than 0.5 m under the ground (edited down while physical, or
            // pushed through) is lifted back. Halcyon adds a velocity change of 1 + 3 |vz| m/s at every Update while it is under
            // (VehicleDynamics.cs:668-678); each lift raises |vz| and so the next lift, about fourfold per Update, and a vehicle
            // edited 2 m under the ground leaves the region's height. Here the lift is a speed that is bounded (1 m/s and 3 m/s
            // for each metre the centre is still under the ground, at most 10 m/s) and goes on until the centre is at the ground,
            // where it ends and takes the vertical speed it gave back off, so the vehicle comes to the ground and stays there.
            // A vehicle already rising faster than the lift speed is left as it is.
            float groundHeight = _body.GetTerrainHeight(_body.Position);
            float aboveGround = _body.Position.Z - groundHeight;
            if (aboveGround < VehicleLimits.MaxGroundPenetration)
                _groundLifting = true;
            if (_groundLifting)
            {
                float rising = _body.LinearVelocity.Z;
                if (aboveGround >= 0f)
                {
                    _groundLifting = false;
                    if (rising > 0f)
                        ApplyLinearVelocityChange(new Vector3(0f, 0f, -rising));
                }
                else
                {
                    float liftSpeed = Math.Min(GroundLiftBase + GroundLiftPerMetre * -aboveGround, GroundLiftMax);
                    if (rising < liftSpeed)
                        ApplyLinearVelocityChange(new Vector3(0f, 0f, liftSpeed - rising));
                }
            }

            // -------------------------------------------------------
            // InWorldz: a boat that runs its leading hull edge onto the ground loses its linear motor.
            if (InWorldz)
                SimulateBoatGroundDrag();

            // -------------------------------------------------------
            // The stall check reads the position change over the step, which is noise inside a collision step: it is
            // made here, once, and the steps read its result. Only the sled's slope assist and InWorldz's motors ask for it.
            if (InWorldz || _sp.Type == VehicleType.Sled)
                IsLinearMotorStalled();

            // -------------------------------------------------------
            // InWorldz's motor-height rule: the down rays, and the clock that switches the motors off.
            SimulateMotorHeight(pTimestep);

            // The wind is not read here: it is sampled and applied in every collision step (StepCore).

            // Any torque added so far is applied now: StepOnce starts its own accumulator.
            TorqueFini();

            // Save state for next frame
            _sp.Dynamics.LastPosition = _body.Position;
            _sp.Dynamics.Timestep = timeStep;
            // What this method changed is not the engine's: the engine's last step stays in the difference.
            _spikeLeftLinear += _hostBody.LinearVelocity - linearBefore;
            _spikeLeftAngular += _hostBody.AngularVelocity - angularBefore;
        }

        /// <summary>
        /// The part of a step that runs once per collision step of the engine (Jolt's step listener) or, for a host with
        /// none, once per step after <see cref="BeginUpdate"/>: the terms that integrate over time. The linear and angular
        /// motors and their ramp and decay, the friction on both, deflection, the vertical attractor, banking, hover, the
        /// sled's slope assist and the vehicle's share of gravity. Each is the exact solution of its equation over
        /// <paramref name="h"/>, the collision step's own length, from the body as it is at the start of the step, and is
        /// written as a change of velocity, never a force or torque (a force added from inside a collision step would be
        /// applied again in each later one).
        /// <para>
        /// It reads the parameters and results <see cref="BeginUpdate"/> left, which nothing changes while the engine
        /// steps, and the velocities, orientation and position of <paramref name="body"/> as of now. <paramref name="body"/>
        /// is the host's view of the body for this step: its velocity writes are what the step is.
        /// </para>
        /// </summary>
        public void StepOnce(float h, IVehicleBody body)
        {
            if (!IsActive) return;
            IVehicleBody host = _body;
            _body = body;
            try
            {
                StepCore(h);
            }
            finally
            {
                _body = host;
            }
        }

        // The length of the step being run. InWorldz's laws have a few constants that are per step, not per second (the
        // smallest change worth making); they were tuned for Halcyon's 15 ms step, so a step of h seconds takes h / 15 ms of
        // each (HalcyonStepShare), the same acceleration at every Update rate and number of collision steps.
        private float _stepSeconds = VehicleLimits.MinPhysicsTimestep;

        // The share of a per-Halcyon-step constant that a step of the current length takes.
        private float HalcyonStepShare => (float)(_stepSeconds / HalcyonStep);

        // The body's state at the start of a step, in the frames the terms work in.
        private void ReadState()
        {
            _vframe = _sp.GetRot(VehRotationParam.ReferenceFrame);
            _rotation = _body.Orientation * _vframe;

            Vector3 physAngVel = _body.AngularVelocity;
            Vector3 physLinVel = _body.LinearVelocity;
            _worldAngularVel = physAngVel;
            _worldLinearVel = physLinVel;

            // Convert to local coordinates
            Quaternion invRotation = Quaternion.Inverse(_rotation);
            _localAngularVel = _worldAngularVel * invRotation;
            _localLinearVel = _worldLinearVel * invRotation;
            _rawWorldAngularVel = physAngVel;
            _rawWorldLinearVel = physLinVel;
            _rawLocalLinearVel = physLinVel * invRotation;
        }

        private void StepCore(float h)
        {
            _stepSeconds = h;

            // -------------------------------------------------------
            // Read current state from physics engine
            ReadState();

            // Halcyon's cut (on under InWorldz by default, [Jolt] VehicleSpikeMitigation) of a spike the engine's contacts put into the vehicle's velocity (it is kept
            // from PhysX; the tests PhysxSpikeTests show Jolt makes the same spikes on rough ground and across seams).
            if (S.SpikeMitigationOn && MitigatePhysxSpikes && MitigatePhysxSpiking(h))
                ReadState();

            TorqueInit();

            // -------------------------------------------------------
            // Deflection + sled run in the order Angular -> Linear -> Sled, BEFORE hover.
            // Angular deflection — swings the nose toward the velocity direction (weathervane).
            if (S.DoAngularDeflection)
            {
                SimulateAngularDeflection(h);
            }

            // Linear deflection — changing velocity toward the forward axis (the "tracking" bite).
            if (S.DoLinearDeflection)
            {
                SimulateLinearDeflection(h);
            }

            // (The sled's slope assist is an acceleration inside the linear motor and friction step: SledAssist.)

            // -------------------------------------------------------
            // Hover — maintain target height above terrain/water/global
            SimulateHover(h);

            // -------------------------------------------------------
            // Vertical attractor and banking
            _attractorChange = Vector3.Zero;
            float tilt = 0f;
            if (S.DoVerticalAttractor)
            {
                SimulateVerticalAttractor(h, out tilt, out _);
                // InWorldz: with the attractor off its tilt is 0 (VehicleDynamics.cs:1226-1227, 1260), which banking reads.
                if (InWorldz && _sp.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f) >= S.MaxAttractTimescale)
                    tilt = 0f;
            }

            // -------------------------------------------------------
            // InWorldz: the wind, or the water's current, at the vehicle's position pushes it (before the motors, as in
            // Halcyon), sampled and applied in every collision step with the step's own length.
            SimulateWind(h);

            // -------------------------------------------------------
            // Motors and friction, linear and angular, then the banking turn motor. They are stepped over the
            // step the engine is about to take (h), not the smoothed one: each is the exact solution over that step.
            SimulateLinearMotorAndFriction(h, S.DoMotors, S.DoLinearFriction);
            // InWorldz: the rate banking asks for is set before the angular motor, which adds the banking turn to its own change
            // about z (SimulateBanking, HalcyonAngularMotorOnItsClock).
            if (InWorldz)
                SimulateBanking(h, tilt, regionAllowsBanking: S.DoVerticalAttractor);
            float angularz = SimulateAngularMotorAndFriction(h, S.DoMotors, S.DoAngularFriction);
            float banking = !InWorldz && S.DoVerticalAttractor ? SimulateBanking(h, tilt) : 0f;
            _motorTorqueVelChange += new Vector3(0f, 0f, angularz + banking);

            // Apply gravity manually (same pattern as BSDynamics)
            ApplyGravity();

            // Apply the accumulated torque
            TorqueFini();

            // Save state for next frame
            _sp.Dynamics.LocalLinearVelocity = _localLinearVel;
            _sp.Dynamics.LocalAngularVelocity = _localAngularVel;
            RememberVelocitiesForSpikeCheck();
        }

        #region Spike mitigation

        // Halcyon's MitigatePhysxSpiking (InWorldz.PhysxPhysics/Vehicle/VehicleDynamics.cs:996-1035), whose comment reads
        // "PhysX can induce severe angular Y-axis forces and severe Z-axis up forces on rough terrain or across prim edges.
        // When these strong impulses are detected they will be compressed." It compares the vehicle's velocity with the
        // one it left the previous step with:
        //   - an angular acceleration about the local Y axis over 100 rad/s^2 (scaled by (pi - tilt) / pi, where tilt is how
        //     far the vehicle has tipped from level) takes off the angular velocity about local X and Y;
        //   - an upward local Z acceleration over 50 m/s^2 takes off the local Z velocity and half the local Y.
        // Halcyon's two numbers are accelerations over one of its physics steps, 15.6 ms (VehicleLimits.MinPhysicsTimestep);
        // here they are taken over the engine's changes in the last 15.6 ms, however many collision steps those are, so a
        // spike is judged by the same size at every step rate. The change counted is the engine's alone: the velocity the
        // controller left at the end of the previous step is subtracted from the velocity the engine hands back.
        private const int SpikeHistory = 16;
        private const float SpikeLinearLimit = 50f, SpikeAngularLimit = 100f;
        private readonly float[] _spikeSeconds = new float[SpikeHistory];
        private readonly float[] _spikeDvz = new float[SpikeHistory];
        private readonly float[] _spikeDwy = new float[SpikeHistory];
        private int _spikeNewest, _spikeCount;
        private Vector3 _spikeLeftLinear, _spikeLeftAngular;
        private bool _spikeLeftValid;

        /// <summary>False turns the spike cut off (a test compares a run with and without it). Under InWorldz it is on.</summary>
        public bool MitigatePhysxSpikes { get; set; } = true;

        /// <summary>How many times the angular half of the spike cut has acted (tests read it).</summary>
        public int SpikeAngularCuts { get; private set; }

        /// <summary>How many times the upward half of the spike cut has acted (tests read it).</summary>
        public int SpikeLinearCuts { get; private set; }

        private void RememberVelocitiesForSpikeCheck()
        {
            _spikeLeftLinear = _body.LinearVelocity;
            _spikeLeftAngular = _body.AngularVelocity;
            _spikeLeftValid = true;
        }

        // True if it changed the body's velocities (the caller reads the state again).
        private bool MitigatePhysxSpiking(float h)
        {
            if (!_spikeLeftValid || h <= 0f)
                return false;
            Quaternion inverse = Quaternion.Inverse(_rotation);
            Vector3 engineLinear = (_worldLinearVel - _spikeLeftLinear) * inverse;
            Vector3 engineAngular = (_worldAngularVel - _spikeLeftAngular) * inverse;

            _spikeNewest = (_spikeNewest + 1) % SpikeHistory;
            _spikeSeconds[_spikeNewest] = h;
            _spikeDvz[_spikeNewest] = engineLinear.Z;
            _spikeDwy[_spikeNewest] = engineAngular.Y;
            if (_spikeCount < SpikeHistory) _spikeCount++;

            // The engine's change over the last 15.6 ms (at least the newest step).
            float span = 0f, dvz = 0f, dwy = 0f;
            for (int i = 0; i < _spikeCount && span < VehicleLimits.MinPhysicsTimestep; i++)
            {
                int k = (_spikeNewest - i + SpikeHistory) % SpikeHistory;
                span += _spikeSeconds[k];
                dvz += _spikeDvz[k];
                dwy += _spikeDwy[k];
            }

            // How far the vehicle has tipped from level: the angle between its up axis and the world's.
            float upZ = Math.Clamp((Vector3.UnitZ * _rotation).Z, -1f, 1f);
            float tilt = (float)Math.Acos(upZ);
            float angularAccel = dwy / span * (float)((Math.PI - tilt) / Math.PI);
            float linearAccel = dvz / span;

            bool changed = false;
            if (Math.Abs(angularAccel) > SpikeAngularLimit)
            {
                Vector3 local = _worldAngularVel * inverse;
                local.Z = 0f;
                _body.AngularVelocity = _body.AngularVelocity - local * _rotation;
                SpikeAngularCuts++;
                changed = true;
            }
            if (linearAccel > SpikeLinearLimit)
            {
                Vector3 local = _worldLinearVel * inverse;
                local.X = 0f;
                local.Y *= 0.5f;
                _body.LinearVelocity = _body.LinearVelocity - local * _rotation;
                SpikeLinearCuts++;
                changed = true;
            }
            if (changed)
                _spikeCount = 0;   // the history spoke of a velocity that is gone
            return changed;
        }

        #endregion // Spike mitigation

        /// <summary>
        /// True when nothing in the vehicle would move a vehicle at rest: on every axis the motor's pull toward its
        /// direction, g(s) * |M|, is below <see cref="IdleMotorSpeed"/> (a motor never set, faded away, or set to
        /// zero, which only brakes), and hover is off or the vehicle is at its hover height. Friction, the attractor
        /// and deflection only slow or turn a vehicle that is already moving, and gravity is held by what it rests
        /// on. A host may then let the body sleep and call <see cref="Rest"/> instead of <see cref="Step"/> while it
        /// sleeps. A vehicle with VEHICLE_FLAG_MOUSELOOK_STEER or _BANK is never idle: a sleeping vehicle would read no
        /// camera, and the InWorldz simulator keeps such a vehicle awake for that reason (its sleep threshold is 0;
        /// VehicleDynamics.cs:784-789). Under InWorldz's behaviour, where the two MOUSEPOINT flags steer and bank from the
        /// camera too, the same holds for them, and for a vehicle with VEHICLE_FLAG_REACT_TO_WIND or _REACT_TO_CURRENTS, which
        /// the wind moves (the simulator's sleep threshold is 0 for it too, VehicleDynamics.cs:784-789).
        /// </summary>
        public bool IsIdle
            => Refreshed() && (_sp.Flags & (ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank)) == 0
            && !(InWorldz && (_sp.Flags & (ExtendedVehicleFlags.MousePointSteer | ExtendedVehicleFlags.MousePointBank
                | ExtendedVehicleFlags.ReactToWind | ExtendedVehicleFlags.ReactToCurrents)) != 0)
            && MotorIdle(_sp.Dynamics.LinearDirection, _sp.GetVec(VehVectorParam.LinearMotorDecayTimescale), _sp.Dynamics.LinearDecayIndex)
            && MotorIdle(_sp.Dynamics.AngularDirection, _sp.GetVec(VehVectorParam.AngularMotorDecayTimescale), _sp.Dynamics.AngularDecayIndex)
            && HoverIdle()
            && AttractorIdle();

        /// <summary>
        /// The rest rule: true when the vehicle rests on something (it touched something in the last step), is idle
        /// (<see cref="IsIdle"/>: no motor pulling, hover and the attractor done), moves slower than the rest speed ([Jolt] VehicleRestSpeed; turning slower than that many radians a
        /// second), and the steady speed the motor-and-friction equation gives on each axis from its present pose is
        /// also below the rest speed:
        ///
        ///   v_steady = (g M / Tm + a) / (g / Tm + 1 / Tf)       (g the motor's grip, a the vehicle's gravity along the axis)
        ///
        /// With no motor grip and no friction on an axis, any acceleration along it gives an unbounded steady speed. Of
        /// its three axes, the one nearest the vertical is the one the
        /// support pushes along, so gravity along that axis is held and left out. A host may then hold the vehicle
        /// still (zero its velocity) instead of stepping it, so the engine can let it sleep. The rule does not hold a
        /// vehicle that the equation would start moving: a sled let go on a slope, or a car whose friction lets it roll
        /// down one.
        /// </summary>
        public bool HoldsAtRest
        {
            get
            {
                RefreshSnapshot();
                float rest = S.RestSpeed;
                // A vehicle in the air is not at rest: one swinging through upright under its attractor is idle for a
                // moment and turning slowly, and holding it would stop the swing.
                if (rest <= 0f || !IsActive || !_body.HasCollision || !IsIdle)
                    return false;
                if (_body.LinearVelocity.Length() >= rest || _body.AngularVelocity.Length() >= rest)
                    return false;
                return SteadySpeed().Length() < rest;
            }
        }

        /// <summary>
        /// The steady velocity, on each vehicle axis, that the linear motor-and-friction equation settles at from the
        /// vehicle's present pose and motor grip, with gravity along the axis the vehicle rests on taken by the support
        /// (<see cref="HoldsAtRest"/>). Infinite on an axis with an acceleration and neither grip nor friction.
        /// </summary>
        internal Vector3 SteadySpeed()
        {
            RefreshSnapshot();
            _rotation = _body.Orientation * _sp.GetRot(VehRotationParam.ReferenceFrame);
            Vector3 accel = _body.Gravity * GravityShare() * Quaternion.Inverse(_rotation) + SledAssist();
            if (_body.HasCollision)
            {
                // What it rests on takes the gravity along the axis nearest the vertical: the largest share of world up
                // along it.
                Vector3 up = Vector3.UnitZ * Quaternion.Inverse(_rotation);
                float ax = Math.Abs(up.X), ay = Math.Abs(up.Y), az = Math.Abs(up.Z);
                if (az >= ax && az >= ay) accel.Z = 0f;
                else if (ay >= ax) accel.Y = 0f;
                else accel.X = 0f;
            }
            Vector3 motor = _sp.Dynamics.LinearDirection;
            Vector3 motorTs = _sp.GetVec(VehVectorParam.LinearMotorTimescale);
            Vector3 decayTs = _sp.GetVec(VehVectorParam.LinearMotorDecayTimescale);
            Vector3 frictionTs = S.DoLinearFriction ? _sp.GetVec(VehVectorParam.LinearFrictionTimescale) : FrictionOff;
            double age = _sp.Dynamics.LinearDecayIndex;
            return new Vector3(
                (float)SteadyOnAxis(accel.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X),
                (float)SteadyOnAxis(accel.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y),
                (float)SteadyOnAxis(accel.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z));
        }

        /// <summary>The size of the steady speed of the motor-and-friction equation on one axis (see <see cref="HoldsAtRest"/>).</summary>
        internal static double SteadyOnAxis(double accel, double motor, double motorTs, double decayTs, double age, double frictionTs)
        {
            double grip = VehicleMotorSolver.Grip(age, Math.Max(decayTs, VehicleMotorSolver.MinTimescale));
            double pull = grip / Math.Max(motorTs, VehicleMotorSolver.MinTimescale);
            double rate = pull + VehicleMotorSolver.FrictionRate(Math.Max(frictionTs, VehicleMotorSolver.MinTimescale));
            double drive = pull * motor + accel;
            if (rate <= 0)
                return drive == 0 ? 0 : double.PositiveInfinity;
            return Math.Abs(drive / rate);
        }

        private bool Refreshed()
        {
            RefreshSnapshot();
            return true;
        }

        // The attractor has nothing left to do when it is off, the vehicle is upright to within AttractorIdleAngle, or
        // it rests on something that holds its tilt (a car parked on a slope).
        private bool AttractorIdle()
        {
            if (_sp.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f) >= S.MaxAttractTimescale || _body.HasCollision)
                return true;
            Quaternion rotation = _body.Orientation * _sp.GetRot(VehRotationParam.ReferenceFrame);
            return (Vector3.UnitZ * rotation).Z >= Math.Cos(AttractorIdleAngle);
        }

        // Upright to within this (radians) the attractor is done.
        internal const double AttractorIdleAngle = 0.002;

        private bool HoverIdle()
        {
            if (!HoverTarget(out float targetZ, out bool upOnly))
                return true;
            float error = targetZ - _body.Position.Z;
            return Math.Abs(error) < HoverIdleHeight || (upOnly && error <= 0f);
        }

        // A vehicle this close to its hover height (m) has nothing left for hover to do.
        internal const float HoverIdleHeight = 0.01f;

        // A motor pull below this (m/s, or rad/s for the angular motor) cannot move a vehicle at rest; it is under
        // the engine's own sleep threshold (Jolt's point-velocity sleep threshold is 0.03 m/s).
        internal const float IdleMotorSpeed = 0.01f;

        private static bool MotorIdle(Vector3 direction, Vector3 decayTs, float age)
            => VehicleMotorSolver.Grip(age, Math.Max(decayTs.X, VehicleLimits.MinPhysicsTimestep)) * Math.Abs(direction.X) < IdleMotorSpeed
            && VehicleMotorSolver.Grip(age, Math.Max(decayTs.Y, VehicleLimits.MinPhysicsTimestep)) * Math.Abs(direction.Y) < IdleMotorSpeed
            && VehicleMotorSolver.Grip(age, Math.Max(decayTs.Z, VehicleLimits.MinPhysicsTimestep)) * Math.Abs(direction.Z) < IdleMotorSpeed;

        /// <summary>
        /// A step in which the body is asleep and the vehicle idle (<see cref="IsIdle"/>): time passes for the motors'
        /// decay, and the body is left alone. At rest every term of the step is zero (the motors pull toward nothing,
        /// friction has no velocity to slow, gravity is held by the ground), so this is the same as stepping it.
        /// </summary>
        public void Rest(float pTimestep)
        {
            if (!IsActive) return;
            RefreshSnapshot();
            _sp.Dynamics.LastAccessTOD = Clock();
            _sp.Dynamics.LinearDecayIndex += pTimestep;
            _sp.Dynamics.AngularDecayIndex += pTimestep;
            _hoverCarry = null;
            _attractorCarry = null;
            _ownsRoll = false;
            _angularDeflectionRate = Vector3.Zero;
        }

        /// <summary>
        /// A step in which the rest rule holds (<see cref="HoldsAtRest"/>): time passes as in <see cref="Rest"/>, the
        /// vehicle's gravity stays on the body, and its velocity is set to zero, so the engine can let it sleep. Whatever
        /// the engine's step then does to it (a body resting on an edge tips) is read on the next step, which steps the
        /// vehicle again if it is no longer at rest.
        /// </summary>
        public void Hold(float pTimestep)
        {
            if (!IsActive) return;
            RefreshSnapshot();
            Rest(pTimestep);
            ApplyGravity();
            _body.LinearVelocity = Vector3.Zero;
            _body.AngularVelocity = Vector3.Zero;
        }

        #endregion // Step

        #region Motor Entry Points (called when LSL sets motor direction)

        /// <summary>
        /// Called when VehicleLinearMotorDirection is set.
        /// Derived from Halcyon VehicleMotor.MoveLinear(); the decay restarts as Second Life documents it.
        /// </summary>
        private void MoveLinear(Vector3 direction)
        {
            if (_props.Type == VehicleType.None) return;

            _props.Dynamics.LastAccessTOD = Clock();

            // InWorldz: "Scale the target if the motor has been decaying" (VehicleDynamics.cs:3054-3062).
            if (InWorldz && _props.Dynamics.LinearDecayIndex > VehicleLimits.ThresholdLinearMotorEngaged)
            {
                Vector3 decayTs = _props.GetVec(VehVectorParam.LinearMotorDecayTimescale);
                float index = _props.Dynamics.LinearDecayIndex;
                _props.Dynamics.LinearTargetVelocity = new Vector3(
                    _props.Dynamics.LinearTargetVelocity.X * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.X),
                    _props.Dynamics.LinearTargetVelocity.Y * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Y),
                    _props.Dynamics.LinearTargetVelocity.Z * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Z));
            }

            // The motor's grip restarts at full on every set and decays from here (s = 0).
            _props.Dynamics.LinearDecayIndex = MotorDecayStart(VehVectorParam.LinearMotorTimescale);

            _props.Dynamics.TargetLinearDelta = direction - _props.Dynamics.LinearDirection;
            _props.Dynamics.LinearDirection = direction;
            _hostBody.KeepAwake();
        }

        /// <summary>
        /// Called when VehicleAngularMotorDirection is set.
        /// Derived from Halcyon VehicleMotor.MoveAngular(); the decay restarts as Second Life documents it.
        /// </summary>
        private void MoveAngular(Vector3 direction)
        {
            if (_props.Type == VehicleType.None) return;

            _props.Dynamics.LastAccessTOD = Clock();

            // InWorldz: "Scale the target if the motor has been decaying" (VehicleDynamics.cs:3087-3095): the velocity the
            // ramp works from is the target it reached, less the decay since.
            if (InWorldz && _props.Dynamics.AngularDecayIndex > VehicleLimits.ThresholdAngularMotorEngaged)
            {
                Vector3 decayTs = _props.GetVec(VehVectorParam.AngularMotorDecayTimescale);
                float index = _props.Dynamics.AngularDecayIndex;
                _props.Dynamics.AngularTargetVelocity = new Vector3(
                    _props.Dynamics.AngularTargetVelocity.X * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.X),
                    _props.Dynamics.AngularTargetVelocity.Y * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Y),
                    _props.Dynamics.AngularTargetVelocity.Z * HalcyonVehicleLaws.MovementExpDecay(index, decayTs.Z));
            }

            // The motor's grip restarts at full on every set and decays from here (s = 0).
            _props.Dynamics.AngularDecayIndex = MotorDecayStart(VehVectorParam.AngularMotorTimescale);

            direction = LimitedAngularRate(direction);
            _props.Dynamics.TargetAngularDelta = direction - _props.Dynamics.AngularDirection;
            _props.Dynamics.AngularDirection = direction;
            _hostBody.KeepAwake();
        }

        /// <summary>
        /// The angular rate limiter. InWorldz scales the angular motor direction it is given by
        ///
        ///   s = (angular deflection timescale + 0.001) / (angular deflection efficiency + 0.00001)
        ///
        /// when s is below 1, but by no less than 0.1: "SL induces an angular rate limiter based on the angular
        /// deflection timescale. Basically angular deflection is enabled even when the vehicle is not moving"
        /// (VehicleDynamics.cs:3107-3116, read, not run). A vehicle whose deflection is strong (efficiency up) and fast
        /// (timescale down) is turned more slowly by its motor. The floor is [Jolt] VehicleAngularLimiterFloor
        /// (<see cref="VehicleSettings.AngularLimiterFloor"/>); with 1 there is no limiter. The scale is taken from the
        /// deflection settings at the moment the direction is set, as InWorldz does; the direction a script set is kept
        /// as it was (GetVecParam) and the motor works on the scaled one. Under both behaviours.
        /// </summary>
        private Vector3 LimitedAngularRate(Vector3 direction)
        {
            float timescale = _props.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float efficiency = _props.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            float scale = (timescale + VehicleLimits.AngularLimiterTimescaleBias) / (efficiency + VehicleLimits.AngularLimiterEfficiencyBias);
            if (scale < 1.0f)
                direction *= Math.Max(scale, S.AngularLimiterFloor);
            return direction;
        }

        /// <summary>The angular motor direction the motor works on: the script's, after the angular rate limiter. For tests.</summary>
        internal Vector3 AngularMotorDirectionInEffect => _props.Dynamics.AngularDirection;

        // InWorldz holds the decay of a quick motor back by a few hundredths of a second, "SL cheat, ramp and decay
        // should proceed simultaneously, but short decays often will prevent motor ramp-up altogether. The cheat is to
        // delay the decay by a tiny amount" (VehicleDynamics.cs:3066-3072 for the linear motor, 3099-3105 for the angular
        // one). A motor whose timescale vector is shorter than 0.9 (its length) starts its decay clock at
        // 0.0156 (1 - f) - 0.06 f, with f = 1 - length: negative, so the grip stays whole for that long (up to 0.06 s,
        // for a motor timescale near 0); any other motor starts it at 0.0156 s. Second Life's decay starts at the set.
        private float MotorDecayStart(VehVectorParam motorTimescale)
        {
            if (!InWorldz)
                return 0.0f;
            float length = _props.GetVec(motorTimescale).Length();
            float f = length < MotorDecayDelayBelow ? 1.0f - length : 0.0f;
            return VehicleLimits.MinPhysicsTimestep * (1.0f - f) - VehicleLimits.ThresholdDelayFubar * f;
        }

        // A motor timescale vector shorter than this (m) has its decay delayed (VehicleDynamics.cs:3070, 3103).
        internal const float MotorDecayDelayBelow = 0.9f;

        // The motor step on one axis. Where the decay clock reads negative (InWorldz's delay) the motor holds its whole
        // grip until the clock reaches zero and decays from there; everywhere else it is VehicleMotorSolver.Step.
        private static double MotorStep(double v0, double motor, double motorTimescale, double decayTimescale, double age,
                                        double frictionTimescale, double h, double accel = 0.0)
        {
            if (age < 0)
            {
                double delay = -age;
                if (h <= delay)
                    return VehicleMotorSolver.StepFullGrip(v0, motor, motorTimescale, frictionTimescale, h, accel);
                double v = VehicleMotorSolver.StepFullGrip(v0, motor, motorTimescale, frictionTimescale, delay, accel);
                return VehicleMotorSolver.Step(v, motor, motorTimescale, decayTimescale, 0.0, frictionTimescale, h - delay, accel);
            }
            return VehicleMotorSolver.Step(v0, motor, motorTimescale, decayTimescale, age, frictionTimescale, h, accel);
        }

        /// <summary>
        /// Check if motors need reset due to stale timestamp.
        /// Returns actual elapsed time.
        /// A motor's grip decays from its set in real time, but the decay clock here advances only while the
        /// controller is stepped; after a gap of over a second (physics off, or just rezzed) the motors are
        /// released rather than resumed at the grip they had when stepping stopped.
        /// </summary>
        private float CheckResetMotors(float timeStep)
        {
            DateTime now = Clock();
            float elapsed = (float)(now - _props.Dynamics.LastAccessTOD).TotalSeconds;
            _props.Dynamics.LastAccessTOD = now;

            if (elapsed > 1.0f)
            {
                // Reset motors — vehicle was idle or just rezzed
                ResetDynamics();
                _body.KeepAwake();
                return timeStep;
            }

            return elapsed;
        }

        /// <summary>
        /// Reset all dynamics state. Derived from Halcyon VehicleMotor.ResetDynamics().
        /// </summary>
        private void ResetDynamics()
        {
            _props.Dynamics.LastAccessTOD = Clock();
            _props.Dynamics.LastPosition = _body.Position;

            _props.Dynamics.LocalLinearVelocity = _localLinearVel;
            _props.Dynamics.LocalAngularVelocity = _localAngularVel;

            _props.Dynamics.LastVerticalAngle = 0.0f;
            _props.Dynamics.VerticalForceAdjust = 1.0f;

            _ownsRoll = false;
            _heightAbove = 0f;
            _timeAboveHeight = 0f;
            _motorsOffByHeight = false;

            // Motors off: no set to decay from, no axis driven, no time carried toward a step of Halcyon's
            _linearAxesDriven = Vector3.Zero;
            _linearMotorCarry = 0.0;
            _angularMotorCarry = 0.0;
            _hoverStepCarry = 0.0;
            _props.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            _props.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            _props.Dynamics.LinearTargetVelocity = Vector3.Zero;
            _props.Dynamics.AngularTargetVelocity = Vector3.Zero;

            // Bank/turn motor off
            _props.Dynamics.BankingDirection = 0;
            _props.Dynamics.BankingTargetVelocity = 0;

            // Deltas zero
            _props.Dynamics.TargetLinearDelta = Vector3.Zero;
            _props.Dynamics.TargetAngularDelta = Vector3.Zero;
        }

        #endregion // Motor Entry Points

        #region Stall Detection

        private void ClearLinearMotorStalled()
        {
            _linearMotorStalled = false;
            _linearMotorStallChecked = false;
        }

        /// <summary>
        /// Detect if the vehicle is jammed against an obstacle.
        /// Ported from Halcyon VehicleMotor.IsLinearMotorStalled().
        /// </summary>
        private bool IsLinearMotorStalled()
        {
            Vector3 currpos = _body.Position;
            Vector3 lastpos = _sp.Dynamics.LastPosition;
            Vector3 posdelta = currpos - lastpos;
            float currspeed = Vector3.Mag(_worldLinearVel);
            float timeStep = _sp.Dynamics.Timestep;

            if (!_linearMotorStallChecked)
            {
                // The position's rate of change, smoothed with a time constant (StallSmoothing): the same smoothing
                // over a span of time whatever the step. (It was 0.8 / 0.2 per step, which is this time constant at
                // the 64 Hz step the code was tuned at.)
                float share = 1f - (float)Math.Exp(-timeStep / StallSmoothing);
                _sp.Dynamics.ShortTermPositionDelta += (posdelta / timeStep - _sp.Dynamics.ShortTermPositionDelta) * share;
                _linearMotorStallChecked = true;

                // If either motor is starting fresh, assume not stalled
                if (_sp.Dynamics.LinearDecayIndex > VehicleLimits.ThresholdLinearMotorEngaged &&
                    _sp.Dynamics.AngularDecayIndex > VehicleLimits.ThresholdAngularMotorEngaged)
                {
                    float stspeed = Vector3.Mag(_sp.Dynamics.ShortTermPositionDelta);

                    if (stspeed < currspeed * 0.5f)
                    {
                        if (currspeed >= VehicleLimits.ThresholdLinearMotorUnstuck)
                        {
                            _linearMotorStalled = true;
                        }
                    }
                }
            }

            return _linearMotorStalled;
        }

        // The stall check's smoothing time constant (s): -0.0156 / ln(0.8), the old per-step 0.8 / 0.2 at 64 Hz.
        private const float StallSmoothing = 0.07f;

        #endregion

        #region Gravity

        // The buoyancy the vehicle has: its VEHICLE_BUOYANCY, except that InWorldz makes a value from 0.977 up to (not
        // including) 1 into 1, "Legacy grid hack" (PhysxPrim.cs:2963-2965, reached from the vehicle's buoyancy through
        // VehicleDynamics.cs:1546-1548): a vehicle set to 0.98 floats, where Second Life's would sink slowly.
        private float Buoyancy()
        {
            float buoyancy = _sp.GetFloat(VehFloatParam.Buoyancy, 0f);
            if (!InWorldz)
                return buoyancy;
            // "Constrain the buoyancy to the gravity multiplier extent" (VehicleDynamics.cs:314-323; a multiplier of 0 reads as 1).
            float extent = Math.Abs(_body.GravityMultiplier);
            if (extent == 0f) extent = 1f;
            buoyancy = Utils.Clamp(buoyancy, -extent, extent);
            if (buoyancy >= LegacyFloatBuoyancy && buoyancy < 1f)
                return 1f;
            return buoyancy;
        }

        // The vehicle's weight in units of the region's gravity before the ground factor: 1 - buoyancy; under InWorldz the
        // gravity multiplier less the buoyancy, "DG = -Buoyancy + GravityMultiplier" (PhysxPrim.cs:349, 1614), which every
        // physical object of the InWorldz simulator has, vehicles included. Second Life's vehicles ignore the multiplier.
        private float GravityBase(float buoyancy) => (InWorldz ? _body.GravityMultiplier : 1f) - buoyancy;

        internal const float LegacyFloatBuoyancy = 0.977f;

        /// <summary>
        /// The share of the world's gravity the vehicle feels: 1 - buoyancy, times the ground factor for a ground
        /// vehicle touching something. With hover on and HOVER_UP_ONLY, buoyancy vanishes above the hover height
        /// (Linden_Vehicle_Tutorial, "Buoyancy": "the buoyancy effect vanishes when the vehicle is above its hover
        /// height").
        /// </summary>
        private float GravityShare()
        {
            if (HoverTurnsGravityOff())
                return 0f;
            float buoyancy = Buoyancy();
            // (InWorldz's hover has no such rule: its buoyancy stays above the hover height.)
            if (HoverTarget(out float targetZ, out bool upOnly) && upOnly && !InWorldz && _body.Position.Z > targetZ)
                buoyancy = 0f;
            float share = GravityBase(buoyancy);   // ComputeGravity(buoyancy), GravModifier = 1

            // Reduce downward force if vehicle is sitting on ground
            if (GroundVehicle && _body.HasCollision)
                share *= GroundGravityFactor;
            return share;
        }

        /// <summary>
        /// The vehicle's gravity: the engine applies its share over the step, spread over the step as for any body.
        /// The linear motor and friction step takes it into its equation and leaves the engine this g h. (Applied
        /// here as a force it was the same, but a force restarts the engine's sleep timer on every step, so a parked
        /// vehicle could never sleep.)
        /// </summary>
        private void ApplyGravity()
        {
            _body.SetGravityFactor(GravityShare());
        }

        #endregion

        #region Torque Accumulator (Halcyon pattern)

        private void TorqueInit()
        {
            _accumTorqueVelChange = Vector3.Zero;
            _accumTorqueImpulse = Vector3.Zero;
            _motorTorqueVelChange = Vector3.Zero;
        }

        /// <summary>
        /// Add torque as a velocity change (mass-independent, instant).
        /// Equivalent to Halcyon AddTorque with ForceMode.VelocityChange.
        /// </summary>
        internal void AddTorqueVelocityChange(Vector3 torque)
        {
            _accumTorqueVelChange += torque;
        }

        /// <summary>
        /// Add a torque (mass-dependent), which the step turns into the change of angular velocity it makes over the step:
        /// the inverse of the body's inertia tensor times the torque times the step. It is never handed to the engine as a
        /// torque: the engine applies an accumulated torque in every collision step and clears it only after the last, so a
        /// torque added from inside a collision step would turn the body again in each later step of the engine step.
        /// </summary>
        internal void AddTorqueImpulse(Vector3 torque)
        {
            _accumTorqueImpulse += torque;
        }

        /// <summary>
        /// Apply all accumulated torques at end of frame.
        /// </summary>
        private void TorqueFini()
        {
            // Clean up near-zero values (a change per Halcyon step, VehicleDynamics.cs:2447-2449: a step takes its share of the limit)
            float tiny = VehicleLimits.MinPhysicsForce * HalcyonStepShare;
            if (Math.Abs(_accumTorqueVelChange.X) < tiny) _accumTorqueVelChange.X = 0f;
            if (Math.Abs(_accumTorqueVelChange.Y) < tiny) _accumTorqueVelChange.Y = 0f;
            if (Math.Abs(_accumTorqueVelChange.Z) < tiny) _accumTorqueVelChange.Z = 0f;

            if (Math.Abs(_accumTorqueImpulse.X) < VehicleLimits.MinPhysicsForce) _accumTorqueImpulse.X = 0f;
            if (Math.Abs(_accumTorqueImpulse.Y) < VehicleLimits.MinPhysicsForce) _accumTorqueImpulse.Y = 0f;
            if (Math.Abs(_accumTorqueImpulse.Z) < VehicleLimits.MinPhysicsForce) _accumTorqueImpulse.Z = 0f;

            // Apply velocity change torques (direct angular velocity modification)
            Vector3 velChange = _accumTorqueVelChange + _motorTorqueVelChange;
            if (velChange != Vector3.Zero)
            {
                _body.AngularVelocity = _body.AngularVelocity + velChange;
            }

            // Apply torques as the angular velocity change they make over the step (mass-scaled)
            if (_accumTorqueImpulse != Vector3.Zero)
            {
                _body.AngularVelocity = _body.AngularVelocity + AngularChangeOf(_accumTorqueImpulse * _stepSeconds);
            }

            // (No activation nudge here; the host keeps an active vehicle body from sleeping.)
        }

        #endregion // Torque Accumulator

        #region Adapter Methods — Physics Engine Interface

        /// <summary>
        /// Apply an instant linear velocity change (mass-independent).
        /// Equivalent to Halcyon AddForce with ForceMode.VelocityChange.
        /// </summary>
        private void ApplyLinearVelocityChange(Vector3 deltaV)
        {
            // The linear motor's offset turn is applied separately (MotorOffsetTurn); the velocity change is central.
            _body.LinearVelocity = _body.LinearVelocity + deltaV;
        }

        #endregion // Adapter Methods

        #region Deflection (Angular + Linear) + Sled movement (gated Type==Sled)

        /// <summary>
        /// Angular deflection, as Second Life documents it (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "Linear and
        /// Angular Deflection"; LlSetVehicleFloatParam): it reorients the vehicle so that its x axis points the way it is
        /// moving, the timescale being the time constant of an exponential decay toward full deflection and the efficiency a
        /// slider from no deflection (0) to the most (1). The angle a between the nose and the velocity decays as
        ///
        ///   a' = -eff s a / T,   so over a step h the nose turns a (1 - e^(-eff s h / T)) toward the velocity,
        ///
        /// with s = speed / the reference speed ([Jolt] VehicleReferenceSpeed, 30 m/s by default; at most 1), the speed
        /// scaling the existing code had. The
        /// documentation gives no speed term; without it a car slowing to a stop turns its nose toward the jitter of
        /// its contact velocity and drifts, and a falling car noses down hard.
        ///
        /// given to the body as a turning rate for the step (and the last step's taken back, so the deflection is a
        /// rate, not a push that builds up). A vehicle moving backwards turns its tail, not its nose, toward its
        /// velocity. Under ThresholdDeflectionSpeed the velocity has no direction to turn to.
        /// </summary>
        private void SimulateAngularDeflection(float h)
        {
            if (InWorldz)
            {
                SimulateAngularDeflectionInWorldz(h);
                return;
            }
            Vector3 rate = Vector3.Zero;
            float timescale = _sp.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float efficiency = _sp.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            float speed = _rawWorldLinearVel.Length();
            if (timescale < VehicleLimits.MaxTimescale && efficiency > 0f && speed >= VehicleLimits.ThresholdDeflectionSpeed)
            {
                Vector3 forward = Vector3.UnitX * _rotation;
                Vector3 heading = _rawWorldLinearVel / speed;
                if (Vector3.Dot(forward, heading) < 0f)
                    heading = -heading;
                Vector3 axis = Vector3.Cross(forward, heading);
                float sin = axis.Length();
                if (sin > MinTurnSine)
                {
                    double angle = Math.Atan2(sin, Vector3.Dot(forward, heading));
                    double speedShare = Math.Min(speed, S.ReferenceSpeed) / S.ReferenceSpeed;
                    double turn = angle * (1.0 - Math.Exp(-efficiency * speedShare * h / timescale));
                    rate = axis / sin * (float)(turn / h);
                }
            }
            _motorTorqueVelChange += rate - _angularDeflectionRate;
            _angularDeflectionRate = rate;
        }

        /// <summary>
        /// InWorldz's angular deflection (VehicleDynamics.cs:1114-1169, read, not run), in place of the documented law
        /// above: a push of angular velocity each step that is not a share of the remaining angle but speedShare x pi x
        /// (h / T) x efficiency x ln(1 + angle), along the normalised Euler angles of the rotation from the nose to the
        /// velocity, with a damping term within pi / 64 of aligned (<see cref="HalcyonVehicleLaws.AngularDeflectionPush"/>);
        /// the push is zero while the linear motor is stalled against something (<see cref="IsLinearMotorStalled"/>), and it is
        /// applied only when one of its components is at least pi / 512, as an accumulated velocity change. It is a push, not a
        /// rate, so nothing of the last step is taken back.
        /// </summary>
        private void SimulateAngularDeflectionInWorldz(float h)
        {
            float timescale = _sp.GetFloat(VehFloatParam.AngularDeflectionTimescale, 1000f);
            float efficiency = _sp.GetFloat(VehFloatParam.AngularDeflectionEfficiency, 0f);
            Vector3 push = HalcyonVehicleLaws.AngularDeflectionPush(_rotation, _worldLinearVel, _localLinearVel, _worldAngularVel,
                timescale, efficiency, h, S.ReferenceSpeed);
            if (push == Vector3.Zero)
                return;
            if (IsLinearMotorStalled())
                return;
            // pi / 512 per Halcyon step (VehicleDynamics.cs:1160-1162)
            float least = VehicleLimits.ThresholdAngularMotorDeltaV * HalcyonStepShare;
            if (Math.Abs(push.X) >= least || Math.Abs(push.Y) >= least || Math.Abs(push.Z) >= least)
                AddTorqueVelocityChange(push);
        }

        // The turning rate angular deflection gave the body in the last step.
        private Vector3 _angularDeflectionRate;

        // Directions closer than this (the sine of the angle between them) are already aligned.
        private const float MinTurnSine = 1e-6f;

        /// <summary>
        /// Linear deflection, as Second Life documents it (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "Linear and
        /// Angular Deflection"; LlSetVehicleFloatParam): it rotates the velocity until it points along the vehicle's x axis,
        /// keeping its speed, with the timescale the time constant of an exponential decay toward full deflection and the
        /// efficiency a slider from none (0) to the most (1). The angle a between the velocity and the nose decays as
        ///
        ///   a' = -eff a / T,   so over a step h the velocity turns a (1 - e^(-eff h / T)) toward the nose.
        ///
        /// With NO_DEFLECTION_UP ("prevents linear deflection parallel to world z-axis") only the horizontal part of
        /// the velocity turns, toward the nose's horizontal heading, and the vertical part is left as it is, so
        /// deflection never adds speed or climbs.
        /// </summary>
        private void SimulateLinearDeflection(float h)
        {
            float timescale = _sp.GetFloat(VehFloatParam.LinearDeflectionTimescale, 1000f);
            float efficiency = _sp.GetFloat(VehFloatParam.LinearDeflectionEfficiency, 0f);
            if (timescale >= VehicleLimits.MaxTimescale || efficiency <= 0f)
                return;
            float share = 1f - (float)Math.Exp(-efficiency * h / timescale);
            Vector3 forward = Vector3.UnitX * _rotation;
            // A vehicle that is backing along its nose axis is on the axis already ("redirect its linear velocity along its
            // preferred X-axis"): the velocity is turned toward the end of the axis it is travelling toward. Turned toward the
            // nose it would be an unstable balance, a sideways speed of a few millimetres a second growing until the vehicle
            // swings round to go forward or goes off sideways, however straight it is driven.
            // (The law of InWorldz, VehicleDynamics.cs:1171-1210, adds the speed to the nose end whatever the direction of travel,
            // and so brakes a vehicle that is backing; this is the form that holds a line.)
            // (Under SecondLife with NO_DEFLECTION_UP only the horizontal parts turn, so it is the horizontal parts that say which way.)
            bool horizontalOnly = !InWorldz && (_sp.Flags & ExtendedVehicleFlags.NoDeflectionUp) != 0;
            Vector3 heading = horizontalOnly ? new Vector3(_rawWorldLinearVel.X, _rawWorldLinearVel.Y, 0f) : _rawWorldLinearVel;
            Vector3 along = horizontalOnly ? new Vector3(forward.X, forward.Y, 0f) : forward;
            Vector3 toward = Vector3.Dot(heading, along) < 0f ? -forward : forward;
            Vector3 change;
            if ((_sp.Flags & ExtendedVehicleFlags.NoDeflectionUp) == 0)
                change = Deflection(_rawWorldLinearVel, toward, share);
            else if (InWorldz)
            {
                // "Stop any upward deflection": InWorldz zeroes the upward part of the change only (VehicleDynamics.cs:1198-1202,
                // SimulateLinearDeflection), so a downward part still deflects.
                change = Deflection(_rawWorldLinearVel, toward, share);
                if (change.Z > 0f)
                    change.Z = 0f;
            }
            else
                change = HorizontalDeflection(_rawWorldLinearVel, toward, share);
            // InWorldz applies the push only if some part of it is more than 0.005 m/s in a step (VehicleDynamics.cs:1204-1208), which
            // keeps a vehicle at rest from being pushed about by a nose a fraction of a degree off its velocity. The 0.005 is per
            // 15 ms step, an acceleration of 0.33 m/s^2, so a step of h seconds takes h / 15 ms of it.
            float least = VehicleLimits.ThresholdLinearMotorDeltaV * (float)(h / HalcyonStep);
            if (InWorldz && Math.Abs(change.X) <= least && Math.Abs(change.Y) <= least && Math.Abs(change.Z) <= least)
                change = Vector3.Zero;
            if (change != Vector3.Zero)
            {
                ApplyLinearVelocityChange(change);
                // InWorldz applies the deflection through the motor's offset, like every linear force (VehicleMotor.AddForce).
                if (InWorldz)
                    _motorTorqueVelChange += MotorOffsetTurn(change);
            }
        }

        /// <summary>
        /// InWorldz's boat ground drag (VehicleDynamics.cs:1037-1073, SimulateBoatGroundDrag, read, not run): a boat or sailboat
        /// with HOVER_WATER_ONLY (and not HOVER_TERRAIN_ONLY) that moves faster than 0.005 m/s and has the hull edge that leads
        /// its motion at or under the terrain has its linear motor killed, its decay clock put on 1200 s and its target velocity
        /// on zero, so a boat does not climb a beach (<see cref="HalcyonVehicleLaws.BoatLeadingEdgeOnGround"/>).
        /// </summary>
        private void SimulateBoatGroundDrag()
        {
            if ((_sp.Type != VehicleType.Boat && _sp.Type != VehicleType.Sailboat)
                || (_sp.Flags & ExtendedVehicleFlags.HoverWaterOnly) == 0
                || (_sp.Flags & ExtendedVehicleFlags.HoverTerrainOnly) != 0
                || _localLinearVel.Length() <= VehicleLimits.ThresholdLinearMotorDeltaV
                || !_body.TryGetBounds(out Vector3 center, out Vector3 half))
                return;
            IVehicleBody body = _body;
            if (HalcyonVehicleLaws.BoatLeadingEdgeOnGround(_localLinearVel, body.Position, _rotation, center, half,
                    (x, y) => body.GetTerrainHeight(new Vector3(x, y, 0f))))
            {
                _sp.Dynamics.LinearDecayIndex = HalcyonVehicleLaws.MotorOffClock;
                _sp.Dynamics.LinearTargetVelocity = Vector3.Zero;
            }
        }

        /// <summary>
        /// The sled's slope assist (the InWorldz sled movement), as an acceleration along the sled's nose:
        ///
        ///   a = k g sqrt(|sin pitch|)            nose down; nose up, a tenth of it, still pushing down the slope
        ///
        /// k the region's sled assist (<see cref="VehicleSettings.SledAssist"/>, 0.045 by default) and g the world's
        /// gravity. The InWorldz code applied a force of 3 g m sqrt(|sin pitch|) times the step for one step; at the
        /// 15 ms step it was tuned at that is 0.045 g, and at any other step it scaled with the step. As an
        /// acceleration it goes into the linear motor and friction equation (<see cref="SimulateLinearMotorAndFriction"/>),
        /// which steps it exactly with the friction. No assist under ThresholdDeflectionAngle of pitch, or while the
        /// sled is stalled against something (<see cref="IsLinearMotorStalled"/>). Zero for every other vehicle type.
        /// </summary>
        private Vector3 SledAssist()
        {
            if (_sp.Type != VehicleType.Sled)
                return Vector3.Zero;
            float noseDown = -(Vector3.UnitX * _rotation).Z;   // sin of the pitch, nose down positive
            if (Math.Abs(noseDown) <= VehicleLimits.ThresholdDeflectionAngle || IsLinearMotorStalled())
                return Vector3.Zero;
            float accel = S.SledAssist * Math.Abs(_body.Gravity.Z) * (float)Math.Sqrt(Math.Abs(noseDown));
            if (noseDown < 0f)
                accel *= -SledUphillShare;
            return new Vector3(accel, 0f, 0f);
        }

        // Nose up, the assist is this share of the nose-down assist, pushing back down the slope.
        private const float SledUphillShare = 0.1f;

        /// <summary>
        /// The velocity change that turns a velocity toward a direction by the given share of the angle between them,
        /// keeping its speed. Zero when the velocity is under ThresholdDeflectionSpeed or already along the direction;
        /// exactly opposed, there is no one way to turn, and it is left for this step.
        /// </summary>
        internal static Vector3 Deflection(Vector3 velocity, Vector3 toward, float share)
        {
            float speed = velocity.Length();
            if (speed < VehicleLimits.ThresholdDeflectionSpeed || toward.Length() < MinDeflectionHeading)
                return Vector3.Zero;
            Vector3 u = velocity / speed, d = Vector3.Normalize(in toward);
            Vector3 axis = Vector3.Cross(u, d);
            float sin = axis.Length();
            if (sin < MinTurnSine)
                return Vector3.Zero;
            axis /= sin;
            double turn = Math.Atan2(sin, Vector3.Dot(u, d)) * share;
            // Rodrigues' rotation of u about an axis perpendicular to it.
            Vector3 turned = u * (float)Math.Cos(turn) + Vector3.Cross(axis, u) * (float)Math.Sin(turn);
            return turned * speed - velocity;
        }

        /// <summary>
        /// The velocity change that turns the horizontal part of a velocity toward the horizontal heading of the nose
        /// by the given share of the angle between them, keeping the horizontal speed; the vertical part is untouched.
        /// Zero when the velocity has too little horizontal speed or the nose points (nearly) straight up or down.
        /// </summary>
        internal static Vector3 HorizontalDeflection(Vector3 velocity, Vector3 forward, float share)
        {
            Vector3 horizontal = new Vector3(velocity.X, velocity.Y, 0f);
            Vector3 heading = new Vector3(forward.X, forward.Y, 0f);
            return Deflection(horizontal, heading, share);
        }

        // A nose whose horizontal heading is shorter than this (pointing within about 6 degrees of straight up or
        // down) gives no horizontal direction to deflect toward.
        private const float MinDeflectionHeading = 0.1f;

        #endregion

        #region Hover

        /// <summary>
        /// Hover, as Second Life documents it (Linden_Vehicle_Tutorial, "Hover"; LlSetVehicleFloatParam): the vehicle
        /// springs toward its hover height over terrain, water or the global height, with HOVER_TIMESCALE the period
        /// to achieve it and HOVER_EFFICIENCY a slider from bouncy (0) to critically damped (1). On the height error
        /// e = z - target:
        ///
        ///   e'' = -e / T^2 - 2 eff e' / T
        ///
        /// stepped exactly (<see cref="VehicleSpring"/>) over the step the engine takes. Gravity, buoyancy, friction
        /// and the motors act alongside it in their own blocks; a vehicle without full buoyancy hovers below its
        /// height, where the spring holds its weight, as the tutorial describes.
        /// HOVER_UP_ONLY: hover does not push down; above its height it does nothing (and buoyancy vanishes there,
        /// see <see cref="GravityShare"/>).
        /// </summary>
        private void SimulateHover(float h)
        {
            if (!HoverTarget(out float targetZ, out bool upOnly))
            {
                LetGoOfHover();
                return;
            }
            float timescale = _sp.GetFloat(VehFloatParam.HoverTimescale, 1000f);
            float efficiency = _sp.GetFloat(VehFloatParam.HoverEfficiency, 0f);
            if (InWorldz)
            {
                SimulateHoverInWorldz(h, targetZ, upOnly, timescale, efficiency);
                return;
            }
            double e0 = _body.Position.Z - targetZ;
            if (upOnly && e0 >= 0)
            {
                // Above the height with HOVER_UP_ONLY hover does nothing, unless the vehicle falls back through the
                // height inside this step: then the spring takes it from there (HoverFromAbove).
                if (!HoverFromAbove(e0, timescale, efficiency, h))
                    LetGoOfHover();
                return;
            }

            // The spring's own vertical velocity: the body's, plus what the last step's move left out of it.
            double read = _rawWorldLinearVel.Z;
            double v0 = read + (_hoverCarry ?? 0.0);
            (double move, double carry) = VehicleSpring.MoveOver(e0, v0, timescale, efficiency, h);
            bool crosses = upOnly && VehicleSpring.Step(e0, v0, timescale, efficiency, h).e > 0;
            if (crosses)
            {
                // Up only: the spring lets go where the vehicle passes its height inside the step, and it coasts on
                // from there at the speed it had, with the gravity that buoyancy no longer cancels above the height
                // (the gravity block applies this step's share, set below the height).
                double at = UpOnlyCrossing(e0, v0, timescale, efficiency, h);
                double through = VehicleSpring.Step(e0, v0, timescale, efficiency, at).v;
                double coast = h - at;
                double extraFall = _body.Gravity.Z * (GravityShareAbove() - GravityShare());
                move = (through * coast + 0.5 * extraFall * coast * coast - e0) / h;
                carry = through + extraFall * coast - move;
            }
            double change = move - read;
            // Up only: the spring itself never pushes down (the fall after a crossing is gravity's, not hover's).
            if (upOnly && !crosses && change < 0)
            {
                change = 0;
                carry = 0;
            }
            _hoverCarry = carry;
            ApplyLinearVelocityChange(new Vector3(0f, 0f, (float)change));
        }

        /// <summary>
        /// InWorldz: Halcyon's hover force law (PhysxPrim.pid.cs:207-336, DoHoverTarget, DoHover and DoHoverDamping; a vehicle's
        /// hover goes through it, VehicleDynamics.cs:1552-1604), in place of the documented spring. It is a change of vertical
        /// velocity per step of 15 ms (<see cref="HalcyonVehicleLaws.HoverVelocityChange"/>), so a collision step runs the
        /// steps of Halcyon's that fall due in it (<see cref="StepsDue"/>), each from the velocity and height the one before
        /// leaves, and a step shorter than 15 ms waits for its turn. Gravity is off while it acts (<see cref="HoverTurnsGravityOff"/>).
        /// A timescale under two physics steps (<see cref="HalcyonVehicleLaws.HoverMinTimescale"/>) leaves the vehicle alone.
        /// </summary>
        private void SimulateHoverInWorldz(float h, float targetZ, bool upOnly, float timescale, float efficiency)
        {
            if (timescale < HalcyonVehicleLaws.HoverMinTimescale)
                return;
            int due = StepsDue(ref _hoverStepCarry, h);
            if (due == 0)
                return;

            float read = _rawWorldLinearVel.Z;
            float velocity = read;
            float z = _body.Position.Z;
            for (int i = 0; i < due; i++)
            {
                float diff = targetZ - z;
                velocity += HalcyonVehicleLaws.HoverVelocityChange(diff, velocity, timescale, efficiency, !upOnly, (float)HalcyonStep);
                z += velocity * (float)HalcyonStep;
            }
            ApplyLinearVelocityChange(new Vector3(0f, 0f, velocity - read));
        }

        // The time toward the next step of Halcyon's hover that earlier collision steps have already covered.
        private double _hoverStepCarry;

        /// <summary>
        /// HOVER_UP_ONLY, a step that starts above the height: the vehicle falls freely (with the gravity buoyancy no
        /// longer cancels above the height) and, if it reaches the height inside the step, the spring acts from that
        /// moment, with the gravity of below the height:
        ///
        ///   e(t) = e0 + v0 t + g_above t^2 / 2   until e(tc) = 0;   then the spring from (0, v0 + g_above tc) for h - tc
        ///
        /// The engine applies this step's gravity share (the one above the height) over the whole step, so the body is
        /// given the velocity that, with it, ends the step where this does, and the rest is carried to the next step.
        /// Returns false (nothing done) when the vehicle stays above the height for the whole step.
        /// </summary>
        private bool HoverFromAbove(double e0, float timescale, float efficiency, float h)
        {
            double read = _rawWorldLinearVel.Z;
            double v0 = read + (_hoverCarry ?? 0.0);
            double gAbove = _body.Gravity.Z * GravityShare();
            if (e0 + v0 * h + 0.5 * gAbove * h * h >= 0)
                return false;

            // The fall reaches the height at tc (by bisection: the fall is past its top or falling, so e(t) crosses
            // zero once in the step).
            double lo = 0, hi = h;
            for (int i = 0; i < UpOnlyCrossingIterations; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (e0 + v0 * mid + 0.5 * gAbove * mid * mid > 0) lo = mid; else hi = mid;
            }
            double tc = 0.5 * (lo + hi);
            double rest = h - tc;

            // From the height: the spring, with the gravity of below the height alongside it as in any step there.
            (double e1, double v1) = VehicleSpring.Step(0.0, v0 + gAbove * tc, timescale, efficiency, rest);
            double gBelow = _body.Gravity.Z * GravityShareBelow();
            e1 += 0.5 * gBelow * rest * rest;
            v1 += gBelow * rest;

            // Less what the engine's gravity (this step's share) adds over the whole step.
            double move = (e1 - 0.5 * gAbove * h * h - e0) / h;
            _hoverCarry = v1 - gAbove * h - move;
            ApplyLinearVelocityChange(new Vector3(0f, 0f, (float)(move - read)));
            return true;
        }

        /// <summary>
        /// InWorldz: while hover acts the vehicle has no gravity, whatever its buoyancy. Halcyon's hover switches the
        /// actor's gravity off on every step it runs (PhysxPrim.pid.cs DoHoverTarget, DoHover: _hoverDisableGravity;
        /// PhysxPrim.cs WantsGravity), so the vehicle is held at its hover height and not below it, where Second Life's
        /// spring would carry its weight (<see cref="SimulateHover"/>). This is what keeps a sailboat, whose preset has
        /// buoyancy 0, on the water. Under SecondLife behaviour the documented spring stands.
        /// </summary>
        private bool HoverTurnsGravityOff()
            => InWorldz && HoverTarget(out _, out _);

        // The gravity share under the hover height: 1 - buoyancy, times the ground factor for a ground vehicle touching.
        private float GravityShareBelow()
        {
            if (HoverTurnsGravityOff())
                return 0f;
            float share = GravityBase(Buoyancy());
            if (GroundVehicle && _body.HasCollision)
                share *= GroundGravityFactor;
            return share;
        }

        // Hover hands the body back its true vertical velocity (what the last move left out) when it stops acting.
        private void LetGoOfHover()
        {
            if (_hoverCarry is double carry && carry != 0)
                ApplyLinearVelocityChange(new Vector3(0f, 0f, (float)carry));
            _hoverCarry = null;
        }

        // The gravity share over the hover height with HOVER_UP_ONLY, where buoyancy vanishes.
        private float GravityShareAbove()
        {
            if (InWorldz)
                return GravityShareBelow();   // buoyancy does not vanish above the height
            float share = 1f;
            if (GroundVehicle && _body.HasCollision)
                share *= GroundGravityFactor;
            return share;
        }

        // When, within a step of h, a spring from e0 < 0 that ends above zero first reaches zero (by bisection: the
        // spring is monotonic up to its first crossing).
        private static double UpOnlyCrossing(double e0, double v0, double timescale, double efficiency, double h)
        {
            double lo = 0, hi = h;
            for (int i = 0; i < UpOnlyCrossingIterations; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (VehicleSpring.Step(e0, v0, timescale, efficiency, mid).e < 0) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        private const int UpOnlyCrossingIterations = 40;

        // What the last hover step's move left out of the spring's velocity (VehicleSpring.MoveOver); null when hover
        // did not act in the last step.
        private double? _hoverCarry;

        /// <summary>
        /// The height hover holds the vehicle at, and whether it only pushes up. False when hover is off (its timescale
        /// at the "off" value).
        /// </summary>
        private bool HoverTarget(out float targetZ, out bool upOnly)
        {
            float hoverHeight = _sp.GetFloat(VehFloatParam.HoverHeight, 0f);
            float hoverTimescale = _sp.GetFloat(VehFloatParam.HoverTimescale, 1000f);
            targetZ = 0f;
            upOnly = (_sp.Flags & ExtendedVehicleFlags.HoverUpOnly) != 0;

            // InWorldz: with HOVER_UP_ONLY the hover force acts only "if diff > 0 || Buoyancy > 0" (diff the height still
            // to climb): below its height the vehicle is lifted as ever, but above it hover acts only while buoyancy is
            // positive, and then pushes the vehicle down to its height as an ordinary hover does ("bug for bug compatible
            // with standard vehicles; unless there is some positive buoyancy, hover does not work", PhysxPrim.pid.cs:323-325;
            // the flag reaches it from VehicleDynamics.cs:1596-1597). So a buoyant up-only hover is a plain hover.
            if (upOnly && InWorldz && Buoyancy() > 0f)
                upOnly = false;

            // If timescale is effectively disabled, skip
            if (hoverTimescale >= S.MaxHoverTimescale)
                return false;

            // InWorldz: a height of zero or below with efficiency zero is no hover, "legacy scripts" (VehicleDynamics.cs:1563-1564).
            if (InWorldz && HoverOffByLegacyRule(_sp))
                return false;

            Vector3 pos = _body.Position;
            float targetBase;

            // Determine the base height based on hover flags. The global height comes first: with HOVER_GLOBAL_HEIGHT
            // set the vehicle hovers at that height rather than above the ground or water (the flag's page on the
            // Second Life wiki), whatever other hover flag is set with it; InWorldz's hover does the same ("Global hover
            // overrides water and ground", VehicleDynamics.cs:1575-1576).
            if ((_sp.Flags & ExtendedVehicleFlags.HoverGlobalHeight) != 0)
            {
                targetBase = 0f; // Global = absolute height, hover height is the target
            }
            else if ((_sp.Flags & ExtendedVehicleFlags.HoverWaterOnly) != 0
                     && (_sp.Flags & ExtendedVehicleFlags.HoverTerrainOnly) == 0)
            {
                targetBase = _body.GetWaterLevel(pos);
            }
            else if ((_sp.Flags & ExtendedVehicleFlags.HoverTerrainOnly) != 0
                     && (_sp.Flags & ExtendedVehicleFlags.HoverWaterOnly) == 0)
            {
                targetBase = _body.GetTerrainHeight(pos);
            }
            else
            {
                // Neither flag, or both: the higher of terrain and water. The Second Life page for the flags leaves
                // both set undefined; InWorldz hovers over both ("both flags set or both clear means hover both",
                // VehicleDynamics.cs:1580-1588, height taken as above the higher of the two, PhysxPrim.pid.cs:372-378).
                targetBase = Math.Max(_body.GetTerrainHeight(pos), _body.GetWaterLevel(pos));
            }

            targetZ = targetBase + hoverHeight;
            return true;
        }

        #endregion

        #region Vertical Attractor

        /// <summary>
        /// The vertical attractor, as Second Life documents it (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "The
        /// Vertical Attractor"; LlSetVehicleFloatParam): a spring that brings the vehicle's local z axis to the world's up, with
        /// VERTICAL_ATTRACTION_TIMESCALE setting its period and VERTICAL_ATTRACTION_EFFICIENCY its damping, from
        /// wobbling around up (0) to reaching it with exponential decay (1). Off at a timescale of 500 s or more.
        ///
        /// It acts on the vehicle's roll (the angle about its x axis that would bring its y axis level) and its
        /// pitch (the nose's angle above or below level), each a spring on the angle e and the angular velocity about
        /// that axis:
        ///
        ///   e'' = -s e / T^2 - 2 eff e' / T          s = 1 for roll; for pitch 1, or 0 with LIMIT_ROLL_ONLY
        ///
        /// stepped exactly (<see cref="VehicleSpring"/>) over the step the engine takes; the body is given the angular
        /// velocity that turns it where the spring would at the step's end. Gives the tilt from up (angle), for
        /// banking.
        ///
        /// VEHICLE_FLAG_LIMIT_ROLL_ONLY unlocks the attractor about the pitch axis (the tutorial's section on the vertical
        /// attractor, and LlSetVehicleFlags, which offers the flag to vehicles that must be able to climb and dive), for
        /// every vehicle type: the attractor then corrects roll and leaves pitch alone.
        ///
        /// Ported from Halcyon VehicleDynamics.SimulateVerticalAttractor(), then changed to the documented damped spring
        /// above. Under InWorldz's behaviour the Halcyon law itself acts (<see cref="SimulateHalcyonVerticalAttractor"/>).
        /// </summary>
        private void SimulateVerticalAttractor(float h, out float angle, out bool inverted)
        {
            float timescale = _sp.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f);
            float efficiency = _sp.GetFloat(VehFloatParam.VerticalAttractionEfficiency, 0f);
            Vector3 x = Vector3.UnitX * _rotation, y = Vector3.UnitY * _rotation, z = Vector3.UnitZ * _rotation;
            // InWorldz measures the tilt as the angle of the rotation that keeps the roll and pitch and drops the yaw
            // (VehicleDynamics.cs:1233-1236); the documented attractor uses the angle between the z axis and up.
            angle = InWorldz ? HalcyonVehicleLaws.TiltAngle(_rotation) : (float)Math.Acos(Utils.Clamp(z.Z, -1f, 1f));
            inverted = false;
            if (timescale >= S.MaxAttractTimescale)
            {
                LetGoOfAttractor(x, y);
                return;
            }

            // InWorldz: the attractor goes dormant once the tilt has stopped changing.
            if (InWorldz && AttractorDormant(angle, h))
            {
                LetGoOfAttractor(x, y);
                return;
            }

            bool rollOnly = (_sp.Flags & ExtendedVehicleFlags.LimitRollOnly) != 0;

            // The roll rate the attractor set last step turns with the vehicle: it is put back on the axis the roll is now
            // about, so a turn about the world's vertical (banking's) cannot leave part of it across that axis as a pitch
            // rate. Under both behaviours.
            Vector3 worldRate = ReaxisOwnedRoll(x);

            // InWorldz: Halcyon's attractor, a push on the angular velocity and a share of it taken away each step, in place of
            // the documented spring below.
            if (InWorldz)
            {
                SimulateHalcyonVerticalAttractor(h, x, y, angle, timescale, efficiency, rollOnly, worldRate);
                return;
            }

            double roll = Math.Atan2(y.Z, z.Z);
            double pitch = Math.Asin(Utils.Clamp(-x.Z, -1f, 1f));
            double stiffness = 1.0 / ((double)timescale * timescale);
            double damping = efficiency / (double)timescale;

            // With LIMIT_ROLL_ONLY the pitch is left alone.
            bool pitchSprung = !rollOnly;

            // The spring's own rates: the body's, plus what the last step's turn left out of them.
            double rollRate = Vector3.Dot(worldRate, x);
            double pitchRate = Vector3.Dot(worldRate, y);
            (double rollTurn, double rollCarry) = VehicleSpring.MoveOverGeneral(roll, rollRate + (_attractorCarry?.X ?? 0), stiffness, damping, h);
            // With LIMIT_ROLL_ONLY the pitch is left alone: no spring and no damping on it (any rate the last step's
            // pitch turn left out is handed back).
            double pitchTurn = pitchRate + (_attractorCarry?.Y ?? 0), pitchCarry = 0;
            if (pitchSprung)
                (pitchTurn, pitchCarry) = VehicleSpring.MoveOverGeneral(pitch, pitchTurn, stiffness, damping, h);
            _attractorCarry = new Vector3((float)rollCarry, (float)pitchCarry, 0f);

            // Applied whole with the motors' change (TorqueFini's per-step clean-up would stop it short).
            _attractorChange = x * (float)(rollTurn - rollRate) + y * (float)(pitchTurn - pitchRate);
            _motorTorqueVelChange += _attractorChange;
            OwnRoll(x, worldRate + _attractorChange);
        }

        // The roll rate the attractor set along the roll axis of its last step (a scalar and that axis), kept so the next step can
        // put it back on the roll axis the vehicle has by then. Not kept when the attractor does not act.
        private bool _ownsRoll;
        private float _ownedRoll;
        private Vector3 _ownedRollAxis;

        // The roll rate the attractor owns is the part of the angular velocity it has just set that is about the roll axis and
        // is not the world's vertical turn: a turn about the vertical (yaw, banking's) has a part along the roll axis of a
        // climbing or diving vehicle, which is that turn and not a roll, and must not be carried round with the axis.
        private void OwnRoll(Vector3 axis, Vector3 worldRateAfter)
        {
            _ownsRoll = true;
            _ownedRoll = Vector3.Dot(worldRateAfter, axis) - worldRateAfter.Z * axis.Z;
            _ownedRollAxis = axis;
        }

        // A move of the owned roll rate smaller than this (rad/s) is rounding in the orientation, not a turn of the roll axis.
        private const float MinRollReaxis = 1e-6f;

        /// <summary>
        /// Moves the roll rate the attractor set last step from the axis it was set about to the vehicle's roll axis now
        /// (<paramref name="x"/>), and gives the world angular velocity with that done. The attractor sets a roll rate along
        /// the roll axis of its step; banking then turns the vehicle about the world's vertical, the roll axis swings
        /// away from the world vector that rate was, and the part of it that is no longer along the roll axis is
        /// a rate about the pitch axis, which a roll-only attractor leaves alone: the vehicle pitched away
        /// (a 30 degree roll on an airplane that banks: pitch -31 degrees by 12 s). With the rate owned by the roll axis it
        /// belongs to, each step's roll rate is worked out against the axes the vehicle has at that step.
        /// </summary>
        private Vector3 ReaxisOwnedRoll(Vector3 x)
        {
            if (!_ownsRoll)
                return _rawWorldAngularVel;
            Vector3 move = (x - _ownedRollAxis) * _ownedRoll;
            if (move.LengthSquared() < MinRollReaxis * MinRollReaxis)
                return _rawWorldAngularVel;
            _motorTorqueVelChange += move;
            return _rawWorldAngularVel + move;
        }

        // The change of angular velocity the attractor gave the body this step (world); zero when it did not act. InWorldz's
        // angular motor leaves an axis alone while the attractor acts on it and the motor has no direction there.
        private Vector3 _attractorChange;

        // What the last attractor step's turn left out of the spring's roll and pitch rates; null when it did not act.
        private Vector3? _attractorCarry;

        /// <summary>
        /// InWorldz's attractor dormancy (VehicleDynamics.cs:1255-1264, read, not run): "Go dormant if in the sweet spot or
        /// if no angular changes are happening. Do not go dormant if the vehicle is overturned." The attractor acts while the
        /// tilt has changed by at least pi / 256 (0.7 degrees) between two steps within the last
        /// <see cref="VehicleLimits.MaxAttractDormancy"/> seconds (1.5 s), or while the tilt is at least
        /// <see cref="VehicleLimits.ThresholdOverturnAngle"/> (0.6 pi). The tilt it is compared with is the one of the last step
        /// the attractor acted on, so a dormant attractor wakes once the tilt has moved 0.7 degrees from where it went to
        /// sleep. Halcyon counts frames of about 15 ms; here the change that wakes it is taken per
        /// <see cref="HalcyonStep"/> of time while the attractor acts (the tilt it is compared with is then the last step's),
        /// so the step rate does not decide how long it stays awake; once it has been dormant for a step the tilt it is
        /// compared with is the stale one and the change is the displacement itself. An airplane is measured from the nearer of upright and
        /// inverted, as there.
        /// </summary>
        private bool AttractorDormant(float tilt, float h)
        {
            VehicleDynamicsState d = _sp.Dynamics;
            bool wasDormant = d.AttractorQuietTime >= VehicleLimits.MaxAttractDormancy;
            d.AttractorQuietTime += h;
            double threshold = VehicleLimits.ThresholdAttractorAngle * (wasDormant ? 1.0 : h / HalcyonStep);
            if (Math.Abs(d.LastVerticalAngle - tilt) >= threshold)
                d.AttractorQuietTime = 0f;
            float measured = _sp.Type == VehicleType.Airplane && tilt > Math.PI / 2.0 ? (float)Math.PI - tilt : tilt;
            return measured < VehicleLimits.ThresholdOverturnAngle && d.AttractorQuietTime >= VehicleLimits.MaxAttractDormancy;
        }

        // ---- InWorldz's attractor (VehicleDynamics.cs, SimulateVerticalAttractor 1217-1334) ----
        //
        // Halcyon scales the attractor's restoring push by a factor that depends on the tilt and on how long the vehicle
        // has been overturned (the push itself and its damping: SimulateHalcyonVerticalAttractor).
        //   - Vehicles other than airplanes "have very strong restorative forces": x (1 + (1 + tilt / pi)^4) (1272-1274).
        //   - Other than airplanes, while overturned (tilt at least 0.6 pi) and not improving (the tilt no smaller than
        //     the step before's) the factor grows by 1.3 every step; otherwise it falls by 1.1 every step, to 1 (1288-1302).
        //     Halcyon's step is 15 ms (PhysxScene.cs:55-56); the growth and fall are taken per 15 ms of time, so they
        //     do not depend on the step rate here.
        //   - Halcyon has no limit on the growth (its PhysX body's angular speed limit bounds the effect); here the factor
        //     stops at MaxAttractorEscalation, the cap the documented-spring form of this attractor needed and this one keeps.
        // The tilt is Halcyon's: the angle of the rotation made of the Euler roll and pitch alone (HalcyonVehicleLaws.TiltAngle),
        // which is larger than the angle between the z axis and up when the vehicle is rolled and pitched at once.

        /// <summary>InWorldz's strength factor for a tilt of <paramref name="tilt"/> radians: 1 + (1 + tilt / pi)^4.</summary>
        internal static float InWorldzAttractorBoost(float tilt)
            => 1f + (float)Math.Pow(1.0 + tilt / Math.PI, 4.0);

        // With LIMIT_ROLL_ONLY, the share of its pitch spring a car, boat or sled keeps: "vtwix.Y *= 0.1f"
        // (VehicleDynamics.cs:1278-1284, "Other vehicles need a tad more stablization because of stronger PhysX collisions").
        internal const double RollOnlyPitchShare = 0.1;

        // Halcyon's physics step, over which its strength factor grows by 1.3 or falls by 1.1.
        internal const double HalcyonStep = 0.015;
        internal const double EscalationGrowth = 1.3, EscalationFall = 1.1;
        internal const float MaxAttractorEscalation = 10f;

        private float AttractorStrength(float tilt, float h)
        {
            VehicleDynamicsState d = _sp.Dynamics;
            float last = d.LastVerticalAngle;
            d.LastVerticalAngle = tilt;
            if (_sp.Type == VehicleType.Airplane)
                return 1f;

            double steps = h / HalcyonStep;
            if (tilt >= VehicleLimits.ThresholdOverturnAngle && tilt >= last)
                d.VerticalForceAdjust = Math.Min(d.VerticalForceAdjust * (float)Math.Pow(EscalationGrowth, steps), MaxAttractorEscalation);
            else
                d.VerticalForceAdjust = Math.Max(d.VerticalForceAdjust / (float)Math.Pow(EscalationFall, steps), 1f);
            return InWorldzAttractorBoost(tilt) * d.VerticalForceAdjust;
        }

        /// <summary>
        /// InWorldz's vertical attractor (VehicleDynamics.cs:1217-1334, read, not run), in place of the documented spring. Each
        /// step it gives the roll and pitch angular velocity a push toward upright of
        /// pi e^(3 efficiency) sin(tilt about the axis) h / T (<see cref="HalcyonVehicleLaws.AttractorPush"/>), scaled by the
        /// strength of <see cref="AttractorStrength"/> (the cars' and boats' stronger righting and its escalation), and takes
        /// the share e^(efficiency / e) - 1 of the roll and pitch angular velocity away
        /// (<see cref="HalcyonVehicleLaws.AttractorDampingShare"/>). Both are worked out from the velocity at the start of the
        /// step and added to the motors' change. With LIMIT_ROLL_ONLY the pitch push of an airplane or balloon is zero and
        /// that of any other vehicle a tenth; the damping stays on both axes. Per step, as Halcyon's: a push of a fixed share
        /// each step, so it depends on the engine's step as Halcyon's did on its own.
        /// </summary>
        private void SimulateHalcyonVerticalAttractor(float h, Vector3 x, Vector3 y, float tilt, float timescale, float efficiency, bool rollOnly,
                                                      Vector3 worldAngularVel)
        {
            float strength = AttractorStrength(tilt, h);
            float pitchShare = 1f;
            if (rollOnly)
                pitchShare = _sp.Type == VehicleType.Airplane || _sp.Type == VehicleType.Balloon ? 0f : (float)RollOnlyPitchShare;

            (float rollPush, float pitchPush) = HalcyonVehicleLaws.AttractorPush(y.Z, x.Z, efficiency, timescale, h, strength, pitchShare);
            // The share is Halcyon's per step of its 15 ms (VehicleDynamics.cs:1306-1308); a step of h seconds takes it
            // h / 15 ms times over, compounded, so the damping a second does not depend on the step.
            float damping = HalcyonVehicleLaws.AttractorDampingShare(efficiency);
            damping = 1f - (float)Math.Pow(1.0 - damping, h / HalcyonStep);
            float rollRate = Vector3.Dot(worldAngularVel, x);
            float pitchRate = Vector3.Dot(worldAngularVel, y);

            _attractorCarry = null;
            _attractorChange = x * (rollPush - damping * rollRate) + y * (pitchPush - damping * pitchRate);
            _motorTorqueVelChange += _attractorChange;
            OwnRoll(x, worldAngularVel + _attractorChange);
        }

        // The attractor hands the body back its true roll and pitch rates when it stops acting.
        private void LetGoOfAttractor(Vector3 x, Vector3 y)
        {
            if (_attractorCarry is Vector3 c)
                _motorTorqueVelChange += x * c.X + y * c.Y;
            _attractorCarry = null;
            _ownsRoll = false;
        }

        #endregion

        #region Banking

        /// <summary>
        /// Banking, as Second Life documents it (Linden_Vehicle_Tutorial, "Banking"; LlSetVehicleFloatParam): a roll
        /// about the vehicle's roll axis gives it an angular velocity about the yaw axis, so it turns; positive
        /// efficiency leans into the turn, negative leans out of it. "The magnitude of the yaw effect will be
        /// proportional to the VEHICLE_BANKING_EFFICIENCY, the angle of the roll rotation, and sometimes the vehicle's
        /// velocity along its preferred axis of motion", the last with BANKING_MIX toward 1:
        ///
        ///   target = -roll * attitude * eff * pi * ((1 - mix) + mix * speed / Vref)       (roll the angle about the
        ///            vehicle's x axis, as the vertical attractor measures it, whatever the pitch; upside down, measured
        ///            from the nearer side, with attitude -1; Vref the reference speed, 30 m/s by default)
        ///
        /// and the yaw rate about world z approaches it with BANKING_TIMESCALE, the documented "time it takes for the
        /// banking behavior to defeat a preexisting angular velocity about the world z-axis":
        ///
        ///   w' = (s target - w) / T,   stepped exactly: w(h) = s target + (w0 - s target) e^(-h / T).
        ///
        /// s is the banking's strength: 1, but with VEHICLE_FLAG_LIMIT_MOTOR_UP in the air (LlSetVehicleFlags: "the
        /// strength of the banking will decay when the vehicle no longer experiences collisions. The decay timescale is
        /// the same as VEHICLE_BANKING_TIMESCALE") s = e^(-t_air / T), t_air the time since it last touched something.
        /// It is whole again on the step after the vehicle touches something. With s fading over the step the
        /// equation's exact step is w(h) = (w0 + s0 target h / T) e^(-h / T), s0 its strength at the step's start.
        ///
        /// The vertical attractor must be on ("must be enabled in order for the banking behavior to function"); off
        /// at a banking timescale of 500 s or more; under ThresholdBankAngle of roll banking leaves the yaw alone.
        /// Returns the change to the angular velocity about world z.
        ///
        /// Under InWorldz the turn is Halcyon's: this only works out the rate banking asks for from the roll (BankingDirection,
        /// VehicleDynamics.cs:1338-1401, SimulateBankingToYaw; zero when banking is off, the vertical attractor is off for the
        /// region (<paramref name="regionAllowsBanking"/> false) or the roll is under the threshold) and returns 0. The turn itself is
        /// Halcyon's banking turn motor, a stepwise log-growth ramp toward that rate, run on Halcyon's 15 ms clock inside
        /// <see cref="HalcyonAngularMotorOnItsClock"/> and added to the angular motor's change about z before the
        /// pi / 512 threshold (VehicleDynamics.cs:2949-3040).
        /// </summary>
        private float SimulateBanking(float h, float tilt, bool regionAllowsBanking = true)
        {
            // The time in the air at the start of this step, for LIMIT_MOTOR_UP's fade; kept whether banking acts or not.
            double airTime = _bankingAirTime;
            _bankingAirTime = _body.HasCollision ? 0 : _bankingAirTime + h;
            if (InWorldz)
                _sp.Dynamics.BankingDirection = 0f;
            if (!regionAllowsBanking)
                return 0f;

            float timescale = _sp.GetFloat(VehFloatParam.BankingTimescale, 1000f);
            float efficiency = _sp.GetFloat(VehFloatParam.BankingEfficiency, 0f);
            // Second Life: "must be enabled [the vertical attractor] in order for the banking behavior to function". InWorldz banks
            // whenever the banking timescale allows (VehicleDynamics.cs:1338-1352, SimulateBankingToYaw), with no attractor.
            if (!S.DoBanking || efficiency == 0f || timescale >= S.MaxAttractTimescale
                || (!InWorldz && _sp.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f) >= S.MaxAttractTimescale))
                return 0f;

            float mix = _sp.GetFloat(VehFloatParam.BankingMix, 0.5f);
            float forwardSpeed = Math.Abs(_rawLocalLinearVel.X);
            float speedShare = Utils.Clamp(forwardSpeed, 0, S.ReferenceSpeed) / S.ReferenceSpeed;

            // The roll: the angle about the x axis that would bring the y axis level (the attractor's roll), upside down
            // measured from the nearer side (so that it turns the way the y axis' rise did), scaled to the banking range.
            const float HalfPi = (float)(Math.PI / 2.0);
            float roll;
            if (InWorldz)
            {
                // InWorldz measures the roll as the rise of the y axis, the sine of the roll (VehicleDynamics.cs:1366-1376), scaled
                // to the banking range a script may set (VEHICLE_BANKING_AZIMUTH) and held to -1..1.
                float range = _sp.GetFloat(VehFloatParam.BankingAzimuth, HalfPi);
                roll = Utils.Clamp((Vector3.UnitY * _rotation).Z * HalfPi / range, -1f, 1f);
            }
            else
            {
                float angle = (float)Math.Atan2((Vector3.UnitY * _rotation).Z, (Vector3.UnitZ * _rotation).Z);
                if (angle > HalfPi)
                    angle = (float)Math.PI - angle;
                else if (angle < -HalfPi)
                    angle = -(float)Math.PI - angle;
                // Second Life's roll range is the whole pi / 2.
                roll = Utils.Clamp(angle * HalfPi / HalfPi, -HalfPi, HalfPi);
            }
            if (Math.Abs(roll) <= VehicleLimits.ThresholdBankAngle)
                return 0f;
            float attitude = tilt > Math.PI / 2.0 ? -1f : 1f;
            // InWorldz: an airplane flying inverted banks by the inverted banking modifier (VehicleDynamics.cs:1241-1247,
            // 1376-1377; only an airplane is marked inverted there).
            if (InWorldz && _sp.Type == VehicleType.Airplane && tilt > Math.PI / 2.0)
                efficiency *= _sp.GetFloat(VehFloatParam.InvertedBankingModifier, 1f);

            float target = -roll * attitude * efficiency * (float)Math.PI * ((1f - mix) + mix * speedShare);
            if (InWorldz)
            {
                // The legacy speed limit with the angular motor engaged and the angular speed limit are Halcyon's turn motor's
                // (HalcyonVehicleLaws.BankingTurnChange). LIMIT_MOTOR_UP's fade in the air stays, as a share of the rate asked for.
                float strength = (_sp.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && !_body.HasCollision
                    ? (float)Math.Exp(-airTime / timescale)
                    : 1f;
                _sp.Dynamics.BankingDirection = target * strength;
                return 0f;
            }
            // With the angular motor engaged, banking turns no faster than the legacy angular speed.
            if (_sp.Dynamics.AngularDecayIndex < VehicleLimits.ThresholdAngularMotorEngaged)
                target = Utils.Clamp(target, -VehicleLimits.MaxLegacyAngularVelocity, VehicleLimits.MaxLegacyAngularVelocity);
            target = Utils.Clamp(target, -S.MaxAngularSpeed, S.MaxAngularSpeed);

            float w0 = _rawWorldAngularVel.Z;
            float w1;
            if ((_sp.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && !_body.HasCollision)
                w1 = (float)((w0 + target * Math.Exp(-airTime / timescale) * h / timescale) * Math.Exp(-h / timescale));
            else
                w1 = target + (w0 - target) * (float)Math.Exp(-h / timescale);
            return w1 - w0;
        }

        // How long the vehicle has been out of touch with anything (s), for LIMIT_MOTOR_UP's banking fade.
        private double _bankingAirTime;

        #endregion

        #region Motors and friction — Linear + Angular, and the Banking turn motor

        /// <summary>
        /// The linear motor, linear friction and the vehicle's gravity over one step of h seconds. On each axis of
        /// the vehicle frame:
        ///
        ///   dv/dt = g(s) * (M - v) / Tm  -  v / Tf  +  a          g(s) = e^(-s / Td)
        ///
        /// M the motor direction, Tm the motor timescale, Td the motor decay timescale, s the time since the motor
        /// was last set, Tf the friction timescale, a the vehicle's gravity along the axis plus a sled's slope
        /// assist (<see cref="SledAssist"/>; see
        /// <see cref="VehicleMotorSolver"/>). The velocity after the step is the exact solution of that equation over
        /// it, so the result does not depend on the step rate. Friction acts on every axis all the time; the motor's
        /// pull fades with its decay and is never cut off. Under InWorldz the motor is Halcyon's instead
        /// (<see cref="HalcyonLinearMotorOnItsClock"/>), a change on top of friction's.
        ///
        /// The engine applies the gravity itself over the step (<see cref="ApplyGravity"/>); the change written here
        /// is the exact solution less that gravity, so the two together end the step on the exact velocity.
        /// </summary>
        // The seconds InWorldz puts a linear motor's decay clock on while hover moderates it (VehicleDynamics.cs:2719).
        internal const float HoverModerationSeconds = 10.0f;

        private void SimulateLinearMotorAndFriction(float h, bool motorOn, bool frictionOn)
        {
            Vector3 v0 = _rawLocalLinearVel;
            Vector3 gravityWorld = _body.Gravity * GravityShare();
            // The constant accelerations along the vehicle's axes: its gravity, and a sled's slope assist.
            Vector3 gravity = gravityWorld * Quaternion.Inverse(_rotation) + SledAssist();
            Vector3 motor = _sp.Dynamics.LinearDirection;
            Vector3 motorTs = _sp.GetVec(VehVectorParam.LinearMotorTimescale);
            Vector3 decayTs = _sp.GetVec(VehVectorParam.LinearMotorDecayTimescale);
            Vector3 frictionTs = frictionOn ? _sp.GetVec(VehVectorParam.LinearFrictionTimescale) : FrictionOff;

            if (!motorOn)
                _sp.Dynamics.LinearDecayIndex = float.PositiveInfinity;
            double age = _sp.Dynamics.LinearDecayIndex;

            // Friction and gravity alone over the step, and the motor with them.
            Vector3 frictionOnly = FrictionStep(v0, frictionTs, h, gravity);
            Vector3 motorChange;
            if (InWorldz)
            {
                motorChange = HalcyonLinearMotorOnItsClock(h, v0, motor, motorTs, decayTs);
            }
            else
            {
                Vector3 v1 = new Vector3(
                    (float)MotorStep(v0.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X, h, gravity.X),
                    (float)MotorStep(v0.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y, h, gravity.Y),
                    (float)MotorStep(v0.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z, h, gravity.Z));
                v1.X = Utils.Clamp(v1.X, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                v1.Y = Utils.Clamp(v1.Y, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                v1.Z = Utils.Clamp(v1.Z, -S.MaxLinearSpeed, S.MaxLinearSpeed);
                motorChange = (v1 - frictionOnly) * _rotation;
            }

            // The step's change in world coordinates: friction's share (with gravity's, less the g h the engine adds),
            // and the motor's share on top of it.
            Vector3 frictionChange = (frictionOnly - v0) * _rotation - gravityWorld * h;

            // InWorldz: "Cheat: Do not fight gravity." Friction that would push the vehicle up in world z is dropped
            // (VehicleDynamics.cs:1494-1495), so a falling vehicle is not slowed by its friction and one rising is. What
            // friction does to the step is the change it makes to the velocity the vehicle would have with no friction
            // (its velocity and the gravity over the step); where that change is upward in world z it is taken out,
            // leaving the vehicle to the engine's gravity on that axis.
            if (InWorldz)
            {
                float frictionUp = ((frictionOnly - (v0 + gravity * h)) * _rotation).Z;
                if (frictionUp > 0f)
                    frictionChange.Z -= frictionUp;
            }

            // VEHICLE_FLAG_LIMIT_MOTOR_UP / LIMIT_MOTOR_DOWN: the motor does not push up (down) in world terms.
            // Friction's share is left whole. Under InWorldz each of Halcyon's motor steps has these applied to its own change
            // (HalcyonLinearMotorOnItsClock).
            if (!InWorldz)
            {
                if ((_sp.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && motorChange.Z > 0)
                    motorChange.Z = 0;
                if ((_sp.Flags & ExtendedVehicleFlags.LimitMotorDown) != 0 && motorChange.Z < 0)
                    motorChange.Z = 0;
            }

            _sp.Dynamics.LinearDecayIndex += h;

            // InWorldz's motor-height rule: the motor is not applied while it holds (the clock above still runs).
            if (_motorsOffByHeight)
                motorChange = Vector3.Zero;
            ApplyLinearVelocityChange(frictionChange + motorChange);
            // The motor's offset: under InWorldz it is the point every linear force acts at (friction and deflection and the
            // slope assist as well as the motor; VehicleMotor.AddForce, VehicleDynamics.cs:2376-2398), under Second Life the
            // page's "where the linear motor is applied".
            _motorTorqueVelChange += MotorOffsetTurn(InWorldz ? frictionChange + motorChange : motorChange);
        }

        // InWorldz's motors are Halcyon's, and Halcyon runs them once in each of its physics steps of 15 ms: a motor step reads the
        // velocity the last step and the friction left, sets it toward the motor's target (the ramp, the decay, and the rule that
        // the target replaces a lower velocity: VehicleDynamics.cs:2587-2589), and the engine then takes 15 ms of contact
        // friction from it. That cadence is part of the law: the replacement is how a car gets going against friction, and the
        // distance a car loses to friction between two motor steps depends on how long they are apart. The controller runs in
        // every collision step of the engine, which is shorter or longer than 15 ms, so each motor keeps a clock of the time
        // carried since its last step, and runs one of Halcyon's steps (always of 15 ms) for each 15 ms that has gone by, at
        // the start of the collision step in which it falls due. A collision step shorter than 15 ms then runs none, as often
        // as not; one longer than 15 ms runs several, one after the other on the velocity the one before left. Friction, the
        // attractor, banking and the rest are integrated in every collision step as before.
        private double _linearMotorCarry;
        private double _angularMotorCarry;

        // The axes of the vehicle's frame the linear motor has had a direction on (1) since the vehicle was made a vehicle: the
        // rule that the target replaces a lower velocity keeps acting on them after the key goes up (the glide to a stop), and
        // not on an axis it never had a direction on (VehicleSettings.MotorTargetOnUndrivenAxes).
        private Vector3 _linearAxesDriven;

        /// <summary>The number of Halcyon steps (of <see cref="HalcyonStep"/>) that fall due when <paramref name="h"/> seconds are
        /// added to the time <paramref name="carry"/> carries; the carry keeps the remainder.</summary>
        internal static int StepsDue(ref double carry, double h)
        {
            carry += h;
            // The small allowance keeps three steps of exactly 5 ms from being one rounding error short of a step.
            int due = (int)Math.Floor(carry / HalcyonStep + 1e-6);
            if (due > 0)
                carry = Math.Max(0.0, carry - due * HalcyonStep);
            return due;
        }

        /// <summary>
        /// The change of world velocity InWorldz's linear motor makes in a collision step of h seconds (VehicleDynamics.cs:2576-2725,
        /// read, not run): the sum over the steps of Halcyon's that fall due in it (<see cref="StepsDue"/>), each worked out from
        /// the velocity the one before leaves, as a change on top of friction's, which is worked out from the same start
        /// velocity. Each step is the ramp toward the direction with the motor's target velocity and the decay blend
        /// (<see cref="HalcyonVehicleLaws.LinearMotor"/>, at 15 ms); nothing is applied when the new velocity is under
        /// 0.005 m/s on every world axis ("let the vehicle eventually sleep"), or when the motor has nothing left to add. The
        /// limit-motor flags, the gravity check and the hover moderation act on each step's change, as they do on Halcyon's.
        /// </summary>
        private Vector3 HalcyonLinearMotorOnItsClock(float h, Vector3 lastvel, Vector3 motor, Vector3 motorTs, Vector3 decayTs)
        {
            double carried = _linearMotorCarry;
            int due = StepsDue(ref _linearMotorCarry, h);
            if (due == 0)
                return Vector3.Zero;

            VehicleDynamicsState d = _sp.Dynamics;
            // Each step covers the 15 ms that has just gone by, so its clock reads what it read at the start of that interval.
            float intervalStart = d.LinearDecayIndex - (float)carried;
            float moderation = 0f;
            Quaternion inverse = Quaternion.Inverse(_rotation);
            Vector3 world = _rawWorldLinearVel;
            Vector3 local = lastvel;
            Vector3 total = Vector3.Zero;
            for (int i = 0; i < due; i++)
            {
                float read = intervalStart + i * (float)HalcyonStep + moderation;
                float index = read;
                Vector3 target = d.LinearTargetVelocity;
                Vector3 next = HalcyonVehicleLaws.LinearMotor((float)HalcyonStep, local, motor, motorTs, decayTs, ref index, ref target,
                    S.MaxLinearSpeed, IsLinearMotorStalled, S.MotorTargetOnUndrivenAxes, _linearAxesDriven);
                if (motor.X != 0f) _linearAxesDriven.X = 1f;
                if (motor.Y != 0f) _linearAxesDriven.Y = 1f;
                if (motor.Z != 0f) _linearAxesDriven.Z = 1f;
                d.LinearTargetVelocity = target;
                if (index != read)
                    d.LinearDecayIndex = index;   // stalled: the clock is put far past any decay

                Vector3 change = Vector3.Zero;
                Vector3 newWorld = next * _rotation;
                if (next != local
                    && (Math.Abs(newWorld.X) >= VehicleLimits.ThresholdLinearMotorDeltaV
                        || Math.Abs(newWorld.Y) >= VehicleLimits.ThresholdLinearMotorDeltaV
                        || Math.Abs(newWorld.Z) >= VehicleLimits.ThresholdLinearMotorDeltaV))
                {
                    change = newWorld - world;

                    // VEHICLE_FLAG_LIMIT_MOTOR_UP / _DOWN (VehicleDynamics.cs:2695-2705).
                    if ((_sp.Flags & ExtendedVehicleFlags.LimitMotorUp) != 0 && change.Z > 0)
                        change.Z = 0;
                    if ((_sp.Flags & ExtendedVehicleFlags.LimitMotorDown) != 0 && change.Z < 0)
                        change.Z = 0;

                    // "Gravity check. Any up Z-forces less than the effective gravity are nulled" (2707-2712): the step's upward
                    // change of velocity has gravity times the share of it not carried by buoyancy added to it (gravity is
                    // negative), and is dropped if that leaves it below zero. It is compared as a change of velocity against an
                    // acceleration, as there.
                    if (change.Z > 0f)
                    {
                        change.Z += _body.Gravity.Z * (1f - _sp.GetFloat(VehFloatParam.Buoyancy, 0f));
                        if (change.Z < 0f)
                            change.Z = 0f;
                    }

                    // "Hover moderation. If hover is present, accelerate the motor decay" (2714-2720): a motor that still moves
                    // the vehicle up or down while a hover flag is set and the hover is on has its decay clock put on ten seconds.
                    if (Math.Abs(change.Z) > VehicleLimits.ThresholdLinearMotorDeltaV
                        && (_sp.Flags & (ExtendedVehicleFlags.HoverGlobalHeight | ExtendedVehicleFlags.HoverTerrainOnly | ExtendedVehicleFlags.HoverWaterOnly)) != 0
                        && _sp.GetFloat(VehFloatParam.HoverTimescale, 1000f) < S.MaxHoverTimescale)
                    {
                        moderation += HoverModerationSeconds;
                        d.LinearDecayIndex += HoverModerationSeconds;
                    }
                }

                total += change;
                world += change;
                local = world * inverse;
            }
            return total;
        }

        /// <summary>
        /// VEHICLE_LINEAR_MOTOR_OFFSET: the offset from the vehicle's centre of mass at which the linear motor acts
        /// (wiki.secondlife.com/wiki/LlSetVehicleVectorParam), so the motor's push also turns the vehicle, as a force at that
        /// point does (the page notes that rockets built with this offset spin out of control; the parameter's own page,
        /// VEHICLE_LINEAR_MOTOR_OFFSET, says the same). The motor's change of velocity this step, dv, is an impulse m dv at the
        /// offset point r (in the vehicle frame); its angular impulse about the centre of mass turns the body by
        ///
        ///   dw = I^-1 (r x m dv)
        ///
        /// with I the body's principal moments of inertia, taken along the body's own axes. dv is the exact motor
        /// share over the step, so the turn over a span of time does not depend on the step either. Friction and
        /// gravity act at the centre of mass and give no turn.
        /// </summary>
        private Vector3 MotorOffsetTurn(Vector3 motorChange)
        {
            Vector3 offset = _sp.GetVec(VehVectorParam.LinearMotorOffset);
            if (offset == Vector3.Zero || motorChange == Vector3.Zero)
                return Vector3.Zero;
            Vector3 r = offset * _rotation;
            return AngularChangeOf(Vector3.Cross(r, motorChange * m_vehicleMass));
        }

        // The change of angular velocity an angular impulse makes: to the body's axes, through the inverse of its principal
        // moments, and back to the world.
        private Vector3 AngularChangeOf(Vector3 angularImpulse)
        {
            Quaternion bodyRotation = _body.Orientation;
            Vector3 local = angularImpulse * Quaternion.Inverse(bodyRotation);
            Vector3 inertia = _body.InertiaDiagonal;
            local = new Vector3(
                inertia.X > 0f ? local.X / inertia.X : 0f,
                inertia.Y > 0f ? local.Y / inertia.Y : 0f,
                inertia.Z > 0f ? local.Z / inertia.Z : 0f);
            return local * bodyRotation;
        }

        // InWorldz's wind: the wind (or water current) at the vehicle's position as of the last collision step.
        private Vector3 _windSample;

        /// <summary>The wind the vehicle last sampled, in the world's frame. For tests.</summary>
        internal Vector3 WindSample => _windSample;

        /// <summary>
        /// InWorldz's wind and water currents (VehicleDynamics.cs:863-992, BuildWindData and SimulateWindForces, read, not run),
        /// only under <see cref="VehicleBehaviour.InWorldz"/>. A vehicle with VEHICLE_FLAG_REACT_TO_WIND set is pushed by the
        /// wind at its position (the surface wind or the wind aloft above the water), and one with VEHICLE_FLAG_REACT_TO_CURRENTS
        /// by the water's current below the water; the wind module of the region says which. The wind is read in every
        /// collision step at the vehicle's position, and the sample must not take a lock (<see cref="IVehicleBody.TryGetWind"/>).
        /// Its push is
        /// <see cref="HalcyonVehicleLaws.WindLinearVelocityChange"/> on the vehicle's velocity, scaled by
        /// VEHICLE_LINEAR_WIND_EFFICIENCY, and <see cref="HalcyonVehicleLaws.WindAngularVelocityChange"/> on its angular
        /// velocity, scaled by VEHICLE_ANGULAR_WIND_EFFICIENCY. With neither flag set, under Second Life's behaviour, or with
        /// no wind module, nothing is read and nothing pushes the vehicle.
        /// Halcyon applies the push only for the wind flag, and reads the current for a vehicle under the water that has either
        /// flag but pushes it only when the wind flag is set, so REACT_TO_CURRENTS alone does nothing there. Here the
        /// current flag does what its name says: a vehicle below the water is pushed by the current with either flag, and a
        /// vehicle above it only with the wind flag.
        /// </summary>
        private void SimulateWind(float h)
        {
            const ExtendedVehicleFlags react = ExtendedVehicleFlags.ReactToWind | ExtendedVehicleFlags.ReactToCurrents;
            if (!InWorldz || (_sp.Flags & react) == 0)
            {
                _windSample = Vector3.Zero;
                return;
            }

            Vector3 position = _body.Position;
            _windSample = _body.TryGetWind(position, out Vector3 wind) && float.IsFinite(wind.X) && float.IsFinite(wind.Y)
                ? new Vector3(wind.X, wind.Y, 0f) : Vector3.Zero;

            bool underWater = position.Z < _body.GetWaterLevel(position);
            if (!underWater && (_sp.Flags & ExtendedVehicleFlags.ReactToWind) == 0)
                return;

            Vector3 windLocal = _windSample * Quaternion.Inverse(_rotation);
            Vector3 push = HalcyonVehicleLaws.WindLinearVelocityChange(windLocal, _localLinearVel,
                _sp.GetVec(VehVectorParam.LinearWindEfficiency), h);
            if (push != Vector3.Zero)
                ApplyLinearVelocityChange(push * _rotation);

            Vector3 turn = HalcyonVehicleLaws.WindAngularVelocityChange(windLocal, _localAngularVel,
                _sp.GetVec(VehVectorParam.AngularWindEfficiency), h);
            if (turn != Vector3.Zero)
                AddTorqueVelocityChange(turn * _rotation);
        }

        // InWorldz's motor-height rule: the last height measured below the vehicle, how long it has been above the limit, and
        // whether the motors are off for it.
        private float _heightAbove;
        private float _timeAboveHeight;
        private bool _motorsOffByHeight;
        private double _heightRayClock;

        /// <summary>True while the motor-height rule has the motors off. For tests.</summary>
        internal bool MotorsOffByHeight => _motorsOffByHeight;

        /// <summary>
        /// InWorldz's motor-height rule (VehicleDynamics.cs:2494-2573, read, not run): a vehicle that has been more than
        /// VEHICLE_DISABLE_MOTORS_ABOVE metres above what lies below it for VEHICLE_DISABLE_MOTORS_AFTER seconds has its
        /// linear and angular motors switched off (friction, the attractor and gravity go on), so a car does not steer or
        /// drive while it flies. The legacy car has 0.75 m and 2.5 s, the legacy sled and boat their own; the airplane and
        /// the rest have 0, which is off. The rule is off for a vehicle that hovers (the hover timescale below its maximum),
        /// has positive buoyancy, or moves slower than <see cref="HalcyonVehicleLaws.MotorHeightMinSpeed"/>, and it is
        /// measured once in eight of Halcyon's 15 ms steps (0.12 s, kept in time): a ray from the lower left edge of the vehicle's box, and if that finds nothing near
        /// from the lower right edge, each starting a metre up the vehicle's own axis and going down it for three times the
        /// limit; the height is the distance less that metre, or the limit plus one metre when nothing is hit. The vehicle's own
        /// parts are not hit. The delay's clock runs as Halcyon's does, eight steps' time on every step
        /// (<see cref="HalcyonVehicleLaws.MotorsOffAboveHeight"/>), so a delay of 2.5 s runs out in about 0.3 s. Only under
        /// InWorldz: Second Life has no such rule.
        /// </summary>
        private void SimulateMotorHeight(float h)
        {
            _motorsOffByHeight = false;
            if (!InWorldz)
                return;

            float limit = _sp.GetFloat(VehFloatParam.DisableMotorsAbove, 0f);
            if (_sp.GetFloat(VehFloatParam.HoverTimescale, 1000f) < S.MaxHoverTimescale
                || _sp.GetFloat(VehFloatParam.Buoyancy, 0f) > 0f
                || _localLinearVel.Length() < HalcyonVehicleLaws.MotorHeightMinSpeed)
            {
                _heightAbove = 0f;
                limit = 0f;
            }

            // Halcyon casts on every eighth frame of its 15 ms timer (VehicleDynamics.cs:2517, frameNum % 8); here the
            // time since the last cast is carried, so the casts come every 0.12 s at any Update rate (the remainder
            // stays, so the average does not drift).
            bool rayDue = false;
            _heightRayClock += h;
            if (_heightRayClock >= HalcyonVehicleLaws.MotorHeightRaySeconds)
            {
                _heightRayClock -= HalcyonVehicleLaws.MotorHeightRaySeconds;
                if (_heightRayClock >= HalcyonVehicleLaws.MotorHeightRaySeconds)
                    _heightRayClock = 0.0;
                rayDue = true;
            }

            if (limit > 0f && rayDue
                && _body.TryGetBounds(out Vector3 center, out Vector3 half))
            {
                Quaternion rot = _body.Orientation;
                Vector3 apos = _body.Position + center * rot;
                Vector3 down = new Vector3(0f, 0f, -1f) * rot;
                Vector3 up = new Vector3(0f, 0f, 1f) * rot;
                Vector3 left = new Vector3(0f, half.Y, -half.Z) * rot;
                Vector3 right = new Vector3(0f, -half.Y, -half.Z) * rot;
                _heightAbove = limit + 1f;
                if (_body.CastRayNearest(apos + left + up, down, limit * 3f, out float d))
                    _heightAbove = d - up.Z;
                if (_heightAbove > limit)
                {
                    _heightAbove = limit + 1f;
                    if (_body.CastRayNearest(apos + right + up, down, limit * 3f, out d))
                        _heightAbove = d - up.Z;
                }
            }

            _motorsOffByHeight = HalcyonVehicleLaws.MotorsOffAboveHeight(_heightAbove, limit,
                _sp.GetFloat(VehFloatParam.DisableMotorsAfter, 0f), h, ref _timeAboveHeight);
        }

        /// <summary>
        /// The angular motor and angular friction over one step of h seconds: the same equation as the linear
        /// motor's (<see cref="SimulateLinearMotorAndFriction"/>) on the angular velocity about each vehicle axis.
        /// With VEHICLE_FLAG_TORQUE_WORLD_Z the third axis is the world's Z instead of the vehicle's, for the motor
        /// and the friction alike. Returns the change about world Z, which the banking turn motor adds to.
        /// </summary>
        private float SimulateAngularMotorAndFriction(float h, bool motorOn, bool frictionOn)
        {
            Vector3 worldVel = _rawWorldAngularVel;
            Vector3 motorTs = _sp.GetVec(VehVectorParam.AngularMotorTimescale);
            Vector3 motor = MouselookMotor(_sp.Dynamics.AngularDirection, motorTs);
            Vector3 decayTs = _sp.GetVec(VehVectorParam.AngularMotorDecayTimescale);
            Vector3 frictionTs = frictionOn ? _sp.GetVec(VehVectorParam.AngularFrictionTimescale) : FrictionOff;
            bool torqueWorldZ = (_sp.Flags & ExtendedVehicleFlags.TorqueWorldZ) != 0;

            // The velocity the equation acts on: about the vehicle's axes, or with TORQUE_WORLD_Z the vehicle's X
            // and Y of the velocity without its world-Z part, and world Z.
            Quaternion invRotation = Quaternion.Inverse(_rotation);
            Vector3 v0;
            if (torqueWorldZ)
            {
                v0 = new Vector3(worldVel.X, worldVel.Y, 0f) * invRotation;
                v0.Z = worldVel.Z;
            }
            else
            {
                v0 = worldVel * invRotation;
            }

            if (!motorOn)
                _sp.Dynamics.AngularDecayIndex = float.PositiveInfinity;
            double age = _sp.Dynamics.AngularDecayIndex;

            if (InWorldz)
                return SimulateAngularMotorAndFrictionInWorldz(h, v0, motor, motorTs, decayTs, frictionTs, torqueWorldZ);

            Vector3 v1 = new Vector3(
                (float)MotorStep(v0.X, motor.X, motorTs.X, decayTs.X, age, frictionTs.X, h),
                (float)MotorStep(v0.Y, motor.Y, motorTs.Y, decayTs.Y, age, frictionTs.Y, h),
                (float)MotorStep(v0.Z, motor.Z, motorTs.Z, decayTs.Z, age, frictionTs.Z, h));
            v1.X = Utils.Clamp(v1.X, -S.MaxAngularSpeed, S.MaxAngularSpeed);
            v1.Y = Utils.Clamp(v1.Y, -S.MaxAngularSpeed, S.MaxAngularSpeed);
            v1.Z = Utils.Clamp(v1.Z, -S.MaxAngularSpeed, S.MaxAngularSpeed);

            Vector3 change;
            if (torqueWorldZ)
            {
                Vector3 before = new Vector3(v0.X, v0.Y, 0f) * _rotation + new Vector3(0f, 0f, v0.Z);
                Vector3 after = new Vector3(v1.X, v1.Y, 0f) * _rotation + new Vector3(0f, 0f, v1.Z);
                change = after - before;
            }
            else
            {
                change = (v1 - v0) * _rotation;
            }

            _sp.Dynamics.AngularDecayIndex += h;

            // The change about world Z is applied in Step, with banking's.
            float aboutWorldZ = change.Z;
            change.Z = 0;
            _motorTorqueVelChange += change;
            return aboutWorldZ;
        }

        /// <summary>
        /// InWorldz's angular motor and friction (VehicleDynamics.cs:2733-2960 and SimulateAngularFriction, 1402-1458; read,
        /// not run), in place of the documented equation above. Each is a change of angular velocity worked out from the
        /// velocity at the start of the step, and the two are added: the motor sets the velocity it would have (see
        /// <see cref="HalcyonAngularMotor"/>), and friction takes the share 1 - e^(-h / Tf) of that start velocity away on
        /// top. So a friction timescale shorter than the motor's does not hold the motor down, as the documented equation
        /// would. The friction is the one of the documented solver (the exact exponential, off on an axis at 1000 s or more).
        /// The motor's change is applied only when it is at least pi / 512 about the world's x or y axis (the z part is
        /// always kept, for banking), as Halcyon's is.
        /// </summary>
        private float SimulateAngularMotorAndFrictionInWorldz(float h, Vector3 v0, Vector3 motor, Vector3 motorTs, Vector3 decayTs,
                                                              Vector3 frictionTs, bool torqueWorldZ)
        {
            Vector3 frictionChange = new Vector3(
                (float)VehicleMotorSolver.FrictionStep(v0.X, frictionTs.X, h) - v0.X,
                (float)VehicleMotorSolver.FrictionStep(v0.Y, frictionTs.Y, h) - v0.Y,
                (float)VehicleMotorSolver.FrictionStep(v0.Z, frictionTs.Z, h) - v0.Z);

            Vector3 motorWorld = HalcyonAngularMotorOnItsClock(h, v0, motor, motorTs, decayTs, torqueWorldZ);
            Vector3 frictionWorld = ChangeToWorld(frictionChange, torqueWorldZ);

            _sp.Dynamics.AngularDecayIndex += h;

            // The change about world Z is applied in Step, with banking's.
            Vector3 change = motorWorld + frictionWorld;
            float aboutWorldZ = change.Z;
            change.Z = 0;
            _motorTorqueVelChange += change;
            return aboutWorldZ;
        }

        // A change of angular velocity in the motor's frame (the vehicle's axes, or with TORQUE_WORLD_Z the vehicle's x and
        // y and the world's z) as a change of world angular velocity.
        private Vector3 ChangeToWorld(Vector3 change, bool torqueWorldZ)
            => torqueWorldZ ? new Vector3(change.X, change.Y, 0f) * _rotation + new Vector3(0f, 0f, change.Z) : change * _rotation;

        /// <summary>
        /// The change of world angular velocity InWorldz's angular motor and banking turn make in a collision step of h seconds:
        /// the sum over the steps of Halcyon's that fall due in it (<see cref="StepsDue"/>, the same clock as the linear motor's;
        /// see the note above <see cref="HalcyonLinearMotorOnItsClock"/>), each worked out from the velocity the one before
        /// leaves (<see cref="HalcyonAngularMotor"/>, at 15 ms). A step's change is kept only when it is at least pi / 512 about
        /// the world's x or y axis; about the world's z the motor's change and the banking turn's
        /// (<see cref="HalcyonVehicleLaws.BankingTurnChange"/>, toward the rate banking asks for) are added and the sum is kept
        /// only when it is at least that (VehicleDynamics.cs:2936-2944 and 3029-3038: "Blend angular Z and banking forces" and
        /// "if the force drops below the threshold, do nothing to let the vehicle eventually sleep"). While the motor-height rule
        /// holds the motors off there is no motor change and no target velocity to carry forward (2746-2749); banking goes on.
        /// </summary>
        private Vector3 HalcyonAngularMotorOnItsClock(float h, Vector3 v0, Vector3 motor, Vector3 motorTs, Vector3 decayTs, bool torqueWorldZ)
        {
            double carried = _angularMotorCarry;
            int due = StepsDue(ref _angularMotorCarry, h);
            if (due == 0)
                return Vector3.Zero;

            VehicleDynamicsState d = _sp.Dynamics;
            bool motorsOff = _motorsOffByHeight;
            if (motorsOff)
                d.AngularTargetVelocity = Vector3.Zero;

            float start = d.AngularDecayIndex;
            float intervalStart = start - (float)carried;
            Vector3 local = v0;
            Vector3 total = Vector3.Zero;
            float turnRate = _rawWorldAngularVel.Z;   // the yaw rate the banking turn reads, carried from one step to the next
            for (int i = 0; i < due; i++)
            {
                float read = intervalStart + i * (float)HalcyonStep;
                Vector3 change = Vector3.Zero;
                Vector3 world = Vector3.Zero;
                if (!motorsOff)
                {
                    d.AngularDecayIndex = read;
                    change = HalcyonAngularMotor((float)HalcyonStep, local, motor, motorTs, decayTs);
                    // Put the clock back where this collision step found it, unless the stall check put it far past any decay.
                    if (d.AngularDecayIndex == read)
                        d.AngularDecayIndex = start;
                    else
                        start = d.AngularDecayIndex;

                    world = ChangeToWorld(change, torqueWorldZ);
                    if (Math.Abs(world.X) < VehicleLimits.ThresholdAngularMotorDeltaV && Math.Abs(world.Y) < VehicleLimits.ThresholdAngularMotorDeltaV)
                    {
                        world.X = 0f;
                        world.Y = 0f;
                        change = new Vector3(0f, 0f, change.Z);
                    }
                }

                // The banking turn's change, from the yaw rate this step starts at; the motor's clock has moved on by the step.
                float bank = HalcyonBankingTurn(turnRate, Math.Max(read + (float)HalcyonStep, start));
                if (Math.Abs(world.Z + bank) < VehicleLimits.ThresholdAngularMotorDeltaV)
                {
                    world.Z = 0f;
                    change = new Vector3(change.X, change.Y, 0f);
                }
                else
                {
                    world.Z += bank;
                }
                total += world;
                local += change;
                turnRate += world.Z;
            }
            return total;
        }

        // Halcyon's banking turn motor for one of its steps (VehicleDynamics.cs:2949-3040): the change of yaw rate toward the
        // rate banking asks for, from the yaw rate the step starts at. clock is the angular motor's decay clock at the end of the
        // step: with the motor engaged (a clock under the threshold) the turn is held to the legacy angular speed.
        private float HalcyonBankingTurn(float lastRate, float clock)
        {
            VehicleDynamicsState d = _sp.Dynamics;
            float timescale = _sp.GetFloat(VehFloatParam.BankingTimescale, 1000f);
            if (d.BankingDirection == 0f || timescale >= S.MaxTimescale)
            {
                d.BankingTargetVelocity = 0f;
                return 0f;
            }
            float target = d.BankingTargetVelocity;
            float change = HalcyonVehicleLaws.BankingTurnChange(lastRate, d.BankingDirection, ref target,
                Math.Max(timescale / (float)HalcyonStep, 1f), S.MaxAngularSpeed,
                clock < VehicleLimits.ThresholdAngularMotorEngaged, IsLinearMotorStalled());
            d.BankingTargetVelocity = target;
            return change;
        }

        /// <summary>
        /// The change of angular velocity InWorldz's angular motor makes in one step of h seconds, in the motor's frame, from
        /// the velocity <paramref name="lastvel"/> (VehicleDynamics.cs:2733-2960, read, not run). On each axis the motor
        /// ramps the velocity toward its direction M by a per-step growth, "a stepwise iteration of the continuous
        /// a*b^(t*Tau) function" (<see cref="HalcyonVehicleLaws.GrowthRate"/>):
        ///
        ///   next = a (1 + ln(|M| / |a|) / (Tm / h)),   a = the velocity, or the motor's target if that is further along
        ///
        /// (from rest, from pi / 64 rad/s, a stiction start; toward zero, by -ln(1 + |a|) / (Tm / h); across zero, to zero and
        /// then the other way), kept as the motor's target for the next step, and the result blended with the velocity
        /// by the motor's grip e^(-s / Td) (<see cref="HalcyonVehicleLaws.MovementExpDecay"/>), s the time since the
        /// direction was set: result = next g + v (1 - g). The grip is dropped (the motor adds nothing) on an axis where the
        /// velocity is already past the motor's, and on an axis with no direction while the vertical attractor is acting;
        /// and on every axis while the linear motor is stalled against something, which also ends the motor.
        ///
        /// While the vertical attractor is on, the roll and pitch speeds the ramp gives are first clamped against it
        /// (<see cref="HalcyonVehicleLaws.AttractorClamp"/>, VehicleDynamics.cs:2809-2856). The ramp's timescale is held to at least the step (steps per timescale at least 1): Halcyon's step
        /// never exceeded its shortest timescale, so that a step longer than a quick motor's timescale does not overshoot
        /// the direction.
        /// </summary>
        private Vector3 HalcyonAngularMotor(float h, Vector3 lastvel, Vector3 motor, Vector3 motorTs, Vector3 decayTs)
        {
            VehicleDynamicsState d = _sp.Dynamics;
            float[] last = { lastvel.X, lastvel.Y, lastvel.Z };
            float[] dir = { motor.X, motor.Y, motor.Z };
            float[] ts = { motorTs.X, motorTs.Y, motorTs.Z };
            float[] decay = { HalcyonVehicleLaws.MovementExpDecay(d.AngularDecayIndex, decayTs.X),
                              HalcyonVehicleLaws.MovementExpDecay(d.AngularDecayIndex, decayTs.Y),
                              HalcyonVehicleLaws.MovementExpDecay(d.AngularDecayIndex, decayTs.Z) };
            float[] target = { d.AngularTargetVelocity.X, d.AngularTargetVelocity.Y, d.AngularTargetVelocity.Z };
            float[] attractor = { _attractorChange.X, _attractorChange.Y, _attractorChange.Z };

            // If the decay hits maximum entropy no further computations are required.
            if (decay[0] == 0 && decay[1] == 0 && decay[2] == 0)
            {
                d.AngularTargetVelocity = Vector3.Zero;
                return Vector3.Zero;
            }

            float[] newvel = new float[3];
            float[] ramp = new float[3];
            float max = S.MaxAngularSpeed;

            // The attractor's timescale as the clamp reads it, held to at least the step; with the attractor switched off for
            // the region there is nothing to overpower, and the clamp is out.
            float attractTimescale = S.DoVerticalAttractor
                ? Math.Max(_sp.GetFloat(VehFloatParam.VerticalAttractionTimescale, 1000f), h)
                : float.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                float dirsign = VehicleMath.PosNeg(dir[i]);

                // A target velocity is used where the velocity is lower, so that friction at low speed cannot hold the
                // motor down: "The target ensures forward progress."
                float adjvel = last[i];
                if (dirsign * (target[i] - adjvel) > 0)
                    adjvel = target[i];

                ramp[i] = HalcyonVehicleLaws.GrowthRate(adjvel, dir[i], Math.Max(ts[i] / h, 1f));
                newvel[i] = adjvel + adjvel * ramp[i];

                // At the crossover point, flip to indicate the velocity is growing.
                if (ramp[i] == VehicleLimits.ThresholdInverseCrossover)
                    ramp[i] = -ramp[i];

                // Avoid zero velocity stiction when the new velocity is increasing.
                if (ramp[i] > 0 && dir[i] != 0 && Math.Abs(newvel[i]) < VehicleLimits.ThresholdAngularMotorDeltaV)
                    newvel[i] = dirsign * VehicleLimits.ThresholdAngularMotorDeltaV * 8;

                // The clamp against the vertical attractor, on the roll and pitch velocities.
                if (i < 2 && attractTimescale < S.MaxAttractTimescale)
                    newvel[i] = HalcyonVehicleLaws.AttractorClamp(newvel[i], _sp.Type, attractTimescale);

                // The new target: the new velocity while ramping toward zero, else held to the direction.
                if (dir[i] * newvel[i] < 0 || ramp[i] < 0)
                    target[i] = newvel[i];
                else
                    target[i] = Utils.Clamp(newvel[i], -Math.Abs(dir[i]), Math.Abs(dir[i]));

                newvel[i] = Utils.Clamp(newvel[i], -max, max);

                // Release the motor when it has decayed and has no direction.
                if (decay[i] == 0 && dir[i] == 0)
                    newvel[i] = last[i];

                // If the velocity on the axis is already past the motor's (the motor adds no power), or the attractor is acting
                // on an axis the motor has no direction on, use the velocity.
                if ((attractor[i] != 0 && dir[i] == 0) || (ramp[i] >= 0 && dirsign * (last[i] - newvel[i]) > 0))
                    decay[i] = 0;
            }

            d.AngularTargetVelocity = new Vector3(target[0], target[1], target[2]);

            // Accelerate the angular decay when the linear motor is stalled.
            if ((decay[0] != 0 || decay[1] != 0 || decay[2] != 0) && IsLinearMotorStalled())
            {
                decay[0] = decay[1] = decay[2] = 0;
                d.AngularDecayIndex = VehicleLimits.MaxDecayTimescale * 100.0f;
                d.AngularTargetVelocity = Vector3.Zero;
            }

            // Apply the decay to each axis.
            return new Vector3(
                newvel[0] * decay[0] + last[0] * (1.0f - decay[0]) - last[0],
                newvel[1] * decay[1] + last[1] * (1.0f - decay[1]) - last[1],
                newvel[2] * decay[2] + last[2] * (1.0f - decay[2]) - last[2]);
        }

        /// <summary>
        /// The angular motor's direction with VEHICLE_FLAG_MOUSELOOK_STEER or VEHICLE_FLAG_MOUSELOOK_BANK set, a
        /// driver's camera known and the driver in mouselook (llSetVehicleFlags):
        ///
        /// - STEER: "make the angular motor try to make the vehicle turn such that its local x-axis points in the same
        ///   direction as the client-side camera". The yaw (z) and pitch (y) axes aim at the camera.
        /// - BANK: "Same as above, but relies on banking. It remaps left-right motions of the client camera (also known
        ///   as "yaw") to rotations about the vehicle's local x-axis". The pitch axis aims as with STEER; the camera's yaw
        ///   aims the roll (x) axis instead, leaning into the turn (a camera to the left rolls the vehicle left), and
        ///   banking turns the vehicle. With both flags set, BANK's remap is used.
        ///
        /// Each aimed axis asks for the turning rate that would close its angle a to the camera in one motor timescale,
        /// held to that axis of the script's motor direction, as the flags "only turn the vehicle up to the current
        /// VEHICLE_ANGULAR_MOTOR_DIRECTION; If VEHICLE_ANGULAR_MOTOR_DIRECTION is set to ZERO_VECTOR, then the vehicle
        /// will not turn":
        ///
        ///   M = clamp(a / Tm, -|M_script|, |M_script|)
        ///
        /// The motor equation then acts on it as on any motor direction, with its timescale and decay. An axis not aimed
        /// keeps the script's direction. Without a driver's camera, or with the driver not in mouselook, the flags change
        /// nothing: the vehicle behaves as if they were clear, as in InWorldz, where steering and banking from the camera
        /// need the camera's mouselook state (VehicleDynamics.cs:813-816, 479-480).
        /// </summary>
        private Vector3 MouselookMotor(Vector3 motor, Vector3 motorTs)
        {
            const ExtendedVehicleFlags mouselook = ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank;
            // InWorldz sets the motor from the camera in SimulateMouselookInput instead.
            if (InWorldz)
                return motor;
            if ((_sp.Flags & mouselook) == 0 || DriverCamera?.Invoke() is not DriverView { InMouselook: true, At: Vector3 camera }
                || !float.IsFinite(camera.X) || !float.IsFinite(camera.Y) || !float.IsFinite(camera.Z)
                || camera.LengthSquared() < MinCameraAxis)
                return motor;

            // Where the camera looks in the vehicle's frame: its yaw from the x axis (about +z, positive to the left) and
            // its pitch (about +y, positive nose down).
            Vector3 d = Vector3.Normalize(in camera) * Quaternion.Inverse(_rotation);
            double yaw = Math.Atan2(d.Y, d.X);
            double pitch = -Math.Atan2(d.Z, Math.Sqrt(d.X * d.X + d.Y * d.Y));
            motor.Y = Aim(pitch, motor.Y, motorTs.Y);
            if ((_sp.Flags & ExtendedVehicleFlags.MouselookBank) != 0)
                motor.X = Aim(-yaw, motor.X, motorTs.X);
            else
                motor.Z = Aim(yaw, motor.Z, motorTs.Z);
            return motor;
        }

        /// <summary>
        /// InWorldz's camera steering (VehicleDynamics.cs:792-861 and 813-856, read, not run), which sets the angular motor
        /// from the driver's camera on every step in place of <see cref="MouselookMotor"/>'s aim. For each axis the sideways
        /// part of the camera's direction in the vehicle's frame (the yaw's sine, and for the pitch the vertical part) is
        /// taken as a share of the range the vehicle gives it (times pi / 2, over MOUSELOOK_AZIMUTH for yaw, over
        /// MOUSELOOK_ALTITUDE for pitch, pi / 4 by default, held to -1..1; <see cref="MouselookShare"/>), which is 0 within
        /// pi / 32 of the heading and otherwise shaped "into a soft exponential: (e^x-1) / (e-1)". STEERING (the mouselook
        /// steer flag with the driver in mouselook, or MOUSEPOINT_STEER in any view) sets the angular motor to
        /// (0, pitch share * the script's y, yaw share * the script's z) when either share is not 0; BANKING (the bank flag
        /// in mouselook, or MOUSEPOINT_BANK in any view) to (-yaw share * the script's x, 0, 0) when the yaw share is not 0.
        /// Both set, banking is the later and stands. Each is an ordinary set of the motor, so its decay restarts and the
        /// angular rate limiter applies; with the camera inside the zone nothing is set and the motor goes on as it was,
        /// fading by its decay. The script's direction is the one stored (<see cref="ScriptMotorIgnored"/> keeps a script's
        /// own set from moving the motor while this acts). LIMIT_ROLL_ONLY leaves the pitch share at 0.
        /// InWorldz reads the camera once in eight steps (frame number mod 8); here it is read on every step. The camera
        /// reaches the simulator some ten to twenty times a second and the motor's timescale is about a second, so a read
        /// on every step only makes the turn answer the camera sooner by under a fifth of a second; the pacing is not taken.
        /// The pitch is taken from where the camera looks: the vertical part of the camera's forward axis in the vehicle's
        /// frame, -1 for a camera looking up the vehicle's z axis to +1 for one looking down it, as InWorldz's own comment
        /// says the value means. InWorldz's code reads the z of the camera's sideways axis there instead (VehicleDynamics.cs:808,
        /// 820, <c>ycam = localUp.Z</c>), which a camera that is not rolled keeps level, so the pitch of its steering never
        /// acted; that line is taken as a slip, and the rest of its steering is kept as it is.
        /// </summary>
        private void SimulateMouselookInput()
        {
            const ExtendedVehicleFlags any = ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank
                | ExtendedVehicleFlags.MousePointSteer | ExtendedVehicleFlags.MousePointBank;
            ExtendedVehicleFlags flags = _sp.Flags;
            if ((flags & any) == 0 || DriverCamera?.Invoke() is not DriverView { At: Vector3 camera } view
                || !float.IsFinite(camera.X) || !float.IsFinite(camera.Y) || !float.IsFinite(camera.Z)
                || camera.LengthSquared() < MinCameraAxis)
                return;

            bool steering = ((flags & ExtendedVehicleFlags.MouselookSteer) != 0 && view.InMouselook) || (flags & ExtendedVehicleFlags.MousePointSteer) != 0;
            bool banking = ((flags & ExtendedVehicleFlags.MouselookBank) != 0 && view.InMouselook) || (flags & ExtendedVehicleFlags.MousePointBank) != 0;
            if (!steering && !banking)
                return;

            Quaternion toVehicle = Quaternion.Inverse(_rotation);
            Vector3 d = Vector3.Normalize(in camera) * toVehicle;
            float xcam = MouselookShare(d.Y, VehFloatParam.MouselookAzimuth);

            // The pitch share is the vertical part of the camera's forward axis in the vehicle's frame, positive for a camera
            // looking down (a camera looking up gives a negative share, which pitches the nose up for a positive y direction).
            float ycam = (flags & ExtendedVehicleFlags.LimitRollOnly) != 0 ? 0f
                : MouselookShare(-d.Z, VehFloatParam.MouselookAltitude);
            Vector3 direction = _sp.GetVec(VehVectorParam.AngularMotorDirection);
            if (steering && (xcam != 0f || ycam != 0f))
                MoveAngular(new Vector3(0f, ycam * direction.Y, xcam * direction.Z));
            if (banking && xcam != 0f)
                MoveAngular(new Vector3(-xcam * direction.X, 0f, 0f));
        }

        /// <summary>
        /// A camera's offset on one axis as InWorldz shapes it: the component of the camera's direction (in the vehicle's
        /// frame) times pi / 2 over the range, held to -1..1; 0 while that is no more than pi / 32 (0.098, "Angle below which
        /// mouselook steer/bank actions do not occur", <see cref="VehicleLimits.ThresholdMouselookAngle"/>), which with the
        /// default range of pi / 4 is a camera within 2.8 degrees of the heading; outside it the sign times
        /// (e^|x| - 1) / (e - 1), a curve that runs from 0 to 1 and is 6 percent of the way at the edge of the zone, 39
        /// percent at 15 degrees and whole from 30 degrees.
        /// </summary>
        private float MouselookShare(float component, VehFloatParam range)
        {
            float most = _sp.GetFloat(range, (float)(Math.PI / 4.0));
            if (!(most > 0f))
                return 0f;
            float share = Utils.Clamp(component * (float)(Math.PI / 2.0) / most, -1f, 1f);
            float size = Math.Abs(share);
            if (size <= VehicleLimits.ThresholdMouselookAngle)
                return 0f;
            return Math.Sign(share) * (float)((Math.Exp(size) - 1.0) / (Math.E - 1.0));
        }

        // InWorldz: "Skip angular motor action if in camera mouselook mode and mouselook steer or bank are enabled"
        // (VehicleDynamics.cs:478-482): while the driver is in mouselook with either flag set, a script's set of the angular
        // motor direction is kept as the direction the camera's share is taken of, and does not move the motor.
        private bool ScriptMotorIgnored()
            => InWorldz && (_props.Flags & (ExtendedVehicleFlags.MouselookSteer | ExtendedVehicleFlags.MouselookBank)) != 0
            && DriverCamera?.Invoke() is DriverView { InMouselook: true };

        private static float Aim(double angle, float most, float timescale)
        {
            double limit = Math.Abs(most);
            return (float)Math.Clamp(angle / Math.Max(timescale, VehicleMotorSolver.MinTimescale), -limit, limit);
        }

        // A camera axis shorter than this (squared) gives no direction.
        private const float MinCameraAxis = 1e-6f;

        // Friction timescales that turn friction off on every axis.
        private static readonly Vector3 FrictionOff = new Vector3((float)VehicleMotorSolver.FrictionOffTimescale);

        private static Vector3 FrictionStep(Vector3 v0, Vector3 frictionTs, float h, Vector3 accel)
            => new Vector3(
                (float)VehicleMotorSolver.FrictionStep(v0.X, frictionTs.X, h, accel.X),
                (float)VehicleMotorSolver.FrictionStep(v0.Y, frictionTs.Y, h, accel.Y),
                (float)VehicleMotorSolver.FrictionStep(v0.Z, frictionTs.Z, h, accel.Z));

        #endregion

        #region Quaternion Math Utilities (replaces Halcyon PhysUtil)

        /// <summary>
        /// Convert quaternion to Euler angles (radians). Replaces Halcyon PhysUtil.Rot2Euler().
        /// </summary>
        private static Vector3 QuatToEuler(Quaternion q)
        {
            // Standard quaternion → Euler (X=roll, Y=pitch, Z=yaw)
            float sinr_cosp = 2.0f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
            float roll = (float)Math.Atan2(sinr_cosp, cosr_cosp);

            float sinp = 2.0f * (q.W * q.Y - q.Z * q.X);
            float pitch;
            if (Math.Abs(sinp) >= 1.0f)
                pitch = (float)Math.PI / 2.0f * Math.Sign(sinp);
            else
                pitch = (float)Math.Asin(sinp);

            float siny_cosp = 2.0f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = (float)Math.Atan2(siny_cosp, cosy_cosp);

            return new Vector3(roll, pitch, yaw);
        }

        /// <summary>
        /// Get the rotation angle of a quaternion. Replaces Halcyon PhysUtil.Rot2Angle().
        /// </summary>
        private static float QuatToAngle(Quaternion q)
        {
            // 2 * acos(|w|) gives the rotation angle
            float w = Math.Abs(q.W);
            if (w > 1.0f) w = 1.0f;
            return 2.0f * (float)Math.Acos(w);
        }

        /// <summary>
        /// Compute the rotation between two vectors. Replaces Halcyon PhysUtil.RotBetween().
        /// </summary>
        private static Quaternion RotBetween(Vector3 a, Vector3 b)
        {
            a = Vector3.Normalize(in a);
            b = Vector3.Normalize(in b);
            float dot = Vector3.Dot(a, b);

            if (dot > 0.999999f)
                return Quaternion.Identity;

            if (dot < -0.999999f)
            {
                // 180 degree rotation — find an orthogonal axis
                Vector3 axis = Vector3.Cross(Vector3.UnitX, a);
                if (Vector3.Mag(axis) < 0.0001f)
                    axis = Vector3.Cross(Vector3.UnitY, a);
                axis = Vector3.Normalize(in axis);
                return new Quaternion(axis.X, axis.Y, axis.Z, 0f);
            }

            Vector3 cross = Vector3.Cross(a, b);
            float w = (float)Math.Sqrt(a.LengthSquared() * b.LengthSquared()) + dot;
            Quaternion result = new Quaternion(cross.X, cross.Y, cross.Z, w);
            return Quaternion.Normalize(in result);
        }

        /// <summary>
        /// Compute the angle between two quaternions. Replaces Halcyon PhysUtil.AngleBetween().
        /// </summary>
        private static float AngleBetween(Quaternion a, Quaternion b)
        {
            Quaternion diff = Quaternion.Inverse(b) * a;
            float w = Math.Abs(diff.W);
            if (w > 1.0f) w = 1.0f;
            return 2.0f * (float)Math.Acos(w);
        }

        #endregion

        #region Vehicle Type Defaults (documented, and legacy ported from Halcyon SetVehicleDefaults)

        /// <summary>
        /// Set all vehicle parameters to the defaults for the given type: the legacy (InWorldz Halcyon) values, and with
        /// the documented preset set (<see cref="VehicleSettings.Presets"/>) Second Life's documented values over them.
        /// The module's own extension params (wind, mouselook, banking azimuth, motor disabling) are not Second Life
        /// params and keep their legacy values in both sets; none of them is simulated.
        /// </summary>
        private void SetVehicleDefaults(VehicleType newType)
        {
            SetLegacyDefaults(newType);
            if (Settings.Presets == VehiclePresetSet.Documented)
                SetDocumentedDefaults(newType);
            // InWorldz's balloon reacts to wind (Halcyon VehicleDynamics.cs:2001; the InWorldz Advanced Vehicle Tutorial's
            // table of defaults gives it REACT_TO_WIND), which is what its wind efficiencies act through. Second Life's
            // balloon has no wind flag.
            if (InWorldz && newType == VehicleType.Balloon)
                _props.Flags |= ExtendedVehicleFlags.ReactToWind;
        }

        /// <summary>
        /// Second Life's documented defaults for each vehicle type, as the wiki page of each type lists them
        /// (https://wiki.secondlife.com/wiki/VEHICLE_TYPE_SLED, _CAR, _BOAT, _AIRPLANE and _BALLOON). A scalar on the
        /// page sets all three axes of a vector param. The flags are the page's llSetVehicleFlags list; every other
        /// flag is cleared. The pages set no motor offset, so it is zero.
        /// </summary>
        private void SetDocumentedDefaults(VehicleType type)
        {
            const ExtendedVehicleFlags noDeflectionUp = ExtendedVehicleFlags.NoDeflectionUp;
            const ExtendedVehicleFlags rollOnly = ExtendedVehicleFlags.LimitRollOnly;
            const ExtendedVehicleFlags upOnly = ExtendedVehicleFlags.HoverUpOnly;
            const ExtendedVehicleFlags motorUp = ExtendedVehicleFlags.LimitMotorUp;
            const ExtendedVehicleFlags waterOnly = ExtendedVehicleFlags.HoverWaterOnly;
            switch (type)
            {
                case VehicleType.Sled:
                    // The page gives HOVER_EFFICIENCY 10; llSetVehicleFloatParam holds an efficiency to 0..1, so 1.
                    // The page's HOVER_TIMESCALE 10 would turn hover on (any timescale under 300 s does), against its
                    // own comment "no hover"; the sled has hover off (1000 s).
                    SetDocumented(linearFriction: new Vector3(30f, 1f, 1000f), angularFriction: new Vector3(1000f),
                        linearMotorTimescale: 1000f, linearMotorDecay: 120f, angularMotorTimescale: 1000f, angularMotorDecay: 120f,
                        hoverHeight: 0f, hoverEfficiency: 1f, hoverTimescale: 1000f, buoyancy: 0f,
                        linearDeflectionEfficiency: 1f, linearDeflectionTimescale: 1f,
                        angularDeflectionEfficiency: 0f, angularDeflectionTimescale: 10f,
                        attractionEfficiency: 1f, attractionTimescale: 1000f,
                        bankingEfficiency: 0f, bankingMix: 1f, bankingTimescale: 10f,
                        flags: noDeflectionUp | rollOnly | motorUp);
                    break;
                case VehicleType.Car:
                    SetDocumented(linearFriction: new Vector3(100f, 2f, 1000f), angularFriction: new Vector3(1000f),
                        linearMotorTimescale: 1f, linearMotorDecay: 60f, angularMotorTimescale: 1f, angularMotorDecay: 0.8f,
                        hoverHeight: 0f, hoverEfficiency: 0f, hoverTimescale: 1000f, buoyancy: 0f,
                        linearDeflectionEfficiency: 1f, linearDeflectionTimescale: 2f,
                        angularDeflectionEfficiency: 0f, angularDeflectionTimescale: 10f,
                        attractionEfficiency: 1f, attractionTimescale: 10f,
                        bankingEfficiency: -0.2f, bankingMix: 1f, bankingTimescale: 1f,
                        flags: noDeflectionUp | rollOnly | upOnly | motorUp);
                    break;
                case VehicleType.Boat:
                    SetDocumented(linearFriction: new Vector3(10f, 3f, 2f), angularFriction: new Vector3(10f),
                        linearMotorTimescale: 5f, linearMotorDecay: 60f, angularMotorTimescale: 4f, angularMotorDecay: 4f,
                        hoverHeight: 0f, hoverEfficiency: 0.5f, hoverTimescale: 2f, buoyancy: 1f,
                        linearDeflectionEfficiency: 0.5f, linearDeflectionTimescale: 3f,
                        angularDeflectionEfficiency: 0.5f, angularDeflectionTimescale: 5f,
                        attractionEfficiency: 0.5f, attractionTimescale: 5f,
                        bankingEfficiency: -0.3f, bankingMix: 0.8f, bankingTimescale: 1f,
                        flags: noDeflectionUp | waterOnly | upOnly | motorUp);
                    break;
                case VehicleType.Airplane:
                    SetDocumented(linearFriction: new Vector3(200f, 10f, 5f), angularFriction: new Vector3(20f),
                        linearMotorTimescale: 2f, linearMotorDecay: 60f, angularMotorTimescale: 4f, angularMotorDecay: 8f,
                        hoverHeight: 0f, hoverEfficiency: 0.5f, hoverTimescale: 1000f, buoyancy: 0f,
                        linearDeflectionEfficiency: 0.5f, linearDeflectionTimescale: 0.5f,
                        angularDeflectionEfficiency: 1f, angularDeflectionTimescale: 2f,
                        attractionEfficiency: 0.9f, attractionTimescale: 2f,
                        bankingEfficiency: 1f, bankingMix: 0.7f, bankingTimescale: 2f,
                        flags: rollOnly);
                    break;
                case VehicleType.Balloon:
                    SetDocumented(linearFriction: new Vector3(5f), angularFriction: new Vector3(10f),
                        linearMotorTimescale: 5f, linearMotorDecay: 60f, angularMotorTimescale: 6f, angularMotorDecay: 10f,
                        hoverHeight: 5f, hoverEfficiency: 0.8f, hoverTimescale: 10f, buoyancy: 1f,
                        linearDeflectionEfficiency: 0f, linearDeflectionTimescale: 5f,
                        angularDeflectionEfficiency: 0f, angularDeflectionTimescale: 5f,
                        attractionEfficiency: 1f, attractionTimescale: 1000f,
                        bankingEfficiency: 0f, bankingMix: 0.7f, bankingTimescale: 5f,
                        flags: ExtendedVehicleFlags.None);
                    break;
            }
        }

        private void SetDocumented(Vector3 linearFriction, Vector3 angularFriction,
            float linearMotorTimescale, float linearMotorDecay, float angularMotorTimescale, float angularMotorDecay,
            float hoverHeight, float hoverEfficiency, float hoverTimescale, float buoyancy,
            float linearDeflectionEfficiency, float linearDeflectionTimescale,
            float angularDeflectionEfficiency, float angularDeflectionTimescale,
            float attractionEfficiency, float attractionTimescale,
            float bankingEfficiency, float bankingMix, float bankingTimescale,
            ExtendedVehicleFlags flags)
        {
            _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]    = linearFriction;
            _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]   = angularFriction;
            _props.ParamsVec[VehVectorParam.LinearMotorDirection]       = Vector3.Zero;
            _props.ParamsVec[VehVectorParam.AngularMotorDirection]      = Vector3.Zero;
            _props.ParamsVec[VehVectorParam.LinearMotorOffset]          = Vector3.Zero;
            _props.ParamsVec[VehVectorParam.LinearMotorTimescale]       = new Vector3(linearMotorTimescale);
            _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]  = new Vector3(linearMotorDecay);
            _props.ParamsVec[VehVectorParam.AngularMotorTimescale]      = new Vector3(angularMotorTimescale);
            _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale] = new Vector3(angularMotorDecay);

            _props.ParamsFloat[VehFloatParam.HoverHeight]                  = hoverHeight;
            _props.ParamsFloat[VehFloatParam.HoverEfficiency]              = hoverEfficiency;
            _props.ParamsFloat[VehFloatParam.HoverTimescale]               = hoverTimescale;
            _props.ParamsFloat[VehFloatParam.Buoyancy]                     = buoyancy;
            _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]   = linearDeflectionEfficiency;
            _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]    = linearDeflectionTimescale;
            _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]  = angularDeflectionEfficiency;
            _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]   = angularDeflectionTimescale;
            _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency] = attractionEfficiency;
            _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]  = attractionTimescale;
            _props.ParamsFloat[VehFloatParam.BankingEfficiency]            = bankingEfficiency;
            _props.ParamsFloat[VehFloatParam.BankingMix]                   = bankingMix;
            _props.ParamsFloat[VehFloatParam.BankingTimescale]             = bankingTimescale;

            _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
            _props.Flags = flags;
        }

        /// <summary>
        /// The legacy defaults for the given type: the InWorldz Halcyon values.
        /// Faithfully ported from Halcyon VehicleDynamics.SetVehicleDefaults().
        /// </summary>
        private void SetLegacyDefaults(VehicleType newType)
        {
            switch (newType)
            {
                case VehicleType.None:
                    _props.ParamsVec.Clear();
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat.Clear();
                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 1000f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 1000f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 1000f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 1000f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot.Clear();
                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;

                    _props.Flags = ExtendedVehicleFlags.None;
                    break;

                case VehicleType.Sled:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(1000f, 1f, 1000f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = new Vector3(0f, 0f, -0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(1000f, 1000f, 1000f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(120f, 120f, 120f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(120f, 120f, 120f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 0.3f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 1f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 1f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.1f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 10f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 10f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly | ExtendedVehicleFlags.LimitMotorUp;
                    break;

                case VehicleType.Car:
                    // InWorldz's car, value for value and flag for flag (VehicleDynamics.cs:1767-1804). It is not the
                    // documented car: its friction, motor and decay timescales, angular deflection and attractor
                    // differ from the page's, and, like the page's car, it has no TORQUE_WORLD_Z.
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(100f, 0.1f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(100f, 100f, 0.3f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(0.5f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.2f, 0.2f, 0.05f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(10f, 2f, 2f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 2f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 2f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.6f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 2f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = -0.2f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 1f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0.75f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 2.5f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly
                                 | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp;
                    break;

                // The two InWorldz vehicle types, which Second Life does not have: the same values with either preset set
                // (there is no documented set for them), InWorldz's own, value for value and flag for flag.
                case VehicleType.Motorcycle:
                    // VehicleDynamics.cs:1806-1844.
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(100f, 0.1f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(3f, 0.2f, 10f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = new Vector3(0f, 0f, -0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(0.5f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.1f, 0.1f, 0.05f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(10f, 1f, 1f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.2f, 0.8f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 2f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.8f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 2f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 1f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0.95f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = -0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 0.1f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 1.5f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 2.5f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp
                                 | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.LimitRollOnly | ExtendedVehicleFlags.TorqueWorldZ;
                    break;

                case VehicleType.Sailboat:
                    // VehicleDynamics.cs:1886-1924. The two wind efficiency vectors and the REACT_TO_WIND and
                    // REACT_TO_CURRENTS flags act under InWorldz's behaviour (SimulateWind) and are only held under Second Life's.
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(200f, 0.5f, 3f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(10f, 1f, 0.2f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1f, 5f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(2f, 2f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(1f, 10f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = new Vector3(0.02f, 0.001f, 0f);
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = new Vector3(0.1f, 0.01f, 0f);

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0.0001f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.8f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 0.5f;
                    // Under InWorldz's behaviour hover switches gravity off (HoverTurnsGravityOff), so the preset's 0 holds the hull at the
                    // surface. Gravity stays on under Second Life's, where the spring alone would let it settle below; full
                    // buoyancy cancels it, as the documented boat has.
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = InWorldz ? 0f : 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 3f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 0.3f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0.8f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = -0.2f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 0.5f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverWaterOnly | ExtendedVehicleFlags.LimitMotorUp
                                 | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.ReactToWind | ExtendedVehicleFlags.ReactToCurrents
                                 | ExtendedVehicleFlags.TorqueWorldZ;
                    break;

                case VehicleType.Boat:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(200f, 0.5f, 3f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(10f, 1f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1f, 5f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(0.2f, 2f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(1f, 10f, 10f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 0.1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = Vector3.Zero;

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.8f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 0.2f;
                    // Buoyancy 1.0 (matches BulletSim's TYPE_BOAT, BSDynamics buoyancy=1.0). Gravity
                    // is fully cancelled (ApplyGravity = gravity*(1-buoyancy) = 0), so a boat CANNOT sink even
                    // on a frame where hover has not run yet - e.g. right after a region reload, before the
                    // controller re-activates. Hover still trims it to the water surface (HoverWaterOnly,
                    // height 0.5); buoyancy holds the baseline, hover positions - the same compose BulletSim uses.
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 3f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 0.2f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 1f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 0.2f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverWaterOnly
                                 | ExtendedVehicleFlags.LimitMotorUp | ExtendedVehicleFlags.LimitMotorDown
                                 | ExtendedVehicleFlags.TorqueWorldZ;
                    break;

                case VehicleType.Airplane:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(200f, 10f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(1f, 0.1f, 0.5f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(2f, 2f, 2f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(1f, 2f, 1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(60f, 60f, 60f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(8f, 8f, 8f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = new Vector3(0.1f, 0f, 0f);
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = new Vector3(0.05f, 0f, 0f);

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 0f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.5f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 1000f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 0.5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 1f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 2f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.9f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 2f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 1f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.7f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 1f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.TorqueWorldZ | ExtendedVehicleFlags.LimitRollOnly;
                    break;

                case VehicleType.Balloon:
                    _props.ParamsVec[VehVectorParam.LinearFrictionTimescale]     = new Vector3(1f, 1f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularFrictionTimescale]    = new Vector3(2f, 0.5f, 1f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDirection]        = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.AngularMotorDirection]       = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorOffset]           = Vector3.Zero;
                    _props.ParamsVec[VehVectorParam.LinearMotorTimescale]        = new Vector3(1f, 5f, 5f);
                    _props.ParamsVec[VehVectorParam.AngularMotorTimescale]       = new Vector3(2f, 2f, 0.3f);
                    _props.ParamsVec[VehVectorParam.LinearMotorDecayTimescale]   = new Vector3(60f, 60f, 60f);
                    _props.ParamsVec[VehVectorParam.AngularMotorDecayTimescale]  = new Vector3(0.3f, 0.3f, 1f);
                    _props.ParamsVec[VehVectorParam.LinearWindEfficiency]        = new Vector3(0.1f, 0.1f, 0.1f);
                    _props.ParamsVec[VehVectorParam.AngularWindEfficiency]       = new Vector3(0.01f, 0.01f, 0f);

                    _props.ParamsFloat[VehFloatParam.HoverHeight]                   = 5f;
                    _props.ParamsFloat[VehFloatParam.HoverEfficiency]               = 0.8f;
                    _props.ParamsFloat[VehFloatParam.HoverTimescale]                = 10f;
                    _props.ParamsFloat[VehFloatParam.Buoyancy]                      = 1f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionEfficiency]    = 0f;
                    _props.ParamsFloat[VehFloatParam.LinearDeflectionTimescale]     = 5f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionEfficiency]   = 0f;
                    _props.ParamsFloat[VehFloatParam.AngularDeflectionTimescale]    = 5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionEfficiency]  = 0.5f;
                    _props.ParamsFloat[VehFloatParam.VerticalAttractionTimescale]   = 4f;
                    _props.ParamsFloat[VehFloatParam.BankingEfficiency]             = 0.05f;
                    _props.ParamsFloat[VehFloatParam.InvertedBankingModifier]       = 1f;
                    _props.ParamsFloat[VehFloatParam.BankingMix]                    = 0.5f;
                    _props.ParamsFloat[VehFloatParam.BankingTimescale]              = 5f;
                    _props.ParamsFloat[VehFloatParam.MouselookAltitude]             = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.MouselookAzimuth]              = (float)Math.PI / 4f;
                    _props.ParamsFloat[VehFloatParam.BankingAzimuth]                = (float)Math.PI / 2f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAbove]            = 0f;
                    _props.ParamsFloat[VehFloatParam.DisableMotorsAfter]            = 0f;

                    _props.ParamsRot[VehRotationParam.ReferenceFrame] = Quaternion.Identity;
                    _props.Flags = ExtendedVehicleFlags.None;  // Halcyon had ReactToWind only
                    break;
            }
        }

        #endregion // Vehicle Type Defaults

        #region Utility

        private static float ClampF(float val, float min, float max)
        {
            return Math.Max(min, Math.Min(val, max));
        }

        #endregion // Utility

        // =================================================================
        // Public accessor for vehicle type (used by the host's VehicleType getter)
        // =================================================================
        public Vehicle Type
        {
            get
            {
                switch (_props.Type)
                {
                    case VehicleType.None:     return Vehicle.TYPE_NONE;
                    case VehicleType.Sled:     return Vehicle.TYPE_SLED;
                    case VehicleType.Car:      return Vehicle.TYPE_CAR;
                    case VehicleType.Boat:     return Vehicle.TYPE_BOAT;
                    case VehicleType.Airplane: return Vehicle.TYPE_AIRPLANE;
                    case VehicleType.Balloon:  return Vehicle.TYPE_BALLOON;
                    case VehicleType.Sailboat:    return Vehicle.TYPE_SAILBOAT;
                    case VehicleType.Motorcycle:  return Vehicle.TYPE_MOTORCYCLE;
                    default:                         return Vehicle.TYPE_NONE;
                }
            }
        }
    }
}
