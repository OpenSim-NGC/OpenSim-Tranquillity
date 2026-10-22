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

/* Copyright (c) 2026 Legion Builds
 *
 * The Second Life springs in this file (StepMoveToSpring, StepHoverSpring, StepAngularSpring and the damping helper) moved here
 * from JoltPrim.Targets.cs and JoltPrim.AngularTarget.cs, which Legion Builds wrote; that code is under the Mozilla Public
 * License, v. 2.0. This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The per-step part of llMoveToTarget, llSetHoverHeight, llGroundRepel, llLookAt and llRotLookAt for a physical prim that is
 * not a vehicle, run before every collision step (the order of Halcyon's PhysxPrim.UpdateDynamicForPIDTargets: move target,
 * rotation look-at, hover).
 * Adapted 2026 by Legion Builds
 */

// The step provider of the target controllers. Jolt splits one Update(dt, n) into n collision steps and calls each
// registered step listener before every one of them (see StepForceProvider.cs). A spring or an approach that is worked out
// once per Update and handed to the engine as one velocity is wrong when the Update is cut into steps in different ways; here
// each controller is worked out in every collision step, over the step's own length, as a change of velocity, and the engine
// integrates it.
//
// The split is the vehicle controller's (VehicleStepProvider): the Update-rate path (JoltPrim.StepTargets) reads the script's
// settings under their lock, decides whether a controller acts, wakes or lets go of the body, and arms this provider with a
// snapshot of what the steps need (the plan below: targets, taus, mass-dependent acceleration, damping, locks). The steps read
// only that, the body through Jolt's no-lock interface, the terrain height (a heightfield replaced whole, never changed in
// place) and the water level. OnStep takes no lock, allocates nothing, and writes velocities and the gravity factor only. The
// last collision step disarms the provider.
//
// Two laws sit behind [Jolt] BasicForcesBehaviour:
//   SecondLife: the critically damped spring of the SL wiki's "critically damps in tau seconds" for move to target, hover and
//     the rotation target (JoltPrim.Targets.cs, JoltPrim.AngularTarget.cs), stepped exactly over the collision step.
//   InWorldz: Halcyon's (HalcyonBasicForces), in the form that does not depend on the step.

using System;
using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using SQuaternion = System.Numerics.Quaternion;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class BasicForcesStepProvider : IStepForceProvider
    {
        private readonly JoltScene _module;
        private readonly JoltPrim _prim;
        private readonly IPhysicsBackend _backend;

        // The attachment to the body it was made for; a recreated body takes its providers with it, so it is made again.
        private readonly object _gate = new object();
        private IDisposable _attachment;
        private BodyId _attachedBody;
        private bool _unavailable;

        // ---- The plan: written by the Update-rate path before the Update (host thread), read by OnStep ----
        private volatile bool _armed;

        internal bool InWorldz;
        internal bool Move, Hover, Angular;
        internal bool Grab;   // the move target is a mouse drag's: Halcyon also takes the spin away (InWorldz only)

        internal SVector3 MoveTarget;
        internal float MoveTau;
        internal float HoverHeight, HoverTau;
        internal PIDHoverType HoverType;
        internal bool HoverRepel;
        internal SVector3 ScriptAcceleration;      // the set force's acceleration (SecondLife: part of the spring's step)
        internal float LinearDamping, AngularDamping;
        internal float ScriptGravity;              // the gravity factor the script's buoyancy and multiplier give

        internal SQuaternion Goal;                 // body frame
        internal float Strength, Damping;
        internal bool LockX, LockY, LockZ;
        internal bool ResetAngular;                // start the rotation spring again from the body

        internal float MaxLinearSpeed, MaxAngularSpeed;

        // ---- State the steps carry from step to step (and Update to Update). The Update-rate path touches it only between Updates. ----
        internal SVector3 MoveCarry;               // what the last move-to step left out of the spring's velocity (SecondLife)
        internal float HoverCarry;                 // the same for hover (vertical)
        internal bool LastWasMove;
        private float _appliedGravity = float.NaN;
        private bool _hoverGravityOff;             // InWorldz: hover has turned gravity off

        private bool _haveVirtual;
        private SQuaternion _angQ;                 // the spring's rotation
        private SVector3 _angW;                    // the spring's angular velocity
        private bool _havePrev;
        private SQuaternion _prevQ;
        private SVector3 _prevCmd;
        private float _prevScale;
        private float _loss;

        // What the last steps found, for the Update-rate path's sleep rule.
        internal volatile bool LinearApproaching = true;
        internal volatile bool AngularApproaching = true;

        // Halcyon's second-order coefficients for the current step length, kept so the powers are worked out once.
        private float _rotKappa, _rotC, _rotH, _rotAlpha, _rotBeta;

        internal BasicForcesStepProvider(JoltScene module, JoltPrim prim, IPhysicsBackend backend)
        {
            _module = module;
            _prim = prim;
            _backend = backend;
        }

        internal bool Armed => _armed;

        /// <summary>Makes the plan current for the next Update's collision steps. False when the backend takes no step providers or the body is gone.</summary>
        internal bool Arm(BodyId body)
        {
            if (!Attach(body))
                return false;
            _appliedGravity = float.NaN;   // the first step sets the share, whatever the host set
            _armed = true;
            return true;
        }

        internal void Disarm() => _armed = false;

        /// <summary>Takes the provider off the body: no controller acts any more (or the prim is gone).</summary>
        internal void Detach()
        {
            lock (_gate)
            {
                _armed = false;
                _attachment?.Dispose();
                _attachment = null;
            }
        }

        /// <summary>Forgets what the steps carried: the linear carries and the rotation spring.</summary>
        internal void ResetLinear()
        {
            MoveCarry = SVector3.Zero;
            HoverCarry = 0f;
            _hoverGravityOff = false;
            LinearApproaching = false;
        }

        internal void ResetAngularState()
        {
            _haveVirtual = false;
            _havePrev = false;
            _loss = 0f;
            AngularApproaching = false;
        }

        private bool Attach(BodyId body)
        {
            if (_unavailable || !body.IsValid)
                return false;
            lock (_gate)
            {
                if (_attachment != null && _attachedBody.Equals(body))
                    return true;
                _attachment?.Dispose();
                _attachment = null;
                try
                {
                    _attachment = _backend.AttachStepForceProvider(body, this);
                    _attachedBody = body;
                    return true;
                }
                catch (Exception e) when (e is NotSupportedException || e is NotImplementedException)
                {
                    _unavailable = true;
                    return false;
                }
                catch (Exception e) when (e is ArgumentException || e is ObjectDisposedException)
                {
                    return false;   // the body is gone from the backend (removed meanwhile): nothing to step this Update
                }
            }
        }

        public void OnStep(in StepBody body, in StepInfo step)
        {
            if (!_armed)
                return;
            long started = Stopwatch.GetTimestamp();
            try
            {
                float h = step.DeltaTime;
                if (InWorldz)
                    StepHalcyon(body, h);
                else
                {
                    if (Move) StepMoveToSpring(body, h);
                    else if (Hover) StepHoverSpring(body, h);
                    if (Angular) StepAngularSpring(body, h);
                }
            }
            finally
            {
                if (step.IsLastStep)
                    _armed = false;
                _module.AddBasicForcesStepTicks(Stopwatch.GetTimestamp() - started);
            }
        }

        private void SetGravity(in StepBody body, bool on)
        {
            float factor = on ? ScriptGravity : 0f;
            if (factor == _appliedGravity)
                return;
            _appliedGravity = factor;
            body.SetGravityFactor(factor);
        }

        // ================= InWorldz: Halcyon's laws =================

        private void StepHalcyon(in StepBody body, float h)
        {
            SVector3 vel = body.LinearVelocity;
            SVector3 pos = body.Position;
            SVector3 dv = SVector3.Zero;
            bool gravityOn = true;
            bool approaching = false;

            if (Move && Grab)
            {
                // DoGrabTarget: a drag takes 90% of the spin away in a step, then goes to its target like a move target.
                SVector3 spin = body.AngularVelocity;
                if (spin != SVector3.Zero)
                    body.SetAngularVelocity(spin * HalcyonBasicForces.GrabAngularKept(h));
            }

            if (Move)
            {
                // DoPIDMoveTarget: the body goes to its target at distance / tau.
                SVector3 distance = MoveTarget - pos;
                gravityOn = false;
                if (distance.LengthSquared() <= HalcyonBasicForces.AtTargetTolerance * HalcyonBasicForces.AtTargetTolerance)
                {
                    // StopLinearMovement: at the target with a velocity, stop dead (and the rest of this step's controllers
                    // still add theirs, as Halcyon's do). Moving without a velocity falls through to the approach below.
                    if (vel != SVector3.Zero)
                        dv -= vel;
                    else
                        dv += HalcyonBasicForces.MoveToVelocity(distance, MoveTau, h);
                    approaching = vel.LengthSquared() > JoltPrim.SettledSpeed * JoltPrim.SettledSpeed;
                }
                else
                {
                    dv += HalcyonBasicForces.MoveToVelocity(distance, MoveTau, h) - vel;
                    approaching = true;
                }
            }

            if (Hover)
            {
                float ground = _module.TerrainHeightAt(pos.X, pos.Y);
                float above = HalcyonBasicForces.HeightAbove(HoverType, pos.Z, ground, _module.WaterLevel);
                HalcyonBasicForces.HoverResult r = HalcyonBasicForces.Hover(above, HoverHeight, HoverTau, HoverRepel, vel.Z, h);
                _hoverGravityOff = r.GravityOff;
                if (r.Acts)
                    dv.Z += r.VerticalVelocity - vel.Z;
                if (r.GravityOff)
                    gravityOn = false;
                approaching |= MathF.Abs(above - HoverHeight) >= HalcyonBasicForces.AtHoverHeightTolerance
                               || MathF.Abs(r.VerticalVelocity) > JoltPrim.SettledSpeed;
            }
            else
                _hoverGravityOff = false;

            if (Move || Hover)
            {
                SetGravity(body, gravityOn);
                LinearApproaching = approaching;
                if (dv != SVector3.Zero)
                {
                    SVector3 v = vel + dv;
                    float len = v.Length();
                    if (len > MaxLinearSpeed)
                        v *= MaxLinearSpeed / len;
                    body.SetLinearVelocity(v);
                }
            }

            if (Angular)
                StepRotLookHalcyon(body, h);
        }

        // DoRotLookTarget. The body's rotation and the goal are in the body's frame, so the spring turns the body to the goal
        // however the prim's shape is axis-corrected.
        private void StepRotLookHalcyon(in StepBody body, float h)
        {
            SQuaternion q = SQuaternion.Normalize(body.Rotation);
            SVector3 w = body.AngularVelocity;
            SVector3 turn = Free(RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(Goal, SQuaternion.Conjugate(q)))), q);
            float strength = Strength, damping = Damping;
            HalcyonBasicForces.ClampRotLookAt(ref strength, ref damping);
            if (h != _rotH || strength != _rotC || damping != _rotKappa)
            {
                (_rotAlpha, _rotBeta) = HalcyonBasicForces.SecondOrder(HalcyonBasicForces.RotLookAtScale / damping, HalcyonBasicForces.RotLookAtScale * strength, h);
                _rotH = h; _rotC = strength; _rotKappa = damping;
            }
            AngularApproaching = turn.Length() > HalcyonBasicForces.AtRotTolerance || w.Length() > JoltPrim.SettledAngularSpeed;
            if (ResetAngular)
            {
                _havePrev = false;
                _loss = 0f;
                ResetAngular = false;
            }
            SVector3 next;
            float scale = h * DampingFor(AngularDamping, h).Mean;
            if (turn.Length() <= HalcyonBasicForces.AtRotTolerance)
            {
                next = SVector3.Zero;
                _havePrev = false;
                _loss = 0f;
            }
            else
            {
                next = w * _rotBeta - turn * _rotAlpha;
                // Contact friction takes up to a fixed amount of velocity in each step whatever the look-at gives, so
                // as the law's command shrinks near the target a body lying on the ground stops short of it with
                // nothing left to move it (the law sets the velocity from the error alone). Once the body has all but
                // stopped it is asked for what the engine took from the last command, as the Second Life spring is;
                // while it is turning, and in the air, nothing is added and the law is Halcyon's as it stands.
                if (Free(w, q).Length() <= JoltPrim.StalledAngularSpeed && _havePrev && _prevCmd.LengthSquared() > 1e-12f)
                {
                    SVector3 u = SVector3.Normalize(_prevCmd);
                    SVector3 made = RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(q, SQuaternion.Conjugate(_prevQ))));
                    float missing = (_prevCmd.Length() * _prevScale - SVector3.Dot(made, u)) / _prevScale;
                    _loss = missing > 0f ? missing : _loss * 0.5f;
                }
                else
                    _loss *= 0.5f;
                float turnLen = turn.Length();
                if (turnLen > JoltPrim.MinLossTurn && _loss > 0f)
                    next += turn * (_loss / turnLen);
                _havePrev = true;
                _prevQ = q;
                _prevCmd = Free(next, q);
                _prevScale = scale;
            }
            next = Free(next, q) + LockedPart(w, q);
            float max = MaxAngularSpeed;
            float len = next.Length();
            if (len > max)
                next *= max / len;
            if (next != w)
                body.SetAngularVelocity(next);
        }

        // ================= SecondLife: the critically damped spring =================

        private void StepMoveToSpring(in StepBody body, float h)
        {
            SVector3 pos = body.Position;
            SVector3 vel = body.LinearVelocity;
            SetGravity(body, false);
            SVector3 e0 = pos - MoveTarget;
            SVector3 v0 = vel + MoveCarry;
            LinearApproaching = e0.Length() >= JoltPrim.HoldTolerance || v0.Length() > JoltPrim.SettledSpeed;
            // A set force's acceleration a is part of the spring's step: e'' = -e / tau^2 - 2 e' / tau + a is the same spring
            // about e = a tau^2.
            SVector3 a = ScriptAcceleration;
            SVector3 shift = a * (MoveTau * MoveTau);
            (double mx, double cx) = VehicleSpring.MoveOver(e0.X - shift.X, v0.X, MoveTau, 1.0, h);
            (double my, double cy) = VehicleSpring.MoveOver(e0.Y - shift.Y, v0.Y, MoveTau, 1.0, h);
            (double mz, double cz) = VehicleSpring.MoveOver(e0.Z - shift.Z, v0.Z, MoveTau, 1.0, h);
            // The spring's velocity at the end of the step, and the velocity that, through the engine's damping and with the
            // force's own share of the motion, moves the body to the spring's position.
            var springEnd = new SVector3((float)(mx + cx), (float)(my + cy), (float)(mz + cz));
            StepDamping d = DampingFor(LinearDamping, h);
            SVector3 moveVel = (new SVector3((float)mx, (float)my, (float)mz) - a * (d.ForceMean * h)) / d.Mean;
            MoveCarry = springEnd - moveVel * d.End - a * (d.ForceEnd * h);

            // The engine caps a body's speed ([Jolt] BodyMaxLinearSpeed); past it the spring starts again from the body's own
            // state at the next step.
            float len = moveVel.Length();
            if (len > MaxLinearSpeed)
            {
                moveVel *= MaxLinearSpeed / len;
                MoveCarry = SVector3.Zero;
            }
            body.SetLinearVelocity(moveVel);
        }

        private void StepHoverSpring(in StepBody body, float h)
        {
            SVector3 pos = body.Position;
            SVector3 vel = body.LinearVelocity;
            SetGravity(body, false);
            // Where the target is now, and where it will be under the object at the end of the step: the spring acts on the
            // height above the ground, so on a slope the object rises with the ground as it moves.
            float targetNow = _prim.HoverTargetAt(pos.X, pos.Y, HoverHeight, HoverType);
            float targetNext = _prim.HoverTargetAt(pos.X + vel.X * h, pos.Y + vel.Y * h, HoverHeight, HoverType);
            float e0 = pos.Z - targetNow;
            float rise = (targetNext - targetNow) / h;
            double ve0 = vel.Z + HoverCarry - rise;
            LinearApproaching = MathF.Abs(e0) >= JoltPrim.HoldTolerance || Math.Abs(ve0) > JoltPrim.SettledSpeed;
            float az = ScriptAcceleration.Z;
            (double move, double carry) = VehicleSpring.MoveOver(e0 - az * HoverTau * HoverTau, ve0, HoverTau, 1.0, h);
            StepDamping d = DampingFor(LinearDamping, h);
            float vz = (rise + (float)move - az * d.ForceMean * h) / d.Mean;
            // Next step's error rate starts from the body's vertical velocity plus this, less the ground's rise then.
            HoverCarry = (float)(move + carry) + rise - vz * d.End - az * d.ForceEnd * h;
            if (MathF.Abs(vz) > MaxLinearSpeed)
            {
                vz = MathF.CopySign(MaxLinearSpeed, vz);
                HoverCarry = 0f;
            }
            body.SetLinearVelocity(new SVector3(vel.X, vel.Y, vz));
        }

        private void StepAngularSpring(in StepBody body, float h)
        {
            SQuaternion q = SQuaternion.Normalize(body.Rotation);
            SVector3 w = body.AngularVelocity;
            float timescale = MathF.Max(Strength, Math.Clamp(Damping, 0f, JoltPrim.MaxLookAtDamping));

            if (ResetAngular || !_haveVirtual || JoltPrim.AngleBetween(_angQ, q) > JoltPrim.ResyncAngle)
            {
                _angQ = q;
                _angW = w;
                _haveVirtual = true;
                _havePrev = false;
                _loss = 0f;
                ResetAngular = false;
            }

            // The spring over this step, on the rotation vector from the target to the spring's rotation.
            SVector3 r = Free(RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(_angQ, SQuaternion.Conjugate(Goal)))), q);
            SVector3 v0 = Free(_angW, q);
            (double mx, double cx) = VehicleSpring.MoveOver(r.X, v0.X, timescale, 1.0, h);
            (double my, double cy) = VehicleSpring.MoveOver(r.Y, v0.Y, timescale, 1.0, h);
            (double mz, double cz) = VehicleSpring.MoveOver(r.Z, v0.Z, timescale, 1.0, h);
            var move = new SVector3((float)mx, (float)my, (float)mz);          // the error's mean rate over the step
            var springEnd = new SVector3((float)(mx + cx), (float)(my + cy), (float)(mz + cz));
            SQuaternion next = SQuaternion.Normalize(SQuaternion.Multiply(FromRotationVector(move * h), _angQ));

            // The angular velocity that takes the body from where it is to the spring's rotation at the end of the step,
            // through the engine's angular damping.
            SVector3 turn = Free(RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(next, SQuaternion.Conjugate(q)))), q);
            StepDamping d = DampingFor(AngularDamping, h);
            float scale = h * d.Mean;

            // What the last command gave: the turn it was meant to make and the turn the body made.
            if (_havePrev && _prevCmd.LengthSquared() > 1e-12f)
            {
                SVector3 u = SVector3.Normalize(_prevCmd);
                SVector3 made = RotationVector(SQuaternion.Normalize(SQuaternion.Multiply(q, SQuaternion.Conjugate(_prevQ))));
                float missing = (_prevCmd.Length() * _prevScale - SVector3.Dot(made, u)) / _prevScale;
                _loss = missing > 0f ? missing : _loss * 0.5f;
            }
            else
                _loss *= 0.5f;

            SVector3 cmd = turn / scale;
            float turnLen = turn.Length();
            if (turnLen > JoltPrim.MinLossTurn && _loss > 0f)
                cmd += turn * (_loss / turnLen);
            float len = cmd.Length();
            if (len > MaxAngularSpeed)
            {
                cmd *= MaxAngularSpeed / len;
                _haveVirtual = false;   // the body cannot keep up: the spring starts again from it
            }
            _angQ = next;
            _angW = Free(springEnd, q);
            _havePrev = true;
            _prevQ = q;
            _prevCmd = cmd;
            _prevScale = scale;
            AngularApproaching = r.Length() >= JoltPrim.HoldAngle || v0.Length() > JoltPrim.SettledAngularSpeed;
            body.SetAngularVelocity(cmd);
        }

        // ---- Shared helpers ----

        // The rotation's axes that are locked (STATUS_ROTATE_*) as region axes: a spring acts on the rest.
        private SVector3 Free(SVector3 v, SQuaternion q)
        {
            if (!(LockX || LockY || LockZ))
                return v;
            if (LockX) { SVector3 a = SVector3.Transform(SVector3.UnitX, q); v -= SVector3.Dot(v, a) * a; }
            if (LockY) { SVector3 a = SVector3.Transform(SVector3.UnitY, q); v -= SVector3.Dot(v, a) * a; }
            if (LockZ) { SVector3 a = SVector3.Transform(SVector3.UnitZ, q); v -= SVector3.Dot(v, a) * a; }
            return v;
        }

        // What of an angular velocity lies along the locked axes (the engine's infinite inertia holds it).
        private SVector3 LockedPart(SVector3 v, SQuaternion q) => LockX || LockY || LockZ ? v - Free(v, q) : SVector3.Zero;

        internal static SVector3 RotationVector(SQuaternion q)
        {
            if (q.W < 0f)
                q = new SQuaternion(-q.X, -q.Y, -q.Z, -q.W);
            var v = new SVector3(q.X, q.Y, q.Z);
            float s = v.Length();
            if (s < 1e-7f)
                return v * 2f;
            return v * (2f * MathF.Atan2(s, q.W) / s);
        }

        internal static SQuaternion FromRotationVector(SVector3 r)
        {
            float angle = r.Length();
            if (angle < 1e-9f)
                return SQuaternion.Identity;
            return SQuaternion.CreateFromAxisAngle(r / angle, angle);
        }

        /// <summary>
        /// How a step of h moves a body, per unit of what it is given. Jolt damps the velocity in each collision step,
        /// v = (v + a h)(1 - c h), and moves the body by that velocity. A velocity v set before the step moves the body by
        /// v h Mean and leaves it at v End; a steady acceleration a from rest moves it by a h^2 ForceMean and leaves it at
        /// a h ForceEnd.
        /// </summary>
        internal readonly record struct StepDamping(float Mean, float End, float ForceMean, float ForceEnd);

        // The same for a damping c (1/s), linear or angular, over a collision step.
        internal static StepDamping DampingFor(float c, float h)
        {
            double q = 1.0 - Math.Max(c, 0f) * h;
            if (q <= 0.0)
                q = 1.0;
            return new StepDamping((float)q, (float)q, (float)q, (float)q);
        }
    }
}
