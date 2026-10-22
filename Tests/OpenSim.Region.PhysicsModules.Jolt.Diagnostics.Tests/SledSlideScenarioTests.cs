/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests;

/// <summary>
/// The sled slide test of the console (`jolt sledtest slide`): the sled is let go at rest on a 15 degree ramp, not dropped
/// onto a slope, and its speed at 1, 2, 3 and 4 s of physics time is read against the controller-level sled scenario within
/// 2%. The layout, the readings and the pass line are SledSlideScenario's; the scene runs here are the console test's
/// steps without a region, at three physics rates, and show that the test passes at each (so it no longer depends on how a
/// landing falls).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SledSlideScenarioTests
{
    private readonly ITestOutputHelper _out;
    public SledSlideScenarioTests(ITestOutputHelper output) { _out = output; }

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;

    [Fact]
    public void The_sled_starts_on_the_ramp_face_not_above_it()
    {
        var (ramp, sled, q) = SledSlideScenario.Layout(128f, 128f, 40f);
        // The sled's position in the ramp's own axes: 20 m up the slope (-x), and its underside the gap above the face.
        Vector3 inRamp = (sled - ramp) * Quaternion.Inverse(q);
        Assert.Equal(-SledSlideScenario.StartUphill, inRamp.X, 4);
        Assert.Equal(0f, inRamp.Y, 4);
        float underside = inRamp.Z - SledSlideScenario.SledSize.Z * 0.5f;
        float face = SledSlideScenario.RampSize.Z * 0.5f;
        Assert.Equal(SledSlideScenario.Gap, underside - face, 4);
        // 15 degrees about y, the box's +x end down the slope.
        Vector3 down = Vector3.UnitX * q;
        Assert.Equal(-Math.Sin(15.0 * Math.PI / 180.0), down.Z, 4);
        Assert.True(down.X > 0f);
    }

    [Fact]
    public void The_ramp_is_long_enough_for_the_four_seconds_and_stays_off_the_ground()
    {
        // The slide covers at most half the last speed times the time (the speed rises about linearly): about 21 m of slope in
        // 4 s. The sled starts 20 m up from the ramp's centre, so 20 m plus half the ramp's length lies below it.
        double distance = 0.5 * SledSlideScenario.Speeds[3] * 4.0;
        double slopeBelowStart = SledSlideScenario.StartUphill + SledSlideScenario.RampSize.X * 0.5;
        Assert.True(slopeBelowStart > distance + 10.0, $"the slide of {distance:0.0} m would run off a ramp with {slopeBelowStart:0.0} m below the start");
        Assert.True(SledSlideScenario.RampHalfDrop() > 8f);
    }

    [Fact]
    public void The_pass_line_is_two_percent_of_the_reference_at_each_reading()
    {
        // Speeds that are exactly the reference at the reading times pass.
        var samples = new List<(double T, double Speed)>();
        for (int i = 0; i < SledSlideScenario.Times.Length; i++)
            samples.Add((SledSlideScenario.Times[i], SledSlideScenario.Speeds[i]));
        samples.Insert(0, (0.0, 0.0));
        Assert.True(SledSlideScenario.Check(samples, out List<string> lines));
        Assert.Equal(4, lines.Count);

        // 1.9% off passes, 2.1% off fails, at each of the four readings.
        for (int i = 0; i < 4; i++)
        {
            var near = new List<(double T, double Speed)>(samples);
            near[i + 1] = (near[i + 1].T, near[i + 1].Speed * 1.019);
            Assert.True(SledSlideScenario.Check(near, out _), $"1.9% off at reading {i} failed");
            var far = new List<(double T, double Speed)>(samples);
            far[i + 1] = (far[i + 1].T, far[i + 1].Speed * 0.979);
            Assert.False(SledSlideScenario.Check(far, out _), $"2.1% off at reading {i} passed");
        }

        // A run that ends before the last reading time does not pass, and an empty one does not either.
        Assert.False(SledSlideScenario.Check(samples.Take(3).ToList(), out _));
        Assert.False(SledSlideScenario.Check(new List<(double, double)>(), out _));
    }

    [Fact]
    public void Readings_between_samples_are_interpolated()
    {
        var samples = new List<(double T, double Speed)> { (0.0, 0.0), (0.9, 9.0), (1.1, 11.0) };
        Assert.Equal(10.0, SledSlideScenario.SpeedAt(samples, 1.0), 6);
        Assert.Equal(11.0, SledSlideScenario.SpeedAt(samples, 5.0), 6);   // past the last sample: the last one
    }

    // ---- The console test's steps, on a scene with no region ----

    [Theory]
    [InlineData(45.0, 2)]
    [InlineData(64.0, 2)]
    [InlineData(32.0, 4)]
    public void The_sled_let_go_on_the_ramp_passes_at_every_rate(double rate, int collisionSteps)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", rate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("PhysicsStepCollisionSteps", collisionSteps.ToString(CultureInfo.InvariantCulture));
        jolt.Set("VehicleBehaviour", "SecondLife");   // the documented sled; the default is InWorldz
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 0f, Heartbeat);
        scene.Simulate(Heartbeat);
        try
        {
            float rampZ = Ground + SledSlideScenario.RampHalfDrop() + 6f;
            var (rampPos, sledPos, tilt) = SledSlideScenario.Layout(128f, 128f, rampZ);
            // As the console test rezzes it: friction 0, which averages with the sled's 0 (SledSlideScenario.Speeds).
            scene.AddPrimShape("ramp", PrimitiveBaseShape.CreateBox(), rampPos, SledSlideScenario.RampSize, tilt, false, 901u).Friction = 0f;
            var sled = Assert.IsType<JoltPrim>(scene.AddPrimShape("sled", PrimitiveBaseShape.CreateBox(), sledPos, SledSlideScenario.SledSize, tilt, true, 902u));
            sled.VehicleType = (int)Vehicle.TYPE_SLED;

            long steps0 = scene.Substeps.Steps;
            double Now() => (scene.Substeps.Steps - steps0) * (double)scene.Substeps.StepSeconds;
            var samples = new List<(double T, double Speed)>();
            while (Now() < SledSlideScenario.Times[^1] + 0.2)
            {
                scene.Simulate(Heartbeat);
                samples.Add((Now(), sled.Velocity.Length()));
            }

            bool pass = SledSlideScenario.Check(samples, out List<string> lines);
            foreach (string line in lines)
                _out.WriteLine($"[sled-slide] {rate} Hz x {collisionSteps} | {line}");
            Assert.True(pass, string.Join("; ", lines));
            // The sled let go at rest keeps to the exact solution far closer than the pass line.
            for (int i = 0; i < SledSlideScenario.Times.Length; i++)
            {
                double got = SledSlideScenario.SpeedAt(samples, SledSlideScenario.Times[i]);
                Assert.InRange(Math.Abs(got - SledSlideScenario.Speeds[i]) / SledSlideScenario.Speeds[i] * 100.0, 0.0, 0.3);
            }
        }
        finally { scene.Dispose(); }
    }
}
