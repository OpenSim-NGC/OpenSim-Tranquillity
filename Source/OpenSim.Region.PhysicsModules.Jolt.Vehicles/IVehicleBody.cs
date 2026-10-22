/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Backend-agnostic vehicle controller.
 *
 * IVehicleBody is the NEUTRAL seam between the vehicle controller
 * (VehicleController) and whatever physics engine hosts the body. No engine
 * types cross this line in either direction.
 */

using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// The minimal physics surface the vehicle controller needs, per frame:
    ///
    ///   read : Position, Orientation, LinearVelocity, AngularVelocity, Mass,
    ///          InertiaDiagonal, Gravity, HasCollision, water height, terrain height
    ///   write: LinearVelocity/AngularVelocity setters (instant velocity change),
    ///          AddForce, AddTorque, KeepAwake
    ///
    /// SEMANTICS (these are what make the extracted math exact - see the seam table in
    /// VehicleController.cs):
    ///  - LinearVelocity/AngularVelocity set = an instant velocity change applied NOW; a
    ///    subsequent get in the same frame MUST read back the just-written value (BulletSim's
    ///    ForceVelocity behaves this way - two velocity changes in one frame compound).
    ///  - AddForce(f)  = continuous central force consumed by the NEXT step
    ///    (deltaV = f / mass * dt). Bullet ApplyCentralForce == Jolt AddForce.
    ///  - AddTorque(t) = continuous torque consumed by the NEXT step
    ///    (deltaW = invInertia * t * dt). Bullet ApplyTorque == Jolt AddTorque.
    ///    (A host that runs the controller inside the engine's collision steps gives the body these two as the velocity
    ///    changes they make over the collision step; the controller itself adds no force and no torque.)
    ///  - Orientation is the PRIM orientation (SL convention), not any engine-internal
    ///    axis-corrected body orientation.
    /// </summary>
    public interface IVehicleBody
    {
        Vector3 Position { get; }
        Quaternion Orientation { get; }

        /// <summary>World linear velocity. Set = instant velocity change (reads back within the frame).</summary>
        Vector3 LinearVelocity { get; set; }

        /// <summary>World angular velocity. Set = instant velocity change (reads back within the frame).</summary>
        Vector3 AngularVelocity { get; set; }

        /// <summary>Total dynamic mass (a linkset root reports the whole linkset's mass).</summary>
        float Mass { get; }

        /// <summary>Local principal moments of inertia (diagonal). Feeds the vertical attractor's
        /// tensor remap (BulletSim: ControllingPrim.Inertia from CalculateLocalInertia).</summary>
        Vector3 InertiaDiagonal { get; }

        /// <summary>World gravity vector, e.g. (0,0,-9.80665). The controller decides how much of it the
        /// vehicle feels (buoyancy, the ground factor) and sets that share with <see cref="SetGravityFactor"/>.</summary>
        Vector3 Gravity { get; }

        /// <summary>The share of <see cref="Gravity"/> the engine applies to the body over its next step. The engine
        /// spreads it over the step as it does any gravity, and it does not keep a resting body awake.</summary>
        void SetGravityFactor(float factor);

        /// <summary>The body's gravity multiplier (llSetPhysicsMaterial GRAVITY_MULTIPLIER), 1 by default. Under
        /// <see cref="VehicleBehaviour.InWorldz"/> the vehicle's weight is this less its buoyancy; Second Life's vehicles
        /// ignore it.</summary>
        float GravityMultiplier => 1f;

        /// <summary>Body currently has a contact (ground-vehicle gravity fudge; unused by boats).</summary>
        bool HasCollision { get; }

        /// <summary>Continuous world-frame central force, consumed by the next step.</summary>
        void AddForce(Vector3 force);

        /// <summary>Continuous world-frame torque, consumed by the next step.</summary>
        void AddTorque(Vector3 torque);

        /// <summary>Wake the body / prevent it idling out (BulletSim: ActivateIfPhysical).</summary>
        void KeepAwake();

        /// <summary>Terrain height at the given position's XY.</summary>
        float GetTerrainHeight(Vector3 pos);

        /// <summary>Water height at the given position's XY.</summary>
        float GetWaterLevel(Vector3 pos);

        /// <summary>
        /// The vehicle's bounding box in its own frame, as a centre and half sizes, for the motor-height rule's down rays.
        /// False when the host has none (the rule then never finds the vehicle high).
        /// </summary>
        bool TryGetBounds(out Vector3 center, out Vector3 halfSize) { center = Vector3.Zero; halfSize = Vector3.Zero; return false; }

        /// <summary>
        /// The nearest thing along a ray other than the vehicle itself (its own parts do not count): the distance from the
        /// origin to it. False when the ray hits nothing within <paramref name="maxDistance"/>, or the host casts none.
        /// </summary>
        bool CastRayNearest(Vector3 origin, Vector3 direction, float maxDistance, out float distance) { distance = 0f; return false; }

        /// <summary>
        /// The wind at a region position, in m/s along the world's x and y: the region's wind module gives the water's
        /// current below the water, the surface wind above it and the wind aloft over that. False when the host has no wind
        /// module; the vehicle then behaves as if the air were still.
        /// </summary>
        bool TryGetWind(Vector3 position, out Vector3 wind) { wind = Vector3.Zero; return false; }
    }
}
