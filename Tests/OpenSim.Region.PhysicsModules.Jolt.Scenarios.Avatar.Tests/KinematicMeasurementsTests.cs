/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
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
/// Measurements of kinematic platforms that the first figures did not cover: a keyframed linkset that turns with riders on a child
/// prim, the collision events between a rider and a moving platform, the cost of many moving platforms, and a sleeping rider when
/// its platform starts. Each figure is a line "KINM ..." in the test output; the assertions are the bounds the figures must stay in.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class KinematicMeasurementsTests
{
    private readonly ITestOutputHelper _out;
    public KinematicMeasurementsTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static readonly (string, string) On = ("MovedPrimsCarryRiders", "true");

    // ------------------------------------------------------------------ a turning linkset, riders on a child prim

    // The root is a 2 x 2 x 0.5 m platform and its one child a 4 x 4 x 0.5 m platform whose middle is 3 m from the root's, so the
    // child's top is a platform 3 m from the axis the object turns about. Keyframed motion turns the object about z at 30 degrees
    // a second for 3 s and sets the child's pose with the root's, as core sets a group's parts. The rider stands on the child, 3 m
    // from the axis.
    private (double platformDegrees, double riderDegrees, double radius, double rise, bool sameBody) TurnWithRiderOnChild(string rider, double rate)
    {
        const float armLength = 3f;
        float angle = 0f;
        JoltPrim child = null;
        BodyId rootBody = default;
        bool sameBody = true;
        Vector3 centre = default;
        double riderDegrees = double.NaN, radius = double.NaN, rise = double.NaN;
        float riderZ0 = float.NaN;
        var sc = KinematicTestRig.Platform("none", 9f, r =>
        {
            int h = r.Heartbeats;
            if (h == 0)
            {
                centre = r.Other.Position;
                // The child, then the rider on it (the rider is made after the child so that it never falls through).
                child = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(4f, 4f, 0.5f), centre + new Vector3(armLength, 0f, 0f), Quaternion.Identity, false, 5501u, linkTo: r.Other);
                float top = centre.Z + 0.25f;
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(centre.X + armLength, centre.Y, top + 0.26f), Quaternion.Identity);
                else
                    r.AddAvatarAt(new Vector3(centre.X + armLength, centre.Y, top + r.AvatarStandHalf + 0.05f), false);
            }
            if (h == 44)
            {
                r.Other.MovedByKeyframedMotion = true;
                rootBody = KinematicTestRig.PlatformPrim(r).BodyHandle;
                Vector3 rel0 = r.Actor.Position - centre;
                riderZ0 = r.Actor.Position.Z;
            }
            if (h >= 44 && h < 44 + 33)
            {
                double t = (h - 44 + 1) * r.Dt;
                angle = (float)(30.0 * t * Math.PI / 180.0);
                Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
                r.Other.Orientation = q;
                child.Position = centre + Vector3.Transform(new Vector3(armLength, 0f, 0f), q);
                child.Orientation = q;
                if (!KinematicTestRig.PlatformPrim(r).BodyHandle.Equals(rootBody))
                    sameBody = false;
            }
            if (h == 44 + 33)
                r.Other.MovedByKeyframedMotion = false;
            if (h == 44 + 33 + 20)
            {
                Vector3 rel = r.Actor.Position - centre;
                radius = Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y);
                riderDegrees = Math.Atan2(rel.Y, rel.X) * 180.0 / Math.PI;
                rise = r.Actor.Position.Z - riderZ0;
            }
        });
        KinematicTestRig.Run(sc, rate);
        return (angle * 180.0 / Math.PI, riderDegrees, radius, rise, sameBody);
    }

    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("box", 45.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    public void A_turning_keyframed_linkset_carries_a_rider_on_its_child_prim(string rider, double rate)
    {
        var f = TurnWithRiderOnChild(rider, rate);
        Say($"KINM linkset turn {rider} on the child, rate {rate:0}: platform turned {F(f.platformDegrees)} degrees, rider at {F(f.riderDegrees)} degrees, {F(f.radius)} m from the axis (3.00 at the start), rise {F(f.rise)} m, root body kept {f.sameBody}");
        Assert.True(f.sameBody, "the root's body was made again while the linkset turned");
        Assert.True(Math.Abs(f.radius - 3.0) < 0.5, $"the rider's radius was {f.radius:0.00} m");
        Assert.True(Math.Abs(f.riderDegrees - f.platformDegrees) < 12.0, $"the rider is at {f.riderDegrees:0.0} degrees, the platform turned {f.platformDegrees:0.0}");
        Assert.True(f.rise > -0.5 && f.rise < 0.5, $"the rider rose {f.rise:0.00} m");
    }

    // ------------------------------------------------------------------ collision events of a rider and a moving platform

    private sealed class EventCount
    {
        // Counted in three phases (0 rest, 1 moving, 2 after); one contact state across them, as core keeps one list.
        public int Phase;
        public readonly int[] Updates = new int[3], Starts = new int[3], Ends = new int[3];
        private bool _touching;
        public void Add(CollisionEventUpdate u, uint otherId)
        {
            bool now = u.m_objCollisionList.ContainsKey(otherId);
            Updates[Phase]++;
            if (now && !_touching) Starts[Phase]++;
            if (!now && _touching) Ends[Phase]++;
            _touching = now;
        }
    }

    // The rider stands on a 6 x 6 platform 4 s, then the platform moves 6 m at 1 m/s (keyframed, hint on), then 2 s more. Counted from
    // the rider's collision updates (what core turns into collision_start, collision and collision_end of the rider's object, or
    // of the avatar's scripts) and from the platform's, in each of the three phases.
    private (EventCount rider, EventCount platform) CollisionRun(string rider, double rate, bool carries)
    {
        var riderCount = new EventCount(); var platform = new EventCount();
        var sc = KinematicTestRig.Platform(rider, 12f, r =>
        {
            int h = r.Heartbeats;
            if (h == 0)
            {
                r.Actor.OnCollisionUpdate += e => riderCount.Add((CollisionEventUpdate)e, r.Other.LocalID);
                r.Actor.SubscribeEvents(100);
                r.Other.OnCollisionUpdate += e => platform.Add((CollisionEventUpdate)e, r.Actor.LocalID);
                r.Other.SubscribeEvents(100);
            }
            if (h == 44) { riderCount.Phase = platform.Phase = 1; r.Other.MovedByKeyframedMotion = true; }
            if (h >= 44 && h < 44 + 66)
                r.Other.Position += new Vector3(0.0909f, 0f, 0f);   // 1 m/s, one set a heartbeat
            if (h == 44 + 66) { riderCount.Phase = platform.Phase = 2; r.Other.MovedByKeyframedMotion = false; }
        });
        KinematicTestRig.Run(sc, rate, ("KeyframedMotionCarriesRiders", carries ? "true" : "false"));
        return (riderCount, platform);
    }

    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("box", 45.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    public void A_rider_on_a_moving_platform_raises_one_contact_not_a_stream_of_them(string rider, double rate)
    {
        var (r, p) = CollisionRun(rider, rate, carries: true);
        var (rOff, pOff) = CollisionRun(rider, rate, carries: false);
        string Line(EventCount c) => $"updates {c.Updates[0]}/{c.Updates[1]}/{c.Updates[2]}, starts {c.Starts[0]}/{c.Starts[1]}/{c.Starts[2]}, ends {c.Ends[0]}/{c.Ends[1]}/{c.Ends[2]}";
        Say($"KINM events {rider} rate {rate:0}, per phase (4 s at rest, 6 s moving, 2 s after): carried: rider {Line(r)}; platform {Line(p)}");
        Say($"KINM events {rider} rate {rate:0}, per phase, not carried (KeyframedMotionCarriesRiders false): rider {Line(rOff)}; platform {Line(pOff)}");
        // Carried, the contact stays the one contact: it starts before the platform moves (4 s at rest) and neither starts again nor
        // ends while it moves, and the updates a second while moving are the ones at rest.
        Assert.Equal(0, r.Starts[1]);
        Assert.Equal(0, r.Ends[1]);
        Assert.Equal(0, r.Starts[2]);
        Assert.Equal(0, r.Ends[2]);
        Assert.Equal(0, p.Starts[1]);
        Assert.Equal(0, p.Ends[1]);
        Assert.True(r.Updates[1] <= 6 * 12, $"{r.Updates[1]} updates in 6 s");
    }

    // ------------------------------------------------------------------ many moving platforms

    // N platforms of 2 x 2 x 0.5 m on a grid, each moved 1 m/s along x by two sets a heartbeat (as the 50 ms timer sets them), 6 s.
    // Returns milliseconds a heartbeat (wall clock from one heartbeat's input to the next, which includes the step and the harness
    // sampling, and not the time spent making the sets), over heartbeats 20 to 60.
    private (double stepMs, double setMs) MsPerHeartbeat(int count, bool hint, double rate)
    {
        var platforms = new List<JoltPrim>();
        var stamps = new List<long>();
        long setTicks = 0;
        var sc = new Scenario
        {
            Name = "many-platforms",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(60f, 60f, r.GroundAt(60f, 60f) + 0.26f), Quaternion.Identity);   // the sampled actor
                for (int i = 0; i < count; i++)
                {
                    float x = 20f + 14f * (i % 16), y = 20f + 14f * (i / 16);
                    float z = r.GroundAt(x, y) + 3f;
                    platforms.Add((JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(2f, 2f, 0.5f), new Vector3(x, y, z), Quaternion.Identity, false, (uint)(7000 + i)));
                }
            },
            Input = r =>
            {
                int h = r.Heartbeats;
                stamps.Add(Stopwatch.GetTimestamp());
                if (h == 4 && hint)
                    foreach (JoltPrim p in platforms) p.MovedByKeyframedMotion = true;
                if (h >= 5)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    for (int k = 0; k < 2; k++)
                        foreach (JoltPrim p in platforms)
                            p.Position += new Vector3(0.0455f, 0f, 0f);
                    long spent = Stopwatch.GetTimestamp() - t0;
                    if (h >= 20 && h < 60) setTicks += spent;
                }
            },
        };
        KinematicTestRig.Run(sc, rate);
        double ticks = stamps[60] - stamps[20] - setTicks;
        return (ticks * 1000.0 / Stopwatch.Frequency / 40.0, setTicks * 1000.0 / Stopwatch.Frequency / 40.0);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void Many_moving_platforms_are_reported_with_the_hint_on_and_off(int count)
    {
        foreach (double rate in new[] { 0.0, 45.0 })
        {
            MsPerHeartbeat(count, true, rate);   // a warm-up run: first-use costs stay out of the figures
            var on = MsPerHeartbeat(count, true, rate);
            var off = MsPerHeartbeat(count, false, rate);
            Say($"KINM many {count} platforms rate {rate:0}: hint on: heartbeat {on.stepMs:0.00} ms (the step and the harness), the sets {on.setMs:0.00} ms; "
                + $"hint off: heartbeat {off.stepMs:0.00} ms, the sets {off.setMs:0.00} ms (rough: builds may run beside this)");
            Assert.True(on.stepMs + on.setMs < 40.0 * (count / 50.0 + 1), $"{on.stepMs + on.setMs:0.0} ms a heartbeat with the hint on");
        }
    }

    // ------------------------------------------------------------------ a sleeping rider when the platform starts

    private (bool asleepBefore, bool wokeAtOnce, double carried, double platformMoved) SleepingCube(bool viaHint, double rate)
    {
        bool asleep = false, woke = false;
        double carried = double.NaN, moved = double.NaN;
        Vector3 cube0 = default, plat0 = default;
        JoltPrim cube = null;
        var sc = KinematicTestRig.Platform("box", 14f, r =>
        {
            int h = r.Heartbeats;
            if (h == 0) cube = (JoltPrim)r.Actor;
            if (h == 77)   // 7 s on the platform: the cube is asleep
            {
                asleep = cube.PhysicalAndAsleep;
                cube0 = r.Actor.Position; plat0 = r.Other.Position;
                if (viaHint) r.Other.MovedByKeyframedMotion = true;
            }
            if (h >= 77 && h < 77 + 22 && (viaHint || h % 2 == 1))
                r.Other.Position += new Vector3(viaHint ? 0.0909f : 0.2f, 0f, 0f);
            if (h == 77 + 1) woke = !cube.PhysicalAndAsleep;
            if (h == 77 + 30)
            {
                carried = r.Actor.Position.X - cube0.X;
                moved = r.Other.Position.X - plat0.X;
            }
        });
        KinematicTestRig.Run(sc, rate, On);
        return (asleep, woke, carried, moved);
    }

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(true, 45.0)]
    [InlineData(false, 0.0)]
    [InlineData(false, 45.0)]
    public void A_cube_asleep_on_a_platform_wakes_and_rides_when_the_platform_starts(bool viaHint, double rate)
    {
        var f = SleepingCube(viaHint, rate);
        Say($"KINM sleeping cube, {(viaHint ? "keyframed" : "plain sets")}, rate {rate:0}: asleep before {f.asleepBefore}, awake one heartbeat after the first move {f.wokeAtOnce}, platform moved {F(f.platformMoved)} m, cube {F(f.carried)} m");
        Assert.True(f.asleepBefore, "the cube was not asleep when the platform started");
        Assert.True(f.carried > 0.5 * f.platformMoved, $"the cube moved {f.carried:0.00} m while the platform moved {f.platformMoved:0.00} m");
    }
}
