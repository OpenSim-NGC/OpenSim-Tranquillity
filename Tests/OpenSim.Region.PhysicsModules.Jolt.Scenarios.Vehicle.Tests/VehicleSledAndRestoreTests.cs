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
/// The sled's slope assist as an acceleration (k g sqrt(|sin pitch|) along the nose, inside the motor and friction
/// equation), the same at every rate and equal to what the InWorldz force gave at its 15 ms step. Pure, parallel.
/// </summary>
public class VehicleSledAssistTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };
    private const double G = 9.80665;

    /// <summary>A body that keeps what the controller sets and moves by its velocity, with the engine's share of
    /// gravity applied over each step (no contacts).</summary>
    private sealed class MovingBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -(float)G);
        public float GravityFactor;
        public void SetGravityFactor(float factor) => GravityFactor = factor;
        public bool HasCollision => false;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // A sled pitched nose down by the given angle, held at that pitch, run for 2 s from rest: its velocity along the
    // nose. Only its friction along the nose (30 s, the documented sled's), gravity and the assist act. Speed along
    // the nose without the assist, for reference: g sin(pitch) 30 (1 - e^(-2 / 30)).
    private static double NoseSpeed(double rate, float pitchDeg, float assist)
    {
        var body = new MovingBody { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, pitchDeg * MathF.PI / 180f) };
        var sled = new VehicleController(body) { Settings = new VehicleSettings { SledAssist = assist } };
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        sled.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        sled.ProcessTypeChange(Vehicle.TYPE_SLED);
        sled.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        sled.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);   // hover off (the documented sled's already is)
        sled.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(30f, 1000f, 1000f));
        double h = 1.0 / rate;
        for (int i = 0; i < (int)Math.Round(2.0 * rate); i++)
        {
            sled.Step((float)h);
            body.LinearVelocity += body.Gravity * body.GravityFactor * (float)h;
            body.Position += body.LinearVelocity * (float)h;
            body.AngularVelocity = Vector3.Zero;   // held at its pitch
            now += h;
        }
        return Vector3.Dot(body.LinearVelocity, Vector3.UnitX * body.Orientation);
    }

    [Theory]
    [InlineData(15f)]
    [InlineData(40f)]
    [InlineData(-15f)]   // nose up: a tenth of it, still pushing down the slope (backwards)
    public void The_assist_is_the_same_at_every_rate_and_what_the_old_force_gave_at_15_ms(float pitchDeg)
    {
        // The InWorldz force: 3 g m sqrt(|sin pitch|) times the step, for one step: at 15 ms an acceleration of
        // 3 g 0.015 sqrt(|sin pitch|) (a tenth of it, reversed, nose up). Through friction 30 s for 2 s.
        double sin = Math.Sin(Math.Abs(pitchDeg) * Math.PI / 180.0);
        double oldAt15ms = 3 * G * 0.015 * Math.Sqrt(sin) * (pitchDeg > 0 ? 1 : -0.1);
        double expected = oldAt15ms * 30.0 * (1 - Math.Exp(-2.0 / 30.0));
        Assert.Equal(VehicleSettings.DefaultSledAssist, 3f * 0.015f, 6);
        foreach (double rate in Rates)
        {
            double withAssist = NoseSpeed(rate, pitchDeg, VehicleSettings.DefaultSledAssist);
            double without = NoseSpeed(rate, pitchDeg, 0f);
            Assert.True(Math.Abs(withAssist - without - expected) <= 0.005 * Math.Abs(expected), $"{rate} Hz: assist {withAssist - without:0.00000} against {expected:0.00000}");
        }
    }

    [Fact]
    public void The_key_sets_its_strength()
    {
        double one = NoseSpeed(45.0, 15f, 0.045f) - NoseSpeed(45.0, 15f, 0f);
        double two = NoseSpeed(45.0, 15f, 0.09f) - NoseSpeed(45.0, 15f, 0f);
        Assert.Equal(2.0, two / one, 3);
    }

    // 2 degrees: a pitch sine under pi/64, where the assist does not act.
    [Fact]
    public void Under_the_threshold_pitch_there_is_no_assist()
        => Assert.Equal(NoseSpeed(45.0, 2f, 0f), NoseSpeed(45.0, 2f, 0.045f), 9);

    // The documented sled has hover off: 100 m over the terrain, level, nothing touching, it falls as gravity alone
    // takes it (its vertical friction is 1000 s, off). With the page's hover timescale of 10 s the hover spring toward
    // the terrain (height 0) would pull it down faster.
    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void The_documented_sled_has_no_hover(double rate)
    {
        var body = new MovingBody();
        var sled = new VehicleController(body);
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        sled.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        sled.ProcessTypeChange(Vehicle.TYPE_SLED);
        Assert.Equal(1000f, sled.GetFloatParam(VehFloatParam.HoverTimescale));
        sled.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);   // nothing turns the fall forward
        double h = 1.0 / rate;
        int steps = (int)Math.Round(2.0 * rate);
        for (int i = 0; i < steps; i++)
        {
            sled.Step((float)h);
            body.LinearVelocity += body.Gravity * body.GravityFactor * (float)h;
            body.Position += body.LinearVelocity * (float)h;
            now += h;
        }
        double expected = -G * steps * h;
        Assert.True(Math.Abs(body.LinearVelocity.Z - expected) <= 0.001 * Math.Abs(expected),
            $"{rate} Hz: vz {body.LinearVelocity.Z:0.0000}, free fall {expected:0.0000}");
    }
}

/// <summary>
/// A vehicle restored with its region: the host zeroes a new vehicle's velocity (no linear motor set) over the steps
/// of its first 0.27 s, so a boat that was falling while the region loaded is caught, at any rate. Serial with the
/// other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleRestoreTests
{
    // A legacy boat (hover 0.5 m, timescale 0.2 s) 10 m over the water, already falling at 30 m/s when it is made a
    // vehicle, as one restored with a region after a long load is.
    private static readonly Scenario RestoredBoat = new()
    {
        Name = "restored-boat",
        DefaultDuration = _ => 10f,
        World = _ => (Course.Heightmap(0f, 10f), Course.Water),
        Setup = r =>
        {
            r.AddBox(new Vector3(3f, 1.5f, 0.5f), new Vector3(60f, 128f, Course.Water + 10f), Quaternion.Identity);
            r.Actor.Velocity = new Vector3(0f, 0f, -30f);
            r.MakeVehicle(OpenSim.Region.PhysicsModules.SharedBase.Vehicle.TYPE_BOAT);
        },
    };

    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_boat_falling_when_it_is_restored_is_caught_and_floats(double rate)
    {
        // Restored with the region: made before the region's first step, which is when the host holds a new vehicle still.
        var o = new HarnessOptions { RateHz = rate, WarmUpStep = false, Jolt = { ["VehiclePresets"] = "legacy" } };
        RunResult r = Harness.Harness.Run(RestoredBoat, o);
        // Its fall is stopped at once, and its hover brings it down to the water without going under: at 30 m/s it
        // would reach the water within 0.4 s and plunge far below it.
        Assert.True(r.Samples.All(s => s.Position.Z > Course.Water + 0.3f), $"{rate} Hz: down to {r.Samples.Min(s => s.Position.Z):0.00}");
        Assert.True(r.Samples[0].Velocity.Z > -15f, $"{rate} Hz: first step's vertical speed {r.Samples[0].Velocity.Z:0.00}");
        // And it ends floating at its hover height over the water.
        Assert.InRange(r.Samples[^1].Position.Z, Course.Water + 0.4f, Course.Water + 0.6f);
    }

    [Fact]
    public void The_span_is_a_time_not_a_count_of_steps()
    {
        // The steps that start within 0.27 s: three at 11 Hz (0, 0.091, 0.182), thirteen at 45 Hz (up to 0.267).
        Assert.Equal(0.27f, JoltPrim.ReassertVehicleSeconds);
        foreach ((double rate, int steps) in new[] { (11.0, 3), (45.0, 13), (90.0, 25) })
        {
            float left = JoltPrim.ReassertVehicleSeconds;
            int n = 0;
            while (left > 0f) { n++; left -= (float)(1.0 / rate); }
            Assert.Equal(steps, n);
        }
    }
}
