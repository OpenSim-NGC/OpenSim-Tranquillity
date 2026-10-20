/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An avatar standing on a platform that keyframed motion starts at a high speed from rest. The platform is a 6 m square, 3 m up,
/// set along +x by a position set every 50 ms with the keyframed-motion hint on from the first set, as core's motion timer does it,
/// and the avatar has stood on it 4 s. Each run reads, at every heartbeat of the first second, how far the avatar is behind its place
/// on the platform and whether its ground is a prim, and at the end of the run where it stands against its place. Each run is a
/// line "FAST ..." in the test output.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class FastStartTests
{
    private const double StartAt = 4.0;
    private const double WindowSeconds = 1.0;
    private readonly ITestOutputHelper _out;
    public FastStartTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // How many of core's 50 ms sets each heartbeat finds: a region's heartbeat is not as regular as its timer.
    private static readonly int[] Uneven = { 2, 1, 3, 2, 1, 2, 3, 1, 2, 1 };

    /// <summary>One run's figures.</summary>
    internal sealed record Outcome(double WorstBehind, bool OnPrimThroughout, double EndBehind, bool EndOnPrim, double EndRise, double StartRise, bool PlatformFixedAtEnd,
                                   List<double> Behind, List<bool> OnPrim);

    /// <param name="pathSeconds">How long the platform moves; a path longer than the window leaves its stop out of what is read.</param>
    /// <param name="hintOffAfterLastSet">The hint goes off one heartbeat after the last set (core's timer ends the motion at the tick
    /// after the last frame, which a heartbeat can find with or without the last set still waiting); false: in the heartbeat that finds the last set.</param>
    internal static Outcome Run(float speed, double rate, float riderX, float thickness, int[] setsPerHeartbeat, double pathSeconds, bool hintOffAfterLastSet = false,
                                bool cube = false)
    {
        var behind = new List<double>();
        var onPrim = new List<bool>();
        double interval = 0.05;
        float length = speed * (float)pathSeconds;
        Vector3 startPos = default;
        float startRise = 0f;
        double nextSet = 0;
        int setIndex = 0, beat = 0;
        bool started = false, hintOn = false, lastSetDone = false;
        int hintOffAtBeat = int.MaxValue;
        PhysicsActor cubeActor = null;
        double endBehind = 0, endRise = 0;
        bool endOnPrim = false;
        var sc = new Scenario
        {
            Name = "fast-start",
            DefaultDuration = _ => (float)(StartAt + pathSeconds + 1.5),
            Setup = r =>
            {
                float top = r.GroundAt(KinematicScenarios.CentreX, KinematicScenarios.CentreY) + KinematicScenarios.PlatformHeight;
                startPos = new Vector3(KinematicScenarios.CentreX, KinematicScenarios.CentreY, top - thickness * 0.5f);
                r.AddOtherBox(new Vector3(6f, 6f, thickness), startPos, Quaternion.Identity, false);
                r.AddAvatarAt(new Vector3(startPos.X + riderX, startPos.Y, top + r.AvatarStandHalf + 0.05f), false);
                if (cube)
                    cubeActor = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(startPos.X + 2f, startPos.Y, top + 0.26f), Quaternion.Identity, true, 5502u);
            },
            Input = r =>
            {
                double until = r.Now + r.Dt;
                if (r.Now >= StartAt - 1e-9)
                {
                    behind.Add(r.Other.Position.X - r.Actor.Position.X + riderX);
                    onPrim.Add(r.Actor.CollidingObj);
                    endBehind = behind[^1];
                    endOnPrim = r.Actor.CollidingObj;
                    endRise = r.Actor.Position.Z - r.Other.Position.Z;
                }
                if (!started && until > StartAt + 1e-9)
                {
                    started = true;
                    nextSet = StartAt;
                    r.Other.MovedByKeyframedMotion = hintOn = true;
                    startRise = r.Actor.Position.Z - r.Other.Position.Z;
                }
                if (!started)
                    return;
                beat++;
                int n = setsPerHeartbeat != null ? setsPerHeartbeat[(beat - 1) % setsPerHeartbeat.Length] : int.MaxValue;
                while (!lastSetDone && (setsPerHeartbeat != null ? n-- > 0 : nextSet < until - 1e-9))
                {
                    double done = Math.Min(1.0, (++setIndex * interval) / pathSeconds);
                    r.Other.Position = startPos + Vector3.UnitX * (length * (float)done);
                    nextSet += interval;
                    if (done >= 1.0)
                    {
                        lastSetDone = true;
                        hintOffAtBeat = hintOffAfterLastSet ? beat + 1 : beat;
                    }
                }
                if (hintOn && beat >= hintOffAtBeat)
                {
                    r.Other.MovedByKeyframedMotion = hintOn = false;
                }
            },
        };
        RunResult run = KinematicTestRig.Run(sc, rate);
        int inWindow = Math.Min(behind.Count, (int)(WindowSeconds / (1.0 / 11.0)) + 1);
        double worst = 0;
        bool always = true;
        for (int i = 0; i < inWindow; i++)
        {
            worst = Math.Max(worst, Math.Abs(behind[i]));
            always &= onPrim[i] || i == 0;
        }
        return new Outcome(worst, always, endBehind, endOnPrim, endRise, startRise, true, behind, onPrim);
    }

    private static string Describe(Outcome o)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(o.Behind.Count, 14); i++)
            sb.Append(CultureInfo.InvariantCulture, $" {F(o.Behind[i])}{(o.OnPrim[i] ? "" : "!")}");
        return $"worst {F(o.WorstBehind)} behind in the first second, end {F(o.EndBehind)} behind {(o.EndOnPrim ? "on a prim" : "NOT on a prim")}; per heartbeat (! = not on a prim):{sb}";
    }

    public static IEnumerable<object[]> StartCases()
    {
        foreach (float speed in new[] { 2f, 5f, 10f, 15f, 22f, 30f })
            foreach (double rate in new[] { 45.0, 0.0 })
                foreach (float riderX in new[] { 0f, -2.88f, 2.88f })
                    foreach (float thickness in new[] { 0.5f, 0.1f })
                        foreach (bool uneven in new[] { false, true })
                            yield return new object[] { speed, rate, riderX, thickness, uneven };
    }

    // The start: from rest to the speed in one heartbeat. The avatar takes the platform's velocity in the step the motion starts and
    // keeps its place on it for the first second, at every speed up to 30 m/s, with heartbeats that find 1, 2 or 3 of the sets
    // (a heartbeat that finds 3 at 30 m/s has 4.5 m to carry, over the 4 m a heartbeat that is taken for a jump).
    [Theory]
    [MemberData(nameof(StartCases))]
    public void An_avatar_keeps_its_place_on_a_platform_that_starts_fast(float speed, double rate, float riderX, float thickness, bool uneven)
    {
        Outcome o = Run(speed, rate, riderX, thickness, uneven ? Uneven : null, pathSeconds: 1.5);
        Say($"FAST start {speed:0} m/s rate {rate:0} rider {riderX:0.00} thick {thickness:0.0}{(uneven ? " uneven" : "")}: {Describe(o)}");
        Assert.True(o.OnPrimThroughout, "the avatar stopped standing on the platform: " + Describe(o));
        Assert.True(o.WorstBehind < 0.5, "the avatar was left behind: " + Describe(o));
    }

    public static IEnumerable<object[]> StopCases()
    {
        foreach (float speed in new[] { 5f, 10f, 22f, 30f })
            foreach (double rate in new[] { 45.0, 0.0 })
                foreach (bool uneven in new[] { false, true })
                    foreach (bool hintOffAfter in new[] { false, true })
                        yield return new object[] { speed, rate, uneven, hintOffAfter };
    }

    // The end: core sets the last pose and turns the hint off at its next tick, so a heartbeat can find the last pose and the hint off
    // together. The body is made a fixed one at the last pose then, and the avatar, carried at the platform's speed up to that
    // heartbeat, is left behind by the whole heartbeat's move (1.4 m at 30 m/s). The last move is carried as the ones before it
    // were, and the avatar ends where it stood on the platform, with the body fixed again afterwards.
    [Theory]
    [MemberData(nameof(StopCases))]
    public void An_avatar_is_carried_through_the_last_heartbeat_of_the_motion(float speed, double rate, bool uneven, bool hintOffAfterLastSet)
    {
        Outcome o = Run(speed, rate, -1.5f, 0.5f, uneven ? Uneven : null, pathSeconds: 0.6, hintOffAfterLastSet, cube: false);
        Say($"FAST stop {speed:0} m/s rate {rate:0}{(uneven ? " uneven" : "")} hint off {(hintOffAfterLastSet ? "a heartbeat after the last set" : "with the last set")}: {Describe(o)} rise {F(o.EndRise - o.StartRise)}");
        Assert.True(o.EndOnPrim, "the avatar is not standing on a prim at the end: " + Describe(o));
        Assert.True(Math.Abs(o.EndBehind) < 0.3, "the avatar ended away from its place on the platform: " + Describe(o));
        Assert.True(Math.Abs(o.EndRise - o.StartRise) < 0.3, "the avatar ended at another height on the platform: " + Describe(o));
    }

    // What leaves an avatar behind on a fast platform that nothing else touches is nothing: the runs above. A physical cube that rests
    // on the platform ahead of the avatar cannot be carried by friction at these speeds, slides back into the avatar and slows it
    // as any body it meets does (a 125 kg cube, an 80 kg avatar), and the avatar is left behind by the cube, not by the platform.
    // The same run without the cube is the control.
    [Theory]
    [InlineData(10f, 0.5)]
    [InlineData(22f, 1.0)]
    public void A_cube_that_slides_back_into_the_avatar_is_what_leaves_it_behind(float speed, double leastBehind)
    {
        Outcome alone = Run(speed, 0.0, -1.5f, 0.5f, null, pathSeconds: 1.5);
        Outcome withCube = Run(speed, 0.0, -1.5f, 0.5f, null, pathSeconds: 1.5, cube: true);
        Say($"FAST cube {speed:0} m/s one step per heartbeat: alone {F(alone.WorstBehind)} m behind, with a cube 2 m ahead of the middle {F(withCube.WorstBehind)} m behind");
        Assert.True(alone.WorstBehind < 0.5, "the avatar alone was left behind: " + Describe(alone));
        Assert.True(withCube.WorstBehind > leastBehind, "the cube did not slow the avatar: " + Describe(withCube));
    }

    // With the motion over the body is a fixed one again, and a platform that is told to move again carries from rest as before.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_platform_is_a_fixed_body_again_after_the_motion_and_carries_the_next_one(double rate)
    {
        bool fixedAfter = false;
        double secondOff = double.NaN;
        float secondStartBehind = 0f;
        var sc = KinematicTestRig.Platform("avatar", 9f, r =>
        {
            int h = r.Heartbeats;
            if (h == 44)
                r.Other.MovedByKeyframedMotion = true;
            if (h >= 44 && h <= 46)
                r.Other.Position += new Vector3(1f, 0f, 0f);
            if (h == 46)
                r.Other.MovedByKeyframedMotion = false;
            if (h == 66)
            {
                fixedAfter = !KinematicTestRig.PlatformPrim(r).IsKinematicBody;
                r.Other.MovedByKeyframedMotion = true;
                secondStartBehind = r.Other.Position.X - r.Actor.Position.X;
            }
            if (h >= 67 && h <= 69)
                r.Other.Position += new Vector3(1.5f, 0f, 0f);
            if (h == 70)
                r.Other.MovedByKeyframedMotion = false;
            if (h == 80)
                secondOff = (r.Other.Position.X - r.Actor.Position.X) - secondStartBehind;
        });
        KinematicTestRig.Run(sc, rate);
        Assert.True(fixedAfter, "the platform's body is not a fixed one after the motion ended");
        Assert.True(Math.Abs(secondOff) < 0.3, $"the avatar ended {secondOff:0.00} m from its place after the second motion");
    }
}
