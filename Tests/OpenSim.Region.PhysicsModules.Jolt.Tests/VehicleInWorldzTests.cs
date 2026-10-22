/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] VehicleBehaviour: Second Life's documented behaviour (the default) or InWorldz's, which turns on six
/// behaviours of the Halcyon vehicle code. Each is checked against the figure its formula gives (the formulas are
/// quoted at the code, with Halcyon's file and lines; Halcyon was read, not run), and the Second Life choice is checked
/// to be what the code gave before the setting existed, to the bit. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleInWorldzTests
{
    private const double Deg = Math.PI / 180.0;
    private const double G = 9.80665;
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };
    public static IEnumerable<object[]> RateData() => Rates.Select(r => new object[] { r });

    private static VehicleSettings SecondLife => new VehicleSettings { Behaviour = VehicleBehaviour.SecondLife };
    private static VehicleSettings InWorldz => new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz };

    private static void Near(double expected, double actual, double relative, double absolute, string what)
        => Assert.True(Math.Abs(expected - actual) <= absolute + relative * Math.Abs(expected), $"{what}: {actual:R} against {expected:R}");

    // ---------------------------------------------------------------------------------------------------------
    // The Second Life choice, bit for bit. The figures were taken from the controller before the setting existed.

    private static readonly Dictionary<string, uint[]> Golden = new()
    {
        ["RollOnlyCar"] = new uint[] { 0x3D8C0FD8, 0x3E842ED0, 0xBC961E25, 0x3F76A80D, 0xBDA8817B, 0x30388571, 0x3D4292EA, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["UpOnlyBalloon"] = new uint[] { 0, 0, 0, 0x3F800000, 0, 0, 0, 0, 0, 0xC0CA22A8, 0x405AEA9B, 0 },
        ["FallingCar"] = new uint[] { 0, 0, 0, 0x3F800000, 0, 0, 0, 0, 0, 0xC18E3627, 0x42A2DE33, 0x3F800000 },
        ["ShortDecayMotor"] = new uint[] { 0, 0, 0x3C9029B8, 0x3F7FF5DB, 0, 0, 0x3E5ED916, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["OverturnedCar"] = new uint[] { 0x3F1F0B8E, 0, 0, 0x3F4899C9, 0xBF5BFC11, 0, 0, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["NearlyBuoyantBalloon"] = new uint[] { 0, 0, 0, 0x3F800000, 0, 0, 0, 0, 0, 0xBE05E4B2, 0x42C7E8F1, 0x3CA3D700 },
    };

    private static readonly Dictionary<string, Func<VehicleSettings, float[]>> Scenarios = new()
    {
        ["RollOnlyCar"] = BehaviourRig.RollOnlyCar,
        ["UpOnlyBalloon"] = BehaviourRig.UpOnlyBalloon,
        ["FallingCar"] = BehaviourRig.FallingCar,
        ["ShortDecayMotor"] = BehaviourRig.ShortDecayMotor,
        ["OverturnedCar"] = BehaviourRig.OverturnedCar,
        ["NearlyBuoyantBalloon"] = BehaviourRig.NearlyBuoyantBalloon,
    };

    public static IEnumerable<object[]> ScenarioNames() => Golden.Keys.Select(k => new object[] { k });

    private static uint[] Bits(float[] state) => state.Select(f => (uint)BitConverter.SingleToInt32Bits(f)).ToArray();

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void The_Second_Life_choice_gives_what_the_controller_gave_before_the_setting_to_the_bit(string scenario)
    {
        // Within float rounding of the figures recorded on x64 (see FloatRounding): the last bit differs on Arm64.
        float[] state = Scenarios[scenario](SecondLife);
        FloatRounding.AssertNear(Golden[scenario], state, scenario);
        // A controller handed no settings is the same controller: bit for bit, on this machine.
        Assert.Equal(Bits(state), Bits(Scenarios[scenario](VehicleSettings.Default)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. LIMIT_ROLL_ONLY keeps a tenth of the pitch righting of a car, boat or sled

    // VehicleDynamics.cs:1278-1284: with LIMIT_ROLL_ONLY the pitch force of an airplane or balloon is zeroed, and that of
    // any other vehicle is multiplied by 0.1. A car pitched 30 degrees with the attractor at 1 s / 1: in the first step
    // its pitch push is sin(30 degrees) x 0.1 x (1 + (1 + 30 / 180)^4) x pi e^3 h / 1 (the boost, then a tenth); an
    // airplane or balloon, or any vehicle under Second Life's choice, turns not at all about its pitch axis.
    [Theory]
    [InlineData(Vehicle.TYPE_CAR, true)]
    [InlineData(Vehicle.TYPE_BOAT, true)]
    [InlineData(Vehicle.TYPE_SLED, true)]
    [InlineData(Vehicle.TYPE_AIRPLANE, false)]
    [InlineData(Vehicle.TYPE_BALLOON, false)]
    public void Roll_only_keeps_a_tenth_of_the_pitch_righting_of_cars_boats_and_sleds(Vehicle type, bool keeps)
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            foreach (bool inWorldz in new[] { false, true })
            {
                var rig = new BehaviourRig(inWorldz ? InWorldz : SecondLife, type, gravity: false);
                rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1f);
                rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
                rig.Flag(ExtendedVehicleFlags.LimitRollOnly, true);
                rig.Tilt(0, 30);
                rig.Step(h);
                float pitchRate = rig.Body.AngularVelocity.Y;
                if (!(inWorldz && keeps))
                {
                    Assert.Equal(0f, pitchRate);
                    continue;
                }
                double boost = 1.0 + Math.Pow(1.0 + 30 * Deg / Math.PI, 4.0);
                double expected = -Math.Sin(30 * Deg) * 0.1 * boost * Math.PI * Math.Exp(3.0) * h / 1.0;
                Near(expected, pitchRate, 1e-3, 1e-6, $"{type} at {rate} Hz: pitch rate");
                Assert.True(pitchRate < 0f, "the nose comes up");
                Assert.Equal(0f, rig.Body.AngularVelocity.X, 5);
            }
        }
    }

    // What a driver notices: a roll-only car left pitched 30 degrees noses back toward level (Second Life's stays put).
    [Fact]
    public void A_roll_only_car_pitched_up_comes_back_toward_level_under_InWorldz_only()
    {
        float Pitch(VehicleSettings s)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
            rig.Flag(ExtendedVehicleFlags.LimitRollOnly, true);
            rig.Tilt(0, 30);
            rig.Run(1.0 / 45, 270);
            return (float)(BehaviourRig.RollAndPitch(rig.Body.Orientation).pitch / Deg);
        }
        Assert.InRange(Pitch(SecondLife), 29.99f, 30.01f);
        Assert.InRange(Pitch(InWorldz), -1f, 5f);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. HOVER_UP_ONLY acts with buoyancy and then pushes down

    // PhysxPrim.pid.cs:323-325: with the up-only flag the hover force acts "if diff > 0 || Buoyancy > 0". A balloon
    // (buoyancy 1) 3 m above its 5 m hover height is pulled down by an ordinary hover, as with no flag at all;
    // Second Life's choice leaves it to fall, hover doing nothing above the height and buoyancy vanishing there.
    [Fact]
    public void A_buoyant_up_only_hover_pushes_the_vehicle_down_as_a_plain_hover_does()
    {
        float[] withFlag = BehaviourRig.UpOnlyBalloon(InWorldz);
        float[] plain = BalloonHover(InWorldz, upOnly: false, buoyancy: 1f, startZ: 8f);
        Assert.Equal(Bits(plain), Bits(withFlag));
        Assert.NotEqual(Bits(plain), Bits(BehaviourRig.UpOnlyBalloon(SecondLife)));
        Assert.InRange(withFlag[10], 5f, 8f);   // pulled toward 5 m, not falling to 3.4 m as Second Life's does
    }

    // The figure: from rest 3 m above the height, the first step's velocity is what Halcyon's hover makes over the 15 ms steps
    // that fall due in it (timescale 2 s, efficiency 0.8), buoyancy 1 leaving the engine no gravity.
    [Theory]
    [MemberData(nameof(RateData))]
    public void The_first_step_of_a_buoyant_up_only_hover_is_Halcyons_hover_law(double rate)
    {
        double h = 1.0 / rate;
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_BALLOON);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.8f);
        rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
        rig.Flag(ExtendedVehicleFlags.HoverUpOnly, true);
        rig.Body.Position = new Vector3(128f, 128f, 8f);
        rig.Step(h);
        // Halcyon's hover law (PhysxPrim.pid.cs DoHover), one step of 15 ms for each 15 ms the collision step covers.
        int due = (int)Math.Floor(h / 0.015 + 1e-6);
        float velocity = 0f, z = 8f;
        for (int i = 0; i < due; i++)
        {
            velocity += HalcyonVehicleLaws.HoverVelocityChange(5f - z, velocity, 2f, 0.8f, true, 0.015f);
            z += velocity * 0.015f;
        }
        Near(velocity, rig.Body.LinearVelocity.Z, 1e-4, 1e-6, $"{rate} Hz: first step's velocity");
        Assert.True(due == 0 || velocity < 0);
    }

    private static float[] BalloonHover(VehicleSettings s, bool upOnly, float buoyancy, float startZ)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_BALLOON);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, buoyancy);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.8f);
        rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
        rig.Flag(ExtendedVehicleFlags.HoverUpOnly, upOnly);
        rig.Body.Position = new Vector3(128f, 128f, startZ);
        rig.Run(1.0 / 45, 45);
        return rig.State();
    }

    // With no buoyancy the guard is "diff > 0": hover lifts a vehicle below its height and does nothing above it, as
    // Second Life documents ("doesn't push down"). The two choices still differ in gravity: InWorldz's hover switches it off
    // while it acts (PhysxPrim.pid.cs DoHover: _hoverDisableGravity), so above the height, with no hover force and no gravity,
    // the vehicle stays where it is, where Second Life's falls; below it InWorldz's is held without the sag a spring needs to
    // carry the weight.
    [Theory]
    [InlineData(3f)]
    [InlineData(8f)]
    public void Without_buoyancy_an_up_only_hover_has_no_gravity_under_InWorldz(float startZ)
    {
        float[] inWorldz = BalloonHover(InWorldz, true, 0f, startZ);
        float[] secondLife = BalloonHover(SecondLife, true, 0f, startZ);
        Assert.NotEqual(Bits(secondLife), Bits(inWorldz));
        if (startZ > 5f)
        {
            Assert.InRange(inWorldz[10], startZ - 0.01f, startZ + 0.01f);
            Assert.True(secondLife[10] < startZ - 0.5f, $"Second Life's vehicle did not fall: {secondLife[10]}");
        }
        else
            Assert.True(inWorldz[10] > secondLife[10] + 0.05f, $"lifted {inWorldz[10]} against Second Life's {secondLife[10]}: not higher without gravity");   // after 1 s of the spring from 3 m toward 5 m
    }

    // With buoyancy and the vehicle below its height, InWorldz's hover acts as it does without the flag.
    [Fact]
    public void With_buoyancy_the_up_only_flag_makes_no_difference_to_InWorldzs_hover()
        => Assert.Equal(Bits(BalloonHover(InWorldz, false, 0.9f, 3f)), Bits(BalloonHover(InWorldz, true, 0.9f, 3f)));

    // Halcyon has no rule that buoyancy vanishes above the height: a vehicle with negative buoyancy (heavier than gravity)
    // above its height keeps it, where Second Life's choice drops it to zero there. The gravity share the controller
    // gives the engine: 1.5 against 1. (With hover acting, InWorldz's gravity is off altogether: the last line below.)
    [Fact]
    public void Buoyancy_does_not_vanish_above_the_hover_height_under_InWorldz()
    {
        float Share(VehicleSettings s, float hoverTimescale)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_BALLOON);
            rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, -0.5f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 5f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, hoverTimescale);
            rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
            rig.Flag(ExtendedVehicleFlags.HoverUpOnly, true);
            rig.Body.Position = new Vector3(128f, 128f, 8f);
            rig.Step(1.0 / 45);
            return rig.Body.GravityFactor;
        }
        Assert.Equal(1f, Share(SecondLife, 2f));
        Assert.Equal(1.5f, Share(InWorldz, 1000f));   // hover off: the 1000 s timescale
        Assert.Equal(0f, Share(InWorldz, 2f));        // hover acting: no gravity at all
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. Friction that would push up is dropped

    // VehicleDynamics.cs:1494-1495 ("Cheat: Do not fight gravity"). A car falling from rest with a 10 s friction on its
    // vertical axis: Second Life's falls at g Tf (1 - e^(-t / Tf)), 17.78 m/s after 2 s; InWorldz's is not slowed,
    // g t, 19.61 m/s, at every step rate.
    [Theory]
    [MemberData(nameof(RateData))]
    public void A_falling_vehicle_is_not_slowed_by_friction_under_InWorldz(double rate)
    {
        double h = 1.0 / rate;
        int steps = (int)Math.Round(2.0 * rate);
        foreach (bool inWorldz in new[] { false, true })
        {
            var rig = new BehaviourRig(inWorldz ? InWorldz : SecondLife, Vehicle.TYPE_CAR);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 10f));
            rig.Run(h, steps);
            double expected = inWorldz ? -G * 2.0 : -G * 10.0 * (1.0 - Math.Exp(-0.2));
            Near(expected, rig.Body.LinearVelocity.Z, 1e-4, 1e-4, $"{(inWorldz ? "InWorldz" : "Second Life")} at {rate} Hz: speed after 2 s");
        }
    }

    // Friction that pushes down, on a vehicle rising, stays, and so does friction along the other axes: the choices
    // agree to the bit.
    [Fact]
    public void A_rising_vehicle_and_the_other_axes_keep_their_friction()
    {
        float[] Run(VehicleSettings s)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_CAR);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(5f, 1000f, 10f));
            rig.Body.LinearVelocity = new Vector3(10f, 0f, 20f);
            rig.Run(1.0 / 45, 20);   // 0.44 s: still rising
            Assert.True(rig.Body.LinearVelocity.Z > 0f);
            return rig.State();
        }
        Assert.Equal(Bits(Run(SecondLife)), Bits(Run(InWorldz)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. A quick motor's decay is delayed

    // VehicleDynamics.cs:3066-3072 and 3099-3105: a motor whose timescale vector is shorter than 0.9 starts its decay
    // clock at 0.0156 (1 - f) - 0.06 f, f = 1 - length; every other motor at 0.0156. Second Life's starts at 0.
    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.2f)]
    [InlineData(0.5f)]      // length 0.866
    [InlineData(0.52f)]     // length 0.9007: not shorter than 0.9
    [InlineData(0.6f)]
    [InlineData(2f)]
    public void A_motor_starts_its_decay_clock_where_Halcyon_does(float timescale)
    {
        foreach (bool linear in new[] { true, false })
        {
            foreach (bool inWorldz in new[] { false, true })
            {
                var rig = new BehaviourRig(inWorldz ? InWorldz : SecondLife, Vehicle.TYPE_CAR);
                if (linear)
                {
                    rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, timescale);
                    rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(1f, 0f, 0f));
                }
                else
                {
                    rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, timescale);
                    rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
                }
                float age = linear ? rig.Car.LinearMotorAge : rig.Car.AngularMotorAge;
                double length = new Vector3(timescale).Length();
                double f = length < 0.9 ? 1.0 - length : 0.0;
                double expected = inWorldz ? 0.0156 * (1.0 - f) - 0.06 * f : 0.0;
                Near(expected, age, 0, 1e-6, $"{(linear ? "linear" : "angular")} motor, timescale {timescale}, {(inWorldz ? "InWorldz" : "Second Life")}");
            }
        }
    }

    // The motor holds its whole grip until the clock reaches zero and decays from there. An angular motor of 1 rad/s,
    // timescale 0.2 s, decay 0.05 s: after 0.2 s. Second Life's, against a fine integration of dv/dt = g (M - v) / Tm with
    // g = 1 until the delay (0 for Second Life) and e^(-(t - delay) / Td) after it. InWorldz's angular motor is Halcyon's
    // stepwise ramp, so it is checked against that ramp worked out apart from the controller, with the clock starting
    // 0.0338 s before the set.
    [Theory]
    [MemberData(nameof(RateData))]
    public void The_decay_of_a_quick_motor_is_held_back_by_its_delay(double rate)
    {
        double h = 1.0 / rate;
        double length = 0.2f * Math.Sqrt(3.0);
        double f = 1.0 - length;
        double delay = -(double)(float)(0.0156 * (1.0 - f) - 0.06 * f);
        Assert.InRange(delay, 0.0338, 0.0339);
        int steps = (int)Math.Round(0.2 * rate);
        if (Math.Abs(steps * h - 0.2) > 1e-9)
            steps = (int)Math.Round(2.0 * rate);   // 22.5 Hz does not divide 0.2 s: use 2 s, where the motor is long dead
        double t = steps * h;
        foreach (bool inWorldz in new[] { false, true })
        {
            var rig = new BehaviourRig(inWorldz ? InWorldz : SecondLife, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.2f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 0.05f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
            rig.Run(h, steps);
            double expected = inWorldz
                ? HalcyonMotorReference.Curve(0.2, 0.05, double.PositiveInfinity, h, steps, 1.0, startIndex: -delay)[steps - 1]
                : Integrate(0.0, 1.0, 0.2, 0.05, 0.0, t);
            Near(expected, rig.Body.AngularVelocity.Z, 2e-4, 2e-5, $"{(inWorldz ? "InWorldz" : "Second Life")} at {rate} Hz, {t:0.000} s");
        }
    }

    // The same motor, the same 2 s, at every rate: the delay ends inside a step at 11 Hz (0.0909 s). Halcyon's ramp is a
    // formula per step, so the speed is not the same at every rate (as the exact solution of Second Life's behaviour is); at each
    // rate it is the stepwise ramp's.
    [Fact]
    public void The_delayed_decay_follows_the_stepwise_ramp_at_every_step_rate()
    {
        double delay = -HalcyonMotorReference.DecayStart(0.2);
        Assert.InRange(delay, 0.0338, 0.0339);
        foreach (double rate in Rates)
        {
            var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.2f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 0.05f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
            int steps = (int)Math.Round(2.0 * rate);
            rig.Run(1.0 / rate, steps);
            double expected = HalcyonMotorReference.Curve(0.2, 0.05, double.PositiveInfinity, 1.0 / rate, steps, 1.0, startIndex: -delay)[steps - 1];
            Near(expected, rig.Body.AngularVelocity.Z, 2e-4, 2e-5, $"speed after 2 s at {rate} Hz");
        }
    }

    private static double Integrate(double v0, double motor, double tm, double td, double delay, double span)
    {
        int n = (int)Math.Round(span / 1e-5);
        double h = span / n, v = v0;
        double F(double time, double vel) => (time < delay ? 1.0 : Math.Exp(-(time - delay) / td)) * (motor - vel) / tm;
        for (int i = 0; i < n; i++)
        {
            double t = i * h;
            double k1 = F(t, v), k2 = F(t + h / 2, v + h / 2 * k1), k3 = F(t + h / 2, v + h / 2 * k2), k4 = F(t + h, v + h * k3);
            v += h / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
        }
        return v;
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. Self-righting: the strength boost and the escalating recovery

    // VehicleDynamics.cs:1272-1274: vehicles other than airplanes have their restoring force multiplied by
    // 1 + (1 + tilt / pi)^4.
    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(0.5, 6.0625)]
    [InlineData(1.0, 17.0)]
    public void The_strength_boost_is_one_plus_one_plus_tilt_over_pi_to_the_fourth(double tiltOverPi, double expected)
        => Assert.Equal(expected, VehicleController.InWorldzAttractorBoost((float)(tiltOverPi * Math.PI)), 4);

    // The first step of a car rolled by 20, 60 or 90 degrees with the attractor at 1 s / efficiency 0.5 turns it by
    // sin(roll) pi e^1.5 h x the boost; an airplane's has no boost.
    [Theory]
    [InlineData(Vehicle.TYPE_CAR, 20.0)]
    [InlineData(Vehicle.TYPE_CAR, 60.0)]
    [InlineData(Vehicle.TYPE_CAR, 90.0)]
    [InlineData(Vehicle.TYPE_BOAT, 60.0)]
    [InlineData(Vehicle.TYPE_AIRPLANE, 60.0)]
    public void The_attractor_is_boosted_for_vehicles_other_than_airplanes(Vehicle type, double rollDegrees)
    {
        foreach (double rate in Rates)
        {
            double h = 1.0 / rate;
            double boost = type == Vehicle.TYPE_AIRPLANE ? 1.0 : 1.0 + Math.Pow(1.0 + rollDegrees * Deg / Math.PI, 4.0);
            var rig = new BehaviourRig(InWorldz, type, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.5f);
            rig.Tilt(rollDegrees, 0);
            rig.Step(h);
            double expected = -Math.Sin(rollDegrees * Deg) * boost * Math.PI * Math.Exp(1.5) * h / 1.0;
            Near(expected, rig.Body.AngularVelocity.X, 1e-3, 1e-6, $"{type} {rollDegrees} degrees at {rate} Hz: roll rate");
        }
    }

    // VehicleDynamics.cs:1288-1302: overturned (tilt at least 0.6 pi) and not improving, the factor grows by 1.3 every
    // step of Halcyon's 15 ms (PhysxScene.cs:55-56); here that is 1.3^(h / 0.015) per step, whatever the rate. A car
    // held on its back (150 degrees, the orientation not allowed to change) for about 0.089 s: 1.3^5.93 = 4.74.
    [Theory]
    [InlineData(11.0, 1)]
    [InlineData(22.5, 2)]
    [InlineData(45.0, 4)]
    [InlineData(90.0, 8)]
    public void An_overturned_vehicle_that_is_not_improving_has_its_strength_grow_by_1_3_per_15_ms(double rate, int steps)
    {
        double h = 1.0 / rate;
        var rig = HeldOnItsBack(h, steps, out List<float> rollRates);
        double expected = Math.Pow(1.3, steps * h / 0.015);
        Near(expected, rig.Car.AttractorEscalation, 1e-5, 0, $"{rate} Hz, {steps} steps");
        // The first step used 1.3^(h / 0.015) of it, with the boost at 150 degrees.
        double boost = 1.0 + Math.Pow(1.0 + 150 * Deg / Math.PI, 4.0);
        double firstPush = -Math.Sin(150 * Deg) * boost * Math.Pow(1.3, h / 0.015) * Math.PI * Math.Exp(3 * 0.6) * h / 1.5;
        Near(firstPush, rollRates[0], 1e-3, 1e-6, $"{rate} Hz: first step's roll rate");
        for (int i = 1; i < rollRates.Count; i++)
            Assert.True(Math.Abs(rollRates[i]) > Math.Abs(rollRates[i - 1]) * 0.999, "the turning force grows while it is held");
    }

    private static BehaviourRig HeldOnItsBack(double h, int steps, out List<float> rollRates, VehicleSettings settings = null, Vehicle type = Vehicle.TYPE_CAR)
    {
        var rig = new BehaviourRig(settings ?? InWorldz, type, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.6f);
        rig.Tilt(150, 0);
        rollRates = new List<float>();
        for (int i = 0; i < steps; i++)
        {
            rig.Car.Step((float)h);
            rollRates.Add(rig.Body.AngularVelocity.X);
            rig.Body.AngularVelocity = Vector3.Zero;   // held: the orientation does not change
        }
        return rig;
    }

    // The factor stops at 10 (ours: Halcyon has no limit, see the code) and falls by 1.1 per 15 ms once the vehicle is
    // improving (tilt below 0.6 pi, or smaller than the step before's), down to 1.
    [Fact]
    public void The_escalation_stops_at_its_limit_and_falls_by_1_1_per_15_ms_when_the_vehicle_improves()
    {
        const double h = 1.0 / 45;
        var rig = HeldOnItsBack(h, 90, out _);   // 2 s on its back
        Assert.Equal(VehicleController.MaxAttractorEscalation, rig.Car.AttractorEscalation);
        // Righted to 100 degrees (under 0.6 pi = 108): the factor falls.
        rig.Tilt(100, 0);
        for (int i = 0; i < 8; i++)
        {
            rig.Car.Step((float)h);
            rig.Body.AngularVelocity = Vector3.Zero;
        }
        Near(10.0 / Math.Pow(1.1, 8 * h / 0.015), rig.Car.AttractorEscalation, 1e-4, 0, "after 8 steps of recovery");
        for (int i = 0; i < 200; i++)
        {
            rig.Car.Step((float)h);
            rig.Body.AngularVelocity = Vector3.Zero;
        }
        Assert.Equal(1f, rig.Car.AttractorEscalation);
    }

    // Airplanes have neither the boost nor the escalation (VehicleDynamics.cs:1272, 1288), and Second Life's choice has
    // neither for any type.
    [Theory]
    [InlineData(Vehicle.TYPE_AIRPLANE, true)]
    [InlineData(Vehicle.TYPE_CAR, false)]
    public void Airplanes_and_the_Second_Life_choice_do_not_escalate(Vehicle type, bool inWorldz)
    {
        var rig = HeldOnItsBack(1.0 / 45, 30, out _, inWorldz ? InWorldz : SecondLife, type);
        Assert.Equal(1f, rig.Car.AttractorEscalation);
    }

    // What a driver notices: a car on its back (150 degrees) has come much further toward upright half a second later.
    [Fact]
    public void An_overturned_car_is_righted_faster_under_InWorldz()
    {
        double Tilt(VehicleSettings s)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1.5f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.6f);
            rig.Tilt(150, 0);
            rig.Run(1.0 / 45, 22);
            return Math.Abs(BehaviourRig.RollAndPitch(rig.Body.Orientation).roll / Deg);
        }
        double iw = Tilt(InWorldz), sl = Tilt(SecondLife);
        Assert.True(iw < sl - 30.0, $"InWorldz {iw:0.0} degrees, Second Life {sl:0.0}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6. The buoyancy adjustment

    // PhysxPrim.cs:2963-2965: 0.977 <= buoyancy < 1 is made 1. The gravity share the controller gives the engine is
    // 1 - buoyancy; the stored VEHICLE_BUOYANCY reads back as set.
    [Theory]
    [InlineData(0.9769f, 0.9769f)]
    [InlineData(0.977f, 1f)]
    [InlineData(0.98f, 1f)]
    [InlineData(0.999f, 1f)]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(-0.3f, -0.3f)]
    public void A_buoyancy_just_under_one_floats_under_InWorldz(float set, float inWorldzBuoyancy)
    {
        foreach (bool inWorldz in new[] { false, true })
        {
            var rig = new BehaviourRig(inWorldz ? InWorldz : SecondLife, Vehicle.TYPE_BALLOON);
            rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, set);
            rig.Step(1.0 / 45);
            float buoyancy = inWorldz ? inWorldzBuoyancy : set;
            Assert.Equal(1f - buoyancy, rig.Body.GravityFactor, 6);
            Assert.Equal(set, rig.Car.GetFloatParam(VehFloatParam.Buoyancy));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The setting and how it combines with VehiclePresets

    private static IConfigSource Source(params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return src;
    }

    private static (VehicleSettings settings, List<string> warnings) Read(params (string key, string value)[] kv)
    {
        var warnings = new List<string>();
        return (JoltConfig.FromConfig(Source(kv), warnings).ToVehicleSettings(), warnings);
    }

    [Fact]
    public void With_no_key_the_choice_is_InWorldz()
    {
        (VehicleSettings s, List<string> warnings) = Read();
        Assert.Empty(warnings);
        Assert.Equal(VehicleBehaviour.InWorldz, s.Behaviour);
        Assert.Equal(VehiclePresetSet.Legacy, s.Presets);
        Assert.Equal(VehicleBehaviour.InWorldz, JoltConfig.FromConfig(null, null).ToVehicleSettings().Behaviour);
        // A controller given no settings at all keeps the documented behaviour: only a region's configuration chooses InWorldz.
        Assert.Equal(VehicleBehaviour.SecondLife, VehicleSettings.Default.Behaviour);
    }

    // The rule: VehiclePresets, where it is set, chooses the preset values; where it is not, VehicleBehaviour does
    // (InWorldz: the InWorldz values, Second Life: the documented ones).
    [Theory]
    [InlineData(null, null, VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy)]
    [InlineData("SecondLife", null, VehicleBehaviour.SecondLife, VehiclePresetSet.Documented)]
    [InlineData("InWorldz", null, VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy)]
    [InlineData("inworldz", null, VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy)]
    [InlineData(null, "legacy", VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy)]
    [InlineData(null, "documented", VehicleBehaviour.InWorldz, VehiclePresetSet.Documented)]
    [InlineData("SecondLife", "legacy", VehicleBehaviour.SecondLife, VehiclePresetSet.Legacy)]
    [InlineData("InWorldz", "documented", VehicleBehaviour.InWorldz, VehiclePresetSet.Documented)]
    [InlineData("InWorldz", "legacy", VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy)]
    public void The_presets_are_VehiclePresets_where_it_is_set_and_follow_the_behaviour_where_it_is_not(
        string behaviour, string presets, VehicleBehaviour expectedBehaviour, VehiclePresetSet expectedPresets)
    {
        var kv = new List<(string, string)>();
        if (behaviour != null) kv.Add(("VehicleBehaviour", behaviour));
        if (presets != null) kv.Add(("VehiclePresets", presets));
        (VehicleSettings s, List<string> warnings) = Read(kv.ToArray());
        Assert.Empty(warnings);
        Assert.Equal(expectedBehaviour, s.Behaviour);
        Assert.Equal(expectedPresets, s.Presets);
    }

    // An unknown value is refused at start-up with a message that names the allowed values, and the default is used.
    [Theory]
    [InlineData("Halcyon")]
    [InlineData("1")]
    [InlineData("")]
    public void An_unknown_value_is_refused_naming_the_allowed_values_and_InWorldz_is_used(string value)
    {
        (VehicleSettings s, List<string> warnings) = Read(("VehicleBehaviour", value));
        Assert.Equal(VehicleBehaviour.InWorldz, s.Behaviour);
        Assert.Equal(VehiclePresetSet.Legacy, s.Presets);
        string warning = Assert.Single(warnings);
        Assert.Contains("VehicleBehaviour", warning);
        Assert.Contains("SecondLife, InWorldz", warning);
        Assert.Contains("using the default InWorldz", warning);
    }

    // A refused VehiclePresets does not count as set: the presets follow the behaviour.
    [Fact]
    public void A_refused_VehiclePresets_leaves_the_presets_to_the_behaviour()
    {
        (VehicleSettings s, List<string> warnings) = Read(("VehicleBehaviour", "InWorldz"), ("VehiclePresets", "bogus"));
        Assert.Single(warnings);
        Assert.Equal(VehiclePresetSet.Legacy, s.Presets);
    }
}
