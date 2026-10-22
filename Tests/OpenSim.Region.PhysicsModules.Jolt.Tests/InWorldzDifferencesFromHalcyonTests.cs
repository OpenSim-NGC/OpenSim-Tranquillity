/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Where the InWorldz behaviour of the Jolt module differs on purpose from Halcyon's code, measured three ways: Halcyon's law at
/// Halcyon's own step of 15 ms (the translated models in HalcyonMotorModel.cs and HalcyonAttitudeAndHoverModels.cs), the same law as it
/// would run at the module's step rates (45 Hz and 11 Hz), and the module's current behaviour. The tests print the figures and
/// assert the facts the comparison rests on. Pure (bodies with no engine), parallel.
/// </summary>
public class InWorldzDifferencesFromHalcyonTests
{
    private readonly ITestOutputHelper _out;
    public InWorldzDifferencesFromHalcyonTests(ITestOutputHelper output) => _out = output;

    private const double Deg = Math.PI / 180.0;
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static VehicleSettings InWorldz(bool targetOnUndrivenAxes = false)
        => new() { Behaviour = VehicleBehaviour.InWorldz, MotorTargetOnUndrivenAxes = targetOnUndrivenAxes };

    // =================================================================================================================
    // The motor's target on an axis the motor never had a direction on: a car that drives off a crest.

    private const double CrestX = 40.0, SlopeDeg = 15.0, ContactMu = 0.5;

    private static readonly double SlopeRad = SlopeDeg * Deg;

    private static float GroundZ(float x) => x <= CrestX ? 0f : (float)(-(x - CrestX) * Math.Tan(SlopeRad));
    private static Vector3 GroundNormal(float x) => x <= CrestX ? Vector3.UnitZ : new Vector3((float)Math.Sin(SlopeRad), 0f, (float)Math.Cos(SlopeRad));

    /// <summary>Flat ground to x = 40 m, then a 15 degree slope down: the contact keeps the point on the ground, takes the speed into
    /// it (less a bounce), rubs against the motion with a constant deceleration, and the body is pitched to the slope once its
    /// point is past the crest, in the air as well (a car leaving a crest noses over).</summary>
    private sealed class CrestBody : IVehicleBody
    {
        public Vector3 Position { get; set; }
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(200f, 400f, 500f);
        public Vector3 Gravity => new(0f, 0f, HalcyonMotorModel.Gravity);
        public void SetGravityFactor(float factor) { }
        public bool HasCollision { get; private set; }
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => GroundZ(pos.X);
        public float GetWaterLevel(Vector3 pos) => -100f;

        /// <summary>A landing faster than 1 m/s bounces by this share of its speed into the ground.</summary>
        public double Restitution { get; init; }

        public void Advance(double h)
        {
            Vector3 v = LinearVelocity + Gravity * (float)h;
            Vector3 p = Position + v * (float)h;
            (p, v, Quaternion rot, HasCollision) = CrestContact(p, v, Orientation, h, Restitution);
            Position = p;
            LinearVelocity = v;
            Orientation = rot;
        }
    }

    private static (Vector3 pos, Vector3 vel, Quaternion rot, bool touching) CrestContact(Vector3 p, Vector3 v, Quaternion rot, double h, double restitution)
    {
        float ground = GroundZ(p.X);
        rot = Quaternion.CreateFromEulers(0f, p.X <= CrestX ? 0f : (float)SlopeRad, 0f);
        if (p.Z > ground + 1e-4f)
            return (p, v, rot, false);
        Vector3 n = GroundNormal(p.X);
        float into = Vector3.Dot(v, n);
        if (into < 0f)
            v -= n * (into * (into < -1f ? 1f + (float)restitution : 1f));
        float speed = v.Length();
        if (speed > 0f)
        {
            float slow = (float)(ContactMu * -HalcyonMotorModel.Gravity * n.Z * h);
            v *= Math.Max(0f, speed - slow) / speed;
        }
        p.Z = ground;
        return (p, v, rot, true);
    }

    private static VehicleController NewCar(IVehicleBody body, VehicleSettings settings, Func<DateTime> clock)
    {
        var car = new VehicleController(body) { Settings = settings, Clock = clock };
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        return car;
    }

    private readonly record struct CrestResult(double Peak, double Steady, double FinalX, double MaxAirSpeed);

    private static CrestResult CrestSummary(List<(double t, double speed, double x)> trace, double leftCrestAt)
    {
        double peak = trace.Where(s => s.t > leftCrestAt).Select(s => s.speed).DefaultIfEmpty(0).Max();
        double steady = trace.Where(s => s.t > trace[^1].t - 5.0).Select(s => s.speed).Average();
        return new CrestResult(peak, steady, trace[^1].x, 0);
    }

    /// <summary>Halcyon's linear motor on the crest, stepped at <paramref name="h"/> (15 ms is Halcyon's own step), with the key
    /// held for the whole run and re-sent every 0.1 s.</summary>
    private static CrestResult CrestModel(double h, bool replaceOnUndrivenAxes, double restitution, double duration = 40.0)
    {
        var car = NewCar(new CrestBody(), InWorldz(), () => DateTime.UtcNow);
        HalcyonMotorModel m = VehicleInWorldzMotorTests.ModelOf(car);
        m.ReplaceOnUndrivenAxes = replaceOnUndrivenAxes;
        var body = new CrestBody { Restitution = restitution };
        var trace = new List<(double, double, double)>();
        double now = 0, nextKey = 0, leftCrest = double.NaN;
        while (now < duration)
        {
            if (now >= nextKey - 1e-9) { m.MoveLinear(new Vector3(8f, 0f, 0f)); nextKey += 0.1; }
            HalcyonMotorModel.Changes c = m.Simulate((float)h, body.Position, body.LinearVelocity, Vector3.Zero, body.Orientation, Vector3.Zero);
            body.LinearVelocity += c.Linear;
            body.Advance(h);
            now += h;
            if (double.IsNaN(leftCrest) && body.Position.X > CrestX) leftCrest = now;
            trace.Add((now, body.LinearVelocity.Length(), body.Position.X));
        }
        return CrestSummary(trace, leftCrest);
    }

    /// <summary>The module's controller on the same crest, an engine step of 1 / <paramref name="rate"/> split into
    /// <paramref name="collisionSteps"/> collision steps.</summary>
    private static CrestResult CrestController(double rate, int collisionSteps, bool targetOnUndrivenAxes, double restitution, double duration = 40.0)
    {
        var body = new CrestBody { Restitution = restitution };
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        VehicleController car = NewCar(body, InWorldz(targetOnUndrivenAxes), () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond)));
        var trace = new List<(double, double, double)>();
        double nextKey = 0, leftCrest = double.NaN, step = 1.0 / rate;
        while (now < duration)
        {
            if (now >= nextKey - 1e-9) { car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f)); nextKey += 0.1; }
            car.BeginUpdate((float)step);
            double sub = step / collisionSteps;
            for (int k = 0; k < collisionSteps; k++)
            {
                car.StepOnce((float)sub, body);
                body.Advance(sub);
                now += sub;
            }
            if (double.IsNaN(leftCrest) && body.Position.X > CrestX) leftCrest = now;
            trace.Add((now, body.LinearVelocity.Length(), body.Position.X));
        }
        return CrestSummary(trace, leftCrest);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.275)]
    public void A_car_driving_off_a_crest_with_Halcyons_law_at_its_own_step_and_at_the_modules_steps(double restitution)
    {
        _out.WriteLine($"restitution {restitution}");
        CrestResult a = CrestModel(0.015, true, restitution), aOff = CrestModel(0.015, false, restitution);
        CrestResult b45 = CrestModel(1.0 / 45, true, restitution), b11 = CrestModel(1.0 / 11, true, restitution);
        CrestResult b45Off = CrestModel(1.0 / 45, false, restitution), b11Off = CrestModel(1.0 / 11, false, restitution);
        CrestResult c45 = CrestController(45, 1, false, restitution), c11 = CrestController(11, 6, false, restitution);
        CrestResult c45On = CrestController(45, 1, true, restitution), c11On = CrestController(11, 6, true, restitution);
        void Row(string name, CrestResult r) => _out.WriteLine($"{name,-58} peak {F(r.Peak)}  last 5 s mean {F(r.Steady)}  x at end {r.FinalX:0.0}");
        Row("(a) Halcyon's law as written, 15 ms", a);
        Row("(a) same, rule off on undriven axes, 15 ms", aOff);
        Row("(b) Halcyon's law as written, 45 Hz", b45);
        Row("(b) Halcyon's law as written, 11 Hz", b11);
        Row("(b) rule off on undriven axes, 45 Hz", b45Off);
        Row("(b) rule off on undriven axes, 11 Hz", b11Off);
        Row("(c) module now (rule off), 45 Hz", c45);
        Row("(c) module now (rule off), 11 Hz x 6 collision steps", c11);
        Row("(c) module with VehicleMotorTargetOnUndrivenAxes, 45 Hz", c45On);
        Row("(c) module with VehicleMotorTargetOnUndrivenAxes, 11 Hz", c11On);

        // Halcyon's rule as written throws a car with an 8 m/s motor past 15 m/s as it noses over the crest, at Halcyon's own step;
        // without the rule on an axis the motor never drove it stays near the motor's speed. The module follows the same split
        // by its setting.
        Assert.True(a.Peak > 15.0, $"Halcyon's law as written, 15 ms: peak {a.Peak}");
        Assert.True(aOff.Peak < 10.0, $"the rule off on undriven axes, 15 ms: peak {aOff.Peak}");
        Assert.True(c45On.Peak > 15.0 && c11On.Peak > 15.0, "the module with the rule on");
        Assert.True(c45.Peak < 10.0 && c11.Peak < 10.0, "the module as it ships");
        Assert.InRange(c45.Steady, 7.0, 9.0);
        Assert.InRange(c11.Steady, 7.0, 9.0);
    }

    // =================================================================================================================
    // Banking: the turn rate it builds toward its target, and the threshold on the sum it is part of.

    private static HalcyonAttitudeModel BankModel(double rollDegrees, float efficiency, float mix, float timescale, float speedX)
        => new()
        {
            BankingEfficiency = efficiency, BankingMix = mix, BankingTimescale = timescale, LocalSpeedX = speedX,
            HoldOrientation = true, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(rollDegrees * Deg)),
            ActorAngularDamping = 0f, Type = VehicleType.Airplane,
        };

    private static List<(double t, double w)> BankingHalcyon(double h, double rollDegrees, float efficiency, float mix, float timescale, float speedX, double duration)
    {
        HalcyonAttitudeModel m = BankModel(rollDegrees, efficiency, mix, timescale, speedX);
        var trace = new List<(double, double)>();
        while (m.Time < duration - 1e-9)
        {
            m.Step((float)h);
            trace.Add((m.Time, m.WorldAngularVel.Z));
        }
        return trace;
    }

    private static List<(double t, double w)> BankingController(double rate, int n, double rollDegrees, float efficiency, float mix, float timescale, float speedX, double duration)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, efficiency);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, mix);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, timescale);
        rig.Body.HoldOrientation = true;
        rig.Body.LinearVelocity = new Vector3(speedX, 0f, 0f);
        rig.Tilt(rollDegrees, 0);
        var trace = new List<(double, double)>();
        double t = 0, step = 1.0 / rate;
        while (t < duration - 1e-9)
        {
            rig.StepUpdate(step, n);
            t += step;
            trace.Add((t, rig.Body.AngularVelocity.Z));
        }
        return trace;
    }

    private static double At(List<(double t, double w)> trace, double t)
    {
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].t >= t - 1e-9)
            {
                double f = (t - trace[i - 1].t) / (trace[i].t - trace[i - 1].t);
                return trace[i - 1].w + f * (trace[i].w - trace[i - 1].w);
            }
        return trace[^1].w;
    }

    private static double TimeToShare(List<(double t, double w)> trace, double target, double share)
    {
        foreach (var s in trace)
            if (Math.Abs(s.w) >= share * Math.Abs(target)) return s.t;
        return double.NaN;
    }

    [Theory]
    [InlineData(30.0, 1f, 0f, 1f)]
    [InlineData(30.0, 1f, 0f, 3f)]
    public void The_banking_turn_rate_builds_toward_its_target_three_ways(double roll, float eff, float mix, float ts)
    {
        double target = -Math.Sin(roll * Deg) * eff * Math.PI;             // static banking, no speed term
        var a = BankingHalcyon(0.015, roll, eff, mix, ts, 0f, 20.0);
        var b45 = BankingHalcyon(1.0 / 45, roll, eff, mix, ts, 0f, 20.0);
        var b11 = BankingHalcyon(1.0 / 11, roll, eff, mix, ts, 0f, 20.0);
        var c45 = BankingController(45, 1, roll, eff, mix, ts, 0f, 20.0);
        var c11 = BankingController(11, 6, roll, eff, mix, ts, 0f, 20.0);
        _out.WriteLine($"roll {roll}, efficiency {eff}, timescale {ts}: target {F(target)} rad/s");
        foreach (var (name, trace) in new[] { ("(a) Halcyon 15 ms", a), ("(b) Halcyon 45 Hz", b45), ("(b) Halcyon 11 Hz", b11), ("(c) module 45 Hz", c45), ("(c) module 11 Hz", c11) })
            _out.WriteLine($"{name,-20} 63% at {F(TimeToShare(trace, target, 0.632))} s, 90% at {F(TimeToShare(trace, target, 0.9))} s; rate at 0.5, 1, 2, 4, 8, 20 s: "
                + string.Join(", ", new[] { 0.5, 1, 2, 4, 8, 20 }.Select(t => F(At(trace, t)))));
        // Halcyon's turn builds from a first kick of 8 pi / 512 rad/s and grows by the log of the ratio to the target, so it reaches
        // the target later than an exponential approach would and ends at it (to the dead band of pi / 512). Under InWorldz the
        // module runs that turn on Halcyon's 15 ms clock, so it reaches 63% and 90% when Halcyon's does, at either rate.
        Assert.InRange(Math.Abs(At(a, 20.0) - target), 0.0, 0.01);
        Assert.InRange(Math.Abs(At(c45, 20.0) - target), 0.0, 0.01);
        Assert.InRange(Math.Abs(At(c11, 20.0) - target), 0.0, 0.01);
        foreach (double share in new[] { 0.632, 0.9 })
        {
            double reference = TimeToShare(a, target, share), slack = Math.Max(0.1, 0.02 * reference);
            Assert.InRange(Math.Abs(TimeToShare(c45, target, share) - reference), 0.0, slack);
            Assert.InRange(Math.Abs(TimeToShare(c11, target, share) - reference), 0.0, slack);
        }
    }

    [Fact]
    public void A_gentle_bank_is_held_to_the_dead_band_by_the_sum_Halcyon_thresholds()
    {
        // A roll of 5 degrees with efficiency 0.2: the target is 0.0548 rad/s, and most of Halcyon's per-step changes are smaller
        // than the pi / 512 rad/s (0.0061) below which a torque is not applied.
        const double roll = 5.0;
        const float eff = 0.2f, ts = 1f;
        double target = -Math.Sin(roll * Deg) * eff * Math.PI;
        var a = BankingHalcyon(0.015, roll, eff, 0f, ts, 0f, 20.0);
        var b45 = BankingHalcyon(1.0 / 45, roll, eff, 0f, ts, 0f, 20.0);
        var b11 = BankingHalcyon(1.0 / 11, roll, eff, 0f, ts, 0f, 20.0);
        var c45 = BankingController(45, 1, roll, eff, 0f, ts, 0f, 20.0);
        var c11 = BankingController(11, 6, roll, eff, 0f, ts, 0f, 20.0);
        _out.WriteLine($"target {F(target)} rad/s; dead band {F(Math.PI / 512)} rad/s");
        foreach (var (name, trace) in new[] { ("(a) Halcyon 15 ms", a), ("(b) Halcyon 45 Hz", b45), ("(b) Halcyon 11 Hz", b11), ("(c) module 45 Hz", c45), ("(c) module 11 Hz", c11) })
            _out.WriteLine($"{name,-20} rate at 0.5, 1, 2, 4, 8, 20 s: " + string.Join(", ", new[] { 0.5, 1, 2, 4, 8, 20 }.Select(t => F(At(trace, t)))));
        Assert.InRange(Math.Abs(At(a, 20.0) - target), 0.0, Math.PI / 512 + 1e-3);
        Assert.InRange(Math.Abs(At(c45, 20.0) - target), 0.0, Math.PI / 512 + 1e-3);
        // Halcyon's turn stops at its first kick of 8 pi / 512 rad/s, 0.049: the rest of the way to 0.055 is under the threshold.
        Assert.InRange(Math.Abs(At(a, 20.0)), 0.048, 0.050);
    }

    // =================================================================================================================
    // The attractor's escalation while overturned.

    private sealed record OverturnResult(double TimeTo20, double TimeTo5, double PeakRate, double Swing, double FinalTilt, double MaxFactor = 1);

    private static OverturnResult OverturnSummary(IEnumerable<(double t, double tilt, double rate)> trace)
    {
        double t20 = double.NaN, t5 = double.NaN, peak = 0, swing = 0, final = 0;
        bool past = false;
        foreach (var (t, tilt, rate) in trace)
        {
            if (double.IsNaN(t20) && tilt < 20) t20 = t;
            if (double.IsNaN(t5) && tilt < 5) t5 = t;
            peak = Math.Max(peak, rate);
            if (!double.IsNaN(t5)) past = true;
            if (past) swing = Math.Max(swing, tilt);
            final = tilt;
        }
        return new OverturnResult(t20, t5, peak, swing, final);
    }

    private static double TiltDeg(Quaternion q) => Math.Acos(Math.Clamp((Vector3.UnitZ * q).Z, -1f, 1f)) / Deg;

    private static OverturnResult OverturnHalcyon(double h, double rollDegrees, float timescale, float efficiency, double duration)
    {
        var m = new HalcyonAttitudeModel
        {
            Type = VehicleType.Car, AttractTimescale = timescale, AttractEfficiency = efficiency,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(rollDegrees * Deg)),
        };
        var trace = new List<(double, double, double)>();
        while (m.Time < duration - 1e-9)
        {
            m.Step((float)h);
            trace.Add((m.Time, TiltDeg(m.Rotation), m.WorldAngularVel.Length()));
        }
        return OverturnSummary(trace) with { MaxFactor = m.MaxVerticalForceAdjust };
    }

    private static OverturnResult OverturnController(double rate, int n, double rollDegrees, float timescale, float efficiency, double duration)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, timescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, efficiency);
        rig.Tilt(rollDegrees, 0);
        var trace = new List<(double, double, double)>();
        double t = 0, step = 1.0 / rate;
        while (t < duration - 1e-9)
        {
            rig.StepUpdate(step, n);
            t += step;
            trace.Add((t, TiltDeg(rig.Body.Orientation), rig.Body.AngularVelocity.Length()));
        }
        return OverturnSummary(trace);
    }

    [Theory]
    [InlineData(179.99)]
    [InlineData(179.9)]
    [InlineData(179.5)]
    [InlineData(175.0)]
    [InlineData(150.0)]
    public void An_overturned_car_with_the_attractor_escalating_without_a_cap_and_with_one(double rollDegrees)
    {
        const float ts = 1.5f, eff = 0.6f;
        OverturnResult a = OverturnHalcyon(0.015, rollDegrees, ts, eff, 15.0);
        OverturnResult b45 = OverturnHalcyon(1.0 / 45, rollDegrees, ts, eff, 15.0), b11 = OverturnHalcyon(1.0 / 11, rollDegrees, ts, eff, 15.0);
        OverturnResult c45 = OverturnController(45, 1, rollDegrees, ts, eff, 15.0), c11 = OverturnController(11, 6, rollDegrees, ts, eff, 15.0);
        _out.WriteLine($"car upside down at {rollDegrees} degrees, attractor {ts} s / {eff}");
        foreach (var (name, r) in new[] { ("(a) Halcyon 15 ms, no cap", a), ("(b) Halcyon 45 Hz", b45), ("(b) Halcyon 11 Hz", b11), ("(c) module 45 Hz, cap 10", c45), ("(c) module 11 Hz, cap 10", c11) })
            _out.WriteLine($"{name,-26} within 20 deg at {F(r.TimeTo20)} s, within 5 deg at {F(r.TimeTo5)} s, peak rate {F(r.PeakRate)} rad/s, largest tilt after that {F(r.Swing)} deg, tilt at 15 s {F(r.FinalTilt)}, largest strength factor {F(r.MaxFactor)}");
        Assert.True(a.TimeTo5 < 4.0 && c45.TimeTo5 < 4.0 && c11.TimeTo5 < 4.0, "Halcyon's law at 15 ms and the module right the car");
        Assert.True(a.Swing < 10.0 && c45.Swing < 10.0 && c11.Swing < 10.0, "neither swings past upright by more than 10 degrees");
        // Halcyon's per-step growth of the strength, taken once for each step of a slower engine, swings the car far past upright.
        Assert.True(b11.Swing > 15.0, $"Halcyon's law per step at 11 Hz: swing {b11.Swing}");
    }

    [Fact]
    public void An_overturned_car_held_at_its_angle_has_its_strength_escalate_without_a_cap_in_Halcyon_and_with_one_in_the_module()
    {
        // Held against something (the body is not turned), the car stays overturned and "makes no progress", so the strength factor
        // keeps growing by 1.3 a step (VehicleDynamics.cs:1286-1296). A float holds up to 3.4e38.
        var m = new HalcyonAttitudeModel
        {
            Type = VehicleType.Car, AttractTimescale = 1.5f, AttractEfficiency = 0.6f, HoldOrientation = true,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(179.9 * Deg)),
        };
        double reachedFloatMax = double.NaN;
        var rows = new List<string>();
        while (m.Time < 10.0 - 1e-9)
        {
            m.Step(0.015f);
            if (double.IsNaN(reachedFloatMax) && (float.IsInfinity(m.VerticalForceAdjust) || float.IsNaN(m.WorldAngularVel.X)))
                reachedFloatMax = m.Time;
            if (Math.Abs(m.Time - Math.Round(m.Time)) < 0.0076 && m.Time >= 0.9)
                rows.Add($"{m.Time:0} s: factor {m.VerticalForceAdjust:G3}");
        }
        _out.WriteLine("Halcyon at 15 ms, car held at 179.9 degrees: " + string.Join(", ", rows) + $"; infinite from {F(reachedFloatMax)} s");
        _out.WriteLine($"the module: strength factor capped at {VehicleController.MaxAttractorEscalation}");
        Assert.False(double.IsNaN(reachedFloatMax), "Halcyon's escalation reaches the end of the float range while the car is held");
        Assert.InRange(reachedFloatMax, 4.0, 6.0);
    }

    // =================================================================================================================
    // The roll rate the attractor sets, with banking turning the vehicle about the vertical: an airplane banked 30 degrees.

    private readonly record struct Attitude(double Roll, double Pitch);

    private static List<(double t, Attitude a)> AirplaneHalcyon(double h, double duration)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);
        VehicleController c = rig.Car;
        var m = new HalcyonAttitudeModel
        {
            Type = VehicleType.Airplane,
            LimitRollOnly = (c.Flags & ExtendedVehicleFlags.LimitRollOnly) != 0,
            AttractTimescale = c.GetFloatParam(VehFloatParam.VerticalAttractionTimescale),
            AttractEfficiency = c.GetFloatParam(VehFloatParam.VerticalAttractionEfficiency),
            BankingEfficiency = c.GetFloatParam(VehFloatParam.BankingEfficiency),
            BankingMix = c.GetFloatParam(VehFloatParam.BankingMix),
            BankingTimescale = c.GetFloatParam(VehFloatParam.BankingTimescale),
            BankingAzimuth = c.GetFloatParam(VehFloatParam.BankingAzimuth),
            InvertedBankingModifier = c.GetFloatParam(VehFloatParam.InvertedBankingModifier),
            AngularFrictionTimescale = c.GetVecParam(VehVectorParam.AngularFrictionTimescale),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(30 * Deg)),
        };
        var trace = new List<(double, Attitude)>();
        while (m.Time < duration - 1e-9)
        {
            m.Step((float)h);
            (double roll, double pitch) = BehaviourRig.RollAndPitch(m.Rotation);
            trace.Add((m.Time, new Attitude(roll / Deg, pitch / Deg)));
        }
        return trace;
    }

    private static List<(double t, Attitude a)> AirplaneController(double rate, int n, double duration)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);
        rig.Tilt(30, 0);
        var trace = new List<(double, Attitude)>();
        double t = 0, step = 1.0 / rate;
        while (t < duration - 1e-9)
        {
            rig.StepUpdate(step, n);
            t += step;
            (double roll, double pitch) = BehaviourRig.RollAndPitch(rig.Body.Orientation);
            trace.Add((t, new Attitude(roll / Deg, pitch / Deg)));
        }
        return trace;
    }

    [Fact]
    public void A_banking_airplane_keeps_its_pitch_with_Halcyons_attractor_at_15_ms_and_in_the_module()
    {
        const double duration = 12.0;
        var a = AirplaneHalcyon(0.015, duration);
        var b45 = AirplaneHalcyon(1.0 / 45, duration);
        var b11 = AirplaneHalcyon(1.0 / 11, duration);
        var c45 = AirplaneController(45, 1, duration);
        var c11 = AirplaneController(11, 6, duration);
        foreach (var (name, trace) in new[] { ("(a) Halcyon 15 ms", a), ("(b) Halcyon 45 Hz", b45), ("(b) Halcyon 11 Hz", b11), ("(c) module 45 Hz", c45), ("(c) module 11 Hz", c11) })
        {
            Attitude AttAt(double t) => trace.First(s => s.t >= t - 1e-9).a;
            _out.WriteLine($"{name,-20} roll/pitch at 1, 3, 6, 12 s: " + string.Join("  ", new[] { 1.0, 3, 6, 12 }.Select(t => $"{AttAt(t).Roll:0.0}/{AttAt(t).Pitch:0.0}"))
                + $"; largest |pitch| {trace.Max(s => Math.Abs(s.a.Pitch)):0.00} deg");
        }
        double worstA = a.Max(s => Math.Abs(s.a.Pitch)), worstC = c45.Max(s => Math.Abs(s.a.Pitch));
        Assert.True(worstC <= 2.5, "the module keeps the pitch within 2.5 degrees (its pinned limit)");
        Assert.True(worstA < 4.0, $"Halcyon's law at 15 ms keeps the pitch within 4 degrees: {worstA}");
        Assert.True(Math.Abs(worstA - worstC) < 1.5, $"the two agree to 1.5 degrees: {worstA} against {worstC}");
    }

    // =================================================================================================================
    // Vehicle hover.

    private sealed record HoverResult(double Undershoot, double SettleTime, double At5, double At10, double At20, double Error20);

    private static HoverResult HoverSummary(List<(double t, double z)> trace, double height)
    {
        double under = trace.Min(s => s.z - height);
        double settle = double.NaN;
        for (int i = trace.Count - 1; i >= 0; i--)
            if (Math.Abs(trace[i].z - height) > 0.1) { settle = i + 1 < trace.Count ? trace[i + 1].t : double.NaN; break; }
        if (double.IsNaN(settle) && trace.All(s => Math.Abs(s.z - height) <= 0.1)) settle = 0;
        double Z(double t) => trace.First(s => s.t >= t - 1e-9).z;
        return new HoverResult(under, settle, Z(5), Z(10), Z(20), trace[^1].z - height);
    }

    private static HoverResult HoverHalcyon(double h, float tau, float eff)
    {
        var m = new HalcyonHoverModel { Height = 5f, Tau = tau, Damping = eff, Z = 8f };
        var trace = new List<(double, double)>();
        double t = 0;
        while (t < 20.0 - 1e-9) { m.Step((float)h); t += h; trace.Add((t, m.Z)); }
        return HoverSummary(trace, 5.0);
    }

    private static HoverResult HoverController(double rate, float tau, float eff)
    {
        // A balloon: a car under InWorldz in this rig, with nothing under it, does not move at all (its rest rule holds it).
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_BALLOON);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, tau);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, eff);
        rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
        rig.Body.Position = new Vector3(128f, 128f, 8f);
        var trace = new List<(double, double)>();
        double t = 0, step = 1.0 / rate;
        while (t < 20.0 - 1e-9) { rig.Run(step, 1); t += step; trace.Add((t, rig.Body.Position.Z)); }
        return HoverSummary(trace, 5.0);
    }

    [Theory]
    [InlineData(1f, 0.2f)]
    [InlineData(1f, 0.8f)]
    [InlineData(3f, 0.2f)]
    [InlineData(3f, 0.8f)]
    [InlineData(0.3f, 0.5f)]
    public void A_vehicle_released_3_m_above_its_hover_height_with_Halcyons_hover_and_the_modules(float tau, float eff)
    {
        HoverResult a = HoverHalcyon(0.015, tau, eff), b45 = HoverHalcyon(1.0 / 45, tau, eff), b11 = HoverHalcyon(1.0 / 11, tau, eff);
        HoverResult c45 = HoverController(45, tau, eff), c11 = HoverController(11, tau, eff);
        _out.WriteLine($"hover height 5 m, released at 8 m, timescale {tau} s, efficiency {eff}");
        foreach (var (name, r) in new[] { ("(a) Halcyon 15 ms", a), ("(b) Halcyon 45 Hz", b45), ("(b) Halcyon 11 Hz", b11), ("(c) module 45 Hz", c45), ("(c) module 11 Hz (one step)", c11) })
            _out.WriteLine($"{name,-20} lowest {F(r.Undershoot)} m from the height, within 0.1 m from {F(r.SettleTime)} s; z at 5, 10, 20 s: {F(r.At5)}, {F(r.At10)}, {F(r.At20)}");
        Assert.True(Math.Abs(a.Error20) < 0.1, $"Halcyon's hover holds the height: {a.Error20}");
        Assert.True(Math.Abs(a.At10 - b45.At10) < 0.1 && Math.Abs(a.At20 - b45.At20) < 0.1, "Halcyon's hover is the same at 15 ms and at 45 Hz");
    }

    // =================================================================================================================
    // The down ray of the motor-height rule.

    private static double CastsPerSecondController(double rate, int n)
    {
        var rig = new BehaviourRig(InWorldz(), Vehicle.TYPE_CAR, gravity: false);
        rig.Body.BoundsHalfSize = new Vector3(1f, 1f, 0.5f);
        rig.Body.RayFloorZ = 95f;
        rig.Car.ProcessFloatVehicleParam((Vehicle)11004, 1f);
        rig.Car.ProcessFloatVehicleParam((Vehicle)11005, 1000f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 6f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(20f, 0f, 0f));
        rig.Body.LinearVelocity = new Vector3(3f, 0f, 0f);
        int updates = (int)Math.Round(10 * rate);
        for (int i = 0; i < updates; i++) rig.StepUpdate(1.0 / rate, n);
        return rig.Body.RaysCast / 2.0 / (updates / rate);       // two rays a cast: the left edge finds nothing near, then the right
    }

    [Fact]
    public void The_down_ray_is_cast_every_eight_frames_of_15_ms_in_Halcyon_and_every_0_12_s_in_the_module()
    {
        double a = 1.0 / (8 * 0.015), b45 = 1.0 / (8 / 45.0), b11 = 1.0 / (8 / 11.0);
        double c45 = CastsPerSecondController(45, 1), c11 = CastsPerSecondController(11, 6), c90 = CastsPerSecondController(90, 2);
        _out.WriteLine($"casts a second: (a) Halcyon 15 ms {F(a)}; (b) every 8th frame at 45 Hz {F(b45)}, at 11 Hz {F(b11)}; (c) module at 45 Hz {F(c45)}, 11 Hz {F(c11)}, 90 Hz {F(c90)}");
        Assert.InRange(c45, a * 0.95, a * 1.05);
        Assert.InRange(c11, a * 0.95, a * 1.05);
        Assert.InRange(c90, a * 0.95, a * 1.05);
    }

    // =================================================================================================================
    // A sled's contact friction: Halcyon zeroes the root's material and every child receives a copy of a preset.

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void A_sled_on_a_15_degree_slope_with_a_frictionless_root_and_Stone_children_or_no_friction_at_all(int children)
    {
        // Halcyon's sled path (VehicleDynamics.cs:1612-1618) gives the root a material of friction 0 / 0 and calls SetMaterialSync
        // for the whole object, which hands each child material.Duplicate (PhysxPrim.cs:3050-3058). That material's preset field was
        // never set, so it holds 0, which is the Stone preset (Material.cs:46, 118, 252-256): friction 0.5 static, 0.35 dynamic.
        // Each of the (children + 1) parts is taken to bear an equal share of the weight on the slope.
        const double g = 9.8, staticStone = 0.5, dynamicStone = 0.35, assist = 0.0;
        double share = children / (children + 1.0), slope = SlopeRad;
        double pull = g * Math.Sin(slope) + assist;
        double rub = share * dynamicStone * g * Math.Cos(slope);
        bool starts = pull > share * staticStone * g * Math.Cos(slope);
        double accel = starts ? pull - rub : 0;
        double v4 = accel * 4;
        // The module: every part of a sled has the sled friction of 0.
        double moduleAccel = pull, moduleV4 = moduleAccel * 4;
        _out.WriteLine($"{children} child parts: Halcyon as written starts: {starts}, acceleration {F(accel)} m/s^2, speed after 4 s {F(v4)} m/s; module {F(moduleAccel)} m/s^2, {F(moduleV4)} m/s");
        if (children == 0) Assert.Equal(moduleAccel, accel, 6);
        if (children >= 3) Assert.False(starts && accel > 0.5 * moduleAccel, "a linkset sled with three or more children is held back to under half the frictionless acceleration or does not start");
    }

    // =================================================================================================================
    // A move target when the object crosses a region border.

    [Fact]
    public void A_move_target_across_a_border_is_shifted_by_Halcyon_only_when_it_lies_outside_the_source_region()
    {
        // An object crossing east out of region A, whose coordinates become region B's (x less 256). The same place in the world
        // is the target less 256 on x, wherever the target lies; that is what the module's shift does.
        float[] targets = { -10f, 0f, 100f, 200f, 250f, 255.9f, 256f, 300f, 400f };
        int same = 0, differ = 0;
        foreach (float tx in targets)
        {
            Vector3 world = new(tx - 256f, 40f, 30f);
            Vector3 halcyon = HalcyonCrossing.ShiftTarget(new Vector3(tx, 40f, 30f));
            bool agrees = Math.Abs(halcyon.X - world.X) < 1e-3 && halcyon.Y == world.Y;
            if (agrees) same++; else differ++;
            _out.WriteLine($"target x {tx,7:0.0} in A: the same place in B is {world.X,7:0.0}; Halcyon gives {halcyon.X,7:0.0}{(agrees ? "" : $"  ({Math.Abs(halcyon.X - world.X):0} m away)")}");
        }
        Assert.Equal(3, same);        // 256, 300, 400: a target already beyond the border
        Assert.Equal(targets.Length - 3, differ);
    }

    // =================================================================================================================
    // The size limits of a physical object: which objects are refused.

    [Fact]
    public void The_module_refuses_exactly_the_objects_Halcyon_makes_kinematic()
    {
        float[] sizes = { 0.001f, 0.008f, 0.009f, 0.01f, 0.5f, 10f, 127f, 128f, 129f, 255f, 256f, 257f, 300f };
        int cases = 0, mismatches = 0, refused = 0;
        foreach (float x in sizes)
            foreach (float y in sizes)
                foreach (float z in sizes)
                {
                    bool halcyon = HalcyonSizeChecks.MakesKinematic(x, y, z);
                    bool module = HalcyonPhysicalLimits.Check(x, y, z) != HalcyonPhysicalLimits.Verdict.Ok;
                    cases++;
                    if (halcyon) refused++;
                    if (halcyon != module) mismatches++;
                }
        _out.WriteLine($"{cases} sizes, {refused} refused by Halcyon, {mismatches} differences with the module");
        Assert.Equal(0, mismatches);
    }
}
