/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A body and controller rig for the vehicle behaviour tests: a body with no contacts that takes the gravity share the
/// controller sets over each step, keeps the velocity the controller gives it and turns by its angular velocity, and a
/// simulated clock. Pure: no engine.
/// </summary>
internal sealed class BehaviourBody : IVehicleBody
{
    public Vector3 Position { get; set; } = new(128f, 128f, 100f);
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public float Mass => 1000f;
    public Vector3 InertiaDiagonal => new(200f, 400f, 500f);
    public Vector3 Gravity { get; set; } = new(0f, 0f, -9.80665f);
    public float GravityFactor { get; private set; } = 1f;

    /// <summary>The body's gravity multiplier (llSetPhysicsMaterial GRAVITY_MULTIPLIER).</summary>
    public float GravityMultiplier { get; set; } = 1f;
    public void SetGravityFactor(float factor) => GravityFactor = factor;
    public bool HasCollision => !float.IsNaN(ContactMu);

    /// <summary>A plane under the body, tilted <see cref="SlopeRad"/> about y (down the way the body's x axis points), that holds
    /// it and rubs against its motion with a constant deceleration of mu times the normal part of gravity, to a stop at most;
    /// NaN (the default) is no plane and no contact.</summary>
    public float ContactMu { get; set; } = float.NaN;
    public double SlopeRad { get; set; }
    public void AddForce(Vector3 force) { }
    public void AddTorque(Vector3 torque) { }
    public void KeepAwake() { }
    public float GetTerrainHeight(Vector3 pos) => 0f;
    public float GetWaterLevel(Vector3 pos) => WaterLevel;

    /// <summary>True: the body moves but does not turn, whatever its angular velocity (a vehicle held at its attitude).</summary>
    public bool HoldOrientation { get; set; }

    /// <summary>The water's height (far below the body by default).</summary>
    public float WaterLevel { get; set; } = -100f;

    /// <summary>The wind the region's wind module gives at a position; null (the default) is a region with no wind module.</summary>
    public Func<Vector3, Vector3> WindAt { get; set; }

    /// <summary>The times the controller asked for the wind.</summary>
    public int WindReads { get; private set; }

    public bool TryGetWind(Vector3 position, out Vector3 wind)
    {
        WindReads++;
        wind = WindAt == null ? Vector3.Zero : WindAt(position);
        return WindAt != null;
    }

    /// <summary>Half the size of the body's box; zero (the default) means the body has none, so the motor-height rule never measures.</summary>
    public Vector3 BoundsHalfSize { get; set; }

    /// <summary>The height of a flat floor the down rays hit (NaN, the default: nothing is hit).</summary>
    public float RayFloorZ { get; set; } = float.NaN;

    /// <summary>The rays cast so far.</summary>
    public int RaysCast { get; private set; }

    public bool TryGetBounds(out Vector3 center, out Vector3 halfSize)
    {
        center = Vector3.Zero;
        halfSize = BoundsHalfSize;
        return halfSize.X > 0f && halfSize.Y > 0f && halfSize.Z > 0f;
    }

    public bool CastRayNearest(Vector3 origin, Vector3 direction, float maxDistance, out float distance)
    {
        RaysCast++;
        distance = 0f;
        if (float.IsNaN(RayFloorZ) || direction.Z >= 0f)
            return false;
        float t = (RayFloorZ - origin.Z) / direction.Z * direction.Length();
        if (t < 0f || t > maxDistance)
            return false;
        distance = t;
        return true;
    }

    /// <summary>The engine's step for this body: the gravity share over h, a move by the velocity it then has, and a
    /// turn by the angular velocity (done in System.Numerics, whose axis-angle rotation keeps the small turns of a
    /// short step).</summary>
    public void Advance(double h)
    {
        LinearVelocity += Gravity * (GravityFactor * (float)h);
        if (!float.IsNaN(ContactMu))
        {
            var normal = new Vector3((float)Math.Sin(SlopeRad), 0f, (float)Math.Cos(SlopeRad));
            LinearVelocity -= normal * Vector3.Dot(LinearVelocity, normal);
            float along = LinearVelocity.Length();
            if (along > 0f)
            {
                float slow = (float)(ContactMu * -Gravity.Z * Math.Cos(SlopeRad) * h);
                LinearVelocity *= Math.Max(0f, along - slow) / along;
            }
        }
        Position += LinearVelocity * (float)h;
        if (HoldOrientation)
            return;
        Vector3 w = AngularVelocity;
        float speed = w.Length();
        if (speed <= 0f)
            return;
        var turn = System.Numerics.Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(w.X, w.Y, w.Z) / speed, (float)(speed * h));
        var now = new System.Numerics.Quaternion(Orientation.X, Orientation.Y, Orientation.Z, Orientation.W);
        var next = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(now, turn));
        Orientation = new Quaternion(next.X, next.Y, next.Z, next.W);
    }
}

internal sealed class BehaviourRig
{
    private const double Deg = Math.PI / 180.0;

    public readonly BehaviourBody Body = new();
    public readonly VehicleController Car;
    private double _now;

    public BehaviourRig(VehicleSettings settings, Vehicle type, bool gravity = true)
    {
        Car = new VehicleController(Body) { Settings = settings };
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Car.Clock = () => t0.AddTicks((long)(_now * TimeSpan.TicksPerSecond));
        Car.ProcessTypeChange(type);
        if (!gravity)
            Body.Gravity = Vector3.Zero;
        // Only what a test sets acts: no attractor, banking, deflection, hover or friction, no buoyancy, no motor-up limit.
        Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
        Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);
        Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 0f);
        Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f));
        Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f));
        Car.ProcessVehicleFlags((int)ExtendedVehicleFlags.LimitMotorUp, true);
    }

    public void Flag(ExtendedVehicleFlags flag, bool on) => Car.ProcessVehicleFlags((int)flag, !on);

    public void Step(double h)
    {
        Car.Step((float)h);
        Body.Advance(h);
        _now += h;
    }

    public void Run(double h, int steps)
    {
        for (int i = 0; i < steps; i++)
            Step(h);
    }

    /// <summary>One engine step of <paramref name="h"/> seconds split into <paramref name="collisionSteps"/> collision steps, as
    /// the engine's step listener runs the controller: the Update-rate part once, then the per-step part before each collision
    /// step, the body advancing by the collision step between them.</summary>
    public void StepUpdate(double h, int collisionSteps)
    {
        Car.BeginUpdate((float)h);
        double sub = h / collisionSteps;
        for (int i = 0; i < collisionSteps; i++)
        {
            Car.StepOnce((float)sub, Body);
            Body.Advance(sub);
            _now += sub;
        }
    }

    /// <summary>A body rolled about its own x axis, then pitched about the world's y (the nose down).</summary>
    public void Tilt(double rollDegrees, double pitchDegrees)
        => Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(pitchDegrees * Deg)) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(rollDegrees * Deg));

    public static (double roll, double pitch) RollAndPitch(Quaternion q)
    {
        Vector3 x = Vector3.UnitX * q, y = Vector3.UnitY * q, z = Vector3.UnitZ * q;
        return (Math.Atan2(y.Z, z.Z), Math.Asin(Math.Clamp(-x.Z, -1f, 1f)));
    }

    public float[] State() => new[]
    {
        Body.Orientation.X, Body.Orientation.Y, Body.Orientation.Z, Body.Orientation.W,
        Body.AngularVelocity.X, Body.AngularVelocity.Y, Body.AngularVelocity.Z,
        Body.LinearVelocity.X, Body.LinearVelocity.Y, Body.LinearVelocity.Z,
        Body.Position.Z, Body.GravityFactor,
    };

    // ---------------------------------------------------------------------------------------------------------
    // One scenario for each behaviour, run with whatever settings are handed in: the Second Life choice's answers are
    // fixed bit for bit in VehicleInWorldzTests.

    /// <summary>LIMIT_ROLL_ONLY car, rolled 20 and pitched 30 degrees, attractor 1 s / 1, 2 s at 45 Hz.</summary>
    public static float[] RollOnlyCar(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
        rig.Flag(ExtendedVehicleFlags.LimitRollOnly, true);
        rig.Tilt(20, 30);
        rig.Run(1.0 / 45, 90);
        return rig.State();
    }

    /// <summary>A buoyant balloon with HOVER_UP_ONLY (global height 5 m, 2 s, efficiency 0.8) released 3 m above it, 1 s at 45 Hz.</summary>
    public static float[] UpOnlyBalloon(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_BALLOON);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.8f);
        rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
        rig.Flag(ExtendedVehicleFlags.HoverUpOnly, true);
        rig.Body.Position = new Vector3(128f, 128f, 8f);
        rig.Run(1.0 / 45, 45);
        return rig.State();
    }

    /// <summary>A car falling from rest with a 10 s friction on its z axis, 2 s at 45 Hz.</summary>
    public static float[] FallingCar(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 10f));
        rig.Run(1.0 / 45, 90);
        return rig.State();
    }

    /// <summary>The angular motor set to 1 rad/s about z with a 0.2 s motor timescale and a 0.05 s decay, 0.2 s at 90 Hz.</summary>
    public static float[] ShortDecayMotor(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 0.05f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        rig.Run(1.0 / 90, 18);
        return rig.State();
    }

    /// <summary>A car on its back (rolled 150 degrees), attractor 1.5 s / 0.6, 2 s at 45 Hz.</summary>
    public static float[] OverturnedCar(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.6f);
        rig.Tilt(150, 0);
        rig.Run(1.0 / 45, 90);
        return rig.State();
    }

    /// <summary>A balloon with VEHICLE_BUOYANCY 0.98 falling from rest, 2/3 s at 45 Hz.</summary>
    public static float[] NearlyBuoyantBalloon(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_BALLOON);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 0.98f);
        rig.Run(1.0 / 45, 30);
        return rig.State();
    }
}
