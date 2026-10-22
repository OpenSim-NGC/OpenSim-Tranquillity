/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] MovedPrimsCarryRiders (default false): a non-physical prim that a series of plain position or rotation sets moves
/// (llSetPos, llSetRegionPos, llSetPrimitiveParams, the build tool; no keyframed-motion hint) carries what stands on it while
/// the series lasts. Off, the prim is a fixed body set to each new place, as before. Each figure is a line "MOVE ..." in the
/// test output. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MovedPrimsCarryRidersTests
{
    private readonly ITestOutputHelper _out;
    public MovedPrimsCarryRidersTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static readonly (string, string) On = ("MovedPrimsCarryRiders", "true");
    private static readonly (string, string) Off = ("MovedPrimsCarryRiders", "false");

    private static KinematicScenarios.Figures Measure(string rider, bool up, float speed, double rate, params (string, string)[] jolt)
        => KinematicScenarios.Measure(KinematicTestRig.Run(KinematicScenarios.Motion(rider, up, speed, keyframed: false, hint: false), rate, jolt), up);

    // ------------------------------------------------------------------ the setting

    [Fact]
    public void The_setting_is_off_by_default_and_read_from_the_config()
    {
        Assert.False(new JoltConfig().MovedPrimsCarryRiders);
        Assert.True(new JoltConfig().KeyframedMotionCarriesRiders);
        Assert.False(JoltConfig.FromConfig(new Nini.Config.IniConfigSource(), null).MovedPrimsCarryRiders);
        var src = new Nini.Config.IniConfigSource();
        src.AddConfig("Jolt").Set("MovedPrimsCarryRiders", "true");
        var warnings = new List<string>();
        Assert.True(JoltConfig.FromConfig(src, warnings).MovedPrimsCarryRiders);
        Assert.Empty(warnings);
    }

    // ------------------------------------------------------------------ carried (setting on)

    public static IEnumerable<object[]> Cases()
    {
        foreach (string rider in new[] { "box", "avatar" })
            foreach (bool up in new[] { false, true })
                foreach (float speed in new[] { 1f, 3f })
                    foreach (double rate in new[] { 0.0, 45.0 })
                        yield return new object[] { rider, up, speed, rate };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_rider_on_a_platform_moved_by_plain_sets_is_carried_with_the_setting_on(string rider, bool up, float speed, double rate)
    {
        KinematicScenarios.Figures f = Measure(rider, up, speed, rate, On);
        Say($"MOVE on {rider} {(up ? "up" : "side")} {speed:0} m/s rate {rate:0}: {f.Verdict}, slide {F(f.Slide)} lag {F(f.MaxLag)} sink {F(f.MaxSink)} air {F(f.MaxAir)}");
        Assert.True(f.OnAtEnd, $"{rider} {(up ? "up" : "sideways")} at {speed} m/s: {f.Verdict}");
        Assert.True(f.MaxSink < 0.5, $"it sank {f.MaxSink:0.00} m below its resting height");
        // The body glides to each step over the time the sets are apart, so it trails the object by up to one step (0.2 m at
        // 1 m/s, 0.6 m at 3 m/s) and a rider with it.
        Assert.True(f.Slide < 0.3 * speed + 0.4, $"it slid {f.Slide:0.00} m on the platform");
    }

    // A platform lifted 0.6 m by each set (3 m/s at 0.2 s): with the setting off, a fixed body set 0.6 m up into the cube throws it and
    // it falls. With it on, the first set already carries it (the body takes the step at no more than 3 m/s), as the sets after it do.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_cube_on_a_platform_lifted_in_big_steps_is_carried_from_the_first_set_with_the_setting_on(double rate)
    {
        KinematicScenarios.Figures on = Measure("box", up: true, 3f, rate, On);
        KinematicScenarios.Figures off = Measure("box", up: true, 3f, rate, Off);
        Say($"MOVE cube up 3 m/s rate {rate:0}: on {on.Verdict} sink {F(on.MaxSink)} air {F(on.MaxAir)}; off {off.Verdict} sink {F(off.MaxSink)}");
        Assert.True(on.OnAtEnd, on.Verdict);
        Assert.True(on.MaxSink < 0.5, $"it sank {on.MaxSink:0.00} m below its resting height");
        Assert.True(on.MaxAir < 0.8, $"it rose {on.MaxAir:0.00} m above its resting height");
        Assert.False(off.OnAtEnd);
    }

    // ------------------------------------------------------------------ off: as before

    [Theory]
    [InlineData("box", false, 1f, 0.0)]
    [InlineData("box", false, 3f, 45.0)]
    [InlineData("avatar", false, 1f, 0.0)]
    [InlineData("avatar", false, 3f, 45.0)]
    [InlineData("box", true, 1f, 0.0)]
    [InlineData("avatar", true, 3f, 45.0)]
    public void Plain_sets_are_as_before_with_the_setting_off(string rider, bool up, float speed, double rate)
    {
        // Every sample of the run, not only its figures: the default and an explicit "false" are the one run, and the platform's
        // body is a fixed one for the whole of it.
        RunResult byDefault = KinematicTestRig.Run(KinematicScenarios.Motion(rider, up, speed, keyframed: false, hint: false), rate);
        RunResult explicitOff = KinematicTestRig.Run(KinematicScenarios.Motion(rider, up, speed, keyframed: false, hint: false), rate, Off);
        Assert.Equal(byDefault.Samples.Count, explicitOff.Samples.Count);
        for (int i = 0; i < byDefault.Samples.Count; i++)
            Assert.Equal(byDefault.Samples[i], explicitOff.Samples[i]);
        bool everKinematic = false;
        var sc = KinematicTestRig.Platform(rider, 8f, r =>
        {
            if (r.Heartbeats >= 44 && r.Heartbeats % 2 == 0 && r.Heartbeats < 70)
                r.Other.Position += new Vector3(0.2f, 0f, 0f);
            everKinematic |= KinematicTestRig.PlatformPrim(r).IsKinematicBody;
        });
        KinematicTestRig.Run(sc, rate);
        Assert.False(everKinematic, "the platform's body stayed a fixed one");
    }

    // ------------------------------------------------------------------ when a series of sets is a motion

    // The platform's state after each heartbeat's input (before that heartbeat's step), for the heartbeats asked for.
    private static List<bool> KinematicAfterSets(Action<Run, JoltPrim> sets, int heartbeats, double rate = 0.0, params (string, string)[] jolt)
    {
        var states = new List<bool>();
        var sc = KinematicTestRig.Platform("none", 12f, r =>
        {
            JoltPrim p = KinematicTestRig.PlatformPrim(r);
            sets(r, p);
            if (r.Heartbeats <= heartbeats)
                states.Add(p.IsKinematicBody);
        });
        KinematicTestRig.Run(sc, rate, jolt);
        return states;
    }

    // Sets 0.18 s apart (two heartbeats at 11 Hz): the second makes the body kinematic.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_second_of_two_sets_a_heartbeat_or_two_apart_begins_a_motion(double rate)
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20 || r.Heartbeats == 22) r.Other.Position += new Vector3(0.2f, 0f, 0f);
        }, 24, rate, On);
        Assert.False(k[20], "the first set is a plain set");   // k[h] is the state at heartbeat h
        Assert.False(k[21]);
        Assert.True(k[22], "the second set made the body kinematic");
    }

    [Fact]
    public void A_single_set_is_not_a_motion()
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20) r.Other.Position += new Vector3(0.2f, 0f, 0f);
        }, 40, 0.0, On);
        Assert.DoesNotContain(true, k);
    }

    [Fact]
    public void Two_sets_in_the_one_heartbeat_are_not_a_motion()
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20)
            {
                r.Other.Position += new Vector3(0.2f, 0f, 0f);
                r.Other.Position += new Vector3(0.2f, 0f, 0f);
                r.Other.Position += new Vector3(0.2f, 0f, 0f);
            }
        }, 40, 0.0, On);
        Assert.DoesNotContain(true, k);
    }

    // The longest gap a series has: 0.25 s, and a heartbeat for the heartbeat the clock counts in (0.27 s is three heartbeats at 11 Hz).
    [Fact]
    public void Sets_three_heartbeats_apart_are_a_series_and_sets_four_apart_are_not()
    {
        List<bool> three = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20 || r.Heartbeats == 23) r.Other.Position += new Vector3(0.2f, 0f, 0f);
        }, 24, 0.0, On);
        Assert.True(three[23], "0.27 s apart is a series");
        List<bool> four = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20 || r.Heartbeats == 24 || r.Heartbeats == 28 || r.Heartbeats == 32) r.Other.Position += new Vector3(0.2f, 0f, 0f);
        }, 40, 0.0, On);
        Assert.DoesNotContain(true, four);   // 0.36 s apart: each set is a plain set
    }

    [Fact]
    public void A_set_further_than_the_jump_distance_is_not_a_motion()
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20) r.Other.Position += new Vector3(0.2f, 0f, 0f);
            if (r.Heartbeats == 22) r.Other.Position += new Vector3(JoltPrim.KinematicJumpDistance + 1f, 0f, 0f);
        }, 26, 0.0, On);
        Assert.DoesNotContain(true, k);
    }

    [Fact]
    public void A_turn_of_more_than_a_quarter_circle_in_one_set_is_not_a_motion()
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats == 20) r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
            if (r.Heartbeats == 22) r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f + 2.0f);
        }, 26, 0.0, On);
        Assert.DoesNotContain(true, k);
    }

    [Fact]
    public void A_series_of_turns_is_a_motion()
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats >= 20 && r.Heartbeats % 2 == 0 && r.Heartbeats < 30)
                r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (r.Heartbeats - 18) * 0.1f);
        }, 24, 0.0, On);
        Assert.True(k[22]);
    }

    // The quiet time: 0.5 s with no set. The last set is at heartbeat 30; the body is kinematic through the gaps of the series, comes
    // to rest at the last pose, and is a fixed body there after the quiet time.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_body_is_fixed_again_where_it_was_left_after_the_quiet_time(double rate)
    {
        Vector3 last = default;
        bool restedAtLast = false, fixedAtLast = false;
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            if (r.Heartbeats >= 20 && r.Heartbeats <= 30 && r.Heartbeats % 2 == 0)
            {
                r.Other.Position += new Vector3(0.2f, 0f, 0f);
                last = r.Other.Position;
            }
            var scene = KinematicTestRig.SceneOf(r);
            if (r.Heartbeats == 34)
                restedAtLast = scene.Backend.TryGetBodyState(p.BodyHandle, out BodyState s) && s.LinearVelocity.Length() < 1e-4f && MathF.Abs(s.Position.X - last.X) < 0.01f;
            if (r.Heartbeats == 44)
                fixedAtLast = !p.IsKinematicBody && scene.Backend.TryGetBodyState(p.BodyHandle, out BodyState s2) && MathF.Abs(s2.Position.X - last.X) < 0.01f;
        }, 50, rate, On);
        Assert.True(k[30], "kinematic at the last set");
        Assert.True(k[31], "and through the heartbeats after it");
        Assert.True(restedAtLast, "the body is at rest at the last pose before the quiet time is over");
        Assert.False(k[50], "fixed again");
        Assert.True(fixedAtLast, "and it is where the object was left");
    }

    // A new series after the quiet time begins as the first did.
    [Fact]
    public void A_second_series_after_a_quiet_time_begins_a_new_motion()
    {
        List<bool> k = KinematicAfterSets((r, p) =>
        {
            int h = r.Heartbeats;
            if ((h >= 20 && h <= 26 && h % 2 == 0) || (h >= 60 && h <= 66 && h % 2 == 0))
                r.Other.Position += new Vector3(0.2f, 0f, 0f);
        }, 70, 0.0, On);
        Assert.True(k[26]);
        Assert.False(k[50]);
        Assert.False(k[60], "the first set of the second series is a plain set");
        Assert.True(k[62], "its second is not");
    }

    // ------------------------------------------------------------------ what is not moved this way

    // Four prims given the same two sets two heartbeats apart: a physical one, a phantom one, a volume detector and a plain solid one
    // (the control, which does become kinematic).
    [Fact]
    public void A_physical_phantom_or_volume_detect_prim_is_not_made_kinematic()
    {
        bool physical = true, phantom = true, volume = true, plain = false;
        var sc = KinematicTestRig.Platform("none", 3f, r =>
        {
            if (r.Heartbeats == 3)
            {
                JoltPrim ph = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(100f, 100f, 40f), Quaternion.Identity, true, 5301u);
                JoltPrim pn = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(104f, 100f, 40f), Quaternion.Identity, false, 5302u);
                JoltPrim vd = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(108f, 100f, 40f), Quaternion.Identity, false, 5303u);
                JoltPrim pl = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(112f, 100f, 40f), Quaternion.Identity, false, 5304u);
                pn.Phantom = true;
                vd.SetVolumeDetect(1);
                for (int step = 0; step < 2; step++)
                    foreach (JoltPrim p in new[] { ph, pn, vd, pl })
                        p.Position += new Vector3(0.1f, 0f, 0f);   // both sets in this heartbeat: the clock has not moved, so not yet a series
                physical = ph.IsKinematicBody; phantom = pn.IsKinematicBody; volume = vd.IsKinematicBody; plain = pl.IsKinematicBody;
            }
        });
        KinematicTestRig.Run(sc, 0.0, On);
        Assert.False(physical);
        Assert.False(phantom);
        Assert.False(volume);
        Assert.False(plain);   // the same heartbeat: not a series
        bool physical2 = true, phantom2 = true, volume2 = true, plain2 = false;
        JoltPrim[] four = null;
        var sc2 = KinematicTestRig.Platform("none", 3f, r =>
        {
            if (r.Heartbeats == 3)
            {
                four = new[]
                {
                    (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(100f, 100f, 40f), Quaternion.Identity, true, 5311u),
                    (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(104f, 100f, 40f), Quaternion.Identity, false, 5312u),
                    (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(108f, 100f, 40f), Quaternion.Identity, false, 5313u),
                    (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(112f, 100f, 40f), Quaternion.Identity, false, 5314u),
                };
                four[1].Phantom = true;
                four[2].SetVolumeDetect(1);
            }
            if (r.Heartbeats == 6 || r.Heartbeats == 8)
                foreach (JoltPrim p in four)
                    p.Position += new Vector3(0.1f, 0f, 0f);
            if (r.Heartbeats == 9)
            {
                physical2 = four[0].IsKinematicBody; phantom2 = four[1].IsKinematicBody; volume2 = four[2].IsKinematicBody; plain2 = four[3].IsKinematicBody;
            }
        });
        KinematicTestRig.Run(sc2, 0.0, On);
        Assert.False(physical2);
        Assert.False(phantom2);
        Assert.False(volume2);
        Assert.True(plain2, "a plain solid prim is the control: it does become kinematic");
    }

    // ------------------------------------------------------------------ a keyframed prim: the hint decides

    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    public void The_setting_changes_nothing_for_a_keyframed_prim(string rider, double rate)
    {
        RunResult off = KinematicTestRig.Run(KinematicScenarios.Motion(rider, up: false, 3f, keyframed: true, hint: true), rate, Off);
        RunResult on = KinematicTestRig.Run(KinematicScenarios.Motion(rider, up: false, 3f, keyframed: true, hint: true), rate, On);
        Assert.Equal(off.Samples.Count, on.Samples.Count);
        for (int i = 0; i < off.Samples.Count; i++)
            Assert.Equal(off.Samples[i], on.Samples[i]);
        Assert.True(KinematicScenarios.Measure(on, up: false).OnAtEnd, "carried, as with the setting off");
    }

    [Fact]
    public void With_keyframed_carrying_off_the_setting_does_not_carry_a_keyframed_prim()
    {
        // The hint, not the pace of the sets, decides: a keyframed prim whose carrying is off is a fixed body whatever
        // MovedPrimsCarryRiders says, and its sets every 50 ms are not taken for a series.
        RunResult res = KinematicTestRig.Run(KinematicScenarios.Motion("avatar", up: false, 1f, keyframed: true, hint: true), 0.0, On, ("KeyframedMotionCarriesRiders", "false"));
        KinematicScenarios.Figures f = KinematicScenarios.Measure(res, up: false);
        Say($"MOVE keyframed, carrying off, moves on: {f.Verdict}, sink {F(f.MaxSink)}");
        Assert.False(f.OnAtEnd);
        Assert.True(f.MaxSink > 2.5);
    }

    [Fact]
    public void A_series_that_is_under_way_hands_over_to_the_hint_and_back()
    {
        bool kinematicBefore = false, kinematicWithHint = false, kinematicAfter = true;
        var sc = KinematicTestRig.Platform("none", 12f, r =>
        {
            JoltPrim p = KinematicTestRig.PlatformPrim(r);
            int h = r.Heartbeats;
            if (h >= 20 && h <= 24 && h % 2 == 0) r.Other.Position += new Vector3(0.2f, 0f, 0f);
            if (h == 25) kinematicBefore = p.IsKinematicBody;
            if (h == 26) r.Other.MovedByKeyframedMotion = true;
            if (h == 27) kinematicWithHint = p.IsKinematicBody;
            if (h == 30) r.Other.MovedByKeyframedMotion = false;
            if (h == 31) kinematicAfter = p.IsKinematicBody;   // the series' own flag was cleared by the hint: fixed again, not left kinematic
        });
        KinematicTestRig.Run(sc, 0.0, On);
        Assert.True(kinematicBefore);
        Assert.True(kinematicWithHint);
        Assert.False(kinematicAfter);
    }

    // ------------------------------------------------------------------ a linkset root, a rotation, a jump, a sleeping rider

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_root_of_a_linkset_stepped_by_plain_sets_carries_an_avatar_and_keeps_its_body(double rate)
    {
        JoltPrim child = null;
        BodyId first = default;
        bool sameBody = true;
        Vector3 startPlat = default;
        var offset = new Vector3(2.5f, 0f, 0f);
        var sc = KinematicTestRig.Platform("avatar", 9f, r =>
        {
            if (r.Heartbeats == 0) startPlat = r.Other.Position;
            if (r.Heartbeats == 1)
                child = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), r.Other.Position + offset, Quaternion.Identity, false, 5401u, linkTo: r.Other);
            // 1 m/s along +x, 0.2 m every 2 heartbeats, from heartbeat 44 (4 s)
            if (r.Heartbeats >= 44 && r.Heartbeats < 44 + 44 && r.Heartbeats % 2 == 0)
            {
                float x = 0.2f * ((r.Heartbeats - 44) / 2 + 1);
                r.Other.Position = startPlat + new Vector3(x, 0f, 0f);
                child.Position = startPlat + new Vector3(x, 0f, 0f) + offset;
                if (r.Heartbeats == 48)
                    first = KinematicTestRig.PlatformPrim(r).BodyHandle;
                if (r.Heartbeats > 48 && !KinematicTestRig.PlatformPrim(r).BodyHandle.Equals(first))
                    sameBody = false;
            }
        });
        RunResult res = KinematicTestRig.Run(sc, rate, On);
        Sample end = res.Samples[^1];
        Say($"MOVE linkset root avatar rate {rate:0}: avatar {F(end.Position.X - end.Other.X)} m from the root along x at the end (2 s after the last set), sat down {F(end.Other.Z - end.Position.Z)} m");
        Assert.True(sameBody, "the root's body was made again while the series ran");
        Assert.True(Math.Abs(end.Position.X - end.Other.X) < 1.0 && end.Position.Z > end.Other.Z, "the avatar is still on the platform");
    }

    // A rotation set in steps of 8.6 degrees every 0.18 s carries an avatar 2 m from the middle round.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_platform_turned_by_plain_sets_carries_an_avatar_round(double rate)
    {
        float angle = 0f;
        var sc = KinematicTestRig.Platform("avatar", 9f, r =>
        {
            if (r.Heartbeats >= 44 && r.Heartbeats < 44 + 44 && r.Heartbeats % 2 == 0)
            {
                angle += 0.15f;
                r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
            }
        });
        RunResult res = KinematicTestRig.Run(sc, rate, On);
        Sample end = res.Samples[^1];
        Vector3 rel = end.Position - end.Other;
        Say($"MOVE turn avatar at the middle rate {rate:0}: platform turned {angle * 180 / Math.PI:0} degrees; avatar {F(Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y))} m from the middle");
        Assert.True(Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y) < 0.5, "an avatar at the middle stays at the middle");
        Assert.True(rel.Z > 0.5, "and on the platform");
    }

    // A set of a long way in the middle of a series, or a half turn, flings nothing: the pose is taken with no velocity.
    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    public void A_jump_in_the_middle_of_a_series_flings_nothing(string rider, double rate)
    {
        double maxUp = 0, maxAcross = 0;
        var sc = KinematicTestRig.Platform(rider, 6f, r =>
        {
            int h = r.Heartbeats;
            if (h >= 20 && h <= 24 && h % 2 == 0) r.Other.Position += new Vector3(0.2f, 0f, 0f);
            if (h == 26) r.Other.Position += new Vector3(10f, 0f, 0f);
            if (h > 26)
            {
                Vector3 v = r.Actor.Velocity;
                maxUp = Math.Max(maxUp, v.Z);
                maxAcross = Math.Max(maxAcross, Math.Sqrt(v.X * v.X + v.Y * v.Y));
            }
        });
        RunResult res = KinematicTestRig.Run(sc, rate, On);
        Say($"MOVE jump {rider} rate {rate:0}: fastest up {F(maxUp)} m/s, across {F(maxAcross)} m/s");
        // The rider moved with the platform at 1 m/s until the jump and keeps that speed as it falls; a fling would be 100 m/s.
        Assert.True(maxAcross < 2.0, $"the rider was thrown sideways at {maxAcross:0.00} m/s");
        // A box rider falls the platform's 3 m to the land and bounces there: wood on land combines to (0.5 + 0.05) / 2 = 0.275
        // under the shipped averaging, so it leaves the land at 0.275 x sqrt(2 g x 3) = 2.1 m/s (measured 1.9 to 2.2). That is
        // the bounce, not a fling, which would be 100 m/s: it is added to the allowance of an avatar, which does not bounce.
        double bounce = rider == "box" ? 0.275 * Math.Sqrt(2 * 9.80665 * KinematicScenarios.PlatformHeight) : 0.0;
        Assert.True(maxUp < 1.0 + bounce * 1.1, $"the rider was thrown up at {maxUp:0.00} m/s");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_half_turn_in_one_set_in_the_middle_of_a_series_flings_nothing(double rate)
    {
        double maxAcross = 0, maxUp = 0;
        var sc = KinematicTestRig.Platform("avatar", 6f, r =>
        {
            int h = r.Heartbeats;
            if (h >= 20 && h <= 24 && h % 2 == 0) r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (h - 18) * 0.05f);
            if (h == 26) r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 3.0f);
            if (h > 26)
            {
                Vector3 v = r.Actor.Velocity;
                maxUp = Math.Max(maxUp, v.Z);
                maxAcross = Math.Max(maxAcross, Math.Sqrt(v.X * v.X + v.Y * v.Y));
            }
        });
        KinematicTestRig.Run(sc, rate, On);
        Say($"MOVE half turn rate {rate:0}: fastest up {F(maxUp)} m/s, across {F(maxAcross)} m/s");
        Assert.True(maxAcross < 1.0);
        Assert.True(maxUp < 1.0);
    }
}
