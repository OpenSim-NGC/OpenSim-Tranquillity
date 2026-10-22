/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A non-physical platform stepped 0.1 m every 50 ms (2 m/s), as a script does with llSetLinkPrimitiveParamsFast at 20 Hz or
/// keyframed motion does: what rests or stands on it follows within a few centimetres while it moves, also when the
/// heartbeats find different numbers of the sets (a region's heartbeat is not as regular as core's 50 ms timer). The body
/// follows the object's pose within <see cref="JoltPrim.KinematicFollowBand"/> at a speed that does not change from one
/// heartbeat to the next (JoltPrim.StepTracked). Each figure is a line "STEP ..." in the test output. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SteppedMoverTests
{
    private readonly ITestOutputHelper _out;
    public SteppedMoverTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static readonly (string, string) On = ("MovedPrimsCarryRiders", "true");

    // How many of core's 50 ms sets each heartbeat finds: a region's heartbeat is not as regular as its timer (about 1.8 on average).
    private static readonly int[] Uneven = { 2, 1, 3, 2, 1, 2, 3, 1, 2, 1 };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string rider in new[] { "box", "avatar" })
            foreach (double rate in new[] { 0.0, 45.0 })
                foreach (bool uneven in new[] { false, true })
                    foreach (string how in new[] { "keyframed", "steps-on" })
                        yield return new object[] { rider, rate, uneven, how };
    }

    private KinematicScenarios.Figures Measure(string rider, bool up, double rate, bool uneven, string how)
    {
        bool keyframed = how == "keyframed";
        RunResult run = KinematicTestRig.Run(KinematicScenarios.Motion(rider, up, 2f, keyframed, keyframed, 0.05, uneven ? Uneven : null), rate,
            how == "steps-on" ? new[] { On } : new (string, string)[0]);
        KinematicScenarios.Figures f = KinematicScenarios.Measure(run, up, 2f);
        Say($"STEP {rider} {(up ? "up" : "side")} rate {rate:0}{(uneven ? " uneven" : "")} {how}: {f.Verdict}, follow {F(f.SteadyFollow)} (whole run {F(f.MaxFollow)}, stop {F(f.StopFollow)}), sink {F(f.MaxSink)} air {F(f.MaxAir)}");
        return f;
    }

    // A rider on a platform going up at 2 m/s stays within a few centimetres of its place while the platform moves: 0.075 m for an
    // avatar, 0.09 for a cube. The body is kept within 0.04 m of the pose, and at 45 Hz the pose moves 2/45 = 0.044 m between two
    // physics steps, so a cube that is a step behind reads up to about 0.084 m. Which part of a step the heartbeat's clock falls in
    // depends on how many heartbeats the region ran before the prim was rezzed (the harness's warm-up step is one): over 0 to 10
    // warm-up heartbeats the cube's steady follow at 45 Hz read 0.058 to 0.085 m, so the 0.065 m this test had before only held for
    // a harness that took no warm-up step. The avatar read up to 0.070 m over the same phases (0.066 at three warm-up heartbeats);
    // a running region can start a move at any phase of the step accumulator, so each margin covers every phase: the step distance
    // plus the body bound, with a little over for the avatar (0.075 m). The run's start
    // and stop are not in the figure: a platform that goes from rest to 2 m/s in one heartbeat, or back, is a change of speed any
    // rider feels, and a cube then rises up to 0.3 m (the whole-run figure).
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_rider_on_a_stepped_platform_going_up_follows_it(string rider, double rate, bool uneven, string how)
    {
        KinematicScenarios.Figures f = Measure(rider, true, rate, uneven, how);
        Assert.True(f.OnAtEnd, f.Verdict);
        Assert.True(f.SteadyFollow <= (rider == "box" ? 0.09 : 0.075), $"{rider} moved {f.SteadyFollow:0.000} m about its place on the platform");
        Assert.True(f.MaxFollow < 0.35, $"{rider} was {f.MaxFollow:0.000} m from its place at some time in the run");
    }

    // Sideways the cube slides back as the platform starts (friction takes about 0.4 m to bring it to 2 m/s; the avatar is carried
    // at the platform's speed at once). Once it moves with the platform, it holds its place within 0.18 m: the heartbeat that
    // finds three sets and the one that finds one push it about. Over 0 to 10 warm-up heartbeats the cube's steady follow read up
    // to 0.175 m, because a running region can start a move at any phase of the 45 Hz step accumulator (the step distance 0.044 m
    // plus the body bound 0.04 m shift how far the cube slides behind), so the margin covers every phase.
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_rider_on_a_stepped_platform_going_sideways_follows_it(string rider, double rate, bool uneven, string how)
    {
        if (how == "steps-on" && rider == "avatar")
            return;   // the first set of a series is not carried yet: see the first-set tests
        KinematicScenarios.Figures f = Measure(rider, false, rate, uneven, how);
        Assert.True(f.OnAtEnd, f.Verdict);
        Assert.True(f.SteadyFollow <= (rider == "box" ? 0.18 : 0.05), $"{rider} moved {f.SteadyFollow:0.000} m about its place on the platform");
    }

    // ------------------------------------------------------------------ the first set

    // One position set of a platform with a rider on it, at the 45th heartbeat (4 s in, the rider settled), then the run goes on 4 s:
    // where the rider ends against its place on the platform, and whether the platform's body was ever a kinematic one.
    private (double Off, bool Kinematic) OneSet(string rider, Vector3 step, double rate, params (string, string)[] jolt)
    {
        bool everKinematic = false;
        var sc = KinematicTestRig.Platform(rider, 8f, r =>
        {
            if (r.Heartbeats == 44)
                r.Other.Position += step;
            everKinematic |= KinematicTestRig.PlatformPrim(r).IsKinematicBody;
        });
        RunResult run = KinematicTestRig.Run(sc, rate, jolt);
        List<Sample> s = run.Samples;
        Sample before = s.FindLast(x => x.T <= 4.0);
        Sample end = s[^1];
        double off = ((end.Position - end.Other) - (before.Position - before.Other)).Length();
        return (off, everKinematic);
    }

    // With the setting on, the first set of a series carries what rests on the prim as the sets after it do: a single llSetPos of a
    // platform with an avatar or a cube on it moves them with it. With it off (the default) they are left where they were.
    [Theory]
    [InlineData("box", 1.0, 0.0)]
    [InlineData("box", 1.0, 45.0)]
    [InlineData("avatar", 1.0, 0.0)]
    [InlineData("avatar", 1.0, 45.0)]
    [InlineData("box", 0.6, 45.0)]
    public void A_single_set_carries_what_rests_on_the_prim_with_the_setting_on(string rider, double metres, double rate)
    {
        var east = new Vector3((float)metres, 0f, 0f);
        var north = new Vector3(0f, 0f, (float)metres);
        foreach (Vector3 step in new[] { east, north })
        {
            var on = OneSet(rider, step, rate, On);
            var off = OneSet(rider, step, rate);
            Say($"FIRST {rider} {metres:0.0} m {(step == east ? "east" : "up")} rate {rate:0}: on {F(on.Off)} m from its place, off {F(off.Off)} m, platform kinematic on {on.Kinematic} off {off.Kinematic}");
            // Up, it is carried with the platform. Sideways, an avatar is carried at the platform's speed; a cube slides back as the
            // platform starts and stops (0.4 to 0.6 m for a 1 m step at 3 m/s) where, with the setting off, it stays where it was.
            double allowed = step == east ? (rider == "avatar" ? 0.1 : 0.7 * metres) : 0.3;
            Assert.True(on.Off < allowed, $"{rider} ended {on.Off:0.00} m from its place with the setting on (off: {off.Off:0.00})");
            Assert.False(off.Kinematic, "the platform's body stayed a fixed one with the setting off");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_single_set_with_nothing_on_the_prim_leaves_its_body_fixed_with_the_setting_on(double rate)
    {
        var sc = KinematicTestRig.Platform("none", 8f, r =>
        {
            if (r.Heartbeats == 44)
                r.Other.Position += new Vector3(1f, 0f, 0f);
            Assert.False(KinematicTestRig.PlatformPrim(r).IsKinematicBody, "the body was made kinematic with nothing on it");
        });
        KinematicTestRig.Run(sc, rate, On);
    }

    // ------------------------------------------------------------------ a child prim moved on its own

    // The root is a 6 x 6 x 0.5 m platform and its one child a 4 x 4 x 0.5 m platform with its middle 3 m east of the root's, its top level
    // with the root's. The rider stands on the child, 1 m past the root's edge. A script moves the child alone (llSetLinkPrimitiveParams)
    // 0.1 m every 50 ms (two sets a heartbeat) for 3 m, east or up; how far the rider ends from its place on the child.
    private double RiderOffsetAfterChildMoves(string rider, bool up, double rate, params (string, string)[] jolt)
    {
        const float armLength = 3f;
        JoltPrim child = null;
        Vector3 centre = default, childAt = default;
        double offset = double.NaN;
        Vector3 start = default;
        var sc = KinematicTestRig.Platform("none", 9f, r =>
        {
            int h = r.Heartbeats;
            if (h == 0)
            {
                centre = r.Other.Position;
                child = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(4f, 4f, 0.5f), centre + new Vector3(armLength, 0f, 0f), Quaternion.Identity, false, 5502u, linkTo: r.Other);
                float top = centre.Z + 0.25f;
                var at = new Vector3(centre.X + armLength + 1f, centre.Y, top);
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), at + new Vector3(0f, 0f, 0.26f), Quaternion.Identity);
                else
                    r.AddAvatarAt(at + new Vector3(0f, 0f, r.AvatarStandHalf + 0.05f), false);
            }
            if (h == 44)
            {
                childAt = child.Position;
                start = r.Actor.Position - childAt;
            }
            if (h >= 44 && h < 44 + 15)
            {
                Vector3 dir = up ? Vector3.UnitZ : Vector3.UnitX;
                for (int k = 0; k < 2; k++)
                {
                    childAt += dir * 0.1f;
                    child.Position = childAt;
                }
            }
            if (h == 44 + 15 + 25)
                offset = ((r.Actor.Position - child.Position) - start).Length();
        });
        KinematicTestRig.Run(sc, rate, jolt);
        return offset;
    }

    // A child prim moved on its own carries what stands on it with the setting on (a root's compound is rebuilt for each step; the
    // rider takes the child's step with each set). With the setting off the rider is left behind sideways and shoved up or dropped, as before.
    [Theory]
    [InlineData("box", false, 0.0)]
    [InlineData("box", false, 45.0)]
    [InlineData("avatar", false, 0.0)]
    [InlineData("avatar", false, 45.0)]
    [InlineData("box", true, 45.0)]
    [InlineData("avatar", true, 45.0)]
    public void A_child_prim_moved_on_its_own_carries_its_riders_with_the_setting_on(string rider, bool up, double rate)
    {
        double on = RiderOffsetAfterChildMoves(rider, up, rate, On);
        double off = RiderOffsetAfterChildMoves(rider, up, rate);
        Say($"CHILD {rider} {(up ? "up" : "east")} 3 m rate {rate:0}: rider {F(on)} m from its place on the child with the setting on, {F(off)} m off");
        Assert.True(on < 0.3, $"the rider ended {on:0.00} m from its place on the child");
        // An avatar on a child that moves up is pushed up by the rebuilt compound with the setting off as well.
        if (!(rider == "avatar" && up))
            Assert.True(off > 1.0, $"with the setting off the rider ended {off:0.00} m from its place: nothing was carried");
    }

    // ------------------------------------------------------------------ a fast start

    // An avatar on a platform that keyframed motion starts at 22, 29 or 40 m/s (6 m in 0.27, 0.2 and 0.15 s) stays on it, with regular
    // heartbeats and with heartbeats that find up to three of the 50 ms sets (3 sets are 4.5 to 6 m at these speeds, over the 4 m a
    // heartbeat that is taken for a jump). A running region has been seen to drop its avatar at these speeds; the harness does not
    // reproduce it, so this holds what the harness does carry.
    [Theory]
    [InlineData(22f)]
    [InlineData(29f)]
    [InlineData(40f)]
    public void An_avatar_on_a_platform_that_starts_fast_stays_on_it(float speed)
    {
        foreach (bool uneven in new[] { false, true })
            foreach (double rate in new[] { 0.0, 45.0 })
            {
                RunResult run = KinematicTestRig.Run(KinematicScenarios.Motion("avatar", false, speed, true, true, 0.05, uneven ? Uneven : null), rate);
                KinematicScenarios.Figures f = KinematicScenarios.Measure(run, false, speed);
                Say($"FAST avatar {speed:0} m/s {(uneven ? "uneven" : "even")} rate {rate:0}: {f.Verdict}, lag {F(f.MaxLag)} slide {F(f.Slide)} sink {F(f.MaxSink)}");
                Assert.True(f.OnAtEnd, f.Verdict);
                Assert.True(f.Slide < 0.5, $"it slid {f.Slide:0.00} m");
            }
    }
}
