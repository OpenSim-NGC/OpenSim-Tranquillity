/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The Jolt implementation of the neutral vehicle seam.
//
// JoltVehicleBody adapts one JoltPrim's live backend body to OpenSim.Region.PhysicsModules.Jolt.Vehicles.IVehicleBody so the
// vehicle controller (VehicleController) can drive it without knowing Jolt exists.
// The BulletSim reference reads Force* properties LIVE from the engine; here BeginFrame() snapshots
// the body state once per frame (nothing moves between controller reads - the controller runs
// BEFORE the step), and every velocity WRITE updates the snapshot AND pushes through, preserving
// BulletSim's read-back-what-you-wrote semantics (two velocity changes in one frame compound).
//
// Orientation handed to the controller is the PRIM orientation (axis-correction removed), matching
// what BulletSim's ForceOrientation reports.

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class JoltVehicleBody : IVehicleBody
    {
        private readonly JoltScene _module;
        private readonly IPhysicsBackend _backend;
        private readonly JoltPrim _prim;

        // Per-frame snapshot (BeginFrame); velocity writes keep it current within the frame.
        private Vector3 _position;
        private Quaternion _orientation;
        private Vector3 _linVel;
        private Vector3 _angVel;

        internal JoltVehicleBody(JoltScene module, IPhysicsBackend backend, JoltPrim prim)
        {
            _module = module;
            _backend = backend;
            _prim = prim;
        }

        // Refresh the snapshot from the live body. False = no live body this frame (skip the step).
        internal bool BeginFrame()
        {
            if (!_prim.BodyHandle.IsValid || !_backend.TryGetBodyState(_prim.BodyHandle, out BodyState s))
                return false;
            _position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            _orientation = _prim.PrimOrientationOf(s.Orientation);
            _linVel = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            _angVel = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
            IsAwake = (s.Flags & BodyStateFlags.Active) != 0;
            return true;
        }

        /// <summary>Whether the engine had the body awake at the snapshot.</summary>
        internal bool IsAwake { get; private set; }

        public Vector3 Position => _position;
        public Quaternion Orientation => _orientation;

        public Vector3 LinearVelocity
        {
            get => _linVel;
            set
            {
                _linVel = value;
                if (_prim.BodyHandle.IsValid)
                    _backend.SetBodyLinearVelocity(_prim.BodyHandle, ToS(value));
            }
        }

        public Vector3 AngularVelocity
        {
            get => _angVel;
            set
            {
                _angVel = value;
                if (_prim.BodyHandle.IsValid)
                    _backend.SetBodyAngularVelocity(_prim.BodyHandle, ToS(value));
            }
        }

        public float Mass => _prim.BodyHandle.IsValid ? _backend.GetBodyMass(_prim.BodyHandle) : 0f;

        public Vector3 InertiaDiagonal
        {
            get
            {
                if (!_prim.BodyHandle.IsValid)
                    return Vector3.Zero;
                SVector3 d = _backend.GetBodyInertiaDiagonal(_prim.BodyHandle);
                return new Vector3(d.X, d.Y, d.Z);
            }
        }

        public Vector3 Gravity
        {
            get
            {
                SVector3 g = _module.DefaultGravity;
                return new Vector3(g.X, g.Y, g.Z);
            }
        }

        public float GravityMultiplier => _prim.GravModifier;

        public void SetGravityFactor(float factor)
        {
            if (_prim.BodyHandle.IsValid)
                _backend.SetBodyGravityFactor(_prim.BodyHandle, factor);
        }

        public bool HasCollision => _prim.IsColliding;   // set from the backend's contacts each step (JoltPrim.StepVehicle)

        public void AddForce(Vector3 force)
        {
            if (_prim.BodyHandle.IsValid)
                _backend.ApplyForce(_prim.BodyHandle, ToS(force));
        }

        public void AddTorque(Vector3 torque)
        {
            if (_prim.BodyHandle.IsValid)
                _backend.ApplyTorque(_prim.BodyHandle, ToS(torque));
        }

        public void KeepAwake()
        {
            if (_prim.BodyHandle.IsValid)
                _backend.ActivateBody(_prim.BodyHandle);
        }

        public float GetTerrainHeight(Vector3 pos) => _module.TerrainHeightAt(pos.X, pos.Y);

        public float GetWaterLevel(Vector3 pos) => _module.WaterLevel;

        public bool TryGetWind(Vector3 position, out Vector3 wind) => _module.TryGetWind(position, out wind);

        public bool TryGetBounds(out Vector3 center, out Vector3 halfSize) => _prim.TryGetVehicleBounds(out center, out halfSize);

        public bool CastRayNearest(Vector3 origin, Vector3 direction, float maxDistance, out float distance)
        {
            distance = 0f;
            if (!_prim.BodyHandle.IsValid)
                return false;
            // The hits come in distance order. The vehicle's own body (a linkset is one body) is skipped; a few of the
            // nearest are kept, so a ray starting inside the vehicle gets past it.
            Span<RayHit> hits = stackalloc RayHit[16];
            int n = _backend.RayCastAll(ToS(origin), ToS(direction), maxDistance, QueryFilter.Default, hits);
            for (int i = 0; i < n; i++)
            {
                if (hits[i].Body.Equals(_prim.BodyHandle))
                    continue;
                distance = hits[i].Distance;
                return true;
            }
            return false;
        }

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
    }
}
