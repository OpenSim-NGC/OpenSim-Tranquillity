/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// llMoveToTarget and llSetHoverHeight on a physical prim: two controllers that act before every physics step.
//
// Core hands both to the root prim's actor (SceneObjectGroup.MoveToTarget, SetHoverHeight) as PIDTarget, PIDTau and
// PIDActive, and PIDHoverHeight, PIDHoverType, PIDHoverTau and PIDHoverActive. The Second Life wiki says each one
// "critically damps" its target "in tau seconds" (llMoveToTarget, llSetHoverHeight). Both are the critically damped
// spring the Jolt vehicle hover uses at HOVER_EFFICIENCY 1 (VehicleSpring), with tau as its timescale. On the error e
// from the target:
//
//   e'' = -e / tau^2 - 2 e' / tau        so from rest   e(t) = e0 (1 + t / tau) e^(-t / tau)
//
// stepped exactly over the step the engine takes, so the motion is the same at every [Jolt] PhysicsStepRate. Gravity
// is off while one acts, as in ubODE (ODEPrim.Move applies gravity only when neither acts), so a held object stays
// where it is held and buoyancy changes nothing then. A set force and torque still act (ubODE adds them on top too):
// a steady force F holds the object F tau^2 / m from its target, where the spring balances it.
//
// Hover holds the height above the ground, the higher of ground and water, the water, or the region's zero, as
// PIDHoverType says, and follows the ground's height along the object's path, so it holds its height over a slope.
// Move to target wins when both are on, as in ubODE.
//
// llGroundRepel reaches the actor as the same hover members plus PIDHoverRepel. The spring above does not tell it apart, so
// under SecondLife it acts as llSetHoverHeight does (it also pulls an object down to its height); under InWorldz it only
// pushes up (Halcyon, PhysxPrim.pid.cs DoGroundRepel). A vehicle keeps its own hover and motion: neither controller acts on
// an active vehicle.
//
// Under InWorldz behaviour the rules follow Halcyon's (PhysxPrim.pid.cs); the code is written for Jolt and holds none of
// Halcyon's.
//
// Where the work is done. This file is the Update-rate half: it reads the script's settings, decides whether a controller
// acts and arms BasicForcesStepProvider, which works the controller out in every collision step as a change of velocity over
// the step's own length. The spring above is stepped exactly over that step, so the motion does not depend
// on the physics rate or the number of collision steps. Under [Jolt] BasicForcesBehaviour = InWorldz (the default) the laws
// are Halcyon's, in HalcyonBasicForces.

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim
    {
        /// <summary>
        /// llMoveToTarget acts on a tau of two physics frames, 2/45 s, or more (wiki.secondlife.com/wiki/LlMoveToTarget). A
        /// smaller tau (and a smaller hover tau) acts as this one.
        /// </summary>
        internal const float MinTau = 2f / 45f;

        /// <summary>llMoveToTarget: the target must be less than 65 m away, or nothing moves (wiki.secondlife.com/wiki/LlMoveToTarget).</summary>
        internal const float MaxTargetDistance = 65f;

        /// <summary>llSetHoverHeight: the limit is 4096 m above the ground (wiki.secondlife.com/wiki/LlSetHoverHeight).</summary>
        internal const float MaxHoverHeight = 4096f;

        /// <summary>
        /// A body asleep this close to where a controller holds it (m) is left asleep. Further away the controller wakes
        /// it.
        /// </summary>
        internal const float HoldTolerance = 0.005f;

        private readonly object _targetLock = new object();
        private Vector3 _pidTarget;
        private float _pidTau;
        private bool _pidActive;
        private float _hoverHeight;
        private PIDHoverType _hoverType;
        private float _hoverTau;
        private bool _hoverActive;
        private bool _hoverRepel;       // llGroundRepel: the hover only pushes up
        private bool _targetsChanged;   // a member changed since the last step: wake the body

        // Step thread only.
        private bool _heldByTarget;     // a controller acted in the last step (the body's gravity is off)
        private bool _vehicleDragged;   // a drag has been running this vehicle's body in place of its controller
        private bool _lastWasMove;
        private BodyId _noSleepBody = BodyId.Invalid;   // the body a controller has told the engine not to put to sleep
        private bool _linApproaching;   // move to target or hover is still bringing the object in
        private bool _angApproaching;   // the rotation target is (JoltPrim.AngularTarget.cs)

        public override Vector3 PIDTarget
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDTarget", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDTarget = value; return; }
                lock (_targetLock) { _pidTarget = value; _targetsChanged = true; }
            }
        }

        public override float PIDTau
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDTau", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDTau = value; return; }
                lock (_targetLock) { _pidTau = value; _targetsChanged = true; }
            }
        }

        public override bool PIDActive
        {
            get { if (_linkRoot != null) return _linkRoot.PIDActive; lock (_targetLock) return _pidActive; }
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDActive = value; return; }
                lock (_targetLock) { _pidActive = value; _targetsChanged = true; }
                if (value) _module.SetTargeted(this, true);
            }
        }

        public override float PIDHoverHeight
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDHoverHeight", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDHoverHeight = value; return; }
                lock (_targetLock)
                {
                    _hoverHeight = Math.Clamp(value, -MaxHoverHeight, MaxHoverHeight);
                    // A height of zero has the effect of llStopHover (wiki.secondlife.com/wiki/LlSetHoverHeight), as in ubODE's changePIDHoverHeight
                    if (value == 0f) _hoverActive = false;
                    _targetsChanged = true;
                }
            }
        }

        public override PIDHoverType PIDHoverType
        {
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDHoverType = value; return; }
                lock (_targetLock) { _hoverType = value; _targetsChanged = true; }
            }
        }

        public override float PIDHoverTau
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "PIDHoverTau", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.PIDHoverTau = value; return; }
                lock (_targetLock) { _hoverTau = value; _targetsChanged = true; }
            }
        }

        public override bool PIDHoverRepel
        {
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDHoverRepel = value; return; }
                lock (_targetLock) { _hoverRepel = value; _targetsChanged = true; }
            }
        }

        public override bool PIDHoverActive
        {
            get { if (_linkRoot != null) return _linkRoot.PIDHoverActive; lock (_targetLock) return _hoverActive; }
            set
            {
                if (_linkRoot != null) { _linkRoot.PIDHoverActive = value; return; }
                lock (_targetLock) { _hoverActive = value; _targetsChanged = true; }
                if (value) _module.SetTargeted(this, true);
            }
        }

        /// <summary>
        /// Called by JoltScene before every backend step (every Update) for a prim that has asked for a target or a hover
        /// height. It is the Update-rate half of the controllers: it reads what the script asked for, decides whether a
        /// controller acts, wakes or lets go of the body, and arms the step provider (BasicForcesStepProvider) with the plan
        /// that the collision steps carry out. Returns false once neither is asked for and the body has been let go, so the
        /// scene stops stepping it.
        /// </summary>
        internal bool StepTargets(float timeStep)
        {
            _basicForces ??= new BasicForcesStepProvider(_module, this, _backend);
            BasicForcesStepProvider plan = _basicForces;
            plan.Move = plan.Hover = plan.Angular = plan.Grab = false;
            plan.InWorldz = _module.BasicForcesBehaviour == JoltConfig.BasicForces.InWorldz;
            bool angular = PrepareAngularTarget(plan);
            bool linear = PrepareLinearTargets(plan);
            if (plan.Move || plan.Hover || plan.Angular)
            {
                plan.LinearDamping = _backend.GetBodyLinearDamping(_body);
                plan.AngularDamping = _module.PrimAngularDamping;
                plan.ScriptGravity = ScriptGravityFactor;
                plan.MaxLinearSpeed = _module.BodyMaxLinearSpeed;
                plan.MaxAngularSpeed = _module.BodyMaxAngularSpeed;
                if (plan.Move || plan.Hover)
                    plan.ScriptAcceleration = ScriptAcceleration();
                // The engine calls a provider only for an awake body; the Update-rate path wakes a sleeping one.
                if (!plan.Arm(_body))
                    plan.Detach();
            }
            else
                plan.Detach();
            return angular || linear;
        }

        private BasicForcesStepProvider _basicForces;

        private bool PrepareLinearTargets(BasicForcesStepProvider plan)
        {
            Vector3 target;
            float tau, height, hoverTau;
            bool move, hover, changed, repel;
            PIDHoverType type;
            lock (_targetLock)
            {
                target = _pidTarget; tau = _pidTau; move = _pidActive;
                height = _hoverHeight; hoverTau = _hoverTau; type = _hoverType; hover = _hoverActive; repel = _hoverRepel;
                changed = _targetsChanged;
                _targetsChanged = false;
            }

            // llMoveToTarget works on physical objects only, yet the request is kept: the wiki notes that a call seems to
            // persist after physics is turned off (wiki.secondlife.com/wiki/LlMoveToTarget). Nothing acts on an active vehicle
            // (the page says not to use it with vehicles; ubODE hands a vehicle to its own controller before either of these).
            bool vehicle = _vehicle != null && _vehicle.IsActive;
            // InWorldz: a drag of a vehicle is run by the drag's target, not the vehicle's controller (see GrabOverridesVehicle).
            // When the drag ends the vehicle's gravity and sleep rule, which the drag changed, are set again.
            if (vehicle && GrabOverridesVehicle)
            {
                vehicle = false;
                _vehicleDragged = true;
            }
            else if (vehicle && _vehicleDragged)
            {
                _vehicleDragged = false;
                if (_body.IsValid && _isPhysical)
                    ApplyVehicleBodyParams();
            }
            if (!_isPhysical || !_body.IsValid || vehicle || !_backend.TryGetBodyState(_body, out BodyState s))
            {
                LetGo(vehicle);
                return move || hover;
            }

            // Selected in the build tool, the object is held still (HoldSelected) and neither controller acts, as ubODE's
            // Move skips a selected prim. The request is kept; let go, the spring starts again from rest where it was held.
            // A change made meanwhile is kept for then, and a body still held here stays stepped so that, if both were
            // stopped meanwhile, it gets its gravity back once it is let go.
            if (_selected)
            {
                plan.MoveCarry = SVector3.Zero;
                plan.HoverCarry = 0f;
                if (changed)
                    lock (_targetLock) _targetsChanged = true;
                return move || hover || _heldByTarget;
            }

            Vector3 pos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            bool awake = (s.Flags & BodyStateFlags.Active) != 0;

            // The Second Life wiki: a tau of 0 or less does nothing (the call silently fails), the target must be less than
            // 65 m away, and a move target wins over a hover (wiki.secondlife.com/wiki/LlMoveToTarget). Halcyon: a tau up to
            // two of its steps does nothing, there is no distance limit, and the move target and the hover both act
            // (PhysxPrim.pid.cs DoPIDMoveTarget, DoHoverTarget).
            bool moving, hovering;
            if (plan.InWorldz)
            {
                moving = move && tau > HalcyonBasicForces.MinTau;
                // A drag overrides the hover (DoHoverTarget: "Grab overrides hover/repel").
                hovering = hover && hoverTau >= HalcyonBasicForces.MinTau && height != 0f && !_grabbed;
                plan.Grab = _grabbed && moving;
            }
            else
            {
                moving = move && tau > 0f && (target - pos).Length() < MaxTargetDistance;
                hovering = !moving && hover && hoverTau > 0f && height != 0f;
            }
            if (!moving && !hovering)
            {
                LetGo(false);
                return move || hover;
            }

            if (!_heldByTarget || moving != _lastWasMove)
            {
                plan.ResetLinear();
                plan.LinearApproaching = true;
            }
            _heldByTarget = true;
            _lastWasMove = moving;

            // How far the controller that is acting is from where it holds the body: a body asleep that close is left asleep.
            float away;
            if (moving)
                away = (pos - target).Length();
            else if (plan.InWorldz)
                away = MathF.Abs(HalcyonBasicForces.HeightAbove(type, pos.Z, _module.TerrainHeightAt(pos.X, pos.Y), _module.WaterLevel) - height);
            else
                away = MathF.Abs(pos.Z - HoverTargetAt(pos.X, pos.Y, height, type));
            if (!awake)
            {
                // Halcyon's controllers stop pushing inside 0.05 m of the target (its butter zone, its at-target radius).
                if (!changed && away < (plan.InWorldz ? HalcyonBasicForces.AtTargetTolerance : HoldTolerance))
                    return true;   // asleep where it is held
                _backend.ActivateBody(_body);
                plan.ResetLinear();
                plan.LinearApproaching = true;
            }

            plan.Move = moving;
            plan.Hover = hovering;
            plan.MoveTarget = ToS(target);
            plan.MoveTau = plan.InWorldz ? tau : MathF.Max(tau, MinTau);
            plan.HoverHeight = height;
            plan.HoverTau = plan.InWorldz ? hoverTau : MathF.Max(hoverTau, MinTau);
            plan.HoverType = type;
            plan.HoverRepel = repel && plan.InWorldz;
            KeepAwakeWhile(plan.LinearApproaching);
            return true;
        }

        // A speed (m/s) under which an object at its target has settled there.
        internal const float SettledSpeed = 0.01f;

        // While a controller is still bringing the object in, the engine must not put it to sleep: that would stop the
        // spring part way. Once it has settled where it is held it may sleep, and the controller wakes it if it is moved.
        private void KeepAwakeWhile(bool approaching)
        {
            _linApproaching = approaching;
            ApplySleepRule();
        }

        // The body may sleep once no controller is still bringing it in.
        private void ApplySleepRule()
        {
            if (!_body.IsValid)
                return;
            bool forbid = _linApproaching || _angApproaching;
            bool forbidden = _noSleepBody.Equals(_body);
            if (forbid == forbidden)
                return;
            _backend.SetBodyAllowSleeping(_body, !forbid);
            _noSleepBody = forbid ? _body : BodyId.Invalid;
        }

        // The acceleration of the script's set force (llSetForce), which the engine applies through the step.
        private SVector3 ScriptAcceleration()
        {
            Vector3 force;
            lock (_scriptForceLock) force = _force;
            if (force == Vector3.Zero)
                return SVector3.Zero;
            float mass = _backend.GetBodyMass(_body);
            return mass > 0f ? ToS(force) / mass : SVector3.Zero;
        }

        /// <summary>
        /// The height hover holds the object's centre at, over (x, y). The SL wiki, llSetHoverHeight
        /// (wiki.secondlife.com/wiki/LlSetHoverHeight): water TRUE means hover above the water too, the higher of the two
        /// (GroundAndWater, as ubODE); FALSE ignores the water (Ground). Water is the water level alone and Absolute the
        /// region's zero. Unless volume detect is on, a negative height with water FALSE does not take the object below the
        /// ground: no target is under the ground unless the prim is volume detect.
        /// </summary>
        internal float HoverTargetAt(float x, float y, float height, PIDHoverType type)
        {
            float ground = _module.TerrainHeightAt(x, y);
            float water = _module.WaterLevel;
            float baseZ = type switch
            {
                PIDHoverType.GroundAndWater => MathF.Max(ground, water),
                PIDHoverType.Water => water,
                PIDHoverType.Absolute => 0f,
                _ => ground,
            };
            float target = baseZ + height;
            if (!_isVolumeDetect && target < ground)
                target = ground;
            return target;
        }

        // Neither controller acts any more: gravity back (unless a vehicle now decides it), the spring's own velocity
        // handed back to the body, and the body woken so it falls from where it was held.
        private void LetGo(bool vehicle)
        {
            if (!_heldByTarget)
                return;
            _heldByTarget = false;
            BasicForcesStepProvider plan = _basicForces;
            // A body that is no longer physical was recreated static, and a physical one is recreated with the script's
            // gravity; only a body still held here needs it back.
            // A vehicle sets its own sleeping rule (JoltPrim.ApplyVehicleBodyParams).
            _linApproaching = false;
            if (vehicle)
                _noSleepBody = BodyId.Invalid;
            else
                ApplySleepRule();
            if (_body.IsValid && _isPhysical && !vehicle)
            {
                _backend.SetBodyGravityFactor(_body, ScriptGravityFactor);
                SVector3 carry = plan == null ? SVector3.Zero : _lastWasMove ? plan.MoveCarry : new SVector3(0f, 0f, plan.HoverCarry);
                if (carry != SVector3.Zero && _backend.TryGetBodyState(_body, out BodyState s))
                    _backend.SetBodyLinearVelocity(_body, s.LinearVelocity + carry);
                _backend.ActivateBody(_body);
            }
            plan?.ResetLinear();
        }
    }
}
