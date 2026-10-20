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
/// Four things of the InWorldz Halcyon vehicle code under [Jolt] VehicleBehaviour = InWorldz, each against figures worked
/// out here from Halcyon's formulas (VehicleDynamics.cs, read, not run), and each left as it was under Second Life's
/// behaviour (the goldens of VehicleSolverUnchangedTests fix that to the bit):
///   a. a value for each axis of the four motor timescales (llSetVehicleVectorParam, codes 30, 31, 34, 35);
///   b. a motor decay timescale held to 1000 s (120 s under Second Life);
///   c. angular deflection as a push per step, with its damping near alignment and its stall cut-off;
///   d. the angular motor's stepwise ramp, with friction added on top of it as its own change of velocity.
/// Pure (a body with no engine), parallel.
/// </summary>
public class VehicleInWorldzSolverTests
{
    private const double Hz45 = 1.0 / 45;

    private static VehicleSettings SecondLife => new VehicleSettings { Behaviour = VehicleBehaviour.SecondLife };
    private static VehicleSettings InWorldz => new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz };

    private static BehaviourRig Rig(VehicleSettings s, Vehicle type = Vehicle.TYPE_CAR, bool gravity = false) => new BehaviourRig(s, type, gravity);

    private static void Near(double expected, double actual, double relative, double absolute, string what)
        => Assert.True(Math.Abs(expected - actual) <= absolute + relative * Math.Abs(expected), $"{what}: {actual:R} against {expected:R}");

    // ---------------------------------------------------------------------------------------------------------
    // a. A value for each axis of the four motor timescales

    private static Vector3 Read(VehicleController car, Vehicle code) => code switch
    {
        Vehicle.LINEAR_MOTOR_TIMESCALE => car.GetVecParam(VehVectorParam.LinearMotorTimescale),
        Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE => car.GetVecParam(VehVectorParam.LinearMotorDecayTimescale),
        Vehicle.ANGULAR_MOTOR_TIMESCALE => car.GetVecParam(VehVectorParam.AngularMotorTimescale),
        Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE => car.GetVecParam(VehVectorParam.AngularMotorDecayTimescale),
        _ => throw new ArgumentException(code.ToString()),
    };

    [Theory]
    [InlineData(Vehicle.LINEAR_MOTOR_TIMESCALE)]
    [InlineData(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE)]
    [InlineData(Vehicle.ANGULAR_MOTOR_TIMESCALE)]
    [InlineData(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE)]
    public void A_vector_sets_each_axis_under_InWorldz_and_is_ignored_under_Second_Life(Vehicle code)
    {
        var iw = Rig(InWorldz);
        var sl = Rig(SecondLife);
        Vector3 slBefore = Read(sl.Car, code);
        iw.Car.ProcessVectorVehicleParam(code, new Vector3(0.2f, 3f, 9f));
        sl.Car.ProcessVectorVehicleParam(code, new Vector3(0.2f, 3f, 9f));
        Assert.Equal(new Vector3(0.2f, 3f, 9f), Read(iw.Car, code));
        Assert.Equal(slBefore, Read(sl.Car, code));
    }

    [Theory]
    [InlineData(Vehicle.LINEAR_MOTOR_TIMESCALE)]
    [InlineData(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE)]
    [InlineData(Vehicle.ANGULAR_MOTOR_TIMESCALE)]
    [InlineData(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE)]
    public void Each_axis_is_held_to_the_range_the_float_setter_holds_all_three_to(Vehicle code)
    {
        var iw = Rig(InWorldz);
        iw.Car.ProcessVectorVehicleParam(code, new Vector3(0.001f, 5000f, -3f));
        Assert.Equal(new Vector3(0.0156f, 1000f, 0.0156f), Read(iw.Car, code));

        // A float on the same code still sets all three.
        iw.Car.ProcessFloatVehicleParam(code, 7f);
        Assert.Equal(new Vector3(7f, 7f, 7f), Read(iw.Car, code));
    }

    [Fact]
    public void A_non_finite_vector_is_refused_as_every_other_parameter_is()
    {
        var iw = Rig(InWorldz);
        iw.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, new Vector3(2f, 3f, 4f));
        iw.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, new Vector3(float.NaN, 3f, 4f));
        Assert.Equal(new Vector3(2f, 3f, 4f), Read(iw.Car, Vehicle.LINEAR_MOTOR_TIMESCALE));
    }

    // The linear motor ramps each axis toward its direction at its own timescale, by Halcyon's stepwise ramp from the 0.04 m/s
    // start (HalcyonLinearReference); the quicker axis is further along. (The earlier figures were the documented equation's.)
    [Fact]
    public void The_linear_motor_follows_the_timescale_of_each_axis()
    {
        var rig = Rig(InWorldz);
        var tm = new Vector3(3f, 4f, 5f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, tm);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, new Vector3(1000f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(5f, 5f, 5f));
        var want = new HalcyonLinearReference(new double[] { 5, 5, 5 }, new double[] { 3, 4, 5 }, new double[] { 1000, 1000, 1000 });
        for (int i = 0; i < 90; i++)   // 2 s
        {
            rig.Step(Hz45);
            want.Step(Hz45);
        }
        Near(want.V[0], rig.Body.LinearVelocity.X, 0.002, 0.003, "x");
        Near(want.V[1], rig.Body.LinearVelocity.Y, 0.002, 0.003, "y");
        Near(want.V[2], rig.Body.LinearVelocity.Z, 0.002, 0.003, "z");
        Assert.True(rig.Body.LinearVelocity.X > rig.Body.LinearVelocity.Y && rig.Body.LinearVelocity.Y > rig.Body.LinearVelocity.Z);
    }

    [Fact]
    public void The_angular_motor_turns_the_quick_axis_faster_than_the_slow_one()
    {
        var rig = Rig(InWorldz);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, new Vector3(0.3f, 3f, 3f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, new Vector3(1000f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(1f, 0f, 1f));
        rig.Run(Hz45, 45);
        Assert.True(rig.Body.AngularVelocity.X > 2 * rig.Body.AngularVelocity.Z, $"{rig.Body.AngularVelocity.X} against {rig.Body.AngularVelocity.Z}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // b. The decay timescale cap: 1000 s under InWorldz, 120 s under Second Life

    [Theory]
    [InlineData(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE)]
    [InlineData(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE)]
    public void A_float_decay_timescale_is_held_to_1000_s_under_InWorldz_and_120_s_under_Second_Life(Vehicle code)
    {
        var iw = Rig(InWorldz);
        var sl = Rig(SecondLife);
        iw.Car.ProcessFloatVehicleParam(code, 500f);
        sl.Car.ProcessFloatVehicleParam(code, 500f);
        Assert.Equal(new Vector3(500f), Read(iw.Car, code));
        Assert.Equal(new Vector3(120f), Read(sl.Car, code));
        iw.Car.ProcessFloatVehicleParam(code, 5000f);
        Assert.Equal(new Vector3(1000f), Read(iw.Car, code));
    }

    private static VehicleSettings Configured(params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        var warnings = new List<string>();
        VehicleSettings s = JoltConfig.FromConfig(src, warnings).ToVehicleSettings();
        Assert.Empty(warnings);
        return s;
    }

    [Fact]
    public void The_region_setting_decides_the_cap_when_it_is_set_and_the_behaviour_when_it_is_not()
    {
        Assert.Equal(1000f, Configured().EffectiveMaxDecayTimescale);   // the shipped default is InWorldz
        Assert.Equal(120f, Configured(("VehicleBehaviour", "SecondLife")).EffectiveMaxDecayTimescale);
        Assert.Equal(1000f, Configured(("VehicleBehaviour", "InWorldz")).EffectiveMaxDecayTimescale);
        Assert.Equal(60f, Configured(("VehicleBehaviour", "InWorldz"), ("VehicleMaxDecayTimescale", "60")).EffectiveMaxDecayTimescale);
        Assert.Equal(120f, Configured(("VehicleBehaviour", "InWorldz"), ("VehicleMaxDecayTimescale", "120")).EffectiveMaxDecayTimescale);
        Assert.Equal(300f, Configured(("VehicleBehaviour", "SecondLife"), ("VehicleMaxDecayTimescale", "300")).EffectiveMaxDecayTimescale);
    }

    // A motor with a 60 s timescale and a decay of 500 s. Under Second Life: A = (Td / Tm)(1 - e^(-t / Td)), v = M (1 - e^(-A)),
    // the decay held to 120 s. Under InWorldz the motor is Halcyon's ramp, blended with the velocity by the grip e^(-s / Td), and
    // the grip is dropped once it falls under 0.002 (at 6.2 Td: 746 s for a decay of 120 s, 3100 s for 500 s). Its ramp works from
    // the target, so the grip matters only when it is nearly gone: with a friction of 50 s the vehicle is held at its speed while
    // the motor lives (500 s: 4.8 m/s at 900 s) and has lost it by 900 s once the motor is gone (120 s: 0.2 m/s).
    [Fact]
    public void A_500_s_decay_keeps_the_motor_pulling_longer_under_InWorldz()
    {
        double Velocity(VehicleSettings s, double seconds, bool friction)
        {
            var rig = Rig(s);
            rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 60f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 500f);
            if (friction)
                rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(50f, 1000f, 1000f));
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(5f, 0f, 0f));
            rig.Run(0.1, (int)(seconds / 0.1));
            return rig.Body.LinearVelocity.X;
        }
        double Reference(double td, double seconds)
        {
            var want = new HalcyonLinearReference(new double[] { 5, 0, 0 }, new double[] { 60, 60, 60 }, new double[] { td, td, td })
            {
                Friction = new double[] { 50, 1000, 1000 },
            };
            for (int i = 0; i < (int)(seconds / 0.1); i++)
                want.Step(0.1);
            return want.V[0];
        }
        double Expected(double td) => 5.0 * (1 - Math.Exp(-(td / 60.0) * (1 - Math.Exp(-100.0 / td))));
        double iw = Velocity(InWorldz, 900, friction: true);
        Near(Reference(500, 900), iw, 0.002, 0.003, "InWorldz, decay 500 s");
        Assert.True(iw > 4.0, $"{iw}");
        Assert.True(Reference(120, 900) < 0.5, $"{Reference(120, 900)} with a decay of 120 s");
        // Under Second Life the 120 s cap holds, as before.
        Near(Expected(120), Velocity(SecondLife, 100, friction: false), 0.01, 0, "Second Life");
    }

    // ---------------------------------------------------------------------------------------------------------
    // c. Angular deflection as Halcyon computes it

    // A plane moving (20, 5, 0) with its nose along x, deflection 0.5 s / 1, at 45 Hz: one step pushes the angular velocity
    // about z by speedShare x pi x (h / T) x efficiency x ln(1 + angle), with speedShare = speed / 30 and angle the
    // angle between the nose and the velocity, 14.04 degrees (the Euler angles of the rotation are (0, 0, angle), whose
    // direction is z). Second Life's law turns the nose by a share of the angle instead (a rate of about 0.33 rad/s here).
    private static BehaviourRig Plane(VehicleSettings s, Vector3 velocity, float timescale, float efficiency)
    {
        var rig = Rig(s, Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, timescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, efficiency);
        rig.Body.LinearVelocity = velocity;
        return rig;
    }

    [Fact]
    public void One_step_pushes_the_angular_velocity_by_the_formula()
    {
        var rig = Plane(InWorldz, new Vector3(20f, 5f, 0f), 0.5f, 1f);
        rig.Car.Step((float)Hz45);
        double angle = Math.Atan2(5, 20);
        double speedShare = Math.Sqrt(20 * 20 + 5 * 5) / 30.0;
        double push = speedShare * Math.PI * (Hz45 / 0.5) * 1.0 * Math.Log(1 + angle);
        Near(push, rig.Body.AngularVelocity.Z, 1e-4, 0, "push about z");
        Assert.Equal(0f, rig.Body.AngularVelocity.X);
        Assert.Equal(0f, rig.Body.AngularVelocity.Y);

        var sl = Plane(SecondLife, new Vector3(20f, 5f, 0f), 0.5f, 1f);
        sl.Car.Step((float)Hz45);
        Assert.True(sl.Body.AngularVelocity.Z > 10 * rig.Body.AngularVelocity.Z, "Second Life's law is a rate, not a push");
    }

    // Velocity up and ahead: the rotation from the nose is about -y, whose Euler angles point along -y, so the push raises the nose.
    [Fact]
    public void A_climbing_velocity_pushes_the_nose_up()
    {
        var rig = Plane(InWorldz, new Vector3(20f, 0f, 5f), 0.5f, 1f);
        rig.Car.Step((float)Hz45);
        double angle = Math.Atan2(5, 20);
        double push = Math.Sqrt(20 * 20 + 5 * 5) / 30.0 * Math.PI * (Hz45 / 0.5) * Math.Log(1 + angle);
        Near(-push, rig.Body.AngularVelocity.Y, 1e-4, 0, "push about y");
        Assert.Equal(0f, rig.Body.AngularVelocity.X);
        Assert.Equal(0f, rig.Body.AngularVelocity.Z);
    }

    // Moving backwards along the nose: "if the local X movement is negative, flip the angle", pi minus it. The tail is what turns
    // toward the velocity: here the velocity is 14.04 degrees from the tail, toward +y, so the push is about -z (the tail, on -x, turns
    // toward +y by going clockwise seen from above). InWorldz keeps the nose's rotation for the direction and so pushes about +z.
    [Fact]
    public void Moving_backwards_uses_pi_minus_the_angle_and_turns_the_tail_toward_the_velocity()
    {
        var rig = Plane(InWorldz, new Vector3(-20f, 5f, 0f), 0.5f, 1f);
        rig.Car.Step((float)Hz45);
        double angle = Math.Atan2(5, 20);   // between the tail and the velocity; pi minus the nose's angle to it
        double push = Math.Sqrt(20 * 20 + 5 * 5) / 30.0 * Math.PI * (Hz45 / 0.5) * Math.Log(1 + angle);
        Near(-push, rig.Body.AngularVelocity.Z, 1e-4, 0, "push about z");
    }

    // Within pi / 64 of aligned a damping term takes part of the angular velocity away:
    // w x (h / T) x efficiency x ln(1 + pi - angle) / ln(1 + pi).
    [Fact]
    public void Near_alignment_the_push_is_damped()
    {
        var rig = Plane(InWorldz, new Vector3(20f, 0.3f, 0f), 0.5f, 1f);
        rig.Body.AngularVelocity = new Vector3(0f, 0f, 1f);
        rig.Car.Step((float)Hz45);
        double angle = Math.Atan2(0.3, 20);
        Assert.True(angle < Math.PI / 64);
        double timepct = Hz45 / 0.5;
        double push = Math.Sqrt(20 * 20 + 0.3 * 0.3) / 30.0 * Math.PI * timepct * Math.Log(1 + angle);
        double damping = 1.0 * timepct * Math.Log(1 + Math.PI - angle) / Math.Log(1 + Math.PI);
        Near(1.0 + push - damping, rig.Body.AngularVelocity.Z, 1e-4, 0, "angular velocity about z");
    }

    [Fact]
    public void Deflection_is_off_below_the_deflection_speed_and_at_a_1000_s_timescale()
    {
        var slow = Plane(InWorldz, new Vector3(0.15f, 0.05f, 0f), 0.5f, 1f);
        slow.Car.Step((float)Hz45);
        Assert.Equal(Vector3.Zero, slow.Body.AngularVelocity);

        var off = Plane(InWorldz, new Vector3(20f, 5f, 0f), 1000f, 1f);
        off.Car.Step((float)Hz45);
        Assert.Equal(Vector3.Zero, off.Body.AngularVelocity);

        var none = Plane(InWorldz, new Vector3(20f, 5f, 0f), 0.5f, 0f);
        none.Car.Step((float)Hz45);
        Assert.Equal(Vector3.Zero, none.Body.AngularVelocity);
    }

    // A push under pi / 512 is not applied: a slow plane with a long timescale.
    [Fact]
    public void A_push_under_the_threshold_is_not_applied()
    {
        var rig = Plane(InWorldz, new Vector3(1f, 0.2f, 0f), 20f, 1f);
        rig.Car.Step((float)Hz45);
        Assert.Equal(Vector3.Zero, rig.Body.AngularVelocity);
    }

    // Held in place while its velocity says it moves (jammed against something): once the position's smoothed rate falls
    // under half the speed the deflection is cut, as Halcyon cuts it while the linear motor is stalled. A moving body keeps being pushed.
    [Fact]
    public void A_vehicle_that_goes_nowhere_is_not_deflected()
    {
        var jammed = Plane(InWorldz, new Vector3(20f, 5f, 0f), 0.5f, 1f);
        float firstPush = 0, lastPush = 0;
        for (int i = 0; i < 90; i++)
        {
            jammed.Car.Step((float)Hz45);                 // no Advance: the body does not move
            lastPush = jammed.Body.AngularVelocity.Z;     // the push of this step on its own
            if (i == 0)
                firstPush = lastPush;
            jammed.Body.AngularVelocity = Vector3.Zero;
        }
        Assert.True(firstPush > 0.01f, $"{firstPush}");
        Assert.Equal(0f, lastPush);

        var moving = Plane(InWorldz, new Vector3(20f, 5f, 0f), 0.5f, 1f);
        float delta = 0;
        for (int i = 0; i < 90; i++)
        {
            moving.Step(Hz45);
            delta = moving.Body.AngularVelocity.Z;
            moving.Body.AngularVelocity = Vector3.Zero;
            moving.Body.Orientation = Quaternion.Identity;   // keep the nose where it was, to push the same way each step
        }
        Assert.True(delta > 0.01f, $"{delta}");
    }

    // The pushes accumulate while the nose is off the heading: a plane given the push every step swings toward its velocity.
    [Fact]
    public void The_nose_swings_toward_the_velocity()
    {
        var rig = Plane(InWorldz, new Vector3(20f, 0f, 0f), 0.3f, 1f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1f));
        rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);    // yawed 29 degrees off the heading
        rig.Run(Hz45, 180);   // 4 s
        Vector3 nose = Vector3.UnitX * rig.Body.Orientation;
        Assert.True(Math.Abs(Math.Atan2(nose.Y, nose.X)) < 0.25, $"nose {nose}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // d. The angular motor's stepwise ramp

    [Fact]
    public void The_growth_rate_follows_Halcyons_cases()
    {
        // Same signs: ln(|end| / |start|) / steps.
        Near(Math.Log(1.0 / 0.25) / 10, HalcyonVehicleLaws.GrowthRate(0.25f, 1f, 10f), 1e-6, 0, "same sign");
        Near(Math.Log(1.0 / 0.25) / 10, HalcyonVehicleLaws.GrowthRate(-0.25f, -1f, 10f), 1e-6, 0, "both negative");
        // From zero: ln(1 + |end|) / steps. Toward zero: -ln(1 + |start|) / steps.
        Near(Math.Log(2.0) / 10, HalcyonVehicleLaws.GrowthRate(0f, 1f, 10f), 1e-6, 0, "from zero");
        Near(-Math.Log(1.5) / 10, HalcyonVehicleLaws.GrowthRate(0.5f, 0f, 10f), 1e-6, 0, "toward zero");
        Assert.Equal(0f, HalcyonVehicleLaws.GrowthRate(0f, 0f, 10f));
        // Across zero: -ln(1 + |start - end|) / steps over 0.3, the crossover sentinel at or under it.
        Near(-Math.Log(1.0 + 1.5) / 10, HalcyonVehicleLaws.GrowthRate(0.5f, -1f, 10f), 1e-6, 0, "across zero");
        Assert.Equal(VehicleLimits.ThresholdInverseCrossover, HalcyonVehicleLaws.GrowthRate(0.2f, -1f, 10f));
    }

    [Fact]
    public void The_decay_share_follows_Halcyons_clock()
    {
        Assert.Equal(1f, HalcyonVehicleLaws.MovementExpDecay(-0.04f, 2f));
        Assert.Equal(1f, HalcyonVehicleLaws.MovementExpDecay(0f, 2f));
        Near(Math.Exp(-1.5 / 3.0), HalcyonVehicleLaws.MovementExpDecay(1.5f, 3f), 1e-6, 0, "e^(-t/T)");
        Assert.Equal(0f, HalcyonVehicleLaws.MovementExpDecay(20f, 2f));          // under 0.002
        Assert.Equal(0f, HalcyonVehicleLaws.MovementExpDecay(float.PositiveInfinity, 2f));
    }

    private static double[] ControllerCurve(VehicleSettings s, float tm, float td, float tf, int steps)
    {
        var rig = Rig(s, Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, tf));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, tm);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, td);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        var curve = new double[steps];
        for (int i = 0; i < steps; i++)
        {
            rig.Step(Hz45);
            curve[i] = rig.Body.AngularVelocity.Z;
        }
        return curve;
    }

    // The probe's own measure: a curve departs when its peak is more than 20 % from the emulation's, or when it is more than
    // 20 % of that peak away from it at any sample.
    public static IEnumerable<object[]> MotorRuns() => new[]
    {
        new object[] { 0.1f, 0.5f, 0.5f }, new object[] { 0.1f, 5f, 0.5f }, new object[] { 0.1f, 120f, 0.5f },
        new object[] { 1f, 0.5f, 0.5f }, new object[] { 1f, 5f, 0.5f }, new object[] { 1f, 120f, 0.5f },
        new object[] { 5f, 0.5f, 0.5f }, new object[] { 5f, 5f, 0.5f }, new object[] { 5f, 120f, 0.5f },
        new object[] { 1f, 5f, 10f },
    };

    [Theory]
    [MemberData(nameof(MotorRuns))]
    public void The_angular_motor_curve_follows_the_Halcyon_emulation(float tm, float td, float tf)
    {
        int steps = 450;   // 10 s at 45 Hz
        double[] iw = ControllerCurve(InWorldz, tm, td, tf, steps);
        double[] expected = HalcyonMotorReference.Curve(tm, td, tf, Hz45, steps, startIndex: HalcyonMotorReference.DecayStart(tm));
        double peakExpected = expected.Max();
        double deviation = iw.Zip(expected, (a, b) => Math.Abs(a - b)).Max() / peakExpected;
        double peakDeviation = Math.Abs(iw.Max() - peakExpected) / peakExpected;
        Assert.True(deviation <= 0.20, $"curve departs by {deviation:P0} (peak {iw.Max():F3} against {peakExpected:F3})");
        Assert.True(peakDeviation <= 0.20, $"peak {iw.Max():F3} against {peakExpected:F3}");
    }

    // Halcyon's motor sets the velocity and friction is a change on top, so a friction timescale shorter than the motor's does
    // not hold the motor down; the documented equation (Second Life's behaviour) settles at M Tf / (Tf + Tm).
    [Fact]
    public void A_short_friction_does_not_hold_the_InWorldz_motor_down()
    {
        double[] iw = ControllerCurve(InWorldz, 1f, 5f, 0.5f, 450);
        double[] sl = ControllerCurve(SecondLife, 1f, 5f, 0.5f, 450);
        Assert.True(iw.Max() > 2 * sl.Max(), $"InWorldz {iw.Max():F3}, Second Life {sl.Max():F3}");
        // The documented equation never gets past the full-grip settle, M Tf / (Tf + Tm) = 1/3 here (the decay brings it lower).
        Assert.True(sl.Max() < 0.5 / (0.5 + 1.0), $"Second Life {sl.Max():F3}");
        Assert.True(iw.Max() > 0.75, $"InWorldz {iw.Max():F3}");
    }

    // Halcyon never took a step longer than its shortest timescale; at 45 Hz a timescale of 0.0156 s is shorter than a
    // step, so the ramp's steps per timescale are held to 1 and the motor does not overshoot its direction.
    [Fact]
    public void A_motor_quicker_than_the_step_does_not_overshoot_its_direction()
    {
        double[] iw = ControllerCurve(InWorldz, 0.0156f, 1000f, 1000f, 90);
        Assert.True(iw.Max() <= 1.0 + 1e-3, $"peak {iw.Max():R}");
        Assert.True(iw.Max() > 0.9, $"peak {iw.Max():R}");
    }

    // The motor's decay clock runs from the set: with a decay of 1 s the motor's effect is gone by about 6.2 s (the share
    // under 0.002), and friction alone ends the spin.
    [Fact]
    public void The_motor_ends_with_its_decay()
    {
        var rig = Rig(InWorldz, Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 1f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        rig.Run(Hz45, 450);
        float settled = rig.Body.AngularVelocity.Z;
        rig.Run(Hz45, 90);
        Assert.Equal(settled, rig.Body.AngularVelocity.Z);   // no friction, no motor: nothing changes it
        Assert.True(settled > 0.1f && settled < 1f, $"{settled}");
    }

    // A new direction after the motor has been decaying: the target the ramp works from is scaled by the decay since.
    [Fact]
    public void A_new_direction_after_a_decay_starts_from_the_decayed_target()
    {
        var rig = Rig(InWorldz, Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 2f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        rig.Run(Hz45, 90);   // 2 s: the motor has reached its direction and decayed to e^-1
        float w = rig.Body.AngularVelocity.Z;
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));   // set again
        rig.Run(Hz45, 45);
        // The motor pulls again toward the direction, so the velocity rises above what it was.
        Assert.True(rig.Body.AngularVelocity.Z > w, $"{rig.Body.AngularVelocity.Z} against {w}");
    }

    // TORQUE_WORLD_Z: the third axis of the motor is the world's z, as under Second Life.
    [Fact]
    public void With_TORQUE_WORLD_Z_the_motor_turns_the_body_about_the_world_z()
    {
        var rig = Rig(InWorldz, Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessVehicleFlags(0x80000, remove: false);   // a script's number for VEHICLE_FLAG_TORQUE_WORLD_Z
        rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);   // pitched
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 100f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        rig.Run(Hz45, 45);
        Assert.True(rig.Body.AngularVelocity.Z > 0.3f, $"{rig.Body.AngularVelocity}");
        Near(0, rig.Body.AngularVelocity.X, 0, 1e-3, "no x");
        Near(0, rig.Body.AngularVelocity.Y, 0, 1e-3, "no y");
    }
}
