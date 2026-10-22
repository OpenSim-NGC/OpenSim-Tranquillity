/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How fast a tilted airplane rights itself with its vertical attractor alone (timescale 2 s, efficiency 0.9, tilted 30
/// degrees about its x axis, hung weightless, no motor, no banking, no deflection), under each behaviour:
/// Second Life's documented damped spring, with the preset's angular friction acting on the angular velocity the spring
/// builds, and InWorldz's Halcyon attractor worked out step by step. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleRightingTests
{
    private const double Deg = Math.PI / 180.0;
    private const double StartTilt = 30.0;
    private static readonly double[] Marks = { 1, 2, 3, 5 };

    private static VehicleSettings Settings(VehicleBehaviour b, VehiclePresetSet p) => new() { Behaviour = b, Presets = p };

    // The airplane as its preset sets it (attraction timescale 2 s, efficiency 0.9, angular friction), with only the
    // attractor acting besides that friction.
    private static BehaviourRig Airplane(VehicleSettings s, Vector3? angularFriction = null)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);               // the rig turns the preset's friction and the like off; put it back
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f));
        if (angularFriction is Vector3 f)
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, f);
        rig.Tilt(StartTilt, 0);
        return rig;
    }

    private static double TiltOf(Quaternion q)
    {
        Vector3 z = Vector3.UnitZ * q;
        return Math.Acos(Math.Clamp(z.Z, -1f, 1f)) / Deg;
    }

    // The tilt (degrees) at each of Marks.
    private static double[] Measure(BehaviourRig rig, double rate)
    {
        var result = new double[Marks.Length];
        double h = 1.0 / rate;
        int steps = 0;
        for (int i = 0; i < Marks.Length; i++)
        {
            int target = (int)Math.Round(Marks[i] * rate);
            rig.Run(h, target - steps);
            steps = target;
            result[i] = TiltOf(rig.Body.Orientation);
        }
        return result;
    }

    // The documented spring on the roll angle e (radians) with the angular friction Tf on its rate:
    //   e'' = -e / T^2 - 2 z e' / T - e' / Tf      (T the timescale, z the efficiency; friction off at Tf >= 1000)
    // integrated with RK4 at a step far smaller than any timescale in it.
    private static double[] Spring(double timescale, double efficiency, double friction)
    {
        double e = StartTilt * Deg, v = 0, h = 1e-4, t = 0;
        double kf = friction >= 1000 ? 0 : 1.0 / friction;
        (double de, double dv) F(double ee, double vv) => (vv, -ee / (timescale * timescale) - 2 * efficiency * vv / timescale - kf * vv);
        var result = new double[Marks.Length];
        for (int i = 0; i < Marks.Length; i++)
        {
            int n = (int)Math.Round((Marks[i] - t) / h);
            for (int k = 0; k < n; k++)
            {
                var k1 = F(e, v);
                var k2 = F(e + h / 2 * k1.de, v + h / 2 * k1.dv);
                var k3 = F(e + h / 2 * k2.de, v + h / 2 * k2.dv);
                var k4 = F(e + h * k3.de, v + h * k3.dv);
                e += h / 6 * (k1.de + 2 * k2.de + 2 * k3.de + k4.de);
                v += h / 6 * (k1.dv + 2 * k2.dv + 2 * k3.dv + k4.dv);
            }
            t = Marks[i];
            result[i] = e / Deg;
        }
        return result;
    }

    // InWorldz's attractor worked out step by step (VehicleDynamics.cs:1262-1334, 1402-1458; an airplane has no strength
    // boost): each step the roll angular velocity w becomes
    //   w (1 - (e^(z / e) - 1)) + w (e^(-h / Tf) - 1) - sin(e) pi e^(3 z) h / T,
    // the damping share and the friction both taken from the velocity at the start of the step, then the angle moves by
    // the new velocity.
    private static double[] Halcyon(double rate, double timescale, double efficiency, double friction)
    {
        double h = 1.0 / rate, e = StartTilt * Deg, w = 0;
        // Halcyon's damping share is per 15 ms frame; a step of h takes it h / 15 ms times over, compounded.
        double share = 1.0 - Math.Pow(1.0 - Math.Clamp(Math.Exp(efficiency / Math.E) - 1.0, 0, 1), h / 0.015);
        double frictionChange = friction >= 1000 ? 0 : Math.Exp(-h / friction) - 1.0;
        double push = Math.PI * Math.Exp(3 * efficiency) * h / Math.Max(timescale, h);
        var result = new double[Marks.Length];
        int steps = 0;
        for (int i = 0; i < Marks.Length; i++)
        {
            int target = (int)Math.Round(Marks[i] * rate);
            for (; steps < target; steps++)
            {
                w = w - share * w + frictionChange * w - Math.Sin(e) * push;
                e += w * h;
            }
            result[i] = e / Deg;
        }
        return result;
    }

    // Within 20% of the expected tilt, or within `floor` degrees of it (the expected tilt is near zero by 3 s).
    private static void Within(double[] expected, double[] actual, string what, double floor = 0.5, double fraction = 0.2)
    {
        for (int i = 0; i < Marks.Length; i++)
        {
            double tilt = Math.Abs(expected[i]);     // the tilt is an angle from up, so an overshoot past it is still a tilt
            Assert.True(Math.Abs(actual[i] - tilt) <= Math.Max(fraction * tilt, floor),
                $"{what}, {Marks[i]} s: {actual[i]:0.00} degrees against {tilt:0.00} (measured {string.Join(" ", actual.Select(a => a.ToString("0.00")))}; expected {string.Join(" ", expected.Select(a => a.ToString("0.00")))})");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Second Life: the documented spring

    // The attractor alone, no angular friction: the damped spring of the documented timescale and efficiency, at any step rate.
    // (e'' = -e / T^2 - 2 z e' / T: z = 0.9 is just under critical damping; 27.2, 21.7, 15.9 and 7.3 degrees at 1, 2, 3, 5 s.)
    [Theory]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void Second_Life_rights_a_tilted_airplane_as_the_documented_spring_does(double rate)
    {
        double[] expected = Spring(2.0, 0.9, 1000);
        Assert.InRange(expected[0], 27.1, 27.3);
        Assert.InRange(expected[3], 7.2, 7.4);
        Within(expected, Measure(Airplane(Settings(VehicleBehaviour.SecondLife, VehiclePresetSet.Documented), new Vector3(1000f)), rate), $"{rate} Hz");
    }

    // With the documented preset's 20 s angular friction on top, the spring still holds to within the same bounds.
    [Theory]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void Second_Life_with_the_documented_airplane_preset_follows_the_spring_and_its_friction(double rate)
    {
        var rig = Airplane(Settings(VehicleBehaviour.SecondLife, VehiclePresetSet.Documented));
        Assert.Equal(new Vector3(20f), rig.Car.GetVecParam(VehVectorParam.AngularFrictionTimescale));
        Within(Spring(2.0, 0.9, 20.0), Measure(rig, rate), $"{rate} Hz");
    }

    // The slow righting seen in world: the legacy airplane preset's roll friction of 1 s takes the angular velocity the
    // spring builds, so the same attractor rights the plane far more slowly (28, 24, 21 and 16 degrees at 1, 2, 3, 5 s
    // against 27, 22, 16 and 7 without it). The friction is the whole difference: the spring's own motion is the one above.
    [Theory]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void The_legacy_airplane_presets_roll_friction_is_what_slows_the_righting(double rate)
    {
        var rig = Airplane(Settings(VehicleBehaviour.SecondLife, VehiclePresetSet.Legacy));
        Assert.Equal(1f, rig.Car.GetVecParam(VehVectorParam.AngularFrictionTimescale).X);
        double[] slow = Spring(2.0, 0.9, 1.0), free = Spring(2.0, 0.9, 1000);
        Assert.True(slow[3] > free[3] + 7, "the figures this test is about differ by a lot");
        Within(slow, Measure(rig, rate), $"{rate} Hz");
    }

    // ---------------------------------------------------------------------------------------------------------
    // InWorldz: Halcyon's attractor

    // The same airplane under InWorldz's behaviour, with its preset's roll friction of 1 s (the legacy presets) and
    // with none: the tilt follows Halcyon's attractor worked out step by step at the engine's 45 Hz step.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void InWorldz_rights_a_tilted_airplane_as_Halcyons_attractor_does(double rollFriction)
    {
        var s = Settings(VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy);
        double[] measured = Measure(Airplane(s, new Vector3((float)rollFriction, 1000f, 1000f)), 45.0);
        // The reference is Halcyon's step by step law at 45 Hz with its damping share taken per 15 ms of time, and Halcyon at
        // its own 15 ms step beside it, which the 45 Hz run follows to within the difference between two first-order steps.
        double[] expected = Halcyon(45.0, 2.0, 0.9, rollFriction);
        double[] atHalcyonsStep = Halcyon(1.0 / 0.015, 2.0, 0.9, rollFriction);
        Within(atHalcyonsStep, measured, $"friction {rollFriction} against Halcyon at 15 ms", floor: 1.3, fraction: 0.3);
        // The attractor goes dormant once the tilt moves under 0.7 degree a step (VehicleDynamics.cs:1255-1264), which the step
        // by step figures do not model, so it may rest that far from upright.
        Within(expected, measured, $"friction {rollFriction}", floor: 0.7);
        if (rollFriction >= 1000)
        {
            // Halcyon's attractor at 45 Hz with nothing else: about 11, 4 and 1.4 degrees at 1, 2 and 3 s.
            Assert.InRange(expected[0], 10.5, 12.0);
            Assert.InRange(expected[1], 3.5, 4.5);
            Assert.InRange(atHalcyonsStep[0], 11.5, 13.0);
        }
    }

    // Halcyon's attractor is far the stronger: from the same 30 degrees, at 2 s the documented spring has taken the plane
    // to 22 degrees, Halcyon's to 2.
    [Fact]
    public void InWorldz_rights_a_tilted_airplane_far_faster_than_Second_Life()
    {
        var friction = new Vector3(1000f);
        double[] sl = Measure(Airplane(Settings(VehicleBehaviour.SecondLife, VehiclePresetSet.Legacy), friction), 45.0);
        double[] iw = Measure(Airplane(Settings(VehicleBehaviour.InWorldz, VehiclePresetSet.Legacy), friction), 45.0);
        Assert.True(sl[1] > 20 && iw[1] < 4, $"at 2 s: Second Life {sl[1]:0.0}, InWorldz {iw[1]:0.0}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The laws

    // The push of one step: (-y.z, x.z) pi e^(3 z) strength h / T, the pitch part times its share; the timescale held to the step.
    [Fact]
    public void The_attractors_push_is_pi_e_to_the_three_efficiency_times_the_sines_over_the_timescale()
    {
        (float roll, float pitch) = HalcyonVehicleLaws.AttractorPush(0.5f, -0.25f, 0.9f, 2f, 1f / 45, 1f, 1f);
        double baseline = Math.PI * Math.Exp(2.7) / 45 / 2;
        Assert.Equal(-0.5 * baseline, roll, 4);
        Assert.Equal(-0.25 * baseline, pitch, 4);

        (_, float tenth) = HalcyonVehicleLaws.AttractorPush(0.5f, -0.25f, 0.9f, 2f, 1f / 45, 1f, 0.1f);
        Assert.Equal(pitch * 0.1f, tenth, 5);
        (_, float none) = HalcyonVehicleLaws.AttractorPush(0.5f, -0.25f, 0.9f, 2f, 1f / 45, 1f, 0f);
        Assert.Equal(0f, none);

        // A timescale under the step is held to the step: the whole of the baseline pi e^(3 z) at once.
        (float quick, _) = HalcyonVehicleLaws.AttractorPush(1f, 0f, 0f, 0.001f, 0.02f, 1f, 1f);
        Assert.Equal(-(float)Math.PI, quick, 4);

        (float strong, _) = HalcyonVehicleLaws.AttractorPush(0.5f, 0f, 0.9f, 2f, 1f / 45, 3f, 1f);
        Assert.Equal(3 * roll, strong, 4);
    }

    // The share of the angular velocity taken away each step: e^(z / e) - 1, between 0 and 1.
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.2019)]
    [InlineData(0.9, 0.3925)]
    [InlineData(1.0, 0.4447)]
    [InlineData(10.0, 1.0)]
    [InlineData(-1.0, 0.0)]
    public void The_damping_share_is_e_to_the_efficiency_over_e_less_one(double efficiency, double expected)
        => Assert.Equal(expected, HalcyonVehicleLaws.AttractorDampingShare((float)efficiency), 3);
}
