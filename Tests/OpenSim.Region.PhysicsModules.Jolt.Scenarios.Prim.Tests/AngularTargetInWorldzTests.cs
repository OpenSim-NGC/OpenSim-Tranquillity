/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The rotation target under <c>[Jolt] BasicForcesBehaviour = InWorldz</c>, the shipped default. It runs every test of
/// <see cref="AngularTargetTests"/>, which tests the Second Life spring. The tests that do not depend on whose law it is
/// (a linkset turning as one body, a locked axis, a vehicle, the gravity and buoyancy left alone, a held torque) run as they
/// are. The others check the law of Halcyon's look-at in place of the spring's, as the overrides below do
/// (InWorldz.PhysxPhysics PhysxPrim.pid.cs DoRotLookTarget and SetRotLookAtTarget):
/// <list type="bullet">
/// <item>the strength is held to 0.1..1 and the damping to 0.1..100, so a strength under 0.0445 is not ignored but
/// works as 0.1, and a damping over 1 is not capped at 1;</item>
/// <item>the angular acceleration toward the target is 100 x angle / damping and the drag 100 x strength x the angular
/// velocity, a spring that overshoots where the drag is under critical;</item>
/// <item>within one degree of the target the angular velocity is set to zero, so a spin a script sets there is taken
/// away at once.</item>
/// </list>
/// The reference is that spring and drag worked out as a differential equation in small steps, the law the module takes at
/// any step length.
/// </summary>
[PinnedMaterials]
public class AngularTargetInWorldzTests : AngularTargetTests
{
    public AngularTargetInWorldzTests(ITestOutputHelper output) : base(output) => MaterialPins.BasicForces = "InWorldz";

    private const double Scale = 100.0;
    private const double AtTargetDegrees = 1.0;

    /// <summary>
    /// The degrees turned toward a target at each 0.2 ms, the spring and drag worked out in small steps. Halcyon looks at
    /// the distance left once in each of its 15 ms steps and zeroes the spin there when it is within a degree, so the
    /// check is made at those steps and a body that crosses the degree between two of them is not stopped by it.
    /// </summary>
    private static List<(double t, double angle)> Law(Func<double, double> target, float strength, float damping, double seconds)
    {
        double c = Scale * Math.Clamp(strength, 0.1f, 1f), k = Scale / Math.Clamp(damping, 0.1f, 100f);
        const double dt = 0.0002;
        const int StepsPerHalcyonStep = 75;   // 15 ms
        var path = new List<(double, double)> { (0, 0) };
        double angle = 0, w = 0;
        int steps = (int)Math.Round(seconds / dt);
        for (int i = 1; i <= steps; i++)
        {
            double t = i * dt, error = target(t) - angle;
            if ((i - 1) % StepsPerHalcyonStep == 0 && Math.Abs(error) <= AtTargetDegrees)
                w = 0;
            if (w != 0 || Math.Abs(error) > AtTargetDegrees)
                w += (k * error - c * w) * dt;
            angle += w * dt;
            path.Add((t, angle));
        }
        return path;
    }

    private static List<(double t, double angle)> Law(float total, float strength, float damping, double seconds)
        => Law(_ => total, strength, damping, seconds);

    private static double LawAt(List<(double t, double angle)> path, double t)
        => path[Math.Clamp((int)Math.Round(t / (path[1].t - path[0].t)), 0, path.Count - 1)].angle;

    /// <summary>
    /// The trace stays within <paramref name="tolerance"/> degrees of the law's path, allowing for the sample's time: the
    /// heartbeat reads the body between two collision steps, so a sample is matched with the law at any time within
    /// half a heartbeat (45 ms) of its own.
    /// </summary>
    private static void Follows(List<Pt> trace, List<(double t, double angle)> law, double tolerance, string what)
    {
        float[] turned = Turned(trace);
        for (int i = 0; i < trace.Count; i++)
        {
            double off = double.MaxValue;
            for (double dt = -0.045; dt <= 0.045 + 1e-9; dt += 0.003)
                off = Math.Min(off, Math.Abs(turned[i] - LawAt(law, Math.Max(0, trace[i].T + dt))));
            Assert.True(off <= tolerance, $"{what}: at t {trace[i].T:0.000} it has turned {turned[i]:0.0} degrees, Halcyon's law gives {LawAt(law, trace[i].T):0.0} (within {off:0.0} degrees at its nearest time)");
        }
    }

    private static double Peak(List<(double t, double angle)> law) => law.Max(p => p.angle);

    public override void It_turns_90_degrees_by_the_spring_rule_without_passing_the_target_in_the_air_and_on_the_ground(double physicsHz, bool air)
    {
        const float T = 0.2f;
        List<Pt> trace = Run(physicsHz, 4f, r => LookAt(Cube(r, 1f, 1f, air), TurnZ(90f), T, T));
        List<(double t, double angle)> law = Law(90f, T, T, 4.2);
        Follows(trace, law, 4.0, $"air {air}");
        // The drag is under critical, so it passes the target by what the law passes it by, and settles.
        Assert.True(Turned(trace).Max() <= Peak(law) + 2f, $"air {air}: passed the target by {Turned(trace).Max() - 90f:0.0} degrees, the law by {Peak(law) - 90:0.0}");
        Pt end = At(trace, 1.6);
        foreach (Pt p in trace.Where(p => p.T >= end.T))
            Assert.True(Between(p.Rot, TurnZ(90f)) < 2f, $"left the target at t {p.T:0.00}");
    }

    public override void It_reaches_the_target_within_2_degrees_the_same_for_any_mass_or_size(double physicsHz, float size, float mass, bool air)
    {
        List<Pt> trace = Run(physicsHz, 3f, r => LookAt(Cube(r, size, mass, air), TurnZ(90f), 0.3f, 0.3f));
        List<(double t, double angle)> law = Law(90f, 0.3f, 0.3f, 3.2);
        Follows(trace, law, 4.0, $"size {size} mass {mass} air {air}");
        // A 1 m cube on the ground comes to rest 2 to 4 degrees short of the target: see the next test.
        if (!air && size <= 1f)
            return;
        Pt end = At(trace, 2.5);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"size {size} mass {mass} air {air}: {Between(end.Rot, TurnZ(90f)):0.0} degrees from the target at t {end.T:0.00}");
    }

    // A cube lying on the ground used to stop 4 degrees short of a look-at target (a 2 m cube 2 degrees) with no spin, and
    // stay there: contact friction takes up to a fixed amount of velocity in each step, and the law's command, set from the
    // error alone, falls below that near the target. The body is now asked again for what the engine took. The 66.7 Hz
    // case is Halcyon's own 15 ms step, where the law is Halcyon's as written.
    [Theory]
    [InlineData(45.0, 1f, 1f)]
    [InlineData(45.0, 1f, 100f)]
    [InlineData(0.0, 1f, 1f)]
    [InlineData(66.66666666666667, 1f, 1f)]
    [InlineData(45.0, 2f, 1f)]
    public void A_cube_on_the_ground_reaches_its_target_within_2_degrees(double physicsHz, float size, float mass)
    {
        List<Pt> trace = Run(physicsHz, 3f, r => LookAt(Cube(r, size, mass, false), TurnZ(90f), 0.3f, 0.3f));
        Pt end = At(trace, 2.5);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"size {size} mass {mass}: {Between(end.Rot, TurnZ(90f)):0.0} degrees from the target at t {end.T:0.00}");
    }

    public override void A_target_moved_many_times_a_second_is_followed_smoothly(double physicsHz, float updatesPerSecond, bool air)
    {
        List<Pt> trace = DragRun(physicsHz, updatesPerSecond, air, 0.1f, 1f, 1f, 1f);
        // The law followed to the target as the viewer's updates move it. The module is handed a target only at a heartbeat
        // (the last update at or before it, from 0.5 s), and keeps it until the next.
        double[] beats = trace.Select(p => p.T).ToArray();
        List<(double t, double angle)> law = Law(t =>
        {
            double beat = beats.LastOrDefault(b => b <= t + 1e-9);
            double tu = Math.Floor(beat * updatesPerSecond + 1e-9) / updatesPerSecond;
            return tu < 0.5 ? 0.0 : Ideal(tu, 1.0, 0.5, 90f);
        }, 0.1f, 0.1f, 6.2);
        Follows(trace, law, 6.0, $"{updatesPerSecond} updates a second, air {air}");
        Pt end = At(trace, 3.0);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"{Between(end.Rot, TurnZ(90f)):0.0} degrees from the target at t {end.T:0.00}");
    }

    // A body held at its target has settled and may sleep. The look-at stops the turn within a degree of the target, so the
    // wake rule leaves a body asleep anywhere inside that degree; it used to wake it unless it was within 0.2 degrees, and
    // the body was awake in every second of the hold.
    [Fact]
    public void A_target_held_still_lets_the_body_sleep_and_stay_asleep()
    {
        PhysicsActor actor = null;
        List<Pt> trace = Run(45.0, 12f, r => LookAt(actor = Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f));
        foreach (Pt p in trace.Where(p => p.T >= 4.0 && p.T <= 11.0))
            Assert.True(p.Awake == 0, $"awake at t {p.T:0.00}");
    }

    public override void A_target_held_still_holds_within_half_a_degree_and_the_body_sleeps_and_a_new_target_wakes_it()
    {
        // The hold within a degree, and a new target moving the body, as under Second Life; whether it sleeps is the test above.
        PhysicsActor actor = null;
        bool second = false;
        List<Pt> trace = Run(45.0, 12f, r => LookAt(actor = Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f),
            (r, t) =>
            {
                if (t >= 8.0 && !second)
                {
                    second = true;
                    actor.APIDTarget = TurnZ(0f);
                }
            });
        // Held 3 to 8 s: within the degree at which the law stops the turn, and not moving.
        foreach (Pt p in trace.Where(p => p.T >= 3.0 && p.T <= 8.0))
        {
            Assert.True(Between(p.Rot, TurnZ(90f)) < 1f, $"drifted to {Between(p.Rot, TurnZ(90f)):0.00} degrees off at t {p.T:0.00}");
            Assert.True(p.Spin.Length() < 0.02f, $"still spinning {p.Spin.Length():0.000} rad/s at t {p.T:0.00}");
        }
        Assert.True(trace.Where(p => p.T > 8.0 && p.T < 8.5).Any(p => p.Awake > 0), "a new target did not wake the body");
        Assert.True(Between(trace[^1].Rot, TurnZ(0f)) < 2f, $"{Between(trace[^1].Rot, TurnZ(0f)):0.0} degrees from the second target at the end");
    }

    public override void Strength_and_damping_set_the_timescale(float strength, float damping, float timescale)
    {
        // The strength and damping set the spring and its drag, held to 0.1..1 and 0.1..100; the timescale of the
        // Second Life spring does not apply.
        List<Pt> trace = Run(45.0, 6f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), strength, damping));
        List<(double t, double angle)> law = Law(90f, strength, damping, 6.2);
        Follows(trace, law, 4.0, $"strength {strength} damping {damping}");
        Assert.True(Turned(trace).Max() <= Peak(law) + 2f, $"strength {strength} damping {damping}: passed the target by {Turned(trace).Max() - 90f:0.0} degrees, the law by {Peak(law) - 90:0.0}");
    }

    public override void A_strength_under_the_minimum_does_nothing(float strength)
    {
        // A strength under 0.1 is held to 0.1 and turns the body.
        List<Pt> trace = Run(45.0, 4f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), strength, 0.5f));
        Follows(trace, Law(90f, 0.1f, 0.5f, 4.2), 4.0, $"strength {strength} held to 0.1");
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f, $"strength {strength}: {Between(trace[^1].Rot, TurnZ(90f)):0.0} degrees from the target");
    }

    public override void It_takes_the_short_way_round_and_turns_about_any_axis()
    {
        // 270 degrees about Z is 90 the other way: the turn goes no further than the law's own peak.
        List<Pt> trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(270f), 0.2f, 0.2f));
        double peak = Peak(Law(90f, 0.2f, 0.2f, 3.2));
        Assert.True(Turned(trace).Max() <= peak + 2f, $"went the long way: {Turned(trace).Max():0.0}, the law peaks at {peak:0.0}");
        Quaternion qRaw = TurnZ(50f) * Turn(30f, Vector3.UnitX); Quaternion q = Quaternion.Normalize(in qRaw);
        trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), q, 0.2f, 0.2f));
        Assert.True(Between(trace[^1].Rot, q) < 2f, $"{Between(trace[^1].Rot, q):0.0} degrees from a target about a tilted axis");
        // A target given as the negative of the same rotation is the same target.
        var neg = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), neg, 0.2f, 0.2f));
        Assert.True(Between(trace[^1].Rot, q) < 2f);
    }

    public override void An_angular_velocity_a_script_sets_is_a_starting_speed_the_spring_takes_to_the_target()
    {
        PhysicsActor actor = null;
        bool kicked = false;
        List<Pt> trace = Run(45.0, 6f, r => LookAt(actor = Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f),
            (r, t) =>
            {
                if (t >= 3.0 && !kicked)
                {
                    kicked = true;
                    actor.RotationalVelocity = new Vector3(0f, 0f, 2f);
                }
            });
        // Within a degree of its target the look-at sets the angular velocity to zero, so the spin a script gives a body
        // that is there is taken away at the next step and the body does not leave the degree.
        float swing = trace.Where(p => p.T >= 3.0).Max(p => Between(p.Rot, TurnZ(90f)));
        Assert.True(swing <= 1.5f, $"swung {swing:0.0} degrees");
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 1f, "did not stay");
    }

    protected override void CheckFigure(string run, float overshoot, float endErr)
    {
        // The 0.2 / 0.2 look-at passes its target by the law's own overshoot.
        double peak = Peak(Law(90f, 0.2f, 0.2f, 3.0)) - 90.0;
        Assert.True(overshoot <= peak + 2.0, $"{run}: passed the target by {overshoot:0.0} degrees, the law by {peak:0.0}");
        Assert.True(endErr < 2f, $"{run}: {endErr:0.0} degrees from the target at the end");
    }
}
