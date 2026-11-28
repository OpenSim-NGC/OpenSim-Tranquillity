/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Where the fast-object cast, physical linksets, resting contacts and avatars meet, through the harness:
/// - a small fast ball against a standing avatar, against the ground with a bounce, and against a box asleep on a platform;
/// - a three-prim physical linkset thrown at an avatar, and walked into by one;
/// - a three-prim linkset asleep on a platform with one part resized, and one asleep on another object found by a ray.
/// Each runs at an 11 Hz heartbeat with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat; the bounce runs
/// its 45 Hz case with a 45 Hz heartbeat, as the bounce-height tests do, so every step is seen.
/// The rules are those the classes for each part already hold: an object meets an avatar with no bounce at their common
/// speed by mass, the avatar's share held to [Jolt] AvatarPushMaxSpeed (AvatarObjectCollisionTests); a contact's
/// restitution is the two prims' product and a body rebounds to the height that gives (RestingContactAndBounceTests);
/// collision_end is raised when a task stops colliding with another (wiki.secondlife.com/wiki/Collision_end), so resting is not
/// ending; a linkset is one body with its parts' summed mass, and a ray reports the part it hit (LinksetTests).
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class FastObjectsLinksetsAndAvatarsTests
{
    private const float G = 9.80665f;
    private static readonly float AvatarMass = JoltCharacter.MassFor(Harness.Run.AvatarSize);   // what JoltCharacter reports: mass from shape and size
    private const float PushCap = 10f;   // [Jolt] AvatarPushMaxSpeed, set to 10 in these runs: the cap rule is what they check (the default is 100)
    private const uint Avatar = Harness.Run.ActorLocalId;
    private const uint BallId = 1500;
    private const uint RootId = 1501, WestId = 1502, EastId = 1503;
    private const float X = AvatarHitScenarios.X, Y = AvatarHitScenarios.Y;
    private const double At = AvatarHitScenarios.At;
    private const double ActAt = 5.0;                  // a resting object is asleep by then (a wood box on wood bounces to 0.5 and sleeps about 3.7 s in)

    private static float Level(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static RunResult Run(Scenario sc, double physicsHz, double rateHz = 11.0)
        => Harness.Harness.Run(sc, new HarnessOptions { RateHz = rateHz, PhysicsRateHz = physicsHz, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });

    private static CollisionWatch Watch(Run r, PhysicsActor pa, string name)
    {
        var w = new CollisionWatch(name);
        r.Watches.Add(w);
        w.Attach(pa, r);
        return w;
    }

    // A physical 0.2 m sphere prim (density 1000, as a SceneObjectPart adds one) with its own local id.
    private static PhysicsActor AddBall(Run r, Vector3 position)
        => r.AddPart(PrimitiveBaseShape.CreateSphere(), new Vector3(0.2f, 0.2f, 0.2f), position, Quaternion.Identity, true, BallId);

    // A physical linkset of three cubes of side `side` in a row along x, its root in the middle, `kg` kilograms in all,
    // added as core adds a linkset's parts (each part, then link() to the root).
    private sealed class Linkset
    {
        public PhysicsActor Root, West, East;
        public PhysicsActor[] Parts => new[] { Root, West, East };
        public float Side;
    }

    private static Linkset AddLinkset(Run r, float side, Vector3 root, float kg)
    {
        var size = new Vector3(side, side, side);
        float density = kg / 3f / (side * side * side) / 0.01f;   // SceneObjectPart.Density x 0.01 is kg/m3
        var l = new Linkset { Side = side };
        l.Root = r.AddPart(PrimitiveBaseShape.CreateBox(), size, root, Quaternion.Identity, true, RootId, null, density);
        l.West = r.AddPart(PrimitiveBaseShape.CreateBox(), size, root - new Vector3(side, 0f, 0f), Quaternion.Identity, true, WestId, l.Root, density);
        l.East = r.AddPart(PrimitiveBaseShape.CreateBox(), size, root + new Vector3(side, 0f, 0f), Quaternion.Identity, true, EastId, l.Root, density);
        return l;
    }

    private static int BodiesOf(Linkset l) => l.Parts.Count(p => ((JoltPrim)p).BodyHandle.IsValid);

    private static int MotionQuality(Run r, PhysicsActor pa)
        => ((JoltPhysicsBackend)((JoltScene)r.PhysicsScene).Backend).BodyMotionQualityForTest(((JoltPrim)pa).BodyHandle);

    // ------------------------------------------------------------------ 1. a fast ball at a standing avatar

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_fast_small_ball_does_not_pass_through_a_standing_avatar_and_both_are_told_its_speed(double physicsHz)
    {
        const float Speed = 50f;
        PhysicsActor ball = null;
        CollisionWatch ballWatch = null, avatarWatch = null;
        float ballMass = 0f;
        var trace = new List<(double T, Vector3 Ball, Vector3 Avatar)>();
        var sc = new Scenario
        {
            Name = "fast-ball-avatar",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                r.AddAvatar(X, Y);
                avatarWatch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    // Level at the avatar's middle, its front 2 m from the avatar's centre, shot east at it.
                    Vector3 at = r.Actor.Position;
                    ball = AddBall(r, new Vector3(at.X - 2f - 0.1f, at.Y, at.Z));
                    ballMass = ball.Mass;
                    ballWatch = Watch(r, ball, "ball");
                    ball.Velocity = new Vector3(Speed, 0f, 0f);
                    r.Stage = 1;
                }
                if (ball != null)
                    trace.Add((r.Now, ball.Position, r.Actor.Position));
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(trace.Max(p => p.Ball.X) - trace[0].Ball.X > 1.5f, $"{res.Name}: the ball was not shot (moved {trace.Max(p => p.Ball.X) - trace[0].Ball.X:0.00} m)");
        foreach ((double t, Vector3 b, Vector3 a) in trace)
            Assert.True(b.X < a.X, $"{res.Name}: the ball is through the avatar at {t:0.00} s (ball x {b.X:0.000}, avatar x {a.X:0.000})");

        // The avatar moves no faster than their common speed by mass, and never past the push cap.
        float common = ballMass * Speed / (AvatarMass + ballMass);
        Vector3 start = res.Samples.First(s => s.T >= At - 1e-9).Position;
        float most = 0f;
        for (int i = 1; i < res.Samples.Count; i++)
            if (res.Samples[i].T > At)
                most = MathF.Max(most, Level(res.Samples[i].Position - res.Samples[i - 1].Position) / (float)(res.Samples[i].T - res.Samples[i - 1].T));
        Assert.True(most <= MathF.Min(common * 1.1f + 0.2f, PushCap), $"{res.Name}: the avatar moved at up to {most:0.00} m/s; they meet at {common:0.00}");
        Assert.True(Level(res.Samples[^1].Position - start) <= common + 0.05f, $"{res.Name}: the avatar ended {Level(res.Samples[^1].Position - start):0.00} m from where it stood");

        // Both are told, each naming the other, with the speed of the strike.
        Assert.True(ballWatch.StartsOf(Avatar) >= 1, $"{res.Name}: {ballWatch}");
        Assert.True(avatarWatch.StartsOf(BallId) >= 1, $"{res.Name}: {avatarWatch}");
        Assert.InRange(-ballWatch.Strikes[Avatar], Speed * 0.95f, Speed * 1.05f);
        Assert.InRange(-avatarWatch.Strikes[BallId], Speed * 0.95f, Speed * 1.05f);
    }

    // ------------------------------------------------------------------ 2. a fast ball's bounce

    // The height a body rises to when it leaves at speed v under g with damping c (dv/dt = -g - c v), and the speed it
    // reaches falling `height` from rest; as in RestingContactAndBounceTests.
    private static double RiseHeight(double v, double c)
    {
        double h = 1e-4, z = 0;
        while (v > 0) { v -= (G + c * v) * h; z += v * h; }
        return z;
    }

    private static double FallSpeed(double height, double c)
    {
        double h = 1e-4, v = 0, d = 0;
        while (d < height) { v += (G - c * v) * h; d += v * h; }
        return v;
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_fast_ball_dropped_20_m_is_cast_and_rebounds_to_the_height_its_restitution_gives(double physicsHz)
    {
        // A 0.2 m ball of restitution 0.5 let fall 20 m (its base above the top) onto a fixed plate of restitution 0.5, so
        // the pair's restitution is the ball's (averaging two equal values).
        const float Drop = 20f, Restitution = 0.5f, PlateTop = 60f, Radius = 0.1f;
        var trace = new List<(Vector3 P, Vector3 V)>();
        bool cast = false;
        var sc = new Scenario
        {
            Name = "fast-ball-bounce",
            DefaultDuration = _ => MathF.Sqrt(2f * Drop / G) + 2.5f,
            Setup = r =>
            {
                r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, PlateTop - 1f), Quaternion.Identity, false).Restitution = Restitution;
                r.AddSphere(2f * Radius, new Vector3(128f, 128f, PlateTop + Drop + Radius)).Restitution = Restitution;
            },
            Input = r =>
            {
                trace.Add((r.Actor.Position, r.Actor.Velocity));
                // The quality the last update used: UpdateCastBySpeed sets it at the start of each update.
                if (MotionQuality(r, r.Actor) == 1)
                    cast = true;
            },
        };
        Run(sc, physicsHz, physicsHz > 0 ? physicsHz : 11.0);

        Assert.True(cast, "the ball was never cast: it did not get the fast object's check");
        var config = new JoltConfig();
        double step = physicsHz > 0 ? 1.0 / (physicsHz * config.CollisionStepsAt((float)physicsHz)) : 1.0 / (11.0 * config.CollisionSteps);
        int bounce = -1;
        for (int i = 1; i < trace.Count; i++)
            if (trace[i - 1].V.Z < -0.5f && trace[i].V.Z > -0.5f) { bounce = i; break; }
        Assert.True(bounce > 0, "no impact");
        Assert.True(trace[bounce - 1].V.Z < -15f, $"it struck at {-trace[bounce - 1].V.Z:0.0} m/s");
        double apex = double.NegativeInfinity;
        for (int i = bounce; i < trace.Count && trace[i].V.Z >= -0.5f; i++)
        {
            double z = trace[i].P.Z - Radius - PlateTop, v = trace[i].V.Z;
            apex = Math.Max(apex, z);
            if (v > 0 && z > 0.001)
                apex = Math.Max(apex, z + RiseHeight(v, JoltConfig.DefaultPrimLinearDamping) - v * step / 2);
        }
        double expected = RiseHeight(Restitution * FallSpeed(Drop, JoltConfig.DefaultPrimLinearDamping), JoltConfig.DefaultPrimLinearDamping);
        Assert.True(Math.Abs(apex - expected) <= expected * 0.05,
            $"rebound {apex:0.000} m, expected {expected:0.000} m ({(apex - expected) / expected * 100:+0.0;-0.0} %)");
    }

    // ------------------------------------------------------------------ 3. a fast ball on a box asleep on a platform

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_fast_ball_landing_on_a_box_asleep_on_a_platform_wakes_it_and_the_platform_keeps_its_contact(double physicsHz)
    {
        HarnessPart platform = null, box = null;
        PhysicsActor ball = null;
        CollisionWatch ballWatch = null;
        float top = 0f;
        double slept = double.NaN, shotAt = double.NaN;
        bool wokeAfterShot = false;
        var trace = new List<(double T, Vector3 Box, Vector3 Ball)>();
        var sc = new Scenario
        {
            Name = "fast-ball-sleeping-box",
            DefaultDuration = _ => 9f,
            Setup = r =>
            {
                top = r.GroundAt(128f, 128f) + ContactScenarios.PlatformHeight;
                platform = PhantomScenarios.AddPart(r, "platform", new Vector3(3f, 3f, 0.5f), new Vector3(128f, 128f, top - 0.25f), false, false, false, true);
                box = PhantomScenarios.AddPart(r, "box", ContactScenarios.BoxSize, new Vector3(128f, 128f, top + 0.5f + 0.25f), true, false, false, true);
                box.Actor.Friction = 0.6f;     // the prim defaults the scene gives a new prim (wood)
                box.Actor.Restitution = 0.5f;
                r.Actor = box.Actor;
                r.ActorSize = ContactScenarios.BoxSize;
            },
            Input = r =>
            {
                var jp = (JoltPrim)box.Actor;
                if (double.IsNaN(slept) && r.Now > 0.5 && jp.PhysicalAndAsleep)
                    slept = r.Now;
                if (double.IsNaN(shotAt) && r.Now >= ActAt - 1e-9)
                {
                    // Straight down at the box's middle, its bottom 2 m above the box's top.
                    ball = AddBall(r, new Vector3(128f, 128f, top + 0.5f + 2f + 0.1f));
                    ballWatch = Watch(r, ball, "ball");
                    ball.Velocity = new Vector3(0f, 0f, -50f);
                    shotAt = r.Now;
                }
                else if (!double.IsNaN(shotAt) && jp.BodyAwake)
                    wokeAfterShot = true;
                trace.Add((r.Now, box.Actor.Position, ball?.Position ?? Vector3.Zero));
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(slept < ActAt - 0.5, $"{res.Name}: the box was not asleep before the shot (slept at {slept:0.00})");
        Assert.True(wokeAfterShot, $"{res.Name}: the box was not woken");
        Assert.True(box.Watch.StartsOf(BallId) >= 1 && ballWatch.StartsOf(box.LocalId) >= 1, $"{res.Name}: {box.Watch} / {ballWatch}");

        // Not through the box: while above it, across it, the ball's middle stays above the box's.
        foreach ((double t, Vector3 b, Vector3 ballAt) in trace.Where(p => p.T > shotAt))
            if (MathF.Abs(ballAt.X - b.X) < 0.35f && MathF.Abs(ballAt.Y - b.Y) < 0.35f)
                Assert.True(ballAt.Z > b.Z, $"{res.Name}: the ball is through the box at {t:0.00} s (ball z {ballAt.Z:0.000}, box z {b.Z:0.000})");

        // The box left the platform only if its base rose clear of Jolt's 2 cm contact distance or slid off its edge.
        bool left = trace.Where(p => p.T > shotAt).Any(p => p.Box.Z - 0.25f - top > 0.02f || MathF.Abs(p.Box.X - 128f) > 1.5f || MathF.Abs(p.Box.Y - 128f) > 1.5f);
        foreach ((CollisionWatch w, uint other) in new[] { (platform.Watch, box.LocalId), (box.Watch, platform.LocalId) })
        {
            List<double> ends = w.TimesOf("end", other), starts = w.TimesOf("start", other);
            Assert.True(starts.Count >= 1 && starts[0] < shotAt, $"{res.Name}: {w.Name} had no start before the shot");
            if (!left)
                Assert.True(ends.Count == 0, $"{res.Name}: {w.Name} got an end at {string.Join(", ", ends.Select(e => e.ToString("0.00")))} s though the box never left the platform");
            else
                Assert.Equal(starts.Count, ends.Count + 1);   // touching again at the end
        }
        Assert.True(Vector3.Distance(trace[^1].Box, trace[^2].Box) < 0.01f, $"{res.Name}: the box is still moving at the end");
    }

    // ------------------------------------------------------------------ 4. a linkset thrown at an avatar

    [Theory]
    [InlineData(45.0, 1f)]
    [InlineData(0.0, 1f)]
    [InlineData(45.0, 100f)]
    [InlineData(0.0, 100f)]
    public void A_linkset_thrown_at_an_avatar_meets_it_by_the_rule_for_a_box_and_its_struck_part_is_named(double physicsHz, float kg)
    {
        const float Speed = 5f;
        float side = kg < 10f ? 0.3f : 0.6f;
        Linkset l = null;
        CollisionWatch avatarWatch = null;
        var partWatches = new Dictionary<uint, CollisionWatch>();
        var trace = new List<(double T, Vector3 East, Vector3 EastVelocity, Vector3 Avatar)>();
        int bodies = -1;
        var sc = new Scenario
        {
            Name = "linkset-hit-avatar",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                r.AddAvatar(X, Y);
                avatarWatch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    // At the avatar's middle, its front part's face 0.75 m from the avatar's centre, as avatar-hit throws a box.
                    Vector3 at = r.Actor.Position;
                    l = AddLinkset(r, side, new Vector3(at.X - 0.75f - side * 1.5f, at.Y, at.Z), kg);
                    foreach ((PhysicsActor p, uint id) in new[] { (l.Root, RootId), (l.West, WestId), (l.East, EastId) })
                        partWatches[id] = Watch(r, p, $"part {id}");
                    l.Root.Velocity = new Vector3(Speed, 0f, 0f);
                    r.Stage = 1;
                }
                if (l != null)
                {
                    trace.Add((r.Now, l.East.Position, l.Root.Velocity, r.Actor.Position));
                    bodies = BodiesOf(l);
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.Equal(1, bodies);
        foreach ((double t, Vector3 east, Vector3 _, Vector3 a) in trace)
            Assert.True(east.X < a.X, $"{res.Name}: the linkset is through the avatar at {t:0.00} s (front part x {east.X:0.000}, avatar x {a.X:0.000})");

        // The rule for a box (AvatarObjectCollisionTests): no faster than their common speed, never past the push cap; a
        // light one barely moves the avatar, a heavy one carries it along; the object goes on no faster than the avatar.
        float common = kg * Speed / (AvatarMass + kg);
        float most = 0f;
        for (int i = 1; i < res.Samples.Count; i++)
            if (res.Samples[i].T > At)
                most = MathF.Max(most, Level(res.Samples[i].Position - res.Samples[i - 1].Position) / (float)(res.Samples[i].T - res.Samples[i - 1].T));
        // 1.3 for a linkset far heavier than the avatar: sampled at one step per heartbeat the avatar reads 6.12 m/s against a
        // common speed of 4.91 (1.25 of it), as the linkset moves it on the contact; 1.1 held with the avatar at 80.
        Assert.True(most <= MathF.Min(common * 1.3f + 0.2f, PushCap), $"{res.Name}: the avatar moved at up to {most:0.00} m/s; they meet at {common:0.00}");
        if (kg >= 100f)
            Assert.True(most > common * 0.3f, $"{res.Name}: the avatar was hardly moved ({most:0.00} m/s, they meet at {common:0.00})");
        Vector3 start = res.Samples.First(s => s.T >= At - 1e-9).Position;
        Assert.True(Level(res.Samples[^1].Position - start) <= common + 0.05f, $"{res.Name}: the avatar ended {Level(res.Samples[^1].Position - start):0.00} m from where it stood");
        Assert.InRange(res.Samples[^1].Position.Z, start.Z - 0.02f, start.Z + 0.02f);
        Assert.True(trace[^1].EastVelocity.X < 0.2f, $"{res.Name}: the linkset still goes on at {trace[^1].EastVelocity}");

        // The parties: the front part names the avatar, and no other part does; the avatar names the linkset by its root,
        // as ubODE names a collider (FlyingSeatedAndFastLinksetAvatarTests), and nothing else.
        Assert.True(partWatches[EastId].StartsOf(Avatar) >= 1, $"{res.Name}: {partWatches[EastId]}");
        Assert.True(avatarWatch.StartsOf(RootId) >= 1, $"{res.Name}: {avatarWatch}");
        Assert.Subset(new HashSet<uint> { 0u, RootId }, avatarWatch.Touched);
        Assert.Equal(0, partWatches[RootId].StartsOf(Avatar));
        Assert.Equal(0, partWatches[WestId].StartsOf(Avatar));
        Assert.InRange(-avatarWatch.Strikes[RootId], Speed * 0.9f, Speed * 1.1f);
        Assert.InRange(-partWatches[EastId].Strikes[Avatar], Speed * 0.9f, Speed * 1.1f);
    }

    // ------------------------------------------------------------------ 5. an avatar walking into a linkset

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_avatar_walking_into_a_linkset_pushes_it_along_as_one_object(double physicsHz)
    {
        const float Side = 0.5f, Kg = 1f;
        Linkset l = null;
        CollisionWatch avatarWatch = null, westWatch = null;
        var offsets = new List<(double T, float West, float East)>();
        var rootTrace = new List<(Vector3 P, Vector3 V)>();
        int bodies = -1;
        var sc = new Scenario
        {
            Name = "avatar-walk-linkset",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                // The avatar 5 m west, the linkset's west face 3 m ahead of it, resting on level ground.
                l = AddLinkset(r, Side, new Vector3(X - 2f + Side * 1.5f, Y, r.GroundAt(X, Y) + Side * 0.5f + 0.01f), Kg);
                westWatch = Watch(r, l.West, "west part");
                r.AddAvatar(X - 5f, Y);
                avatarWatch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    r.Actor.TargetVelocity = new Vector3(AvatarHitScenarios.WalkRequest, 0f, 0f);
                    r.Stage = 1;
                }
                // Each part's distance from the root, in the root's frame: one object keeps them.
                Quaternion inv = Quaternion.Inverse(l.Root.Orientation);
                offsets.Add((r.Now, ((l.West.Position - l.Root.Position) * inv).X, ((l.East.Position - l.Root.Position) * inv).X));
                rootTrace.Add((l.Root.Position, l.Root.Velocity));
                bodies = BodiesOf(l);
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.Equal(1, bodies);
        foreach ((double t, float west, float east) in offsets.Where(o => o.T > 0.5))
        {
            Assert.True(MathF.Abs(west + Side) < 0.01f, $"{res.Name}: the west part is {west:0.000} m from the root at {t:0.00} s");
            Assert.True(MathF.Abs(east - Side) < 0.01f, $"{res.Name}: the east part is {east:0.000} m from the root at {t:0.00} s");
        }

        // As a 1 kg box (AvatarObjectCollisionTests): pushed ahead at no more than the avatar's pace, most of the way it
        // walks; neither is launched.
        float avatarSpeed = res.Samples.Where(s => s.T > At).Max(s => s.HorizontalSpeed);
        float linksetSpeed = rootTrace.Max(p => Level(p.V));
        float moved = Level(rootTrace[^1].P - rootTrace[0].P);
        Assert.True(linksetSpeed <= avatarSpeed + 0.05f, $"{res.Name}: the linkset reached {linksetSpeed:0.00} m/s, the avatar {avatarSpeed:0.00}");
        float ground = res.Samples[0].Position.Z;
        Assert.True(res.Samples.Max(s => s.Position.Z) < ground + 0.05f, $"{res.Name}: the avatar rose to {res.Samples.Max(s => s.Position.Z) - ground:0.00} m");
        Assert.True(rootTrace.Max(p => p.P.Z) < rootTrace[0].P.Z + 0.05f, $"{res.Name}: the linkset rose");
        Assert.True(moved > 8f, $"{res.Name}: the linkset was pushed only {moved:0.00} m");
        Assert.True(res.Samples[^1].Position.X < l.West.Position.X, $"{res.Name}: the avatar got past the linkset");
        Assert.True(westWatch.StartsOf(Avatar) >= 1 && avatarWatch.StartsOf(RootId) >= 1, $"{res.Name}: {westWatch} / {avatarWatch}");
    }

    // ------------------------------------------------------------------ 6. a part of a sleeping linkset resized

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_part_of_a_linkset_asleep_on_a_platform_resized_leaves_it_one_body_at_rest_and_touching(double physicsHz)
    {
        HarnessPart platform = null;
        Linkset l = null;
        float top = 0f;
        double slept = double.NaN, resizedAt = double.NaN, sleptAgain = double.NaN;
        Vector3 rested = Vector3.Zero;
        float maxMove = 0f, endSpeed = 0f, bodyMass = 0f, partsMass = 0f;
        int bodies = -1;
        var sc = new Scenario
        {
            Name = "linkset-resize-asleep",
            DefaultDuration = _ => 9f,
            Setup = r =>
            {
                top = r.GroundAt(128f, 128f) + ContactScenarios.PlatformHeight;
                platform = PhantomScenarios.AddPart(r, "platform", new Vector3(5f, 4f, 0.5f), new Vector3(128f, 128f, top - 0.25f), false, false, false, true);
                l = AddLinkset(r, 1f, new Vector3(128f, 128f, top + 0.5f + 0.01f), 30f);
                r.Actor = l.Root;
            },
            Input = r =>
            {
                var root = (JoltPrim)l.Root;
                if (double.IsNaN(slept) && r.Now > 0.5 && root.PhysicalAndAsleep)
                    slept = r.Now;
                if (double.IsNaN(resizedAt))
                {
                    if (r.Now >= ActAt - 1e-9)
                    {
                        rested = l.Root.Position;
                        l.East.Size = new Vector3(1f, 1.5f, 1f);   // the build tool's resize of a child
                        resizedAt = r.Now;
                    }
                    return;
                }
                if (double.IsNaN(sleptAgain) && root.PhysicalAndAsleep)
                    sleptAgain = r.Now;
                maxMove = MathF.Max(maxMove, Vector3.Distance(l.Root.Position, rested));
                endSpeed = l.Root.Velocity.Length();
                bodies = BodiesOf(l);
                bodyMass = root.BodyMass;
                partsMass = l.Parts.Sum(p => p.Mass);
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(slept < ActAt - 0.5, $"{res.Name}: the linkset was not asleep before the resize (slept at {slept:0.00})");
        Assert.Equal(1, bodies);
        Assert.Equal(partsMass, bodyMass, 2);
        Assert.True(maxMove <= 0.01f, $"{res.Name}: moved {maxMove * 1000f:0.#} mm after the resize");
        Assert.True(endSpeed < 0.01f, $"{res.Name}: still moving at {endSpeed:0.###} m/s");
        Assert.False(double.IsNaN(sleptAgain), $"{res.Name}: never asleep again after the resize");

        // The platform: one collision_start, from the landing, naming the linkset by its root, and nothing after: no end and
        // no new start across the resize. Every part that rests on it names the same object (CollisionNamingTests), so the
        // platform hears of one object, not of each part.
        CollisionWatch w = platform.Watch;
        Assert.True(w.ObjectStarts == 1 && w.TimesOf("start")[0] < resizedAt, $"{res.Name}: {w}");
        Assert.Equal(0, w.ObjectEnds);
        Assert.Equal(new HashSet<uint> { RootId }, w.Touched);
    }

    // ------------------------------------------------------------------ 7. a ray at a sleeping linkset on another object

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_ray_at_a_part_of_a_linkset_asleep_on_another_object_reports_that_part(double physicsHz)
    {
        const uint SlabId = 1510;
        Linkset l = null;
        PhysicsActor slab = null;
        bool asleep = false, cast = false;
        var filtered = new uint[4];
        var plain = new uint[4];
        var sc = new Scenario
        {
            Name = "linkset-ray-asleep",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                // A physical 5 x 3 x 0.5 m slab on the ground, and the linkset of 1 m cubes resting on it.
                float ground = r.GroundAt(128f, 128f);
                slab = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(5f, 3f, 0.5f), new Vector3(128f, 128f, ground + 0.25f + 0.01f),
                                 Quaternion.Identity, true, SlabId);
                l = AddLinkset(r, 1f, new Vector3(128f, 128f, ground + 0.5f + 0.5f + 0.03f), 30f);
                r.Actor = l.Root;
            },
            Input = r =>
            {
                if (cast || r.Now < ActAt)
                    return;
                cast = true;
                asleep = ((JoltPrim)l.Root).PhysicalAndAsleep && ((JoltPrim)slab).PhysicalAndAsleep;
                float ground = r.GroundAt(128f, 128f);
                // Down onto the root, the west and east parts, and onto the slab beside them.
                var points = new[] { new Vector3(l.Root.Position.X, 128f, 0f), new Vector3(l.West.Position.X, 128f, 0f),
                                     new Vector3(l.East.Position.X, 128f, 0f), new Vector3(128f, 128f + 1.2f, 0f) };
                for (int i = 0; i < points.Length; i++)
                {
                    var from = new Vector3(points[i].X, points[i].Y, ground + 6f);
                    var hits = (List<ContactResult>)r.PhysicsScene.RaycastWorld(from, -Vector3.UnitZ, 10f, 1, RayFilterFlags.AllPrims | RayFilterFlags.land);
                    filtered[i] = hits.Count > 0 ? hits[0].ConsumerID : 0;
                    List<ContactResult> all = r.PhysicsScene.RaycastWorld(from, -Vector3.UnitZ, 10f, 1);
                    plain[i] = all.Count > 0 ? all[0].ConsumerID : 0;
                }
            },
        };
        Run(sc, physicsHz);

        Assert.True(cast);
        Assert.True(asleep, "the linkset and the slab were not both asleep when the rays were cast");
        Assert.Equal(new[] { RootId, WestId, EastId, SlabId }, filtered);
        Assert.Equal(new[] { RootId, WestId, EastId, SlabId }, plain);
    }
}
