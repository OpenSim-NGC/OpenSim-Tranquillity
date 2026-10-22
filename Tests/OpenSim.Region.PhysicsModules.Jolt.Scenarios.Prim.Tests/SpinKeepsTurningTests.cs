/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object spun by llApplyRotationalImpulse keeps turning: the spin is slowed by the prim's angular damping
/// ([Jolt] PrimAngularDamping, 0.05 per second), by contact friction where it touches something, and by a vehicle's angular
/// friction timescale, and by nothing else (https://wiki.secondlife.com/wiki/LlApplyRotationalImpulse). It must not stop, or
/// lose a share of its speed, at a fixed time after the impulse. The other rotational-impulse tests stop at 1.2 s, before
/// that time. A body that is falling when buoyancy 1 is set keeps the speed it has (buoyancy takes gravity away, not speed),
/// so it lands, and the landing ends its spin: that is not a spin that stops by itself, and the last test shows it. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class SpinKeepsTurningTests
{
    private readonly ITestOutputHelper _out;
    public SpinKeepsTurningTests(ITestOutputHelper output) => _out = output;

    private static readonly Vector3 High = new(128f, 128f, 150f);

    private readonly record struct Point(double T, float Spin, Quaternion Rotation, int Awake, float Height);

    /// <summary>
    /// Runs the scene for <paramref name="seconds"/>, gives the prim <paramref name="impulse"/> (about region z) at the fifth
    /// heartbeat and returns its spin about z and rotation before every heartbeat, with the physics time since the impulse.
    /// </summary>
    private List<Point> Spin(float seconds, double physicsHz, Action<Run> setup, float impulse, Action<Run> beforeImpulse = null, bool pinned = false)
    {
        var trace = new List<Point>();
        double t0 = -1;
        var sc = new Scenario
        {
            Name = "spin-keeps-turning",
            DefaultDuration = _ => seconds + 1f,
            Setup = setup,
            Input = r =>
            {
                if (r.Heartbeats == 5)
                {
                    t0 = r.PhysicsTime;
                    beforeImpulse?.Invoke(r);
                    r.Actor.AddAngularForce(new Vector3(0f, 0f, impulse), false);
                }
                if (t0 >= 0)
                    trace.Add(new Point(r.PhysicsTime - t0, r.Actor.RotationalVelocity.Z, r.Actor.Orientation, r.AwakeBodies, r.Actor.Position.Z));
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { UseDefaults = !pinned, RateHz = 11.0, PhysicsRateHz = physicsHz, SimulatorDefaults = true });
        foreach (Point p in trace.Where((_, i) => i % 5 == 0))
            _out.WriteLine($"t {p.T:F2} spin {p.Spin:F3} awake {p.Awake}");
        return trace;
    }

    private static Point At(List<Point> trace, double t) => trace.First(p => p.T >= t - 1e-9);

    // ---- a plain box floating free: only the angular damping slows it ----

    [Theory]
    [InlineData(45.0, 0.1f)]
    [InlineData(45.0, 1f)]
    [InlineData(0.0, 0.1f)]
    [InlineData(0.0, 1f)]
    public void A_floating_box_keeps_turning_and_loses_only_its_damping(double physicsHz, float impulse)
    {
        // A 0.5 m cube: 1.25 kg, inertia 1.25 x 0.5 / 12.
        List<Point> trace = Spin(5f, physicsHz, r => { r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), High, Quaternion.Identity); r.Actor.Buoyancy = 1f; }, impulse);
        float w1 = At(trace, 0.5).Spin;
        Assert.True(w1 > 0.5f, $"it never turned (spin {w1})");
        // exp(-0.05 t): 0.5 s to 4.5 s is 4 s of damping, a factor 0.819.
        float expected = w1 * MathF.Exp(-0.05f * 4f);
        float w2 = At(trace, 4.5).Spin;
        Assert.InRange(w2, expected * 0.95f, expected * 1.05f);
        // and it did not stop or lose speed in between
        for (double t = 0.6; t <= 4.5; t += 0.1)
            Assert.InRange(At(trace, t).Spin, w1 * MathF.Exp(-0.05f * (float)(t - 0.5)) * 0.95f, w1 * 1.001f);
        // the heading kept changing to the end
        Quaternion a = At(trace, 4.0).Rotation, b = At(trace, 4.5).Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(a, b)) < 0.9999f, "the heading stopped changing");
    }

    // ---- the same box on the ground: friction slows it, at no fixed time ----

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_on_the_ground_slows_by_friction_and_does_not_stop_at_a_fixed_time(double physicsHz)
    {
        // A 1 m cube (10 kg, inertia 1.67): 100 kg m/s gives 60 rad/s, cut to the engine's limit of 47.
        List<Point> trace = Spin(4f, physicsHz, r => r.AddBoxOnGround(Vector3.One, 128f, 128f, Quaternion.Identity), 100f);
        float w0 = At(trace, 0.3).Spin;
        Assert.True(w0 > 20f, $"it never turned (spin {w0})");
        // Friction takes speed off at a steady rate: the speed never rises, and between 1.2 s and 1.8 s it does not fall by
        // more than a steady rate from the 0.6 s before would suggest.
        float prev = w0;
        for (double t = 0.4; t <= 2.0; t += 0.1)
        {
            float w = At(trace, t).Spin;
            Assert.True(w <= prev + 0.01f, $"the spin rose at {t}: {prev} to {w}");
            prev = w;
        }
        float a = At(trace, 0.6).Spin, b = At(trace, 1.2).Spin, c = At(trace, 1.8).Spin;
        Assert.True(c > 0f, $"stopped by 1.8 s (spin {c})");
        Assert.InRange((b - c) / (a - b), 0f, 1.6f);   // the fall in the 0.6 s after 1.2 s is about the fall in the 0.6 s before it
    }

    // ---- an idle vehicle: only its angular friction timescale slows it ----

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_idle_vehicle_turns_and_slows_only_by_its_angular_friction(double physicsHz)
    {
        const float timescale = 4f;
        List<Point> trace = Spin(5f, physicsHz,
            r =>
            {
                r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), High, Quaternion.Identity);
                r.Actor.VehicleType = (int)Vehicle.TYPE_BALLOON;
                r.Actor.VehicleFloatParam((int)Vehicle.BUOYANCY, 1f);
                r.Actor.VehicleVectorParam((int)Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(timescale, timescale, timescale));
            }, 0.1f, pinned: true);
        float w1 = At(trace, 0.5).Spin;
        Assert.True(w1 > 0.5f, $"it never turned (spin {w1})");
        float expected = w1 * MathF.Exp(-4f / timescale);
        float w2 = At(trace, 4.5).Spin;
        Assert.InRange(w2, expected * 0.9f, expected * 1.1f);
        Quaternion a = At(trace, 4.0).Rotation, b = At(trace, 4.5).Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(a, b)) < 0.9999f, "the heading stopped changing");
    }

    // ---- a falling body given buoyancy 1 keeps falling, and lands ----

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_body_given_buoyancy_after_it_started_to_fall_lands_and_the_landing_ends_its_spin(double physicsHz)
    {
        float ground = 0f;
        var landed = new List<Point>();
        List<Point> trace = Spin(4f, physicsHz,
            r =>
            {
                ground = r.GroundAt(128f, 128f);
                r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(128f, 128f, ground + 12f), Quaternion.Identity);
            },
            0.1f,
            beforeImpulse: r => r.Actor.Buoyancy = 1f, pinned: true);   // after four heartbeats of free fall
        foreach (Point p in trace)
        {
            if (p.Height < ground + 0.3f) landed.Add(p);
        }
        Assert.NotEmpty(landed);   // it came down although buoyancy was 1
        // in the air it kept turning (only its damping slowed it) ...
        foreach (Point p in trace.Where(p => p.T > 0.3 && p.Height > ground + 0.6f))
            Assert.True(p.Spin > 1.5f, $"the spin was {p.Spin} at {p.T}, {p.Height - ground} m up");
        // ... and the landing ended it
        Assert.InRange(At(trace, landed[0].T + 0.6).Spin, -0.05f, 0.05f);
    }
}
