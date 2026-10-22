/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// llRotLookAt and llLookAt on a physical prim: a rotation target that acts before every physics step.
//
// The engine is handed the target (APIDTarget), how hard to go for it (APIDStrength), how long to take to settle
// (APIDDamping) and whether it is on (APIDActive). The Second Life wiki says of llRotLookAt
// (wiki.secondlife.com/wiki/LlRotLookAt) that the object turns smoothly to the target, strength working against damping,
// damping being the seconds to critically damp in and capped at 1.0 (more is reduced to 1.0), and strength below about
// 0.0445 having no effect; and of llLookAt (wiki.secondlife.com/wiki/LlLookAt) that strength is an inverse value (a lower
// one turns the object faster) and damping smooths its movement, preventing jitter and sharp movements. Neither page
// gives a formula.
//
// What is built is a critically damped spring on the rotation, the rotation counterpart of move to target (see
// JoltPrim.Targets.cs). With r the rotation vector (axis x angle, region axes, the short way round) that takes the target
// to the object's rotation, and w the object's angular velocity:
//
//   r'' = -r / T^2 - 2 r' / T        T = max(strength, min(damping, 1)) seconds
//
// from rest r(t) = r0 (1 + t / T) e^(-t / T): it never passes the target. T is the choice the wiki does not settle: both
// numbers are times, so the slower one sets the response. A strength under 0.0445 does nothing (the request is kept), as
// does a strength that is not a number.
//
// The spring is a trajectory, not the body's own state: the controller keeps a virtual rotation and angular velocity that
// follow the spring exactly, and each step sets the body's angular velocity to the one that takes it, over the step the
// engine takes (through its angular damping), from the rotation it really has to the spring's rotation at the end of the
// step. So the motion is the same at every [Jolt] PhysicsStepRate, for any mass or size (a velocity does not depend on
// inertia), and what friction or a bump takes from the body in one step is asked for again in the next: an object resting
// on the ground, which friction holds against a torque, still turns. Contact friction takes up to a fixed share of the
// velocity it is given in each step, whatever it is given, so a body that lags is also asked for what the engine took from
// the last step's command (the estimate halves each step that takes nothing). The virtual state carries on across a new target, so a
// target that moves many times a second (a drag) is followed smoothly. Where the body has strayed further than ResyncAngle
// from it (something else turned it, or the body was recreated) the spring starts again from where the body is.
//
// Gravity and buoyancy are not touched (a move to target turns gravity off while it acts; this turns nothing off). A set
// force or a move to target act as they do without it. A set torque is held against by the same tracking. An angular
// velocity a script sets (llSetAngularVelocity, llTargetOmega on a physical prim) is taken as the speed the spring starts
// from and is brought to the target, not fought. STATUS_ROTATE locks: the spring acts on the axes left free. A vehicle
// keeps its own controller: nothing acts on an active vehicle.
//
// Switching it off sets the angular velocity to zero before the next step, as core does when it stops a look-at
// (SceneObjectPart.StopLookAt): it was turning at the spring's speed and nothing else would stop it, since the body's
// own damping takes about a minute. The rotation it had reached is kept. An angular velocity written since the last step
// is kept instead (core gives a script's llTargetOmega back when a rotate-drag ends, just after it switches the target off).
//
// A body that has settled at its target may sleep; a new target, or a body found asleep away from its target, wakes it.
//
// This file is the Update-rate half. The spring is worked out in every collision step by BasicForcesStepProvider (which also
// holds the virtual rotation and the loss estimate), over the step's own length, so the motion is the same at every
// physics rate and number of collision steps. Under [Jolt] BasicForcesBehaviour = InWorldz (the default) the rotation target
// is Halcyon's (HalcyonBasicForces.RotLook, PhysxPrim.pid.cs DoRotLookTarget), the same spring and drag in a form that is
// stable at any step length.

using System;
using System.Threading;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed partial class JoltPrim
    {
        /// <summary>llRotLookAt: a strength below about 0.0445 has no effect (wiki.secondlife.com/wiki/LlRotLookAt).</summary>
        internal const float MinLookAtStrength = 0.0445f;

        /// <summary>llRotLookAt: damping is capped at 1.0, a larger value being reduced to it (wiki.secondlife.com/wiki/LlRotLookAt).</summary>
        internal const float MaxLookAtDamping = 1f;

        /// <summary>The body this far (rad) from the spring's rotation is taken to have been moved by something else: the spring starts again from it.</summary>
        internal const float ResyncAngle = 0.35f;

        /// <summary>A body asleep this close to its target (rad) is left asleep.</summary>
        internal const float HoldAngle = 0.0035f;

        // A turn to make (rad) under which no allowance is made for what the engine takes from the command: the body is there.
        internal const float MinLossTurn = 0.0005f;

        // A spin (rad/s) under which a body at its target has settled there.
        internal const float SettledAngularSpeed = 0.01f;

        // A spin (rad/s) under which a body being turned by the InWorldz look-at is taken to have stalled against friction.
        internal const float StalledAngularSpeed = 0.2f;

        private SQuaternion _apidTarget = SQuaternion.Identity;   // _targetLock
        private float _apidStrength;                              // _targetLock
        private float _apidDamping;                               // _targetLock
        private bool _apidActive;                                 // _targetLock
        private bool _apidChanged;                                // _targetLock: wake the body
        private int _apidSpunByScript;                            // set by RotationalVelocity, read by the step

        // Update-rate path only (the spring itself lives in BasicForcesStepProvider).
        private bool _angActing;           // the controller set the body's angular velocity in the last step

        public override Quaternion APIDTarget
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "APIDTarget", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.APIDTarget = value; return; }
                SQuaternion q = SQuaternion.Normalize(ToS(value));
                lock (_targetLock) { _apidTarget = q; _apidChanged = true; }
            }
        }

        public override float APIDStrength
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "APIDStrength", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.APIDStrength = value; return; }
                lock (_targetLock) { _apidStrength = value; _apidChanged = true; }
            }
        }

        public override float APIDDamping
        {
            set
            {
                if (!NonFiniteGuard.Ok(value)) { NonFiniteGuard.Rejected(ref _nonFiniteLogTicks, "prim", LocalID, "APIDDamping", value.ToString()); return; }
                if (_linkRoot != null) { _linkRoot.APIDDamping = value; return; }
                lock (_targetLock) { _apidDamping = value; _apidChanged = true; }
            }
        }

        public override bool APIDActive
        {
            set
            {
                if (_linkRoot != null) { _linkRoot.APIDActive = value; return; }
                lock (_targetLock) { _apidActive = value; _apidChanged = true; }
                if (value) _module.SetTargeted(this, true);
            }
        }

        /// <summary>
        /// The Update-rate half of the rotation target (from <see cref="StepTargets"/>): reads the request, wakes or lets go of
        /// the body, and fills in the plan that BasicForcesStepProvider carries out in every collision step. Returns false once
        /// the target is off and the body has been let go.
        /// </summary>
        private bool PrepareAngularTarget(BasicForcesStepProvider plan)
        {
            SQuaternion target;
            float strength, damping;
            bool active, changed;
            lock (_targetLock)
            {
                target = _apidTarget; strength = _apidStrength; damping = _apidDamping; active = _apidActive;
                changed = _apidChanged;
                _apidChanged = false;
            }
            bool spun = Interlocked.Exchange(ref _apidSpunByScript, 0) != 0;

            bool vehicle = _vehicle != null && _vehicle.IsActive;
            if (!active || !_isPhysical || !_body.IsValid || vehicle || !_backend.TryGetBodyState(_body, out BodyState s))
            {
                AngularLetGo(vehicle, keepVelocity: spun);
                return active;
            }

            // InWorldz: a mouse drag overrides the rotation target (PhysxPrim.pid.cs DoRotLookTarget: "Grab overrides rot
            // target"). The request is kept and acts again when the drag ends; the drag takes the spin away itself.
            if (plan.InWorldz && _grabbed)
            {
                AngularLetGo(false, keepVelocity: true);
                return true;
            }

            // Selected in the build tool the object is held still and the target does not act (as the move to target); the
            // request is kept, and a change made meanwhile is kept for then.
            if (_selected)
            {
                plan.ResetAngularState();
                if (changed)
                    lock (_targetLock) _apidChanged = true;
                return true;
            }

            // Second Life: "The minimum value for strength seems to be 0.0445 for the function to have any effect": under it
            // the request is kept and nothing acts. Halcyon holds the strength to 0.1..1 and the damping to 0.1..100 and acts
            // on whatever it is given (SetRotLookAtTarget).
            if (!plan.InWorldz && !(strength >= MinLookAtStrength))
            {
                AngularLetGo(false);
                return true;
            }

            SQuaternion goal = SQuaternion.Normalize(BodyOrientationOf(new Quaternion(target.X, target.Y, target.Z, target.W)));
            SQuaternion q = SQuaternion.Normalize(s.Orientation);
            bool awake = (s.Flags & BodyStateFlags.Active) != 0;
            bool fresh = !_angActing || spun;
            if (!awake)
            {
                // Halcyon's look-at stops the turn within a degree of the target (AtRotTolerance), so a body that has
                // settled anywhere inside it is at its target; the Second Life spring settles much closer (HoldAngle).
                float hold = plan.InWorldz ? HalcyonBasicForces.AtRotTolerance : HoldAngle;
                if (!changed && !spun && AngleBetween(q, goal) <= hold)
                    return true;   // asleep at its target
                _backend.ActivateBody(_body);
                fresh = true;
            }
            if (fresh)
            {
                plan.ResetAngular = true;
                plan.AngularApproaching = true;
            }
            _angActing = true;

            plan.Angular = true;
            plan.Goal = goal;
            plan.Strength = strength;
            plan.Damping = damping;
            plan.LockX = _bodyLockX;
            plan.LockY = _bodyLockY;
            plan.LockZ = _bodyLockZ;
            _angApproaching = plan.AngularApproaching;
            ApplySleepRule();
            return true;
        }

        private bool _bodyLockX, _bodyLockY, _bodyLockZ;   // the body's own axes with an infinite inertia (ApplyRotationLocks)

        // The target is off, not usable, or the body is not one it acts on: the spin it set is ended, the body may sleep.
        // An angular velocity written since the last step (core applies a script's llTargetOmega again when a rotate-drag is
        // let go, right after it switches the target off) is the one the body keeps.
        private void AngularLetGo(bool vehicle, bool keepVelocity = false)
        {
            _basicForces?.ResetAngularState();
            if (!_angActing)
                return;
            _angActing = false;
            _angApproaching = false;
            if (vehicle || !_body.IsValid || !_isPhysical)
            {
                if (!_linApproaching) _noSleepBody = BodyId.Invalid;
                return;
            }
            if (!keepVelocity)
                _backend.SetBodyAngularVelocity(_body, SVector3.Zero);
            ApplySleepRule();
        }

        /// <summary>The angle (rad) between two rotations, the short way.</summary>
        internal static float AngleBetween(SQuaternion a, SQuaternion b)
        {
            float dot = MathF.Min(1f, MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W));
            return 2f * MathF.Acos(dot);
        }
    }
}
