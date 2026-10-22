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
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Avatars and physical objects in the cases the standing avatar's tests (AvatarObjectCollisionTests,
/// FastObjectsLinksetsAndAvatarsTests) leave out, through the harness:
/// - a flying avatar hit by a box (light and slow, light and fast, heavy), and flying into a fixed prim, a light physical
///   box and a heavy one;
/// - a seated avatar, which core takes out of physics (ScenePresence.RemoveFromPhysicalScene on sitting), and an avatar
///   standing up next to, on top of and inside an object (ScenePresence.StandUp places it with no clearance check);
/// - a fast physical linkset striking a standing, a walking and a flying avatar.
/// The rule is the one for a box: an object meets an avatar with no bounce at their common speed by mass, the avatar's
/// share held to [Jolt] AvatarPushMaxSpeed, and both sides are told the speed they closed at (ContactPoint.RelativeSpeed,
/// which core's collision sounds and impact damage read). Second Life documents "You can push physical object by walking or
/// flying your avatar into them" (wiki.secondlife.com/wiki/Push) and nothing on how far either moves. Where it is silent the
/// module follows ubODE: an avatar's collision names the object it touched by its root part, as ubODE names every collider
/// (ODEScene.Collision_accounting_events, ParentActor.m_baseLocalID), so an attachment's llDetectedKey and llDetectedName
/// are the object's.
/// Each case runs at an 11 Hz heartbeat with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. Speeds are
/// taken over physics time: at 45 Hz a heartbeat runs four or five steps.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class FlyingSeatedAndFastLinksetAvatarTests
{
    private static readonly float AvatarMass = JoltCharacter.MassFor(Harness.Run.AvatarSize);   // what JoltCharacter reports: mass from shape and size
    private const float PushCap = 10f;   // [Jolt] AvatarPushMaxSpeed, set to 10 in these runs: the cap rule is what they check (the default is 100)
    private const float Walk = 4.096f;                 // ScenePresence's walk request at speed modifier 1
    private const float Fly = 16.384f;                 // its fly request (four times the walk)
    private const uint Avatar = Harness.Run.ActorLocalId;
    private const uint BoxId = 1500, RootId = 1501, WestId = 1502, EastId = 1503;
    private const float X = 170f, Y = 60f;
    private const double At = 1.0;

    private readonly ITestOutputHelper _out;

    public FlyingSeatedAndFastLinksetAvatarTests(ITestOutputHelper output) => _out = output;

    private static float Level(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // pinned: the test holds an invariant only the harness's earlier avatar keeps (see HarnessPins).
    private static RunResult Run(Scenario sc, double physicsHz, bool pinned = false)
        => Harness.Harness.Run(sc, new HarnessOptions
        {
            RateHz = 11.0, PhysicsRateHz = physicsHz, Jolt = { ["AvatarPushMaxSpeed"] = "10", ["PrimLinearDamping"] = "0.05", ["PrimAngularDamping"] = "0.05" },
            SimulatorAvatars = pinned ? HarnessPins.EarlierAvatar : true,
        });

    private static CollisionWatch Watch(Run r, PhysicsActor pa, string name)
    {
        var w = new CollisionWatch(name);
        r.Watches.Add(w);
        w.Attach(pa, r);
        return w;
    }

    // A physical box prim of `kg` kilograms (SceneObjectPart.Density x 0.01 is kg/m3), or a fixed one.
    private static PhysicsActor AddBox(Run r, Vector3 size, Vector3 position, float kg, uint id = BoxId)
    {
        bool physical = kg > 0f;
        PhysicsActor pa = r.AddPart(PrimitiveBaseShape.CreateBox(), size, position, Quaternion.Identity, physical, id);
        if (physical)
            pa.Density = kg / (size.X * size.Y * size.Z) / 0.01f;
        return pa;
    }

    // The most an avatar moved per second of physics time, over the trace's consecutive points.
    private static float MostSpeed(List<(double T, Vector3 P)> trace)
    {
        float most = 0f;
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].T > trace[i - 1].T)
                most = MathF.Max(most, Vector3.Distance(trace[i].P, trace[i - 1].P) / (float)(trace[i].T - trace[i - 1].T));
        return most;
    }

    // ------------------------------------------------------------------ 1. a flying avatar hit by a box

    [Theory]
    [InlineData(45.0, 1f, 5f)]
    [InlineData(0.0, 1f, 5f)]
    [InlineData(45.0, 1f, 20f)]
    [InlineData(0.0, 1f, 20f)]
    [InlineData(45.0, 100f, 5f)]
    [InlineData(0.0, 100f, 5f)]
    public void A_flying_avatar_hit_by_a_box_is_pushed_by_the_rule_for_a_box_and_keeps_flying(double physicsHz, float kg, float speed)
    {
        float side = kg <= 20f ? 0.5f : 0.8f;
        float gap = speed > 10f ? 2f : 0.75f;
        PhysicsActor box = null;
        CollisionWatch avatarWatch = null, boxWatch = null;
        var trace = new List<(double T, Vector3 P)>();
        var boxTrace = new List<(double T, Vector3 Box, Vector3 Avatar)>();
        bool stillFlying = true;
        var sc = new Scenario
        {
            Name = "flying-avatar-hit",
            DefaultDuration = _ => 5f,
            Setup = r =>
            {
                r.AddAvatarAt(new Vector3(X, Y, r.GroundAt(X, Y) + 10f), true);
                avatarWatch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    Vector3 at = r.Actor.Position;
                    box = AddBox(r, new Vector3(side, side, side), new Vector3(at.X - gap - side * 0.5f, at.Y, at.Z), kg);
                    boxWatch = Watch(r, box, "box");
                    box.Velocity = new Vector3(speed, 0f, 0f);
                    r.Stage = 1;
                }
                if (box != null)
                {
                    trace.Add((r.PhysicsTime, r.Actor.Position));
                    boxTrace.Add((r.Now, box.Position, r.Actor.Position));
                    stillFlying &= r.Actor.Flying;
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        // Never through while level with it (once past, a thrown box falls away below the flying avatar). A box of the avatar's
        // own mass (1 kg against 1.75) at 20 m/s overlaps it by up to a tenth of a metre while the avatar takes its share of the
        // speed on its next step (measured 0.12 m at one step per heartbeat).
        float through = kg <= 1f && speed >= 20f ? 0.15f : 0f;
        foreach ((double t, Vector3 b, Vector3 a) in boxTrace)
            if (MathF.Abs(b.Z - a.Z) < 1f)
                Assert.True(b.X < a.X + through, $"{res.Name}: the box is through the avatar at {t:0.00} s (box x {b.X:0.000}, avatar x {a.X:0.000})");

        // Pushed no faster than their common speed by mass, never past the push cap, along the throw and not up or down;
        // it stays flying, and the push fades.
        float common = kg * speed / (AvatarMass + kg);
        float most = MostSpeed(trace);
        Assert.True(most <= MathF.Min(common * 1.1f + 0.2f, PushCap), $"{res.Name}: the avatar moved at up to {most:0.00} m/s; they meet at {common:0.00}");
        Vector3 start = trace[0].P, end = trace[^1].P;
        Assert.True(end.X - start.X > -0.01f, $"{res.Name}: the avatar went against the throw ({end.X - start.X:0.000} m)");
        if (kg >= 100f)
            Assert.True(end.X - start.X > common * 0.5f, $"{res.Name}: the avatar was hardly moved ({end.X - start.X:0.00} m)");
        // A box of about the avatar's own mass thrown fast at it, falling as it arrives, pushes it down about 1 m/s as well as
        // along; a flying avatar's push fades over a second (PushFadeFlyingSeconds) and is not taken back up, so it settles
        // about 1 m lower (measured 35.000 to 34.007 at one step a heartbeat), against 0.05 m at 80.
        foreach ((double t, Vector3 p) in trace)
            Assert.True(MathF.Abs(p.Z - start.Z) < (kg <= 1f && speed >= 20f ? 1.1f : 0.05f), $"{res.Name}: the avatar's height went from {start.Z:0.000} to {p.Z:0.000} at {t:0.00} s");
        Assert.True(stillFlying, $"{res.Name}: the avatar stopped flying");
        // A flying avatar's push fades with a time constant of a second (PushFadeFlyingSeconds): over the last half second,
        // nearly four seconds after the hit, it drifts at no more than a tenth of their common speed.
        int last = trace.FindIndex(p => p.T >= trace[^1].T - 0.5);
        float drift = Vector3.Distance(trace[^1].P, trace[last].P) / (float)(trace[^1].T - trace[last].T);
        Assert.True(drift <= common * 0.1f + 0.01f, $"{res.Name}: the avatar still drifts at {drift:0.000} m/s at the end");

        // Both are told, each naming the other, with the speed of the strike.
        Assert.True(avatarWatch.StartsOf(BoxId) >= 1, $"{res.Name}: {avatarWatch}");
        Assert.True(boxWatch.StartsOf(Avatar) >= 1, $"{res.Name}: {boxWatch}");
        Assert.InRange(-avatarWatch.Strikes[BoxId], speed * 0.95f, speed * 1.05f);
        Assert.InRange(-boxWatch.Strikes[Avatar], speed * 0.95f, speed * 1.05f);
    }

    // ------------------------------------------------------------------ 2. a flying avatar flying into an object

    [Theory]
    [InlineData(45.0, 0f)]
    [InlineData(0.0, 0f)]
    [InlineData(45.0, 1f)]
    [InlineData(0.0, 1f)]
    [InlineData(45.0, 10000f)]
    [InlineData(0.0, 10000f)]
    public void A_flying_avatar_flying_into_an_object_stops_at_a_fixed_or_heavy_one_and_pushes_a_light_one_at_its_pace(double physicsHz, float kg)
    {
        // A 0.6 x 0.6 x 2.2 m post standing on the ground 8 m ahead (fixed when kg is 0), the avatar flying east at its
        // middle, 0.6 m above where it would stand. The avatar pushes with up to 2000 N ([Jolt] AvatarObjectPushMaxForce),
        // which tips a tall post over (the avatar then flies on over it) and moves anything under about 340 in mass. So the
        // heavy case is a cube of the greatest density, 10000 asked and the density limit giving 220 kg a cubic metre. It holds
        // against the push only if the ground's friction on it is at least 2000 N: wood (0.6) on land (0.4) averages to 0.5 now,
        // where the geometric mean was 0.6, so it needs more than 2000 / (0.5 x 9.81) = 408 kg: a 1.2 m cube (380 kg) slid on
        // under the 130 N left over, 1.7 m in the run, and a 1.3 m cube (483 kg, friction 2370 N) holds.
        var size = kg >= 100f ? new Vector3(1.3f, 1.3f, 1.3f) : new Vector3(0.6f, 0.6f, 2.2f);
        PhysicsActor post = null;
        CollisionWatch avatarWatch = null, postWatch = null;
        var trace = new List<(double T, Vector3 P)>();
        var postTrace = new List<(double T, Vector3 P)>();
        float postStartX = 0f;
        var sc = new Scenario
        {
            Name = "flying-avatar-into",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                post = AddBox(r, size, new Vector3(X, Y, g + size.Z * 0.5f + 0.01f), kg);
                postWatch = Watch(r, post, "post");
                postStartX = X;
                r.AddAvatarAt(new Vector3(X - 8f, Y, g + r.AvatarStandHalf + 0.6f), true);
                avatarWatch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    r.Actor.TargetVelocity = new Vector3(Fly, 0f, 0f);
                    r.Stage = 1;
                }
                if (r.Stage == 1)
                {
                    trace.Add((r.PhysicsTime, r.Actor.Position));
                    postTrace.Add((r.PhysicsTime, post.Position));
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        float flySpeed = MostSpeed(trace.Take(5).ToList());
        // It holds its height. A light post topples ahead of it, and the avatar flying on slides over or under the post
        // lying in its way: within 0.3 m then.
        float start = trace[0].P.Z, holds = kg == 1f ? 0.3f : 0.05f;
        foreach ((double t, Vector3 p) in trace)
            Assert.True(MathF.Abs(p.Z - start) < holds, $"{res.Name}: the avatar's height went from {start:0.000} to {p.Z:0.000} at {t:0.00} s");
        Assert.True(avatarWatch.StartsOf(BoxId) >= 1 && postWatch.StartsOf(Avatar) >= 1, $"{res.Name}: {avatarWatch} / {postWatch}");
        Assert.InRange(-avatarWatch.Strikes[BoxId], flySpeed * 0.9f, flySpeed * 1.05f);
        Assert.True(MostSpeed(trace) <= flySpeed * 1.02f + 0.05f, $"{res.Name}: the avatar flew at up to {MostSpeed(trace):0.00} m/s, asked {flySpeed:0.00}");

        if (kg == 0f || kg >= 100f)
        {
            // Stopped at its face: a fixed post is not moved by more than a centimetre, a heavy one (the density limit's greatest)
            // is moved a little by the avatar's push, up to 1 m measured (0.18 m at 45 Hz physics, 0.6 m at one step a heartbeat),
            // and the avatar does not go into it.
            float moved = Level(postTrace[^1].P - postTrace[0].P);
            float face = postStartX + moved - size.X * 0.5f;
            Assert.True(trace[^1].P.X < face + 0.01f, $"{res.Name}: the avatar is at {trace[^1].P.X:0.000}, the post's face at {face:0.000}");
            Assert.True(trace[^1].P.X > face - 0.4f, $"{res.Name}: the avatar stopped short, at {trace[^1].P.X:0.000}");
            Assert.True(moved < (kg == 0f ? 0.01f : 1f), $"{res.Name}: the post moved {moved:0.000} m");
        }
        else
        {
            // Pushed along, never faster than the avatar flies and never up into the air.
            float postSpeed = MostSpeed(postTrace);
            Assert.True(postTrace[^1].P.X - postStartX > 5f, $"{res.Name}: the post was pushed only {postTrace[^1].P.X - postStartX:0.00} m");
            Assert.True(postSpeed <= flySpeed * 1.1f + 0.2f, $"{res.Name}: the post reached {postSpeed:0.00} m/s, the avatar flies at {flySpeed:0.00}");
            Assert.True(postTrace.Max(p => p.P.Z) < postTrace[0].P.Z + 0.1f, $"{res.Name}: the post rose to {postTrace.Max(p => p.P.Z):0.00}");
        }
    }

    // ------------------------------------------------------------------ 3. a seated avatar

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_seated_avatar_has_no_body_so_its_seat_keeps_its_mass_and_is_told_the_avatar_left(double physicsHz)
    {
        // Core takes a seated avatar out of physics (ScenePresence.RemoveFromPhysicalScene from the sit handlers); the engine
        // is not told of a sitter. So the seat keeps its own mass, a ball dropped where the avatar sat lands on the seat, and
        // the seat's collision with the avatar ends.
        const float SeatKg = 50f;
        var size = new Vector3(2f, 2f, 0.5f);
        PhysicsActor seat = null, ball = null, avatar = null;
        CollisionWatch seatWatch = null;
        float massBefore = 0f, massAfter = 0f;
        double satAt = double.NaN;
        var sc = new Scenario
        {
            Name = "seated-avatar",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                seat = AddBox(r, size, new Vector3(X, Y, g + size.Z * 0.5f + 0.01f), SeatKg);
                seatWatch = Watch(r, seat, "seat");
                avatar = r.AddAvatarAt(new Vector3(X, Y, g + size.Z + 0.01f + r.AvatarStandHalf + 0.01f), false);
                Watch(r, avatar, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    massBefore = seat.Mass;
                    Vector3 sat = avatar.Position;
                    r.PhysicsScene.RemoveAvatar(avatar);
                    satAt = r.Now;
                    ball = r.AddPart(PrimitiveBaseShape.CreateSphere(), new Vector3(0.3f, 0.3f, 0.3f), new Vector3(sat.X, sat.Y, sat.Z + 2.5f),
                                     Quaternion.Identity, true, RootId);
                    r.Actor = seat;
                    r.Stage = 1;
                }
                if (r.Stage == 1)
                    massAfter = seat.Mass;
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.InRange(massBefore, SeatKg * 0.99f, SeatKg * 1.01f);
        Assert.Equal(massBefore, massAfter);
        float seatTop = seat.Position.Z + size.Z * 0.5f;
        Assert.InRange(ball.Position.Z, seatTop + 0.1f, seatTop + 0.2f);
        Assert.True(seatWatch.TimesOf("start", Avatar).Count >= 1, $"{res.Name}: {seatWatch}");
        Assert.True(seatWatch.TimesOf("end", Avatar).Any(t => t > satAt), $"{res.Name}: the seat was not told the avatar left: {seatWatch}");
        Assert.True(seatWatch.StartsOf(RootId) >= 1, $"{res.Name}: the ball did not land on the seat: {seatWatch}");
    }

    // ------------------------------------------------------------------ 4. standing up next to, on and in objects

    public static IEnumerable<object[]> StandCases()
    {
        foreach (double hz in new[] { 45.0, 0.0 })
            foreach (string where in new[] { "next", "top", "inside", "inside-light", "inside-heavy", "on-physical" })
                yield return new object[] { hz, where };
    }

    [Theory]
    [MemberData(nameof(StandCases))]
    public void An_avatar_standing_up_next_to_on_or_inside_an_object_is_set_clear_of_it_and_not_launched(double physicsHz, string where)
    {
        // Where ScenePresence.StandUp puts an avatar, with no check for what is there: beside a fixed 2 m cube, just above
        // its top, at its middle; at the middle of a light or a heavy physical 1.2 m cube on the ground; just above a
        // physical 2 m x 2 m board. Second Life documents nothing on where a standing avatar goes; ubODE corrects an overlap
        // at up to 60 m/s (ODEScene, WorldSetContactMaxCorrectingVel), which can throw an avatar out of what it stood up
        // in. Here it is set clear and stays where it is set.
        PhysicsActor thing = null, avatar = null;
        Vector3 cubeSize = where.StartsWith("inside-") ? new Vector3(1.2f, 1.2f, 1.2f) : where == "on-physical" ? new Vector3(2f, 2f, 0.5f) : new Vector3(2f, 2f, 2f);
        float kg = where switch { "inside-light" => 1f, "inside-heavy" => 100f, "on-physical" => 50f, _ => 0f };
        Vector3 thingStart = Vector3.Zero, placed = Vector3.Zero;
        float ground = 0f, half = 0f;
        var trace = new List<(double T, Vector3 P)>();
        var thingTrace = new List<Vector3>();
        var steps = new List<CharacterStepTrace>();
        var sc = new Scenario
        {
            Name = "stand-up-" + where,
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                // Each avatar update, through the backend's test trace (on the test's own thread in the harness).
                ((JoltPhysicsBackend)((JoltScene)r.PhysicsScene).Backend).CharacterStepped += t => steps.Add(t);
                ground = r.GroundAt(X, Y);
                half = r.AvatarStandHalf;
                thing = AddBox(r, cubeSize, new Vector3(X, Y, ground + cubeSize.Z * 0.5f + 0.01f), kg);
                r.Actor = thing;   // until the avatar arrives
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    thingStart = thing.Position;
                    float top = ground + cubeSize.Z + 0.01f;
                    placed = where switch
                    {
                        "next" => new Vector3(X - cubeSize.X * 0.5f - 0.3f, Y, ground + half + 0.01f),
                        "top" => new Vector3(X, Y, top + half + 0.1f),
                        "on-physical" => new Vector3(X + 0.65f, Y, top + half + 0.1f),
                        _ => new Vector3(X, Y, ground + MathF.Min(1f, half + 0.01f)),
                    };
                    avatar = r.AddAvatarAt(placed, false);
                    r.Stage = 1;
                }
                if (r.Stage == 1)
                {
                    trace.Add((r.PhysicsTime, avatar.Position));
                    thingTrace.Add(thing.Position);
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        // The avatar's height above where it was put and its vertical speed in each update, and the object's movement in
        // each heartbeat, in the test output.
        float rise = 0f;
        for (int i = 0; i < steps.Count; i++)
        {
            rise += steps[i].Displacement.Z;
            _out.WriteLine($"{res.Name} step {i}: dt {steps[i].Seconds:0.0000} s, placed {steps[i].Placed}, rise {rise:0.0000} m, " +
                           $"moved z {steps[i].Displacement.Z / steps[i].Seconds:0.0000} m/s, asked z {steps[i].Asked.Z:0.0000}, reported z {steps[i].Reported.Z:0.0000}, " +
                           $"moved across {MathF.Sqrt(steps[i].Displacement.X * steps[i].Displacement.X + steps[i].Displacement.Y * steps[i].Displacement.Y):0.0000} m");
        }
        for (int i = 0; i < thingTrace.Count; i++)
            _out.WriteLine($"{res.Name} heartbeat {i}: t {trace[i].T:0.0000} s, avatar rise {trace[i].P.Z - placed.Z:0.0000} m, object moved {thingTrace[i] - thingStart}");

        Vector3 end = trace[^1].P;
        float radius = 0.3f;
        // Never thrown up, and at rest within half a second.
        // The "inside" cases put the avatar 1 m above the ground (the cap on where it is put), which is 0.06 m below where the
        // simulator's 2.1 m avatar stands: it rises that much to stand, and not more than 0.05 m beyond it. A launch is metres.
        Assert.True(trace.Max(p => p.P.Z) < placed.Z + MathF.Max(0f, ground + half - placed.Z) + 0.05f, $"{res.Name}: the avatar rose to {trace.Max(p => p.P.Z) - placed.Z:0.000} m above where it was put");
        int halfSecond = trace.FindIndex(p => p.T >= trace[0].T + 0.5);
        for (int i = halfSecond + 1; i < trace.Count; i++)
            Assert.True(Vector3.Distance(trace[i].P, trace[i - 1].P) < 0.01f, $"{res.Name}: the avatar still moves at {trace[i].T:0.00} s");
        switch (where)
        {
            case "next":
                Assert.True(Vector3.Distance(end, placed) < 0.02f, $"{res.Name}: the avatar moved from {placed} to {end}");
                break;
            case "top":
            case "on-physical":
                // On the top: within a few centimetres of standing height on it, not moved across.
                Assert.InRange(end.Z, ground + cubeSize.Z + half - 0.1f, ground + cubeSize.Z + half + 0.05f);
                Assert.True(Level(end - placed) < 0.05f, $"{res.Name}: the avatar moved {Level(end - placed):0.000} m across");
                break;
            default:
                // Out of the cube, on the ground beside it, having gone no further than clear of it.
                float out_ = MathF.Max(MathF.Abs(end.X - thingTrace[^1].X), MathF.Abs(end.Y - thingTrace[^1].Y));
                Assert.True(out_ >= cubeSize.X * 0.5f + radius - 0.1f, $"{res.Name}: the avatar is still in the cube ({out_:0.000} m from its middle)");
                Assert.True(Level(end - placed) <= cubeSize.X * 0.5f + radius + 0.1f, $"{res.Name}: the avatar went {Level(end - placed):0.000} m");
                Assert.InRange(end.Z, ground + half - 0.05f, ground + half + 0.05f);
                break;
        }
        // What it stood up in or on is not thrown: a fixed one stays, a heavy one barely moves, a light one is nudged aside.
        float thingMoved = Vector3.Distance(thingTrace[^1], thingStart);
        Assert.True(thingMoved < (kg == 1f ? cubeSize.X : 0.02f), $"{res.Name}: the object moved {thingMoved:0.000} m");
        Assert.True(thingTrace.Max(p => p.Z) < thingStart.Z + 0.05f, $"{res.Name}: the object rose");
    }

    // ------------------------------------------------------------------ 5. a fast linkset at an avatar

    // A physical linkset of three cubes in a row along x, its root in the middle, `kg` kilograms in all, added as core adds a
    // linkset's parts (each part, then link() to the root).
    private static PhysicsActor[] AddLinkset(Run r, float side, Vector3 root, float kg)
    {
        var size = new Vector3(side, side, side);
        float density = kg / 3f / (side * side * side) / 0.01f;
        PhysicsActor rootPa = r.AddPart(PrimitiveBaseShape.CreateBox(), size, root, Quaternion.Identity, true, RootId, null, density);
        PhysicsActor west = r.AddPart(PrimitiveBaseShape.CreateBox(), size, root - new Vector3(side, 0f, 0f), Quaternion.Identity, true, WestId, rootPa, density);
        PhysicsActor east = r.AddPart(PrimitiveBaseShape.CreateBox(), size, root + new Vector3(side, 0f, 0f), Quaternion.Identity, true, EastId, rootPa, density);
        return new[] { rootPa, west, east };
    }

    public static IEnumerable<object[]> LinksetCases()
    {
        foreach (double hz in new[] { 45.0, 0.0 })
        {
            yield return new object[] { hz, 1f, 20f, "standing" };
            yield return new object[] { hz, 100f, 20f, "standing" };
            yield return new object[] { hz, 1f, 50f, "standing" };
            yield return new object[] { hz, 1f, 20f, "walking" };
            yield return new object[] { hz, 100f, 20f, "walking" };
            yield return new object[] { hz, 1f, 20f, "flying" };
            yield return new object[] { hz, 100f, 20f, "flying" };
            yield return new object[] { hz, 100f, 50f, "flying" };
        }
    }

    [Theory]
    [MemberData(nameof(LinksetCases))]
    public void A_fast_linkset_does_not_pass_through_an_avatar_moves_it_within_the_push_limits_and_is_named_by_its_root(
        double physicsHz, float kg, float speed, string avatarIs)
    {
        // Thrown west at the avatar's middle from 3 m away; a walking avatar walks east into it.
        float side = kg < 10f ? 0.3f : 0.6f;
        PhysicsActor[] parts = null;
        CollisionWatch avatarWatch = null;
        var partWatches = new Dictionary<uint, CollisionWatch>();
        var trace = new List<(double T, Vector3 P)>();
        var fronts = new List<(double T, Vector3 Front, Vector3 Avatar)>();
        float walkSpeed = 0f;
        var sc = new Scenario
        {
            Name = "fast-linkset-" + avatarIs,
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                if (avatarIs == "flying")
                    r.AddAvatarAt(new Vector3(X, Y, g + 10f), true);
                else
                    r.AddAvatar(X, Y);
                avatarWatch = Watch(r, r.Actor, "avatar");
                if (avatarIs == "walking")
                    r.Actor.TargetVelocity = new Vector3(Walk, 0f, 0f);
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    walkSpeed = r.Actor.Velocity.X;
                    Vector3 at = r.Actor.Position;
                    parts = AddLinkset(r, side, new Vector3(at.X + 3f + side * 1.5f + walkSpeed * 0.15f, at.Y, at.Z), kg);
                    foreach ((PhysicsActor p, uint id) in new[] { (parts[0], RootId), (parts[1], WestId), (parts[2], EastId) })
                        partWatches[id] = Watch(r, p, $"part {id}");
                    parts[0].Velocity = new Vector3(-speed, 0f, 0f);
                    r.Stage = 1;
                }
                if (parts != null)
                {
                    trace.Add((r.PhysicsTime, r.Actor.Position));
                    fronts.Add((r.Now, parts[1].Position, r.Actor.Position));
                }
            },
        };
        RunResult res = Run(sc, physicsHz, pinned: avatarIs == "flying" && kg < 10f && physicsHz == 0.0);

        // Never through: while level with the avatar, the front part stays on its own side of it (once past, a linkset
        // thrown at a flying avatar falls away below it).
        foreach ((double t, Vector3 f, Vector3 a) in fronts)
            if (MathF.Abs(f.Z - a.Z) < 1f)
                Assert.True(f.X > a.X, $"{res.Name}: the linkset is through the avatar at {t:0.00} s (front part x {f.X:0.000}, avatar x {a.X:0.000})");

        // Moved by the rule for a box: a standing or flying avatar no faster than their common speed, never past the push
        // cap; a heavy linkset carries it along the throw. A walking avatar walks on with the push on top.
        float common = kg * speed / (AvatarMass + kg);
        float most = MostSpeed(trace);
        Vector3 start = trace[0].P, end = trace[^1].P;
        if (avatarIs == "walking")
            Assert.True(most <= PushCap + walkSpeed + 0.2f, $"{res.Name}: the avatar moved at up to {most:0.00} m/s");
        else
        {
            Assert.True(most <= MathF.Min(common * 1.1f + 0.2f, PushCap * 1.02f), $"{res.Name}: the avatar moved at up to {most:0.00} m/s; they meet at {common:0.00}");
            Assert.True(end.X - start.X < 0.01f, $"{res.Name}: the avatar went against the throw ({end.X - start.X:0.000} m)");
            if (kg >= 100f)
                Assert.True(start.X - end.X > 0.3f, $"{res.Name}: the avatar was carried only {start.X - end.X:0.000} m");
        }
        // Not launched: on the ground it keeps its height. Flying, it may follow the linkset by up to 0.3 m: at one step per
        // heartbeat a linkset that has struck it and fallen away can pass under its feet, and Jolt's character steps down
        // onto it (its stick-to-floor step) or up off it, as it would onto the ground.
        float holds = avatarIs == "flying" ? 0.3f : 0.05f;
        foreach ((double t, Vector3 p) in trace)
            Assert.True(MathF.Abs(p.Z - start.Z) < holds, $"{res.Name}: the avatar's height went from {start.Z:0.000} to {p.Z:0.000} at {t:0.00} s");

        // The parties: the struck front part names the avatar; the avatar names the linkset by its root, and nothing else
        // but the land; both carry the speed they closed at.
        float closing = speed + MathF.Max(0f, walkSpeed);
        Assert.True(partWatches[WestId].StartsOf(Avatar) >= 1, $"{res.Name}: {partWatches[WestId]}");
        Assert.True(avatarWatch.StartsOf(RootId) >= 1, $"{res.Name}: {avatarWatch}");
        Assert.Subset(new HashSet<uint> { 0u, RootId }, avatarWatch.Touched);
        Assert.InRange(-avatarWatch.Strikes[RootId], closing * 0.9f, closing * 1.1f);
        Assert.InRange(-partWatches[WestId].Strikes[Avatar], closing * 0.9f, closing * 1.1f);
    }
}
