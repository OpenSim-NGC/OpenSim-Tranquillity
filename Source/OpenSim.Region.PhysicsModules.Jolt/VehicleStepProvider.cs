/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A vehicle's step force provider: runs the vehicle controller's per-step terms before every collision step of the
// engine's Update (Jolt's step listener), so the ramps, decays, friction, deflection, attractor, banking, hover and the
// vehicle's gravity are integrated over the collision step's own length, whatever the Update rate and the number of
// collision steps.
//
// The split. The host's Update-rate path (JoltPrim.StepVehicle) runs VehicleController.BeginUpdate before the Update: the
// events, the rays, the stall check, the wind, and the parameter snapshot. It then arms this provider with the numbers the
// steps need from the host (mass, inertia, whether the vehicle touches something). Inside the Update the listener calls
// OnStep once per collision step for an awake body; OnStep reads the body's position, rotation and velocities through
// Jolt's no-lock interface, runs VehicleController.StepOnce on that view, and writes the velocities back (twice at most
// per step). The last collision step disarms the provider, so a step that the host did not arm for does nothing.
//
// What OnStep may do. It runs on a worker thread or on the thread that called Update, while Jolt holds every body and
// constraint mutex. It takes no lock, does not call back into the backend, allocates nothing, and changes velocities only
// (a force or torque added from inside a collision step would be applied again in each later one). It reads only what
// the Update-rate path wrote before the Update, the terrain's heightfield (replaced whole, never changed in place) and the
// water height.

using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class VehicleStepProvider : IStepForceProvider, IVehicleBody
    {
        private readonly JoltScene _module;
        private readonly JoltPrim _prim;
        private readonly VehicleController _controller;

        // Written by Arm on the host's thread before the Update, read by OnStep during it.
        private volatile bool _armed;
        private float _mass;
        private Vector3 _inertia;
        private bool _hasCollision;
        private float _gravityMultiplier = 1f;
        private float _appliedGravityFactor = float.NaN;
        private IWindModule _wind;   // the region's wind module, taken in Arm; OnStep never looks it up

        // The body as the current collision step sees it. Position and rotation do not change inside a step; the velocities
        // are read once and written back once if the controller changed them, and read back what was written in between.
        private StepBody _step;
        private float _stepSeconds;
        private Vector3 _position, _linear, _angular;
        private Quaternion _rotation;
        private bool _linearDirty, _angularDirty;

        internal VehicleStepProvider(JoltScene module, JoltPrim prim, VehicleController controller)
        {
            _module = module;
            _prim = prim;
            _controller = controller;
        }

        /// <summary>True from <see cref="Arm"/> until the Update's last collision step (or <see cref="Disarm"/>).</summary>
        internal bool Armed => _armed;

        /// <summary>Lets the next Update's collision steps run the controller. Called on the host's thread before the Update.</summary>
        internal void Arm(float mass, Vector3 inertiaDiagonal, bool hasCollision)
        {
            _mass = mass;
            _inertia = inertiaDiagonal;
            _hasCollision = hasCollision;
            _gravityMultiplier = _prim.GravModifier;
            _wind = _module.WindModule;
            _appliedGravityFactor = float.NaN;   // the first step sets the vehicle's share, whatever the host set before
            _armed = true;
        }

        internal void Disarm() => _armed = false;

        public void OnStep(in StepBody body, in StepInfo step)
        {
            if (!_armed)
                return;
            long started = Stopwatch.GetTimestamp();
            _step = body;
            _stepSeconds = step.DeltaTime;
            SVector3 p = body.Position, v = body.LinearVelocity, w = body.AngularVelocity;
            _position = new Vector3(p.X, p.Y, p.Z);
            _rotation = _prim.PrimOrientationOf(body.Rotation);
            _linear = new Vector3(v.X, v.Y, v.Z);
            _angular = new Vector3(w.X, w.Y, w.Z);
            _linearDirty = _angularDirty = false;
            try
            {
                _controller.StepOnce(step.DeltaTime, this);
            }
            finally
            {
                if (_linearDirty)
                    body.SetLinearVelocity(new SVector3(_linear.X, _linear.Y, _linear.Z));
                if (_angularDirty)
                    body.SetAngularVelocity(new SVector3(_angular.X, _angular.Y, _angular.Z));
                if (step.IsLastStep)
                    _armed = false;
                _module.AddVehicleStepTicks(Stopwatch.GetTimestamp() - started);
            }
        }

        // ---- IVehicleBody, as the controller's step sees the body ----

        public Vector3 Position => _position;
        public Quaternion Orientation => _rotation;

        public Vector3 LinearVelocity
        {
            get => _linear;
            set { _linear = value; _linearDirty = true; }
        }

        public Vector3 AngularVelocity
        {
            get => _angular;
            set { _angular = value; _angularDirty = true; }
        }

        public float Mass => _mass;
        public Vector3 InertiaDiagonal => _inertia;

        public Vector3 Gravity
        {
            get
            {
                SVector3 g = _module.DefaultGravity;
                return new Vector3(g.X, g.Y, g.Z);
            }
        }

        public void SetGravityFactor(float factor)
        {
            if (factor == _appliedGravityFactor)
                return;
            _appliedGravityFactor = factor;
            _step.SetGravityFactor(factor);
        }

        public bool HasCollision => _hasCollision;
        public float GravityMultiplier => _gravityMultiplier;

        // A force over the collision step is the velocity change it makes over it.
        public void AddForce(Vector3 force)
        {
            if (_mass > 0f)
                LinearVelocity = _linear + force * (_stepSeconds / _mass);
        }

        // A torque over the collision step is the angular velocity change it makes over it, through the inverse of the
        // body's principal moments.
        public void AddTorque(Vector3 torque)
        {
            Vector3 local = torque * Quaternion.Inverse(_rotation);
            local = new Vector3(
                _inertia.X > 0f ? local.X / _inertia.X : 0f,
                _inertia.Y > 0f ? local.Y / _inertia.Y : 0f,
                _inertia.Z > 0f ? local.Z / _inertia.Z : 0f);
            AngularVelocity = _angular + local * _rotation * _stepSeconds;
        }

        // The body is awake in every step the provider runs for, and waking one is the Update-rate path's work.
        public void KeepAwake() { }

        public float GetTerrainHeight(Vector3 pos) => _module.TerrainHeightAt(pos.X, pos.Y);
        public float GetWaterLevel(Vector3 pos) => _module.WaterLevel;

        // A wind sample for the step, from the module Arm captured. The module answers without a lock (ZephyrWind publishes an
        // immutable snapshot with each update), so a collision step takes none.
        public bool TryGetWind(Vector3 position, out Vector3 wind) => JoltScene.SampleWind(_wind, position, out wind);
    }
}
