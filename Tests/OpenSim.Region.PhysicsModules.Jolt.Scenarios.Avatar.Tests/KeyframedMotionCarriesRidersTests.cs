/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An avatar or a physical object standing on a non-physical prim that keyframed motion (llSetKeyframedMotion) is moving is
/// carried with it: core tells the prim's actor while the motion plays (PhysicsActor.MovedByKeyframedMotion), the prim's body is
/// kinematic for that time and is moved to each new pose over the step time, so it carries a velocity. The same platform moved by
/// plain position sets (llSetPos, the build tool), or by keyframed motion the actor was not told of, is a fixed body as before and
/// carries nothing. Each figure is a line "KIN ..." in the test output.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class KeyframedMotionCarriesRidersTests
{
    private readonly ITestOutputHelper _out;
    public KeyframedMotionCarriesRidersTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private static RunResult Run(Scenario sc, double physicsRate)
        => Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate });

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ keyframed: carried

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
    public void A_rider_on_a_keyframed_platform_is_carried_with_it(string rider, bool up, float speed, double physicsRate)
    {
        RunResult res = Run(KinematicScenarios.Motion(rider, up, speed, keyframed: true, hint: true), physicsRate);
        KinematicScenarios.Figures f = KinematicScenarios.Measure(res, up);
        Say($"KIN keyframed hint {rider} {(up ? "up" : "side")} {speed:0} m/s rate {physicsRate:0}: {f.Verdict}, slide {F(f.Slide)} lag {F(f.MaxLag)} sink {F(f.MaxSink)} air {F(f.MaxAir)}");

        Assert.True(f.OnAtEnd, $"{rider} {(up ? "up" : "sideways")} at {speed} m/s: {f.Verdict}");
        Assert.True(f.MaxSink < 0.5, $"it sank {f.MaxSink:0.00} m below its resting height");
        // An avatar takes the platform's velocity from it, so it hardly slides; a cube is carried by friction alone and slides back
        // on the platform while it starts and stops (Second Life's, the same).
        if (rider == "avatar")
            Assert.True(f.Slide < 0.3, $"the avatar slid {f.Slide:0.00} m on the platform");
        else
            Assert.True(f.Slide < 2.2, $"the cube slid {f.Slide:0.00} m on the platform");
    }

    // Every run of the recon, today against now, at one physics step per heartbeat and at 45 Hz: the figures as "KIN TABLE" lines.
    // "today" is the platform not told of the motion (what the module did before), "now" is it told, as core does.
    [Fact]
    public void The_figures_of_every_run_today_against_now_are_reported()
    {
        int runs = 0;
        foreach (string rider in new[] { "box", "avatar" })
            foreach (bool up in new[] { false, true })
                foreach (float speed in new[] { 1f, 3f })
                    foreach (bool keyframed in new[] { true, false })
                        foreach (double rate in new[] { 0.0, 45.0 })
                        {
                            KinematicScenarios.Figures today = Measure(rider, up, speed, keyframed, hint: false, rate);
                            KinematicScenarios.Figures now = Measure(rider, up, speed, keyframed, hint: keyframed, rate);
                            Say($"KIN TABLE|{rider}|{(up ? "up" : "side")}|{speed:0}|{(keyframed ? "keyframed" : "setpos")}|{(rate == 0 ? "1/heartbeat" : "45 Hz")}"
                                + $"|today: {today.Verdict}, slide {F(today.Slide)} lag {F(today.MaxLag)} sink {F(today.MaxSink)} air {F(today.MaxAir)}"
                                + $"|now: {now.Verdict}, slide {F(now.Slide)} lag {F(now.MaxLag)} sink {F(now.MaxSink)} air {F(now.MaxAir)}");
                            if (!keyframed)   // plain sets: the hint is never given, so today and now are the one run
                                Assert.Equal(today, now);
                            runs++;
                        }
        Assert.Equal(64 / 2, runs);
    }

    private static KinematicScenarios.Figures Measure(string rider, bool up, float speed, bool keyframed, bool hint, double rate)
        => KinematicScenarios.Measure(Run(KinematicScenarios.Motion(rider, up, speed, keyframed, hint), rate), up);

    // ------------------------------------------------------------------ plain sets: as before

    // What a platform did before keyframed motion was told apart (the recon's figures for plain sets every 0.2 s, and for
    // keyframed ones the actor is not told of): a sideways move leaves the rider behind to fall the platform's 3 m; a move up
    // leaves an avatar and a cube on it, except a cube on a platform lifted 0.6 m in each step.
    [Theory]
    [InlineData("box", false, 1f, 0.0, false)]
    [InlineData("box", false, 3f, 0.0, false)]
    [InlineData("avatar", false, 1f, 0.0, false)]
    [InlineData("avatar", false, 3f, 0.0, false)]
    [InlineData("box", false, 1f, 45.0, false)]
    [InlineData("avatar", false, 3f, 45.0, false)]
    [InlineData("box", true, 1f, 0.0, true)]
    [InlineData("avatar", true, 3f, 0.0, true)]
    [InlineData("box", true, 3f, 0.0, false)]
    [InlineData("box", true, 3f, 45.0, false)]
    public void Plain_position_sets_still_carry_nothing_sideways(string rider, bool up, float speed, double physicsRate, bool staysOn)
    {
        RunResult res = Run(KinematicScenarios.Motion(rider, up, speed, keyframed: false, hint: false), physicsRate);
        KinematicScenarios.Figures f = KinematicScenarios.Measure(res, up);
        Say($"KIN setpos {rider} {(up ? "up" : "side")} {speed:0} m/s rate {physicsRate:0}: {f.Verdict}, sink {F(f.MaxSink)}");
        Assert.Equal(staysOn, f.OnAtEnd);
        if (!staysOn)
            Assert.True(f.MaxSink > 2.5, $"{rider} {(up ? "up" : "sideways")} at {speed} m/s: sank only {f.MaxSink:0.00} m ({f.Verdict})");
    }

    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("box", 45.0)]
    [InlineData("avatar", 45.0)]
    public void Keyframed_motion_the_platform_was_not_told_of_is_as_plain_sets(string rider, double physicsRate)
    {
        RunResult res = Run(KinematicScenarios.Motion(rider, up: false, speed: 1f, keyframed: true, hint: false), physicsRate);
        KinematicScenarios.Figures f = KinematicScenarios.Measure(res, up: false);
        Say($"KIN keyframed no hint {rider} side 1 m/s rate {physicsRate:0}: {f.Verdict}, sink {F(f.MaxSink)}");
        Assert.False(f.OnAtEnd);
        Assert.True(f.MaxSink > 2.5);
    }

    // ------------------------------------------------------------------ the body

    private sealed class Rig
    {
        public Run R;
        public JoltPrim Platform => (JoltPrim)R.Other;
        public JoltScene Scene => (JoltScene)R.PhysicsScene;
        public readonly List<string> Log = new();
    }

    private static Scenario Custom(Rig rig, string rider, float duration, Action<Run> input, Action<Run> extraSetup = null)
        => new()
        {
            Name = "kinematic-custom",
            DefaultDuration = _ => duration,
            Setup = r =>
            {
                rig.R = r;
                float top = r.GroundAt(KinematicScenarios.CentreX, KinematicScenarios.CentreY) + KinematicScenarios.PlatformHeight;
                var start = new Vector3(KinematicScenarios.CentreX, KinematicScenarios.CentreY, top - KinematicScenarios.PlatformThickness * 0.5f);
                r.AddOtherBox(new Vector3(6f, 6f, 0.5f), start, Quaternion.Identity, false);
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(start.X, start.Y, top + 0.26f), Quaternion.Identity);
                else if (rider == "avatar")
                    r.AddAvatarAt(new Vector3(start.X, start.Y, top + r.AvatarStandHalf + 0.05f), false);
                else   // a box on the ground far from the platform: the run needs an actor to sample
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(60f, 60f, r.GroundAt(60f, 60f) + 0.26f), Quaternion.Identity);
                extraSetup?.Invoke(r);
            },
            Input = input,
        };

    private static bool BodyIsAtRest(JoltPrim p, IPhysicsBackend backend)
        => backend.TryGetBodyState(p.BodyHandle, out BodyState s) && s.LinearVelocity.Length() < 1e-4f && s.AngularVelocity.Length() < 1e-4f;

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_body_is_kinematic_while_the_motion_plays_and_fixed_again_after_it(double physicsRate)
    {
        var rig = new Rig();
        bool wasKinematic = false, layerOk = false, fixedAgain = false, bodyAtEnd = false, fixedLater = false;
        Vector3 end = default;
        var sc = Custom(rig, "none", 6f, r =>
        {
            if (r.Heartbeats == 11)
            {
                r.Other.MovedByKeyframedMotion = true;
                wasKinematic = rig.Platform.IsKinematicBody;
                layerOk = rig.Scene.Backend.RayCast(new System.Numerics.Vector3(KinematicScenarios.CentreX, KinematicScenarios.CentreY, 80f),
                                                    new System.Numerics.Vector3(0, 0, -1), 100f, QueryFilter.Static, out RayHit h) && h.UserData == r.Other.LocalID;
            }
            if (r.Heartbeats >= 11 && r.Heartbeats < 33)
                r.Other.Position += new Vector3(0.1f, 0f, 0f);
            if (r.Heartbeats == 33)
            {
                end = r.Other.Position;
                r.Other.MovedByKeyframedMotion = false;
                fixedAgain = !rig.Platform.IsKinematicBody;
            }
            if (r.Heartbeats == 36)
            {
                bodyAtEnd = rig.Scene.Backend.TryGetBodyState(rig.Platform.BodyHandle, out BodyState s) && MathF.Abs(s.Position.X - end.X) < 0.01f;
                fixedLater = !rig.Platform.IsKinematicBody;
            }
        });
        Run(sc, physicsRate);
        Assert.True(wasKinematic);
        Assert.True(layerOk, "a ray cast for fixed prims finds the platform while it is kinematic");
        Assert.True(fixedAgain);
        Assert.True(bodyAtEnd, "the fixed body is where the object was left");
        Assert.True(fixedLater);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_body_stops_where_it_was_last_set_when_no_pose_follows(double physicsRate)
    {
        var rig = new Rig();
        Vector3 last = default;
        int heartbeatAtLastSet = -1;
        bool movedWhileSetting = false, restedAfter = false, atLast = false;
        var sc = Custom(rig, "none", 5f, r =>
        {
            if (r.Heartbeats == 5)
                r.Other.MovedByKeyframedMotion = true;
            if (r.Heartbeats >= 5 && r.Heartbeats < 15)
            {
                r.Other.Position += new Vector3(0.3f, 0f, 0f);
                last = r.Other.Position;
                heartbeatAtLastSet = r.Heartbeats;
                if (r.Heartbeats == 9)
                    movedWhileSetting = !BodyIsAtRest(rig.Platform, rig.Scene.Backend);
            }
            // No set from heartbeat 15 on, the hint still on: two heartbeats later the body has been stopped at the last pose.
            if (r.Heartbeats == 18)
            {
                restedAfter = BodyIsAtRest(rig.Platform, rig.Scene.Backend);
                atLast = rig.Scene.Backend.TryGetBodyState(rig.Platform.BodyHandle, out BodyState s) && MathF.Abs(s.Position.X - last.X) < 0.01f;
            }
        });
        Run(sc, physicsRate);
        Assert.True(heartbeatAtLastSet > 0);
        Assert.True(movedWhileSetting, "the body has a velocity while poses keep coming");
        Assert.True(restedAfter, "the body keeps no velocity once poses stop");
        Assert.True(atLast, "and it is where the last pose put it");
    }

    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("box", 45.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    public void A_jump_is_not_a_motion_and_flings_nothing(string rider, double physicsRate)
    {
        var rig = new Rig();
        double maxUp = 0, maxAcross = 0;
        var sc = Custom(rig, rider, 4f, r =>
        {
            if (r.Heartbeats == 22)
            {
                r.Other.MovedByKeyframedMotion = true;
                // The jump: 10 m in one set (in one heartbeat), longer than any motion a heartbeat carries.
                r.Other.Position += new Vector3(10f, 0f, 0f);
            }
            if (r.Heartbeats > 22)
            {
                Vector3 v = r.Actor.Velocity;
                maxUp = Math.Max(maxUp, v.Z);
                maxAcross = Math.Max(maxAcross, Math.Sqrt(v.X * v.X + v.Y * v.Y));
            }
        });
        RunResult res = Run(sc, physicsRate);
        Say($"KIN jump {rider} rate {physicsRate:0}: fastest up {F(maxUp)} m/s, fastest across {F(maxAcross)} m/s, ended {F(res.Samples[^1].Position.Z)} m up");
        Assert.True(maxAcross < 1.0, $"the rider was thrown sideways at {maxAcross:0.00} m/s");
        // A box rider falls the platform's 3 m to the land and bounces there: wood on land combines to (0.5 + 0.05) / 2 = 0.275
        // under the shipped averaging, so it leaves the land at 0.275 x sqrt(2 g x 3) = 2.1 m/s (measured 1.9 to 2.2). That is
        // the bounce, not a fling, which would be 100 m/s: it is added to the allowance of an avatar, which does not bounce.
        double bounce = rider == "box" ? 0.275 * Math.Sqrt(2 * 9.80665 * KinematicScenarios.PlatformHeight) : 0.0;
        Assert.True(maxUp < 1.0 + bounce * 1.1, $"the rider was thrown up at {maxUp:0.00} m/s");
        Assert.True(res.Samples[^1].Position.Z < res.Samples[^1].Other.Z - 1.5f, "the platform is gone from under it: it fell to the ground");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_fast_motion_below_the_jump_distance_still_carries_an_avatar(double physicsRate)
    {
        // 1.5 m in each heartbeat is 16.5 m/s, a motion. The avatar takes the platform's velocity in the step the motion starts
        // (AvatarOnKinematicStartTests pins the figure); this test keeps the loose bound it was first written with.
        var rig = new Rig();
        double across = 0;
        var sc = Custom(rig, "avatar", 6f, r =>
        {
            if (r.Heartbeats == 22)
                r.Other.MovedByKeyframedMotion = true;
            if (r.Heartbeats >= 22 && r.Heartbeats < 32)
                r.Other.Position += new Vector3(1.5f, 0f, 0f);
            if (r.Heartbeats == 33)
                across = Math.Abs(r.Actor.Position.X - r.Other.Position.X);
        });
        Run(sc, physicsRate);
        Say($"KIN fast 16.5 m/s avatar rate {physicsRate:0}: {F(across)} m from the middle of the platform after 10 heartbeats");
        Assert.True(across < 2.5, $"the avatar was {across:0.00} m from the middle of the platform");
    }

    // ------------------------------------------------------------------ what is not moved this way

    [Fact]
    public void A_physical_phantom_or_volume_detect_prim_is_not_made_kinematic()
    {
        var rig = new Rig();
        bool physical = true, phantom = true, volume = true, plain = false;
        var sc = Custom(rig, "none", 1f, r =>
        {
            if (r.Heartbeats != 3)
                return;
            JoltPrim ph = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(100f, 100f, 40f), Quaternion.Identity, true, 5101u);
            JoltPrim pn = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(104f, 100f, 40f), Quaternion.Identity, false, 5102u);
            JoltPrim vd = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(108f, 100f, 40f), Quaternion.Identity, false, 5103u);
            JoltPrim pl = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(112f, 100f, 40f), Quaternion.Identity, false, 5104u);
            pn.Phantom = true;
            vd.SetVolumeDetect(1);
            foreach (JoltPrim p in new[] { ph, pn, vd, pl })
                p.MovedByKeyframedMotion = true;
            physical = ph.IsKinematicBody;
            phantom = pn.IsKinematicBody;
            volume = vd.IsKinematicBody;
            plain = pl.IsKinematicBody;
            // A phantom prim made solid again while the motion plays becomes kinematic then, and fixed when it is phantom again.
            pn.Phantom = false;
            Assert.True(pn.IsKinematicBody);
            pn.Phantom = true;
            Assert.False(pn.IsKinematicBody);
        });
        Run(sc, 0.0);
        Assert.False(physical);
        Assert.False(phantom);
        Assert.False(volume);
        Assert.True(plain, "a plain solid prim is the control: it does become kinematic");
    }

    [Fact]
    public void The_setting_is_on_by_default_and_off_leaves_the_platform_a_fixed_body()
    {
        Assert.True(new JoltConfig().KeyframedMotionCarriesRiders);
        var off = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 0.0, Jolt = { ["KeyframedMotionCarriesRiders"] = "false" } };
        RunResult res = Harness.Harness.Run(KinematicScenarios.Motion("box", up: false, 1f, keyframed: true, hint: true), off);
        KinematicScenarios.Figures f = KinematicScenarios.Measure(res, up: false);
        Say($"KIN keyframed hint, setting off, box side 1 m/s: {f.Verdict}, sink {F(f.MaxSink)}");
        Assert.False(f.OnAtEnd);
        Assert.True(f.MaxSink > 2.5);
    }

    [Fact]
    public void The_hint_does_nothing_for_a_prim_that_is_not_told()
    {
        var rig = new Rig();
        var sc = Custom(rig, "none", 1f, r => { });
        Run(sc, 0.0);
        Assert.False(rig.Platform.MovedByKeyframedMotion);
        Assert.False(rig.Platform.IsKinematicBody);
    }

    [Fact]
    public void The_new_layer_collides_as_a_fixed_prim_does_and_has_no_other_pairs()
    {
        MethodInfo should = typeof(JoltPhysicsBackend).GetMethod("ShouldCollide", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(should);
        bool Collide(PhysicsLayer a, PhysicsLayer b) => (bool)should.Invoke(null, new object[] { a, b });
        foreach (PhysicsLayer other in Enum.GetValues<PhysicsLayer>())
        {
            Assert.Equal(Collide(PhysicsLayer.Static, other == PhysicsLayer.MovingStatic ? PhysicsLayer.Static : other), Collide(PhysicsLayer.MovingStatic, other));
            Assert.Equal(Collide(PhysicsLayer.MovingStatic, other), Collide(other, PhysicsLayer.MovingStatic));
        }
        Assert.True(Collide(PhysicsLayer.MovingStatic, PhysicsLayer.Dynamic));
        Assert.True(Collide(PhysicsLayer.MovingStatic, PhysicsLayer.Avatar));
        Assert.False(Collide(PhysicsLayer.MovingStatic, PhysicsLayer.Static));
        Assert.False(Collide(PhysicsLayer.MovingStatic, PhysicsLayer.Terrain));
        Assert.False(Collide(PhysicsLayer.MovingStatic, PhysicsLayer.MovingStatic));
    }

    // ------------------------------------------------------------------ a linkset

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_keyframed_linkset_carries_an_avatar_and_its_body_is_not_made_again_each_step(double physicsRate)
    {
        var rig = new Rig();
        JoltPrim child = null;
        BodyId first = default;
        bool sameBody = true;
        var offset = new Vector3(2.5f, 0f, 0f);
        Vector3 startPlat = default;
        double slide = 0, sink = 0;
        var sc = Custom(rig, "avatar", 9f, r =>
        {
            if (r.Heartbeats == 0)
                startPlat = r.Other.Position;
            if (r.Heartbeats == 1)
            {
                child = (JoltPrim)r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), r.Other.Position + offset, Quaternion.Identity, false, 5201u, linkTo: r.Other);
            }
            if (r.Heartbeats == 44)
            {
                r.Other.MovedByKeyframedMotion = true;
                first = rig.Platform.BodyHandle;
            }
            // 1 m/s along +x for 4 s, a pose set every 50 ms: the root first, then the child, as core sets a group's parts.
            if (r.Heartbeats >= 44 && r.Heartbeats < 44 + 44)
            {
                double t = (r.Heartbeats - 44) * r.Dt;
                for (int k = 0; k < 2; k++)
                {
                    float x = (float)(t + k * 0.5 * r.Dt);
                    r.Other.Position = startPlat + new Vector3(x, 0f, 0f);
                    child.Position = startPlat + new Vector3(x, 0f, 0f) + offset;
                }
                if (!rig.Platform.BodyHandle.Equals(first))
                    sameBody = false;
            }
            if (r.Heartbeats == 44 + 44 + 11)
                r.Other.MovedByKeyframedMotion = false;
        });
        RunResult res = Run(sc, physicsRate);
        KinematicScenarios.Figures f = KinematicScenarios.Measure(res, up: false);
        slide = f.Slide; sink = f.MaxSink;
        Say($"KIN linkset avatar rate {physicsRate:0}: {f.Verdict}, slide {F(slide)} sink {F(sink)}, same body {sameBody}");
        Assert.True(sameBody, "the root's body was made again while the motion played");
        Assert.True(sink < 0.5, $"the avatar sank {sink:0.00} m");
        Assert.True(f.OnAtEnd || slide < 4.5, f.Verdict);
    }

    // ------------------------------------------------------------------ a platform that turns

    [Theory]
    [InlineData("box", 0.0)]
    [InlineData("box", 45.0)]
    [InlineData("avatar", 0.0)]
    [InlineData("avatar", 45.0)]
    public void A_turning_keyframed_platform_is_reported(string rider, double physicsRate)
    {
        RunResult res = Run(KinematicScenarios.Turn(rider), physicsRate);
        KinematicScenarios.TurnFigures t = KinematicScenarios.MeasureTurn(res);
        Say($"KIN turn {rider} rate {physicsRate:0}: platform turned {t.PlatformDegrees:0} degrees, {rider} at {F(t.RiderDegrees)} degrees, {F(t.RadiusMetres)} m from the centre, {F(t.RiseMetres)} m above its resting height");
        Assert.True(double.IsFinite(t.RiderDegrees) && double.IsFinite(t.RadiusMetres));
        Assert.True(t.RiseMetres > -1.0, "the rider fell through the platform");
    }
}
