/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// VEHICLE_FLAG_MOUSELOOK_STEER and VEHICLE_FLAG_MOUSELOOK_BANK on the controller alone, at the four heartbeat rates
/// (llSetVehicleFlags): the angular motor turns the vehicle so that its x axis points where the driver's camera looks
/// (STEER), or turns the camera's yaw into a roll that banking then turns into a yaw (BANK), and "only turn the vehicle up
/// to the current VEHICLE_ANGULAR_MOTOR_DIRECTION; If VEHICLE_ANGULAR_MOTOR_DIRECTION is set to ZERO_VECTOR, then the
/// vehicle will not turn". They act only while the driver is in mouselook; out of it the vehicle behaves as if the flags
/// were clear. A body with no gravity or contacts that keeps what the controller sets and turns by its angular
/// velocity. Pure, parallel.
/// </summary>
public class VehicleMouselookTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public VehicleMouselookTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };
    private const int SteerFlag = 0x80, BankFlag = 0x100;

    private sealed class FreeBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(200f, 400f, 500f);
        public Vector3 Gravity => Vector3.Zero;
        public void SetGravityFactor(float factor) { }
        public bool HasCollision => false;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;

        // The engine's step for this body: turn by the world angular velocity over h.
        public void Turn(double h)
        {
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

    // A car with everything that turns it off but the angular motor (timescale 1 s, no decay to speak of in the run):
    // no friction, deflection, attractor or banking. The motor is set once.
    private static (VehicleController car, FreeBody body, Action<double> step) Car(int flags, Vector3 motor, Vector3? camera, bool inMouselook = true)
        => Car(flags, motor, camera, () => inMouselook);

    private static (VehicleController car, FreeBody body, Action<double> step) Car(int flags, Vector3 motor, Vector3? camera, Func<bool> inMouselook)
    {
        var body = new FreeBody();
        var car = new VehicleController(body);
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        car.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        car.ProcessVehicleFlags(flags, remove: false);
        if (camera.HasValue)
            car.DriverCamera = () => new DriverView(camera.Value, inMouselook());
        car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, motor);
        void Step(double h)
        {
            car.Step((float)h);
            body.Turn(h);
            now += h;
        }
        return (car, body, Step);
    }

    private static Vector3 Level(double heading) => new((float)Math.Cos(heading), (float)Math.Sin(heading), 0f);

    // STEER, the camera 60 degrees left: the car turns left, never faster than the motor's 2 rad/s, and comes to point
    // where the camera looks and stays there (the aim and the motor's lag make a spring with half critical damping, so
    // it overshoots by about a sixth and settles: by 15 s it is within 0.5 degrees and still). The aim is taken once a
    // step from where the car points at the step's start and held over the step, so mid-turn the heading depends on the
    // step a little: after 1 s it agrees with 90 Hz's within 2% at 45 Hz and within 7% at 11 Hz (measured 0.9% and
    // 6.2%), the way the motor-offset turn leads by part of a step at 11 Hz.
    [Fact]
    public void Steer_turns_the_nose_to_the_camera_and_no_further()
    {
        const double Aim = Math.PI / 3;
        var at1s = new Dictionary<double, double>();
        foreach (double rate in Rates)
        {
            var (_, body, step) = Car(SteerFlag, new Vector3(0f, 0f, 2f), Level(Aim));
            for (int i = 1; i <= (int)Math.Round(15 * rate); i++)
            {
                step(1.0 / rate);
                Assert.True(body.AngularVelocity.Z <= 2f + 1e-4f, $"{rate} Hz at {i / rate:0.000} s: turning {body.AngularVelocity.Z} rad/s");
                if (i == (int)Math.Round(rate))
                    at1s[rate] = Harness.Harness.Heading(body.Orientation);
            }
            double end = Harness.Harness.Heading(body.Orientation);
            Assert.True(Math.Abs(end - Aim) < 0.5 * Math.PI / 180, $"{rate} Hz: heading {end * 180 / Math.PI:0.00} deg at 15 s, the camera at 60");
            Assert.True(Math.Abs(body.AngularVelocity.Z) < 1e-3f, $"{rate} Hz: still turning {body.AngularVelocity.Z} rad/s at 15 s");
        }
        Assert.True(at1s[90.0] > 0.1, $"heading after 1 s {at1s[90.0]}");
        foreach (double rate in Rates)
        {
            output.WriteLine($"{rate} Hz: heading after 1 s {at1s[rate]:0.0000} rad");
            double within = rate >= 45.0 ? 0.02 : 0.07;
            Assert.True(Math.Abs(at1s[rate] - at1s[90.0]) <= within * at1s[90.0], $"{rate} Hz: heading after 1 s {at1s[rate]:0.0000} against {at1s[90.0]:0.0000} at 90 Hz");
        }
    }

    // The motor direction is the fastest it turns: at 0.3 rad/s the car turns no faster and still reaches the camera; at
    // ZERO_VECTOR it does not turn. With no driver's camera known, the flag changes nothing: the motor turns the car at
    // its direction as it would without the flag, to the bit.
    [Fact]
    public void The_motor_direction_is_the_fastest_it_turns()
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            var (_, slow, stepSlow) = Car(SteerFlag, new Vector3(0f, 0f, 0.3f), Level(Math.PI / 3));
            var (_, still, stepStill) = Car(SteerFlag, Vector3.Zero, Level(Math.PI / 3));
            var (_, blind, stepBlind) = Car(SteerFlag, new Vector3(0f, 0f, 0.3f), null);
            var (_, plain, stepPlain) = Car(0, new Vector3(0f, 0f, 0.3f), null);
            for (int i = 1; i <= (int)Math.Round(15 * rate); i++)
            {
                stepSlow(h); stepStill(h); stepBlind(h); stepPlain(h);
                Assert.True(slow.AngularVelocity.Z <= 0.3f + 1e-4f, $"{rate} Hz at {i * h:0.000} s: turning {slow.AngularVelocity.Z} rad/s");
                Assert.Equal(Vector3.Zero, still.AngularVelocity);
                Assert.Equal(plain.AngularVelocity, blind.AngularVelocity);
            }
            double end = Harness.Harness.Heading(slow.Orientation);
            Assert.True(Math.Abs(end - Math.PI / 3) < 0.5 * Math.PI / 180, $"{rate} Hz: heading {end * 180 / Math.PI:0.00} deg at 15 s");
            Assert.Equal(0f, Harness.Harness.Heading(still.Orientation));
            Assert.Equal(Harness.Harness.Heading(plain.Orientation), Harness.Harness.Heading(blind.Orientation));
        }
    }

    // Out of mouselook the flags do nothing: the driver's camera is looked at only in mouselook (InWorldz acts on the
    // camera for these two flags only while the camera is in mouselook). With the camera 60 degrees left but the driver
    // not in mouselook, a STEER car and a BANK car turn exactly as the same car with the flag clear: at the script's
    // motor direction, on past the camera, to the bit, at every rate.
    [Theory]
    [InlineData(SteerFlag, 0f, 0f, 0.3f)]
    [InlineData(BankFlag, 0.5f, 0f, 0f)]
    [InlineData(SteerFlag | BankFlag, 0.5f, 0f, 0.3f)]
    [InlineData(SteerFlag, 0f, 1f, 0f)]
    public void Out_of_mouselook_the_flags_change_nothing(int flags, float x, float y, float z)
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            var motor = new Vector3(x, y, z);
            var (_, away, stepAway) = Car(flags, motor, Level(Math.PI / 3), inMouselook: false);
            var (_, plain, stepPlain) = Car(0, motor, null);
            var (_, aimed, stepAimed) = Car(flags, motor, Level(Math.PI / 3), inMouselook: true);
            for (int i = 1; i <= (int)Math.Round(8 * rate); i++)
            {
                stepAway(h); stepPlain(h); stepAimed(h);
                Assert.Equal(plain.AngularVelocity, away.AngularVelocity);
                Assert.Equal(plain.Orientation, away.Orientation);
            }
            // The control: in mouselook the same car does not turn as the plain one does (it aims, so it differs).
            Assert.NotEqual(plain.Orientation, aimed.Orientation);
        }
    }

    // The driver can leave and enter mouselook while driving: the camera steers only while he is in it. A STEER car aims
    // at a camera 60 degrees left, the driver leaves mouselook at 0.5 s and the motor's direction (2 rad/s) turns the car
    // on past the camera; the same car that stays in mouselook settles on the camera.
    [Fact]
    public void Leaving_mouselook_stops_the_steering_at_once()
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate, t = 0;
            bool inMouselook = true;
            var (_, leaves, stepLeaves) = Car(SteerFlag, new Vector3(0f, 0f, 2f), Level(Math.PI / 3), () => inMouselook);
            var (_, stays, stepStays) = Car(SteerFlag, new Vector3(0f, 0f, 2f), Level(Math.PI / 3), true);
            for (int i = 1; i <= (int)Math.Round(4 * rate); i++)
            {
                t = i * h;
                inMouselook = t <= 0.5;
                stepLeaves(h); stepStays(h);
            }
            Assert.True(leaves.AngularVelocity.Z > 1.5f, $"{rate} Hz: left mouselook, turning {leaves.AngularVelocity.Z} rad/s at 4 s");
            Assert.True(Harness.Harness.Heading(leaves.Orientation) > Math.PI / 3 + 0.3 || Harness.Harness.Heading(leaves.Orientation) < 0, $"{rate} Hz: heading {Harness.Harness.Heading(leaves.Orientation)}");
            Assert.True(Math.Abs(stays.AngularVelocity.Z) < 1f, $"{rate} Hz: stayed in mouselook, turning {stays.AngularVelocity.Z} rad/s at 4 s");
        }
    }

    // STEER points the x axis where the camera looks, up and down too: the camera 20 degrees above level, the motor
    // <0,1,0>, the nose rises to 20 degrees (the angular motor's y axis pitches it; negative is nose up) and stays.
    [Fact]
    public void Steer_aims_the_nose_up_to_the_camera()
    {
        const double Up = 20 * Math.PI / 180;
        foreach (double rate in Rates)
        {
            var camera = new Vector3((float)Math.Cos(Up), 0f, (float)Math.Sin(Up));
            var (_, body, step) = Car(SteerFlag, new Vector3(0f, 1f, 0f), camera);
            for (int i = 1; i <= (int)Math.Round(10 * rate); i++)
                step(1.0 / rate);
            Vector3 nose = Vector3.UnitX * body.Orientation;
            double pitch = Math.Asin(Math.Clamp(nose.Z, -1f, 1f));
            Assert.True(Math.Abs(pitch - Up) < 0.5 * Math.PI / 180, $"{rate} Hz: nose {pitch * 180 / Math.PI:0.00} deg up at 10 s, the camera at 20");
            Assert.True(Math.Abs(Harness.Harness.Heading(body.Orientation)) < 1e-4, $"{rate} Hz: heading {Harness.Harness.Heading(body.Orientation)}");
        }
    }

    // BANK, the camera 60 degrees left, the motor <0.5,0,0>: the camera's yaw becomes a roll to the left (negative about
    // x), never faster than 0.5 rad/s, and the motor itself does not yaw the car (banking off: the heading stays put).
    // With banking on (attractor on, slow) the roll turns the car left, toward the camera.
    [Fact]
    public void Bank_rolls_toward_the_camera_and_leaves_the_turn_to_banking()
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            var (_, rolls, stepRolls) = Car(BankFlag, new Vector3(0.5f, 0f, 0f), Level(Math.PI / 3));
            var (banks, turns, stepTurns) = Car(BankFlag, new Vector3(0.5f, 0f, 0f), Level(Math.PI / 3));
            banks.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
            banks.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 400f);
            banks.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
            banks.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, 0f);
            banks.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 1f);
            for (int i = 1; i <= (int)Math.Round(1 * rate); i++)
            {
                stepRolls(h); stepTurns(h);
                Vector3 local = rolls.AngularVelocity * Quaternion.Inverse(rolls.Orientation);
                Assert.True(local.X < 0f && local.X >= -0.5f - 1e-4f, $"{rate} Hz at {i * h:0.000} s: rolling {local.X} rad/s");
            }
            Assert.True(Math.Abs(Harness.Harness.Heading(rolls.Orientation)) < 1e-4, $"{rate} Hz: heading {Harness.Harness.Heading(rolls.Orientation)} with banking off");
            Assert.True(Harness.Harness.Heading(turns.Orientation) > 0.01, $"{rate} Hz: heading {Harness.Harness.Heading(turns.Orientation)} with banking on");
        }
    }
}

/// <summary>
/// The banking turn and the mouselook-steered turn on the real engine, at one physics step per heartbeat and at
/// PhysicsStepRate 45 (11 Hz heartbeats). Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleBankingAndMouselookHarnessTests
{
    private static RunResult Run(string scenario, double physicsRate, Action<HarnessOptions> more = null)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate }.SecondLife();
        more?.Invoke(o);
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    // The yaw rate banking gives the climbing airplane follows the documented model with nothing else turning it (no
    // angular friction or deflection, the attractor on but slow, static banking): from the tilt at 2 s, rolled
    // r = 20 degrees, w(t) = A (1 - e^(-t / T)) with A = -r eff pi, so the heading has turned A (t - T (1 - e^(-t / T))).
    // The 30 degree climb does not weaken it. Within 3% of that after 4 s at both rates, and the rates within 2%.
    [Fact]
    public void A_banking_turn_follows_the_roll_angle_at_both_rates()
    {
        const double T = 2.0, Efficiency = 1.0, Span = 4.0;
        double a = -Harness.Harness.BankTurnRoll * Efficiency * Math.PI;
        double expected = a * (Span - T * (1 - Math.Exp(-Span / T)));
        var turned = new Dictionary<double, double>();
        foreach (double physics in new[] { 0.0, 45.0 })
        {
            RunResult r = Run("bank-turn", physics, o =>
            {
                o.VehicleParams.Add(VehicleParamSetting.Parse("ANGULAR_FRICTION_TIMESCALE=1000,1000,1000"));
                o.VehicleParams.Add(VehicleParamSetting.Parse("ANGULAR_DEFLECTION_EFFICIENCY=0"));
                o.VehicleParams.Add(VehicleParamSetting.Parse("LINEAR_DEFLECTION_EFFICIENCY=0"));
                o.VehicleParams.Add(VehicleParamSetting.Parse("VERTICAL_ATTRACTION_EFFICIENCY=0"));
                o.VehicleParams.Add(VehicleParamSetting.Parse("VERTICAL_ATTRACTION_TIMESCALE=400"));
                o.VehicleParams.Add(VehicleParamSetting.Parse("BANKING_MIX=0"));
                o.VehicleParams.Add(VehicleParamSetting.Parse("BANKING_TIMESCALE=2"));
            });
            Sample tilt = r.Samples.First(s => s.T >= Harness.Harness.AttractStart - 1e-9);
            Sample later = r.Samples.First(s => s.T >= tilt.T + Span - 1e-9);
            double turn = Wrap(Harness.Harness.Heading(later.Rotation) - Harness.Harness.Heading(tilt.Rotation));
            turned[physics] = turn;
            Assert.True(Math.Abs(turn - expected) <= 0.03 * Math.Abs(expected),
                        $"physics {physics} Hz: turned {turn:0.0000} rad in {Span} s, the model {expected:0.0000}");
        }
        Assert.True(Math.Abs(turned[45.0] - turned[0.0]) <= 0.02 * Math.Abs(turned[0.0]), $"turned {turned[0.0]:0.0000} and {turned[45.0]:0.0000}");
    }

    // The documented airplane's own banking turn, as the scenario runs it: it turns right (heading falling) at both
    // rates, and the rates agree within 3% on the heading after 4 s and within 3 degrees on the most it rolled.
    [Fact]
    public void The_airplane_banks_into_a_right_turn_alike_at_both_rates()
    {
        var turned = new Dictionary<double, double>();
        foreach (double physics in new[] { 0.0, 45.0 })
        {
            RunResult r = Run("bank-turn", physics);
            Sample tilt = r.Samples.First(s => s.T >= Harness.Harness.AttractStart - 1e-9);
            Sample later = r.Samples.First(s => s.T >= tilt.T + 4.0 - 1e-9);
            turned[physics] = Wrap(Harness.Harness.Heading(later.Rotation) - Harness.Harness.Heading(tilt.Rotation));
            Assert.True(turned[physics] < -0.2, $"physics {physics} Hz: turned {turned[physics]:0.000} rad");
        }
        Assert.True(Math.Abs(turned[45.0] - turned[0.0]) <= 0.03 * Math.Abs(turned[0.0]), $"turned {turned[0.0]:0.0000} and {turned[45.0]:0.0000}");
    }

    // The same car with its driver out of mouselook is not steered by the camera: its angular motor, held at 1.5 rad/s,
    // keeps turning it round and round (still turning at 1.4 rad/s or more in the last two seconds of the hold, and
    // through north without stopping there), at both rates.
    [Fact]
    public void The_car_is_not_steered_by_the_camera_out_of_mouselook()
    {
        foreach (double physics in new[] { 0.0, 45.0 })
        {
            RunResult r = Run("mouselook-turn", physics, o => o.DriverInMouselook = false);
            Assert.Equal(0, r.Summary.NonFinite);
            foreach (Sample s in r.Samples.Where(s => s.T >= 4.0 && s.T <= r.Summary.ReleaseT))
                Assert.True(s.AngularVelocity.Z >= 1.4f, $"physics {physics} Hz at {s.T:0.000} s: turning {s.AngularVelocity.Z:0.000} rad/s");
        }
    }

    // The car steered by the camera turns left to face north, no faster than its 1.5 rad/s, and drives on north: at
    // the release (6 s) it faces within 2 degrees of north at both rates, and the time it took to come within 5 degrees
    // agrees within 5%.
    [Fact]
    public void The_mouselook_car_turns_to_the_camera_alike_at_both_rates()
    {
        var reached = new Dictionary<double, double>();
        foreach (double physics in new[] { 0.0, 45.0 })
        {
            RunResult r = Run("mouselook-turn", physics);
            Assert.Equal(0, r.Summary.NonFinite);
            foreach (Sample s in r.Samples)
                Assert.True(s.AngularVelocity.Z <= 1.5f + 0.02f, $"physics {physics} Hz at {s.T:0.000} s: turning {s.AngularVelocity.Z:0.000} rad/s");
            Sample release = r.Samples.Last(s => s.T <= r.Summary.ReleaseT + 1e-9);
            double off = Wrap(Harness.Harness.Heading(release.Rotation) - Math.PI / 2);
            Assert.True(Math.Abs(off) < 2 * Math.PI / 180, $"physics {physics} Hz: {off * 180 / Math.PI:0.00} deg off north at the release");
            reached[physics] = r.Samples.First(s => Math.Abs(Wrap(Harness.Harness.Heading(s.Rotation) - Math.PI / 2)) < 5 * Math.PI / 180).T;
            Assert.True(release.Position.Y > 100f, $"physics {physics} Hz: y {release.Position.Y:0.0} at the release");
        }
        Assert.True(Math.Abs(reached[45.0] - reached[0.0]) <= 0.05 * reached[0.0], $"faced north after {reached[0.0]:0.000} and {reached[45.0]:0.000} s");
    }

    // The mouselook airplane rolls right toward the camera 60 degrees right of east, and banking turns it right until it
    // faces the camera, at both rates. It has the documented airplane's slow roll motor (4 s) and banking (2 s) and
    // swings on past the camera (to about -93 degrees by 14 s) before turning back. The time it first faces within 2
    // degrees of the camera agrees within 5%, and the headings agree within 3 degrees at every second.
    [Fact]
    public void The_mouselook_airplane_banks_to_the_camera_alike_at_both_rates()
    {
        var runs = new Dictionary<double, RunResult>();
        var faced = new Dictionary<double, double>();
        foreach (double physics in new[] { 0.0, 45.0 })
        {
            RunResult r = runs[physics] = Run("mouselook-bank", physics);
            Assert.Equal(0, r.Summary.NonFinite);
            Sample first = r.Samples.FirstOrDefault(s => Math.Abs(Wrap(Harness.Harness.Heading(s.Rotation) + Math.PI / 3)) < 2 * Math.PI / 180);
            Assert.True(first.T > 0, $"physics {physics} Hz: never faced the camera");
            faced[physics] = first.T;
            Vector3 roll = Vector3.UnitY * r.Samples.First(s => s.T >= 3.0 - 1e-9).Rotation;
            Assert.True(roll.Z > 0.02f, $"physics {physics} Hz: at 3 s the left side rose only {roll.Z:0.000}");
        }
        Assert.True(Math.Abs(faced[45.0] - faced[0.0]) <= 0.05 * faced[0.0], $"faced the camera at {faced[0.0]:0.000} and {faced[45.0]:0.000} s");
        for (int i = 0; i < runs[0.0].Samples.Count; i += 11)
        {
            double a = Harness.Harness.Heading(runs[0.0].Samples[i].Rotation), b = Harness.Harness.Heading(runs[45.0].Samples[i].Rotation);
            Assert.True(Math.Abs(Wrap(a - b)) < 3 * Math.PI / 180, $"at {runs[0.0].Samples[i].T:0.0} s: headings {a * 180 / Math.PI:0.0} and {b * 180 / Math.PI:0.0} deg");
        }
    }

    private static double Wrap(double a) => Math.IEEERemainder(a, 2 * Math.PI);
}
