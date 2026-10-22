/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A fixed prim that is moved or turned wakes every physical body resting on it, however many there are. The engine
/// wakes nothing when a static body moves, so the module wakes them; a limit on how many it finds would leave the rest
/// asleep in mid-air. Here 100 boxes sleep on a platform and the platform is moved away (llSetPos) or turned.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class WakeAroundMovedSupportTests
{
    private const int PerSide = 10;
    private const float Spacing = 2f;
    private static readonly Vector3 BoxSize = new(0.5f, 0.5f, 0.5f);
    private const double ActAt = 6.0;

    private sealed class Result
    {
        public readonly List<HarnessPart> Boxes = new();
        public HarnessPart Platform;
        public double ActedAt = double.NaN;
        public int AsleepBeforeAct, AsleepAfterAct = -1;
        public float[] RestZ;
        public readonly List<(double T, float[] Z)> Trace = new();
    }

    private static Result Run(Action<Result> act)
    {
        var res = new Result();
        var sc = new Scenario
        {
            Name = "wake-around-moved-support",
            DefaultDuration = _ => 9f,
            Setup = r =>
            {
                float top = r.GroundAt(128f, 128f) + 3f;
                res.Platform = PhantomScenarios.AddPart(r, "platform", new Vector3(24f, 24f, 0.5f), new Vector3(128f, 128f, top - 0.25f),
                                                        false, false, false, false);
                for (int i = 0; i < PerSide; i++)
                    for (int j = 0; j < PerSide; j++)
                    {
                        float x = 128f + (i - (PerSide - 1) * 0.5f) * Spacing, y = 128f + (j - (PerSide - 1) * 0.5f) * Spacing;
                        res.Boxes.Add(PhantomScenarios.AddPart(r, $"box{i}_{j}", BoxSize, new Vector3(x, y, top + BoxSize.Z * 0.5f + 0.01f),
                                                               true, false, false, false));
                    }
                r.Actor = res.Boxes[0].Actor;
                r.ActorSize = BoxSize;
            },
            Input = r =>
            {
                res.Trace.Add((r.Now, res.Boxes.Select(b => b.Actor.Position.Z).ToArray()));
                if (!double.IsNaN(res.ActedAt) && res.AsleepAfterAct < 0)
                    res.AsleepAfterAct = res.Boxes.Count(b => ((JoltPrim)b.Actor).PhysicalAndAsleep);   // the next heartbeat
                if (double.IsNaN(res.ActedAt) && r.Now >= ActAt)
                {
                    res.AsleepBeforeAct = res.Boxes.Count(b => ((JoltPrim)b.Actor).PhysicalAndAsleep);
                    res.RestZ = res.Trace[^1].Z;
                    act(res);
                    res.ActedAt = r.Now;
                }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });
        return res;
    }

    private static void AssertEveryBoxFell(Result res)
    {
        Assert.Equal(PerSide * PerSide, res.AsleepBeforeAct);
        (double T, float[] Z) later = res.Trace.First(p => p.T >= res.ActedAt + 1.5 - 1e-9);
        // Free fall covers 4.9 m in a second and the ground is 3 m down.
        int stuck = 0;
        for (int i = 0; i < res.Boxes.Count; i++)
            if (later.Z[i] > res.RestZ[i] - 2.5f)
                stuck++;
        Assert.True(stuck == 0, $"{stuck} of {res.Boxes.Count} boxes were still in the air 1.5 s after the platform moved");
    }

    [Fact]
    public void Every_one_of_100_sleeping_boxes_wakes_and_falls_when_its_platform_is_moved_away()
    {
        Result res = Run(s => s.Platform.Actor.Position += new Vector3(40f, 0f, 0f));   // llSetPos on the platform
        AssertEveryBoxFell(res);
    }

    [Fact]
    public void Every_one_of_100_sleeping_boxes_wakes_when_its_platform_is_turned()
    {
        // Turned in place (llSetRot): most boxes stay on it, so the check is that all of them are awake at the next heartbeat.
        Result res = Run(s => s.Platform.Actor.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f));
        Assert.Equal(PerSide * PerSide, res.AsleepBeforeAct);
        Assert.True(res.AsleepAfterAct == 0, $"{res.AsleepAfterAct} of {res.Boxes.Count} boxes were still asleep after the platform turned");
    }

    [Fact]
    public void Every_one_of_100_sleeping_boxes_wakes_when_its_platform_is_moved_up_out_from_under_them()
    {
        // Moved straight up: the new place no longer overlaps the boxes, but the old one does.
        Result res = Run(s => s.Platform.Actor.Position += new Vector3(0f, 0f, 30f));
        AssertEveryBoxFell(res);
    }
}
