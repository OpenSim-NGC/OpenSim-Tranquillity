/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The step provider of a script force or torque given in the object's own axes (llSetForce and llSetTorque with
// local = TRUE). A constant force in the world's axes needs no provider: Jolt applies an AddForce in every collision step
// of an Update. A force in the object's axes has to turn with the object, and an object that spins turns between two
// collision steps, so the vector is rotated by the body's rotation at the start of each collision step and applied as a
// change of velocity over that step: force x deltaTime for the linear velocity (divided by the mass by the engine),
// torque x deltaTime through the inverse inertia for the angular velocity. The result then does not depend on the Update
// rate or on the number of collision steps.
//
// The Update-rate path (JoltPrim.StepScriptForces) arms the provider before each Update with the vector in the body's own
// frame and the caps; the last collision step disarms it. OnStep takes no lock, allocates nothing and changes velocities
// only, as the step provider contract says (StepForceProvider.cs).

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using SQuaternion = System.Numerics.Quaternion;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class ScriptForceStepProvider : IStepForceProvider
    {
        private readonly JoltPrim _prim;
        private readonly IPhysicsBackend _backend;

        // The attachment to the body it was made for. A body that is recreated takes its providers with it, so the
        // attachment is made again for the new one.
        private readonly object _gate = new object();
        private IDisposable _attachment;
        private BodyId _attachedBody;
        private bool _unavailable;

        // Written by Arm on the host's thread before the Update, read by OnStep during it.
        private volatile bool _armed;
        private SVector3 _force, _torque;   // in the prim's own axes, N and N m
        private float _maxImpulse, _maxAngularImpulse;

        internal ScriptForceStepProvider(JoltPrim prim, IPhysicsBackend backend)
        {
            _prim = prim;
            _backend = backend;
        }

        /// <summary>True from <see cref="Arm"/> until the Update's last collision step (or <see cref="Disarm"/>).</summary>
        internal bool Armed => _armed;

        /// <summary>
        /// Lets the next Update's collision steps apply the force and torque (in the prim's own axes). The caps are the
        /// largest impulse and angular impulse one collision step may give, which bring the body from rest to the engine's
        /// speed cap. False when the backend takes no step providers or the body is gone: nothing is armed then.
        /// </summary>
        internal bool Arm(BodyId body, SVector3 force, SVector3 torque, float maxImpulse, float maxAngularImpulse)
        {
            if (!Attach(body))
                return false;
            _force = force;
            _torque = torque;
            _maxImpulse = maxImpulse;
            _maxAngularImpulse = maxAngularImpulse;
            _armed = true;
            return true;
        }

        internal void Disarm() => _armed = false;

        /// <summary>Takes the provider off the body (no force or torque in the object's axes any more, or the prim is gone).</summary>
        internal void Detach()
        {
            lock (_gate)
            {
                _armed = false;
                _attachment?.Dispose();
                _attachment = null;
            }
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
            try
            {
                // The prim's rotation in the middle of this collision step, worked out from the rotation and the angular velocity
                // at its start: the vector turns with the body, and the middle of the step is where a force held through it acts
                // on average (the start would lag a spinning body by half a step, a lag that grows with the step).
                float h = step.DeltaTime;
                SQuaternion bodyRotation = body.Rotation;
                SVector3 spin = body.AngularVelocity;
                float turn = spin.Length() * h * 0.5f;
                if (turn > 1e-7f)
                    bodyRotation = SQuaternion.Concatenate(bodyRotation, SQuaternion.CreateFromAxisAngle(spin / spin.Length(), turn));
                Quaternion rotation = _prim.PrimOrientationOf(bodyRotation);
                if (_force != SVector3.Zero)
                    body.AddImpulse(Capped(Rotated(_force, rotation) * h, _maxImpulse));
                if (_torque != SVector3.Zero && _maxAngularImpulse > 0f)
                    body.AddAngularImpulse(Capped(Rotated(_torque, rotation) * h, _maxAngularImpulse));
            }
            finally
            {
                if (step.IsLastStep)
                    _armed = false;
            }
        }

        private static SVector3 Rotated(SVector3 v, Quaternion q)
        {
            Vector3 w = new Vector3(v.X, v.Y, v.Z) * q;
            return new SVector3(w.X, w.Y, w.Z);
        }

        private static SVector3 Capped(SVector3 impulse, float max)
        {
            float len = impulse.Length();
            return len > max && len > 0f ? impulse * (max / len) : impulse;
        }
    }
}
