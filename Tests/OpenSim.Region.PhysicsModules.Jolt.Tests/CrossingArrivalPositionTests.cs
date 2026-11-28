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
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An object that crosses into the region arrives with the position and velocity the region it came from took at the
/// border, some time ago (the hand-over takes tens of milliseconds). The engine's first heartbeat after it arrives used to
/// move it for the whole heartbeat, as if it had been in the region all that time, so a viewer that kept extrapolating it
/// at its old velocity saw it jump forward when its first update from the new region came. Told how old its sample is
/// (PhysicsActor.CrossedIn), the object moves only as far as the time that has really passed: no further, and no less
/// when the hand-over took longer than a heartbeat. The correction never puts it back across the border it came over.
///
/// The region runs at the default physics rate (45 Hz steps, four to a heartbeat of 1/11 s), and the arrival clock is the
/// test's: the heartbeat runs "now", 2 ms after the object was taken in. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class CrossingArrivalPositionTests
{
    private readonly ITestOutputHelper _out;
    public CrossingArrivalPositionTests(ITestOutputHelper output) { _out = output; }

    private const float Heartbeat = StepRateRig.Heartbeat;
    private const float Height = 40f;   // well above the 25 m ground: nothing touches the box
    private const float Y = 128f;
    private const double TakenInAfter = 0.002;   // the heartbeat runs this long after the object was taken in

    /// <summary>A region (256 m, flat ground at 25 m) at <paramref name="rate"/> physics steps a second (0: one step per
    /// heartbeat), with a weightless 2 x 1 x 0.5 m box placed and given its velocity, as an arriving object is.</summary>
    private sealed class Arrival : IDisposable
    {
        public readonly JoltScene Scene;
        public double Now = 100.0;
        public readonly JoltPrim Prim;

        public Arrival(float x, Vector3 velocity, float sampleAgeSeconds, float rate = 45f, bool crossIn = true)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("physics", "Jolt");
            startup.Set("meshing", "Meshmerizer");
            IConfig jolt = config.AddConfig("Jolt");
            jolt.Set("PhysicsStepRate", rate.ToString(CultureInfo.InvariantCulture));
            MaterialPins.Apply(jolt);
            Scene = new JoltScene();
            Scene.Initialise(config);
            var heights = new float[StepRateRig.Size * StepRateRig.Size];
            Array.Fill(heights, StepRateRig.Ground);
            Scene.InitialiseWithoutScene("Test Region", StepRateRig.Size, StepRateRig.Size, heights, 0f, CrossingArrivalPositionTests.Heartbeat);
            Scene.Simulate(CrossingArrivalPositionTests.Heartbeat);
            Scene.ArrivalClock = () => Now;

            Prim = Assert.IsType<JoltPrim>(Scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), new Vector3(x, Y, Height),
                new Vector3(2f, 1f, 0.5f), Quaternion.Identity, true, 900u));
            Prim.Density = 1000f;
            Prim.Buoyancy = 1f;   // weightless: it keeps its height and speed
            Prim.Velocity = velocity;
            if (crossIn)
                Prim.CrossedIn(sampleAgeSeconds);
        }

        public void Heartbeat()
        {
            Now += TakenInAfter;
            Scene.Simulate(CrossingArrivalPositionTests.Heartbeat);
            Now += CrossingArrivalPositionTests.Heartbeat - TakenInAfter;
        }

        public void Dispose() => Scene.Dispose();
    }

    [Theory]
    [InlineData(10f, 0.028f)]   // the median hand-over in one process (CrossAsync to the destination's scene)
    [InlineData(30f, 0.028f)]
    [InlineData(60f, 0.028f)]
    [InlineData(30f, 0.150f)]   // a slower hand-over than a heartbeat, as between two simulators
    public void A_crossing_object_arrives_where_its_velocity_and_the_time_elapsed_put_it(float speed, float sampleAge)
    {
        const float arrivedAt = 0.61f;
        using var a = new Arrival(arrivedAt, new Vector3(speed, 0f, 0f), sampleAge);

        a.Heartbeat();

        float expected = arrivedAt + speed * (float)(sampleAge + TakenInAfter);
        float x = a.Prim.Position.X;
        _out.WriteLine($"{speed} m/s, sample {sampleAge * 1000:0} ms old: x {x:0.000}, expected {expected:0.000}, error {x - expected:+0.000;-0.000} m");
        Assert.InRange(x, expected - 0.05f, expected + 0.05f);
        Assert.InRange(a.Prim.Velocity.X, speed - 0.5f, speed + 0.5f);   // it keeps going at its own speed
        Assert.False(a.Prim.HasLeftRegion);
    }

    [Fact]
    public void With_one_physics_step_per_heartbeat_the_arrival_is_placed_the_same_way()
    {
        const float arrivedAt = 0.61f, speed = 30f, sampleAge = 0.028f;
        using var a = new Arrival(arrivedAt, new Vector3(speed, 0f, 0f), sampleAge, rate: 0f);

        a.Heartbeat();

        float expected = arrivedAt + speed * (float)(sampleAge + TakenInAfter);
        _out.WriteLine($"one step per heartbeat: x {a.Prim.Position.X:0.000}, expected {expected:0.000}");
        Assert.InRange(a.Prim.Position.X, expected - 0.05f, expected + 0.05f);
    }

    [Fact]
    public void An_arrival_whose_first_heartbeat_comes_more_than_a_second_later_is_left_where_the_engine_put_it()
    {
        // A region that stalled for 1.5 s after taking the object in: the sample is too old to place the object by, and a
        // body moved for one heartbeat must not be carried on for many.
        const float arrivedAt = 0.61f, speed = 30f;
        using var a = new Arrival(arrivedAt, new Vector3(speed, 0f, 0f), 0.028f);

        a.Now += 1.5;
        a.Heartbeat();

        float engine = arrivedAt + speed * 4f / 45f;
        _out.WriteLine($"late first heartbeat: x {a.Prim.Position.X:0.000}, engine's step {engine:0.000}");
        Assert.InRange(a.Prim.Position.X, engine - 0.05f, engine + 0.05f);
    }

    // Leaving: the body waits no further than 2 m past the edge, whatever its speed, and reports how far that cut it back.
    // The position a crossing sends is where it waits, so a region that does not read the cut gets the position it always
    // did. One heartbeat at 45 Hz covers 4/45 s: from 0.1 m inside, 30 m/s ends 2.57 m out and 60 m/s 5.23 m out.
    [Theory]
    [InlineData(255.9f, 30f, 258f)]
    [InlineData(255.9f, 60f, 258f)]
    [InlineData(0.1f, -60f, -2f)]
    public void A_fast_object_leaving_waits_two_metres_out_and_reports_how_far_it_was_cut_back(float from, float speed, float waits)
    {
        using var a = new Arrival(from, new Vector3(speed, 0f, 0f), 0f, crossIn: false);

        a.Heartbeat();

        float reached = from + speed * 4f / 45f;
        _out.WriteLine($"{speed} m/s leaving: waits at x {a.Prim.Position.X:0.000}, cut {a.Prim.CrossingCutOffset.X:0.000}, its step reached {reached:0.000}");
        Assert.True(a.Prim.HasLeftRegion);
        Assert.InRange(a.Prim.Position.X, waits - 0.001f, waits + 0.001f);
        Assert.InRange(a.Prim.CrossingCutOffset.X, reached - waits - 0.05f, reached - waits + 0.05f);
        Assert.Equal(0f, a.Prim.CrossingCutOffset.Y);
        Assert.Equal(0f, a.Prim.CrossingCutOffset.Z);
    }

    [Fact]
    public void An_object_leaving_within_two_metres_has_nothing_cut()
    {
        using var a = new Arrival(255.9f, new Vector3(10f, 0f, 0f), 0f, crossIn: false);

        a.Heartbeat();

        Assert.True(a.Prim.HasLeftRegion);
        Assert.InRange(a.Prim.Position.X, 255.9f + 10f * 4f / 45f - 0.05f, 255.9f + 10f * 4f / 45f + 0.05f);
        Assert.Equal(Vector3.Zero, a.Prim.CrossingCutOffset);
    }

    [Fact]
    public void After_a_failed_crossing_the_object_reports_no_cut()
    {
        using var a = new Arrival(255.9f, new Vector3(60f, 0f, 0f), 0f, crossIn: false);
        a.Heartbeat();
        Assert.True(a.Prim.CrossingCutOffset.X > 3f, $"cut {a.Prim.CrossingCutOffset.X:0.000} while waiting");

        a.Prim.CrossingFailure();

        Assert.Equal(Vector3.Zero, a.Prim.CrossingCutOffset);
    }

    [Theory]
    [InlineData(0.028f)]
    [InlineData(0.150f)]
    public void A_60_mps_object_cut_back_at_the_edge_arrives_where_it_really_is(float sampleAge)
    {
        // The engine's two ends, side by side in one process: the first region's waiting place, as a position in the second
        // region, plus the cut is where the second one's body is put. The test adds them itself; that a region does so with
        // what it is sent is covered by CrossingSampleAgeTests.
        const float from = 255.9f, speed = 60f;
        using var leaving = new Arrival(from, new Vector3(speed, 0f, 0f), 0f, crossIn: false);
        leaving.Heartbeat();
        float sent = leaving.Prim.Position.X - StepRateRig.Size;
        float cut = leaving.Prim.CrossingCutOffset.X;
        Assert.InRange(sent, 2f - 0.001f, 2f + 0.001f);

        using var arriving = new Arrival(sent + cut, new Vector3(speed, 0f, 0f), sampleAge);
        arriving.Heartbeat();

        float expected = from + speed * 4f / 45f - StepRateRig.Size + speed * (float)(sampleAge + TakenInAfter);
        float x = arriving.Prim.Position.X;
        _out.WriteLine($"sample {sampleAge * 1000:0} ms old: sent {sent:0.000}, cut {cut:0.000}, x {x:0.000}, true position {expected:0.000}, error {x - expected:+0.000;-0.000} m");
        Assert.InRange(x, expected - 0.05f, expected + 0.05f);
    }

    [Fact]
    public void A_slow_vehicle_that_just_crossed_in_moves_on_by_the_time_elapsed_and_never_back_across()
    {
        const float arrivedAt = 0.2f;   // the closest to the border an arriving object is placed (GetObjectDestination)
        using var a = new Arrival(arrivedAt, new Vector3(0.5f, 0f, 0f), 0f);

        a.Heartbeat();
        float first = a.Prim.Position.X;
        _out.WriteLine($"after the first heartbeat x {first:0.0000}");
        Assert.InRange(first, arrivedAt + 0.5f * (float)TakenInAfter - 0.01f, arrivedAt + 0.5f * (float)TakenInAfter + 0.01f);

        float last = first;
        for (int i = 0; i < 30; i++)
        {
            a.Heartbeat();
            float x = a.Prim.Position.X;
            Assert.True(x >= last - 1e-4f, $"heartbeat {i + 2}: moved back from {last:0.0000} to {x:0.0000}");
            Assert.True(x > 0f, $"heartbeat {i + 2}: x {x:0.0000} is back across the border");
            Assert.False(a.Prim.HasLeftRegion, $"heartbeat {i + 2}: left the region");
            last = x;
        }
        Assert.InRange(last, first + 0.5f * 30 * Heartbeat * 0.9f, first + 0.5f * 30 * Heartbeat * 1.1f);
    }

    [Fact]
    public void An_arrival_heading_back_out_is_never_carried_across_the_border_by_the_correction()
    {
        // Half a second old and moving back towards the border it came over: the time elapsed would put it past the
        // border, so the correction is not made and the engine's own step stands.
        const float arrivedAt = 0.2f;
        using var a = new Arrival(arrivedAt, new Vector3(-1f, 0f, 0f), 0.5f);

        a.Heartbeat();

        float x = a.Prim.Position.X;
        _out.WriteLine($"x {x:0.0000}");
        Assert.True(x > 0f, $"x {x:0.0000} is across the border");
        Assert.False(a.Prim.HasLeftRegion);
        Assert.InRange(x, arrivedAt - 1f * 4 / 45f - 0.01f, arrivedAt - 1f * 4 / 45f + 0.01f);
    }
}
