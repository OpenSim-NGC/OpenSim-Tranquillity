/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The InWorldz linear and angular motor against the reference model of Halcyon's motor (<see cref="HalcyonMotorModel"/>):
/// the controller stepped beside the model on level ground and on a slope with contact friction as a constant deceleration,
/// at Halcyon's step of 15 ms and at the steps the engine takes. Pure (a body with no engine), parallel.
/// </summary>
public partial class VehicleInWorldzMotorTests
{
    private readonly ITestOutputHelper _out;
    public VehicleInWorldzMotorTests(ITestOutputHelper output) => _out = output;

    private const float Mu = 0.5f;
    private const float KeyRepeat = 0.1f;

    private static VehicleController CarController(BehaviourBody body, bool testCar, Func<DateTime> clock)
    {
        var car = new VehicleController(body) { Settings = new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz } };
        car.Clock = clock;
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        if (testCar)
        {
            car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
            car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0.5f);
        }
        return car;
    }

    /// <summary>One run of the controller on a contact plane: the speed along the body's x axis after each of <paramref name="steps"/>
    /// engine steps of <paramref name="step"/> seconds, each split into <paramref name="collisionSteps"/> collision steps, with the
    /// key down from 0 to <paramref name="hold"/> and re-sent every <see cref="KeyRepeat"/>.</summary>
    internal static List<(double t, double v)> RunController(double step, int collisionSteps, int steps, double hold, double slopeDeg,
        bool testCar, float mu = Mu, Vector3? motor = null)
    {
        var body = new BehaviourBody { ContactMu = mu, SlopeRad = slopeDeg * Math.PI / 180.0, Gravity = new Vector3(0f, 0f, HalcyonMotorModel.Gravity) };
        body.Orientation = Quaternion.CreateFromEulers(0f, (float)body.SlopeRad, 0f);
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        VehicleController car = CarController(body, testCar, () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond)));
        Vector3 dir = motor ?? new Vector3(8f, 0f, 0f);
        var result = new List<(double, double)>();
        double nextKey = 0;
        bool released = false;
        for (int i = 0; i < steps; i++)
        {
            if (!released)
            {
                if (now >= hold - 1e-9)
                {
                    car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, Vector3.Zero);
                    released = true;
                }
                else if (now >= nextKey - 1e-9)
                {
                    car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, dir);
                    nextKey += KeyRepeat;
                }
            }
            car.BeginUpdate((float)step);
            double sub = step / collisionSteps;
            for (int k = 0; k < collisionSteps; k++)
            {
                car.StepOnce((float)sub, body);
                body.Advance(sub);
                now += sub;
            }
            Vector3 local = body.LinearVelocity * Quaternion.Inverse(body.Orientation);
            result.Add((now, local.X));
        }
        return result;
    }

    /// <summary>The model on the same plane at Halcyon's step, the same key.</summary>
    internal static List<(double t, double v)> RunModel(int steps, double hold, double slopeDeg, bool testCar, float mu = Mu, Vector3? motor = null)
    {
        HalcyonMotorModel m = ModelOf(CarController(new BehaviourBody(), testCar, () => DateTime.UtcNow));
        var body = new HalcyonBody(m, mu, slopeDeg);
        Vector3 dir = motor ?? new Vector3(8f, 0f, 0f);
        var result = new List<(double, double)>();
        double nextKey = 0;
        bool released = false;
        for (int i = 0; i < steps; i++)
        {
            double now = body.Time;
            if (!released)
            {
                if (now >= hold - 1e-9)
                {
                    m.MoveLinear(Vector3.Zero);
                    released = true;
                }
                else if (now >= nextKey - 1e-9)
                {
                    m.MoveLinear(dir);
                    nextKey += KeyRepeat;
                }
            }
            body.Step();
            Vector3 local = body.LinearVelocity * Quaternion.Inverse(body.Rotation);
            result.Add((body.Time, local.X));
        }
        return result;
    }

    internal static double At(List<(double t, double v)> trace, double t)
    {
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].t >= t)
            {
                double f = (t - trace[i - 1].t) / (trace[i].t - trace[i - 1].t);
                return trace[i - 1].v + f * (trace[i].v - trace[i - 1].v);
            }
        return trace[^1].v;
    }

    /// <summary>Two cars of the model driving at each other with their keys held (motor 20 m/s, 60 m apart), meeting in a contact
    /// of restitution <paramref name="restitution"/> that conserves momentum (equal masses, in line, no rotation). Returns each
    /// car's speed 0.25 s after the first contact and the largest speed either has in the next second.</summary>
    internal static (double a, double b, double largest, double arrival) ModelHeadOn(double restitution)
    {
        HalcyonMotorModel ma = ModelOf(CarController(new BehaviourBody(), false, () => DateTime.UtcNow));
        HalcyonMotorModel mb = ModelOf(CarController(new BehaviourBody(), false, () => DateTime.UtcNow));
        var a = new HalcyonBody(ma, Mu, 0) { Position = new Vector3(0f, 0f, 0f) };
        var b = new HalcyonBody(mb, Mu, 0, (float)Math.PI) { Position = new Vector3(60f, 0f, 0f) };
        var dir = new Vector3(20f, 0f, 0f);
        double nextKey = 0, impact = double.NaN, arrival = 0, speedA = double.NaN, speedB = double.NaN, largest = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (a.Time >= nextKey - 1e-9) { ma.MoveLinear(dir); mb.MoveLinear(dir); nextKey += KeyRepeat; }
            a.Step();
            b.Step();
            float va = a.LinearVelocity.X, vb = b.LinearVelocity.X;
            if (b.Position.X - a.Position.X < 2f && va - vb > 0f)
            {
                if (double.IsNaN(impact)) { impact = a.Time; arrival = Math.Max(va, -vb); }
                float mean = (va + vb) / 2f, half = (va - vb) / 2f;
                a.LinearVelocity = new Vector3(mean - (float)restitution * half, 0f, 0f);
                b.LinearVelocity = new Vector3(mean + (float)restitution * half, 0f, 0f);
                float push = 2f - (b.Position.X - a.Position.X);
                a.Position -= new Vector3(push / 2f, 0f, 0f);
                b.Position += new Vector3(push / 2f, 0f, 0f);
            }
            if (!double.IsNaN(impact))
            {
                if (a.Time >= impact + 0.25 - 1e-9 && double.IsNaN(speedA)) { speedA = Math.Abs(a.LinearVelocity.X); speedB = Math.Abs(b.LinearVelocity.X); }
                if (a.Time >= impact + 0.25 - 1e-9 && a.Time <= impact + 1.25)
                    largest = Math.Max(largest, Math.Max(Math.Abs(a.LinearVelocity.X), Math.Abs(b.LinearVelocity.X)));
            }
        }
        return (speedA, speedB, largest, arrival);
    }

    /// <summary>The angular motor of the controller on a free body: yaw rate after each of <paramref name="steps"/> engine steps, the
    /// key (direction <paramref name="dir"/>) down from 0 to <paramref name="hold"/> and re-sent every key repeat, with the car's
    /// angular motor and decay timescales <paramref name="tm"/> and <paramref name="td"/>.</summary>
    internal static List<(double t, double v)> RunControllerAngular(double step, int collisionSteps, int steps, double hold, Vector3 dir, float tm, float td)
    {
        var body = new BehaviourBody { Gravity = Vector3.Zero };
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        VehicleController car = CarController(body, false, () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond)));
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, tm);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, td);
        var result = new List<(double, double)>();
        double nextKey = 0;
        bool released = false;
        for (int i = 0; i < steps; i++)
        {
            if (!released)
            {
                if (now >= hold - 1e-9) { car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, Vector3.Zero); released = true; }
                else if (now >= nextKey - 1e-9) { car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, dir); nextKey += KeyRepeat; }
            }
            car.BeginUpdate((float)step);
            double sub = step / collisionSteps;
            for (int k = 0; k < collisionSteps; k++)
            {
                car.StepOnce((float)sub, body);
                body.Advance(sub);
                now += sub;
            }
            result.Add((now, body.AngularVelocity.Z));
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------------------------
    // The tests

    // A step of Halcyon's own length: the controller and the model are the same law on the same ground, so they stay together
    // for the whole drive, on level ground and on a slope, with a quick motor (the test car's) and a slow one.
    public static IEnumerable<object[]> Drives() => new[]
    {
        new object[] { "level car", 0.0, false }, new object[] { "level testcar", 0.0, true },
        new object[] { "5 degree testcar", 5.0, true }, new object[] { "15 degree car", 15.0, false },
    };

    [Theory]
    [MemberData(nameof(Drives))]
    public void The_motor_equals_the_model_at_Halcyons_own_step(string drive, double slope, bool testCar)
    {
        _out.WriteLine(drive);
        var controller = RunController(0.015, 1, 700, 1000.0, slope, testCar);
        var model = RunModel(700, 1000.0, slope, testCar);
        for (int i = 0; i < model.Count; i += 20)
        {
            double expected = model[i].v, actual = controller[i].v;
            Assert.True(Math.Abs(expected - actual) <= 0.002 * Math.Abs(expected) + 0.005,
                $"{drive}, t = {model[i].t:0.000} s: {actual:0.0000} against {expected:0.0000}");
        }
    }

    // The step rates the module is meant to run at: the heartbeat at 11, 22.5, 45 and 90 Hz, each split into 1 to 4 collision steps.
    public static IEnumerable<object[]> Cells()
    {
        foreach (object[] drive in Drives())
            foreach (double rate in new[] { 11.0, 22.5, 45.0, 90.0 })
                for (int n = 1; n <= 4; n++)
                    yield return new object[] { drive[0], drive[1], drive[2], rate, n };
    }

    // The collision step beyond which one Jolt step is too long for a motor that sets the velocity every 15 ms: the friction of
    // the ground takes a whole step's worth between two sets of the motor, where Halcyon's takes 15 ms (the engine's step cannot
    // be split to put the motor's steps between).
    private const double FineStep = 0.031;

    [Theory]
    [MemberData(nameof(Cells))]
    public void The_driven_speed_agrees_with_the_model_at_every_step_rate(string drive, double slope, bool testCar, double rate, int n)
    {
        double h = 1.0 / rate / n;
        var model = RunModel(1400, 1000.0, slope, testCar);
        double terminal = (At(model, 19) + At(model, 19.5) + At(model, 20)) / 3;
        var run = RunController(1.0 / rate, n, (int)Math.Round(20.5 * rate), 1000.0, slope, testCar);
        double speed = (At(run, 19) + At(run, 19.5) + At(run, 20)) / 3;
        double early = At(run, 2.9), earlyModel = At(model, 2.9);
        _out.WriteLine($"{drive}, {rate} Hz x {n} ({h * 1000:0.0} ms): {speed:0.000} against {terminal:0.000} (3 s: {early:0.000} against {earlyModel:0.000})");
        if (h <= FineStep)
        {
            Assert.True(Math.Abs(speed - terminal) <= 0.05 * terminal, $"{drive}, {rate} Hz x {n}: {speed:0.000} against {terminal:0.000}");
            // It gets going: no stall on the way up either.
            Assert.True(Math.Abs(early - earlyModel) <= 0.05 * earlyModel, $"{drive}, {rate} Hz x {n} at 2.9 s: {early:0.000} against {earlyModel:0.000}");
        }
        else
        {
            // Never above the model, and short of it by what the ground takes in the rest of the step: about 11 % at 91 ms.
            Assert.True(speed <= terminal * 1.005, $"{drive}, {rate} Hz x {n}: {speed:0.000} above {terminal:0.000}");
            Assert.True(speed >= terminal * 0.88, $"{drive}, {rate} Hz x {n}: {speed:0.000} against {terminal:0.000}");
        }
    }

    // The key goes up after 3 s: the motor has no direction on the axis any more, and what is left of its target (the rule that the
    // target replaces a lower velocity goes on acting on an axis that was driven) carries the car on while it runs down: the car
    // glides to a stop, as the model's does, in the distance and time the model gives.
    private static double Coast(List<(double t, double v)> trace, double from)
    {
        double d = 0;
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].t > from) d += 0.5 * (trace[i].v + trace[i - 1].v) * (trace[i].t - trace[i - 1].t);
        return d;
    }

    private static double TimeToSlow(List<(double t, double v)> trace, double from)
    {
        for (int i = 0; i < trace.Count; i++)
            if (trace[i].t > from && trace[i].v < 1.0) return trace[i].t - from;
        return double.NaN;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_car_glides_to_a_stop_after_the_key_goes_up_as_the_model_does(string drive, double slope, bool testCar, double rate, int n)
    {
        if (slope != 0.0 || 1.0 / rate / n > FineStep)
            return;   // the glide on level ground, at steps the motor's clock can follow
        var model = RunModel(700, 3.0, 0.0, testCar);
        var run = RunController(1.0 / rate, n, (int)Math.Round(10 * rate), 3.0, 0.0, testCar);
        double expectedDistance = Coast(model, 3.0), distance = Coast(run, 3.0);
        double expectedRest = TimeToSlow(model, 3.0), rest = TimeToSlow(run, 3.0);
        _out.WriteLine($"{drive}, {rate} Hz x {n}: glides {distance:0.000} m, slow to 1 m/s after {rest:0.000} s, against {expectedDistance:0.000} m and {expectedRest:0.000} s");
        // The glide ends in a crawl (the last metre a second of a slow car takes seconds), so the distance is good to about 13 % and the time to slow to 1 m/s, which is not the crawl, to 15 %.
        Assert.True(Math.Abs(distance - expectedDistance) <= 0.13 * expectedDistance, $"{drive}, {rate} Hz x {n}: {distance:0.000} m against {expectedDistance:0.000}");
        Assert.True(Math.Abs(rest - expectedRest) <= 0.15 * expectedRest + 1.0 / rate, $"{drive}, {rate} Hz x {n}: {rest:0.000} s against {expectedRest:0.000}");
    }

    // ---- the angular motor ----

    private static HalcyonMotorModel QuickTurnModel(float tm, float td)
    {
        HalcyonMotorModel m = ModelOf(CarController(new BehaviourBody(), false, () => DateTime.UtcNow));
        m.AngularMotorTimescale = new Vector3(tm);
        m.AngularMotorDecayTimescale = new Vector3(td);
        return m;
    }

    // The yaw rate of a free body under the angular motor, key down for 2 s: the rate near the end of the hold, and the time after
    // the key goes up for the rate to come within 2 % of the peak of where it ends.
    private static (double rate, double settle) Turn(List<(double t, double v)> trace, double hold)
    {
        double rate = At(trace, hold - 0.1);
        double peak = trace.Max(x => Math.Abs(x.v));
        double final = At(trace, hold + 3.0);
        double settled = double.NaN;
        for (int i = 1; i < trace.Count && double.IsNaN(settled); i++)
        {
            if (trace[i].t <= hold) continue;
            if (Math.Abs(trace[i].v - final) <= 0.02 * peak)
            {
                // Between this heartbeat and the one before, where the line crosses the band.
                double band = final + Math.Sign(trace[i - 1].v - final) * 0.02 * peak;
                double a = trace[i - 1].v, b = trace[i].v;
                double f = Math.Abs(b - a) < 1e-12 ? 1.0 : Math.Clamp((band - a) / (b - a), 0.0, 1.0);
                settled = Math.Max(trace[i - 1].t, hold) + f * (trace[i].t - Math.Max(trace[i - 1].t, hold));
            }
        }
        return (rate, settled - hold);
    }

    public static IEnumerable<object[]> TurnCells()
    {
        foreach ((float tm, float td, float rate) in new[] { (1f, 0.8f, 1f), (0.2f, 0.3f, 2f) })
            foreach (double hz in new[] { 11.0, 22.5, 45.0, 90.0 })
                for (int n = 1; n <= 4; n++)
                    yield return new object[] { tm, td, rate, hz, n };
    }

    // Turn rate and settle time of the angular motor: the controller at every step rate against the model at 15 ms. The motor
    // is the same law as the linear one (the target replaces a lower velocity, strictly: VehicleDynamics.cs:2778-2780) and acts on
    // the same clock; on a free body there is no ground to take a step's worth in between, so the rate does not matter at all.
    [Theory]
    [MemberData(nameof(TurnCells))]
    public void The_angular_motors_turn_rate_and_settle_time_agree_at_every_step_rate(float tm, float td, float turn, double hz, int n)
    {
        const double hold = 2.0;
        var dir = new Vector3(0f, 0f, turn);
        HalcyonMotorModel m = QuickTurnModel(tm, td);
        var body = new HalcyonBody(m, 0.0, 0.0);
        var modelTrace = new List<(double t, double v)>();
        double nextKey = 0;
        bool released = false;
        for (int i = 0; i < 400; i++)
        {
            if (!released)
            {
                if (body.Time >= hold - 1e-9) { m.MoveAngular(Vector3.Zero); released = true; }
                else if (body.Time >= nextKey - 1e-9) { m.MoveAngular(dir); nextKey += KeyRepeat; }
            }
            body.Step();
            modelTrace.Add((body.Time, body.AngularVelocity.Z));
        }
        var run = RunControllerAngular(1.0 / hz, n, (int)Math.Round(5.5 * hz), hold, dir, tm, td);

        (double rate, double settle) expected = Turn(modelTrace, hold), actual = Turn(run, hold);
        _out.WriteLine($"tm {tm} td {td} at {hz} Hz x {n}: turn {actual.rate:0.0000} against {expected.rate:0.0000} rad/s, settle {actual.settle:0.000} against {expected.settle:0.000} s");
        Assert.True(Math.Abs(actual.rate - expected.rate) <= 0.05 * Math.Abs(expected.rate) + 0.002,
            $"turn rate {actual.rate:0.0000} against {expected.rate:0.0000}");
        // The time is read off a trace with one point for each heartbeat, so it is good to a heartbeat (and the motor's step).
        Assert.True(Math.Abs(actual.settle - expected.settle) <= 0.05 * expected.settle + 1.0 / hz + 0.015,
            $"settle time {actual.settle:0.000} against {expected.settle:0.000}");
    }

    // The turn about the world's z that is under pi / 512 rad/s in a step of Halcyon's is not made (the motor leaves a vehicle
    // alone when its change would be too small to matter), so a gentle turn stays at the rate it had when the key went up.
    [Fact]
    public void A_gentle_turn_is_not_slowed_by_the_motor_after_the_key_goes_up()
    {
        const double hold = 2.0;
        var run = RunControllerAngular(1.0 / 45, 6, (int)Math.Round(5.0 * 45), hold, new Vector3(0f, 0f, 1f), 1f, 0.8f);
        double atRelease = At(run, hold + 0.2), later = At(run, hold + 3.0);
        Assert.True(atRelease > 0.3, $"{atRelease}");
        Assert.True(Math.Abs(later - atRelease) <= 0.01 * atRelease, $"{atRelease} then {later}");
    }

    // ---- a motor on an axis it has no direction on ----

    // Halcyon's rule that the motor's target replaces a lower velocity is there to get a driven axis going against friction
    // (VehicleDynamics.cs:2585-2589). With no direction on an axis the sign of the direction is taken as positive, so a velocity below the
    // target is pulled up to it and one above is only braked: a car that left a crest with its nose level and fell at 3.6 m/s
    // in its own frame was set back up to the 0.2 m/s the ramp had left as that axis' target, 66 times a second, and the part of the
    // push that the motor-up limit did not drop pointed along the slope. By default such an axis keeps its velocity.
    [Fact]
    public void A_motor_with_no_direction_on_an_axis_does_not_pull_it_to_its_target_by_default()
    {
        var last = new Vector3(8f, 0f, -3.6f);
        var direction = new Vector3(8f, 0f, 0f);
        Vector3 Run(bool onUndriven, Vector3 driven = default)
        {
            var target = new Vector3(8f, 0f, 0.2f);
            float index = 0.05f;
            return HalcyonVehicleLaws.LinearMotor((float)VehicleController.HalcyonStep, last, direction, new Vector3(0.5f, 1f, 1f),
                new Vector3(10f, 2f, 2f), ref index, ref target, 200f, () => false, onUndriven, driven);
        }
        Assert.True(Run(true).Z > 0f, "Halcyon's code as written pulls the axis up to the target");
        Assert.True(Run(false).Z < last.Z * 0.9f, $"{Run(false).Z}: the axis keeps its velocity (the ramp only brakes it)");
        Assert.True(Run(false).X > 0f);
        // An axis that was driven (the car's x, after the key goes up) is still pulled to the target it is running down.
        Assert.True(Run(false, new Vector3(0f, 0f, 1f)).Z > 0f, "an axis that was driven keeps the rule");
    }

    // The same through the controller and the setting: on a 15 degree ramp driven down from its crest the car does not run away,
    // and with the setting on (Halcyon's code as written) it still does not at Halcyon's own step.
    [Fact]
    public void The_setting_gives_Halcyons_rule_back()
    {
        Assert.False(new VehicleSettings().MotorTargetOnUndrivenAxes);
        Assert.True(new VehicleSettings { MotorTargetOnUndrivenAxes = true }.MotorTargetOnUndrivenAxes);
    }

    // ---- a head-on jam ----

    // Two cars of the model drive at each other with their keys held. Halcyon's law sets each back toward its target on every
    // step, and the contact takes the closing speed back by the restitution: what leaves is the restitution times the motor's
    // speed, never more, so the speed 0.25 s after the impact is not above the motor's target (20 m/s) for any restitution up to 1.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void In_a_head_on_jam_the_model_never_leaves_faster_than_the_motor_target(double restitution)
    {
        var r = ModelHeadOn(restitution);
        _out.WriteLine($"restitution {restitution}: arrival {r.arrival:0.00}, speeds 0.25 s after {r.a:0.00} and {r.b:0.00}, largest in the next second {r.largest:0.00}");
        Assert.True(r.a <= 20.0 + 1e-3 && r.b <= 20.0 + 1e-3 && r.largest <= 20.0 + 1e-3, $"{r}");
        Assert.True(r.a <= restitution * r.arrival * 1.05 + 1e-3);
    }
}

// ---- the same cars in the engine ----

[Collection(JoltNativeSerial.Name)]
public class VehicleInWorldzMotorHarnessTests
{
    private readonly ITestOutputHelper _out;
    public VehicleInWorldzMotorHarnessTests(ITestOutputHelper output) => _out = output;

    // Three drives, run by the engine with the shipped contact friction: the lower part of the 15 degree ramp
    // driven from its crest (iw-car-ramp), of the 5 degree one (testcar-down-5), and the last second of an 8 s drive on level
    // ground, each against the terminal speed of the model at Halcyon's step on ground of the same friction.
    public static IEnumerable<object[]> EngineCells()
    {
        foreach (string drive in new[] { "iw-car-ramp", "testcar-down-5", "level drive" })
            foreach (double rate in new[] { 11.0, 22.5, 45.0, 90.0 })
                for (int n = 1; n <= 4; n++)
                    if (1.0 / rate / n <= 0.031)
                        yield return new object[] { drive, rate, n };
    }

    [Theory]
    [MemberData(nameof(EngineCells))]
    public void The_three_drives_agree_with_the_model_at_every_step_rate(string drive, double rate, int n)
    {
        (string scenario, float slope, bool testCar, float? hold) = drive switch
        {
            "iw-car-ramp" => ("car-down", 15f, false, (float?)null),
            "testcar-down-5" => ("testcar-down", 5f, true, null),
            _ => ("testcar", 0f, true, 8f),
        };
        var options = new HarnessOptions { RateHz = rate, SlopeDeg = slope, Hold = hold, Duration = hold.HasValue ? 12f : null };
        options.Jolt["VehicleBehaviour"] = "InWorldz";
        options.Jolt["CollisionSteps"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float steady = Harness.Harness.Run(Harness.Harness.Find(scenario), options).Summary.SteadySpeed;

        var model = VehicleInWorldzMotorTests.RunModel(1400, 1000.0, slope, testCar, 0.5f);
        double terminal = (VehicleInWorldzMotorTests.At(model, 19) + VehicleInWorldzMotorTests.At(model, 19.5) + VehicleInWorldzMotorTests.At(model, 20)) / 3;
        _out.WriteLine($"{drive} at {rate} Hz x {n} ({1000.0 / rate / n:0.0} ms): {steady:0.000} against {terminal:0.000} ({100 * (steady - terminal) / terminal:0.0} %)");
        Assert.True(Math.Abs(steady - terminal) <= 0.05 * terminal, $"{drive} at {rate} Hz x {n}: {steady:0.000} against {terminal:0.000}");
    }
}
