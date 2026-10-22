/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Per-body force providers: code that changes a body's velocity before every collision step of an Update.
//
// Jolt splits one Update(dt, n) into n collision steps of dt / n and calls each registered PhysicsStepListener before
// every one of them (Jolt/Physics/PhysicsSystem.cpp, JobStepListeners; PhysicsStepListener.h). A term that must be
// recomputed between collision steps (a ramp, an exponential decay, a spring, a force that follows the body's
// rotation or the wind where the body now is) belongs there. A constant world-frame force does not: Jolt already
// applies an AddForce in every collision step.
//
// Providers change velocities and never add a force or torque. Jolt applies the accumulated force in every collision
// step and clears it only after the last (PhysicsSystem.cpp, ApplyGravity and the sleep check), so a force added from
// OnStep is applied again in each later step of the same Update: with n collision steps it delivers (n + 1) / 2 times
// the intended change. A velocity change made in OnStep is applied once.

using System;
using System.Numerics;
using JoltPhysicsSharp;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    /// <summary>The collision step a provider is called for.</summary>
    public readonly struct StepInfo
    {
        /// <summary>The collision step's own length in seconds: the Update's length divided by its collision steps.</summary>
        public readonly float DeltaTime;
        public readonly bool IsFirstStep;
        public readonly bool IsLastStep;

        public StepInfo(float deltaTime, bool isFirstStep, bool isLastStep)
        {
            DeltaTime = deltaTime;
            IsFirstStep = isFirstStep;
            IsLastStep = isLastStep;
        }
    }

    /// <summary>
    /// One body, as a provider sees it inside a collision step: reads through Jolt's no-lock body interface, and velocity
    /// changes only. It is valid for the duration of the call; do not keep it.
    /// </summary>
    public readonly struct StepBody
    {
        private readonly BodyInterface _bodies;
        private readonly BodyID _id;

        internal StepBody(BodyInterface bodies, BodyID id)
        {
            _bodies = bodies;
            _id = id;
        }

        /// <summary>The body's position (read only inside a step: the broad phase updates concurrently).</summary>
        public Vector3 Position => _bodies.GetPosition(_id);
        public Quaternion Rotation => _bodies.GetRotation(_id);
        public Vector3 LinearVelocity => _bodies.GetLinearVelocity(_id);
        public Vector3 AngularVelocity => _bodies.GetAngularVelocity(_id);

        /// <summary>Adds <paramref name="delta"/> to the linear velocity (a velocity change, applied once).</summary>
        public void AddLinearVelocity(Vector3 delta) => _bodies.AddLinearVelocity(_id, delta);

        /// <summary>Adds <paramref name="delta"/> to the angular velocity (a velocity change, applied once).</summary>
        public void AddAngularVelocity(Vector3 delta) => _bodies.SetAngularVelocity(_id, _bodies.GetAngularVelocity(_id) + delta);

        /// <summary>
        /// Changes the linear velocity by <c>impulse / mass</c>, once. Jolt's AddImpulse changes the velocity at once and leaves
        /// no force behind, so it is safe from OnStep. A force held over a collision step is <c>force * deltaTime</c>.
        /// </summary>
        public void AddImpulse(Vector3 impulse) => _bodies.AddImpulse(_id, impulse);

        /// <summary>
        /// Changes the angular velocity by the inverse inertia tensor times <paramref name="impulse"/> (about the body's
        /// principal axes, as Jolt has them), once, as <see cref="AddImpulse"/>. A torque held over a collision step is
        /// <c>torque * deltaTime</c>.
        /// </summary>
        public void AddAngularImpulse(Vector3 impulse) => _bodies.AddAngularImpulse(_id, impulse);

        public void SetLinearVelocity(Vector3 velocity) => _bodies.SetLinearVelocity(_id, velocity);
        public void SetAngularVelocity(Vector3 velocity) => _bodies.SetAngularVelocity(_id, velocity);

        /// <summary>
        /// Sets the share of gravity the engine applies to the body. The engine reads it when it applies gravity, after the
        /// step listeners, so a change made here is the gravity of this collision step.
        /// </summary>
        public void SetGravityFactor(float factor) => _bodies.SetGravityFactor(_id, factor);
    }

    /// <summary>
    /// A term applied to one body before every collision step of an Update, attached with
    /// <see cref="IPhysicsBackend.AttachStepForceProvider"/>.
    /// <para>
    /// OnStep runs on a worker thread or on the thread that called Update, while Jolt holds every body and constraint
    /// mutex. It may read the body through <paramref name="body"/> and change its velocities. It must not take the
    /// backend's region lock or any lock Update holds, call back into the backend (no body create, remove, attach or
    /// detach), add a force or torque, or allocate. It reads only state the Update-rate path writes before the Update
    /// starts, or an immutable snapshot of it. It is called only for a body that is awake at the start of the step.
    /// </para>
    /// </summary>
    public interface IStepForceProvider
    {
        void OnStep(in StepBody body, in StepInfo step);
    }
}
