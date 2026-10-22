/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A keyframed platform carrying a rider reaches the region's edge. A stand-in for what core does, since the harness has one
/// region and no neighbour: the motion's timer sets the platform's pose each heartbeat, the pose that takes its middle outside
/// the region starts a crossing (SceneObjectGroup.CrossAsync: KeyframeMotion.StartCrossingCheck stops the timer and leaves the
/// hint on, PhysicsActor.CrossingStart does nothing for a prim that is not physical), and half a second later either the object
/// has crossed (core deletes the group here, which removes the actor) or the crossing failed (CrossAsyncCompleted puts the group
/// back half a metre inside the edge, stopped; CrossingFailure restarts the motion later). The rider stands on the half of the
/// platform that is still inside the region when the platform's middle goes out. Core hands over a seated avatar with the object
/// and leaves a standing one (SceneObjectGroup.CrossAsync reads only m_sittingAvatars), so a rider on a platform that crossed
/// stays in this region; what the harness can show is what the module does with it, and what it does when the crossing fails.
/// Each result is a line "KINX ..." in the test output. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class KeyframedCrossingStandInTests
{
    private readonly ITestOutputHelper _out;
    public KeyframedCrossingStandInTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private const float Edge = 256f, Y = 128f, Side = 6f, Thickness = 0.5f, Height = 3f, RiderBehind = 1.5f;
    private const float StartX = 254f, Speed = 1f;                       // the platform's middle reaches the edge after 2 s
    private const int SettleBeats = 11, TransitBeats = 6;                // a second to settle; the crossing check takes about half a second
    private const double Heartbeat = 1.0 / 11.0;

    private sealed record Outcome(double RiderHeightAtEnd, double MaxSinkWhileOnIt, double MaxSlide, double RiderOffsetAtEnd, bool PlatformRemoved, bool RiderOutsideRegion);

    private Outcome Cross(string rider, bool crossed, double physicsRate)
    {
        int crossBeat = SettleBeats + (int)Math.Round((Edge - StartX) / Speed / Heartbeat);
        int resolvedBeat = crossBeat + TransitBeats;
        int resumeBeat = resolvedBeat + 11;
        int endBeat = resumeBeat + 22;
        float top = 0f;
        Vector3 start = default;
        Vector3 offset0 = default;
        bool removed = false, outside = false;
        double maxSink = 0, maxSlide = 0;
        var sc = new Scenario
        {
            Name = "kinematic-crossing",
            DefaultDuration = _ => (float)((endBeat + 2) * Heartbeat),
            Setup = r =>
            {
                top = r.GroundAt(StartX, Y) + Height;
                start = new Vector3(StartX, Y, top - Thickness * 0.5f);
                r.AddOtherBox(new Vector3(Side, Side, Thickness), start, Quaternion.Identity, false);
                var at = new Vector3(StartX - RiderBehind, Y, 0f);
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(at.X, at.Y, top + 0.26f), Quaternion.Identity);
                else
                    r.AddAvatarAt(new Vector3(at.X, at.Y, top + r.AvatarStandHalf + 0.05f), false);
                offset0 = r.Actor.Position - start;
            },
            Input = r =>
            {
                int h = r.Heartbeats;
                if (h == SettleBeats)
                    r.Other.MovedByKeyframedMotion = true;   // the motion starts
                if (h >= SettleBeats && h <= crossBeat)
                    r.Other.Position = start + new Vector3(Speed * (float)((h - SettleBeats + 1) * Heartbeat), 0f, 0f);
                // Core's AbsolutePosition set puts the pose outside; the crossing check stops the timer: no more poses from here.
                if (crossed && h == resolvedBeat)
                {
                    KinematicTestRig.SceneOf(r).RemovePrim(r.Other);
                    removed = true;
                }
                if (!crossed && h == resolvedBeat)
                {
                    // CrossAsyncCompleted: clamped half a metre inside the edge, stopped; the actor is told.
                    r.Other.Position = new Vector3(Edge - 0.5f, Y, start.Z);
                    r.Other.CrossingFailure();
                }
                if (!crossed && h >= resumeBeat && h < endBeat)
                    r.Other.Position = new Vector3(Edge - 0.5f - 1f * (float)((h - resumeBeat + 1) * Heartbeat), Y, start.Z);   // the motion runs again, back west at 1 m/s
                if (!removed && h > SettleBeats)
                {
                    Vector3 rel = r.Actor.Position - r.Other.Position;
                    maxSink = Math.Max(maxSink, offset0.Z - rel.Z);
                    maxSlide = Math.Max(maxSlide, Math.Abs(rel.X - offset0.X));
                }
                if (r.Actor.Position.X >= Edge)
                    outside = true;
            },
        };
        RunResult res = KinematicTestRig.Run(sc, physicsRate);
        Sample last = res.Samples[^1];
        return new Outcome(last.Position.Z - (top - Height), maxSink, maxSlide, removed ? double.NaN : (last.Position - last.Other - offset0).Length(), removed, outside);
    }

    // The crossing failed: the platform is put back, stopped, and the motion runs again. The rider stays on it through the
    // jump back (0.7 m in one heartbeat is a motion, not a jump) and is carried again when the motion resumes.
    [Theory]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    [InlineData("box", 0.0)]
    [InlineData("box", 45.0)]
    public void A_rider_stays_on_a_keyframed_platform_whose_crossing_failed_and_is_carried_when_the_motion_runs_again(string rider, double rate)
    {
        Outcome o = Cross(rider, crossed: false, rate);
        Say($"KINX failed crossing {rider}, rate {rate:0}: rider off its place on the platform at the end {F(o.RiderOffsetAtEnd)} m, most it sank {F(o.MaxSinkWhileOnIt)} m, most it slid {F(o.MaxSlide)} m, height above the ground at the end {F(o.RiderHeightAtEnd)} m");
        Assert.False(o.RiderOutsideRegion, "the rider went outside the region (the rider is meant to stand on the half that stays in)");
        Assert.True(o.MaxSinkWhileOnIt < 0.3, $"the rider sank {o.MaxSinkWhileOnIt:0.00} m into or below its place on the platform");
        Assert.True(o.MaxSlide < 1.5, $"the rider slid {o.MaxSlide:0.00} m on the platform");
        Assert.True(o.RiderOffsetAtEnd < 1.5, $"the rider ended {o.RiderOffsetAtEnd:0.00} m from its place on the platform");
        Assert.True(o.RiderHeightAtEnd > Height, $"the rider ended {o.RiderHeightAtEnd:0.00} m above the ground, under the platform's top at {Height:0.0} m");
    }

    // The crossing succeeded: core removed the object here. What stood on it is not carried across (core hands over a seated
    // avatar only), so it falls to the ground in this region: the module has nothing to carry it with.
    [Theory]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    [InlineData("box", 0.0)]
    [InlineData("box", 45.0)]
    public void A_rider_on_a_keyframed_platform_that_crossed_out_of_the_region_is_left_behind_and_falls_to_the_ground(string rider, double rate)
    {
        Outcome o = Cross(rider, crossed: true, rate);
        Say($"KINX crossed {rider}, rate {rate:0}: platform removed {o.PlatformRemoved}, the rider ended {F(o.RiderHeightAtEnd)} m above the ground, outside the region {o.RiderOutsideRegion}");
        Assert.True(o.PlatformRemoved);
        Assert.False(o.RiderOutsideRegion);
        Assert.True(o.RiderHeightAtEnd < 1.2, $"the rider ended {o.RiderHeightAtEnd:0.00} m above the ground; the platform's top was {Height:0.0} m up");
    }
}
