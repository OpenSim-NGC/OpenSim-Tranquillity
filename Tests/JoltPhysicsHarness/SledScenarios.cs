/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// sled-drop: the console test "jolt sledtest slide" in the harness, with a trace of every collision step.
//
// A 2 x 1 x 0.5 m box turned 20 degrees nose down, 1 m above the ground at (128, 128), made a VEHICLE_TYPE_SLED with its
// documented presets and no motor. It is let go at once and glides down its nose. The console test reads the speed
// nine times, 0.5 s apart, and passes when the largest horizontal speed is over 0.5 m/s; the run lasts 4.5 s here
// too, so the same nine instants (t = 0.5 ... 4.5) can be read from the trace.
//
// The trace comes from a PhysicsStepListener on the backend's physics system. Jolt calls it before each collision step
// of every Update, with every body locked, so it reads the body through the no-lock interface and writes nothing. A row
// is one collision step: the time it starts, its length, the sled's pose and velocities then, and, read from the
// controller as the Update left it (the controller runs once per Update, before the first collision step):
//   stalled   the controller's stall check has switched the slope assist off
//   held      seconds the rest rule has been holding the sled still (0 when it is not)
//   assist    the slope assist's acceleration along the nose that the controller's rule gives for the pose
//   awake     the engine has the body active
//   touching  the engine reported contact for the body in the last Update (the vehicle's ground check)
// Rows are written when the run ends, with the height of the sled's lowest point above the terrain under it.
//
// --terrain FILE gives the run the terrain of a region (raw little-endian float32, 256 x 256, x fastest); without it the
// ground is flat at the course's height. --trace-dir DIR makes the trace file, named by heartbeat rate, physics rate and
// collision steps.

using System.Globalization;
using System.Reflection;
using System.Text;
using JoltPhysicsSharp;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using NVec = System.Numerics.Vector3;
using VehicleController = OpenSim.Region.PhysicsModules.Jolt.Vehicles.VehicleController;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class SledScenarios
{
    public const float RezX = 128f, RezY = 128f, RezHeight = 1.0f, NoseDownDegrees = 20f;
    public static readonly Vector3 SledSize = new(2f, 1f, 0.5f);

    /// <summary>One collision step of the sled.</summary>
    public readonly record struct TraceRow(int Update, int Step, bool Last, double T, float Dt, NVec Position, System.Numerics.Quaternion Rotation,
                                           NVec LinearVelocity, NVec AngularVelocity, bool Awake, bool Touching, bool Stalled, float Held, float Assist);

    /// <summary>Reads the sled before every collision step. Registered with the physics system by <see cref="Attach"/>, removed
    /// by the run's cleanup.</summary>
    private sealed class StepTrace : PhysicsStepListener
    {
        private static readonly FieldInfo SystemField = typeof(JoltPhysicsBackend).GetField("_system", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo IdsField = typeof(JoltPhysicsBackend).GetField("_joltToRecord", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo BackendField = typeof(JoltScene).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo StalledField = typeof(VehicleController).GetField("_linearMotorStalled", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo HeldField = typeof(JoltPrim).GetField("_vehicleHeldTime", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly BodyID _id;
        private readonly JoltPrim _prim;
        private readonly VehicleController _controller;
        private readonly float _assistK, _gravity;
        public readonly List<TraceRow> Rows = new();
        private double _t;
        private int _update = -1, _step;
        public string Error;

        public PhysicsSystem PhysicsSystem { get; }

        public StepTrace(Run r, JoltPrim prim)
        {
            var backend = (JoltPhysicsBackend)BackendField.GetValue(r.Scene);
            PhysicsSystem = (PhysicsSystem)SystemField.GetValue(backend);
            var records = (System.Collections.IDictionary)IdsField.GetValue(backend);
            uint handle = prim.BodyHandle.Value;
            uint native = 0;
            foreach (System.Collections.DictionaryEntry e in records)
            {
                object rec = e.Value;
                if ((uint)rec.GetType().GetField("Handle").GetValue(rec) == handle)
                    native = (uint)rec.GetType().GetField("NativeBodyId").GetValue(rec);
            }
            if (native == 0)
                throw new InvalidOperationException("sled-drop: the sled's body is not in the backend's records");
            _id = new BodyID(native);
            _prim = prim;
            _controller = prim.VehicleControl;
            _assistK = r.Scene.VehicleSettings.SledAssist;
            _gravity = MathF.Abs(r.Scene.DefaultGravity.Z);
        }

        protected override void OnStep(in PhysicsStepListenerContext context)
        {
            try
            {
                if (context.IsFirstStep)
                {
                    _update++;
                    _step = 0;
                }
                BodyInterface bi = PhysicsSystem.BodyInterfaceNoLock;
                NVec p = bi.GetPosition(_id);
                System.Numerics.Quaternion q = bi.GetRotation(_id);
                bool stalled = (bool)StalledField.GetValue(_controller);
                // The assist's rule (VehicleController.SledAssist), from the pose: nose-down sine, a threshold, then
                // k g sqrt(|sin|), a tenth of it back down the slope when the nose is up. Zero while stalled.
                float noseDown = -NVec.Transform(NVec.UnitX, q).Z;
                float assist = 0f;
                if (!stalled && MathF.Abs(noseDown) > OpenSim.Region.PhysicsModules.Jolt.Vehicles.VehicleLimits.ThresholdDeflectionAngle)
                {
                    assist = _assistK * _gravity * MathF.Sqrt(MathF.Abs(noseDown));
                    if (noseDown < 0f) assist *= -0.1f;
                }
                Rows.Add(new TraceRow(_update, _step++, context.IsLastStep, _t, context.DeltaTime, p, q,
                                      bi.GetLinearVelocity(_id), bi.GetAngularVelocity(_id), bi.IsActive(_id),
                                      _prim.IsColliding, stalled, (float)HeldField.GetValue(_prim), assist));
                _t += context.DeltaTime;
            }
            catch (Exception e)
            {
                Error ??= e.ToString();
            }
        }
    }

    private static void Attach(Run r, JoltPrim prim)
    {
        var trace = new StepTrace(r, prim);
        trace.PhysicsSystem.AddStepListener(trace);
        r.Cleanups.Add(() =>
        {
            trace.PhysicsSystem.RemoveStepListener(trace);
            if (trace.Error != null)
                throw new InvalidOperationException("sled-drop trace: " + trace.Error);
            if (r.Options.TraceDir != null)
                WriteTrace(r, trace.Rows);
            trace.Dispose();
        });
    }

    private static void WriteTrace(Run r, List<TraceRow> rows)
    {
        Directory.CreateDirectory(r.Options.TraceDir);
        string physics = r.Scene.Substepping ? r.Scene.Substeps.RateHz.ToString("0.###", CultureInfo.InvariantCulture) : "0";
        int n = rows.Count > 0 ? rows.Count(x => x.Update == rows[0].Update) : 0;
        string name = $"sled-drop-h{r.Options.RateHz.ToString("0.###", CultureInfo.InvariantCulture)}-p{physics}-n{n}.csv";
        var sb = new StringBuilder("update,step,last,t,dt,x,y,z,qx,qy,qz,qw,vx,vy,vz,wx,wy,wz,speedXY,awake,touching,stalled,held,assist,gap\n");
        foreach (TraceRow w in rows)
        {
            float gap = w.Position.Z - Harness.HalfExtent(new Quaternion(w.Rotation.X, w.Rotation.Y, w.Rotation.Z, w.Rotation.W), SledSize, Vector3.UnitZ)
                        - r.GroundAt(w.Position.X, w.Position.Y);
            sb.Append(CultureInfo.InvariantCulture, $"{w.Update},{w.Step},{(w.Last ? 1 : 0)},{w.T:0.######},{w.Dt:0.######},{w.Position.X:0.#####},{w.Position.Y:0.#####},{w.Position.Z:0.#####},")
              .Append(CultureInfo.InvariantCulture, $"{w.Rotation.X:0.#####},{w.Rotation.Y:0.#####},{w.Rotation.Z:0.#####},{w.Rotation.W:0.#####},")
              .Append(CultureInfo.InvariantCulture, $"{w.LinearVelocity.X:0.#####},{w.LinearVelocity.Y:0.#####},{w.LinearVelocity.Z:0.#####},")
              .Append(CultureInfo.InvariantCulture, $"{w.AngularVelocity.X:0.#####},{w.AngularVelocity.Y:0.#####},{w.AngularVelocity.Z:0.#####},")
              .Append(CultureInfo.InvariantCulture, $"{MathF.Sqrt(w.LinearVelocity.X * w.LinearVelocity.X + w.LinearVelocity.Y * w.LinearVelocity.Y):0.#####},")
              .Append(CultureInfo.InvariantCulture, $"{(w.Awake ? 1 : 0)},{(w.Touching ? 1 : 0)},{(w.Stalled ? 1 : 0)},{w.Held:0.####},{w.Assist:0.#####},{gap:0.#####}\n");
        }
        File.WriteAllText(Path.Combine(r.Options.TraceDir, name), sb.ToString());
    }

    /// <summary>A terrain file: Course.Size x Course.Size little-endian float32, x fastest.</summary>
    public static float[] LoadTerrain(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != Course.Size * Course.Size * sizeof(float))
            throw new ArgumentException($"--terrain: expected {Course.Size * Course.Size * sizeof(float)} bytes, the file has {bytes.Length}");
        var heights = new float[Course.Size * Course.Size];
        Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
        return heights;
    }

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        new()
        {
            Name = "sled-drop",
            Description = "The console test 'jolt sledtest slide': a 2 x 1 x 0.5 m sled turned 20 degrees nose down 1 m over (128, 128), no motor, 4.5 s, with a trace of every collision step (--trace-dir; --terrain for a region's ground).",
            DefaultDuration = _ => 4.5f,
            AllowsTerrainFile = true,
            Setup = r =>
            {
                float nose = NoseDownDegrees * MathF.PI / 180f;
                r.AddBox(SledSize, new Vector3(RezX, RezY, r.GroundAt(RezX, RezY) + RezHeight), Quaternion.CreateFromAxisAngle(Vector3.UnitY, nose));
                r.MakeVehicle(Vehicle.TYPE_SLED);
                r.ApplyVehicleOverrides();
                Attach(r, r.Scene.Prims[Run.ActorLocalId]);
            },
        },
    };
}
