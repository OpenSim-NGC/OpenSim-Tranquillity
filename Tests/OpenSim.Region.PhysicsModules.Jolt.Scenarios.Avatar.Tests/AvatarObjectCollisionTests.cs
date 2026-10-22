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

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Avatars and physical objects meeting (the harness's avatar-hit, avatar-walk, avatar-on-box, avatar-box-drop and
/// avatar-fall scenarios), at an 11 Hz heartbeat with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat.
/// Second Life documents that an avatar walking into a physical object pushes it, that an object hitting an avatar moves
/// it, and that a resident is damaged by colliding with a physical object (wiki.secondlife.com, Push and Damage); it
/// gives no figures. The module follows ubODE: an object meets an avatar as two bodies with no bounce, the avatar held
/// to the push limits ([Jolt] AvatarPushMaxSpeed). Core plays collision sounds and works out impact and fall damage from
/// ContactPoint.RelativeSpeed (SceneObjectPart.PhysicsCollision, ScenePresence's damage: below -5 m/s hurts), which
/// each test checks is the speed of the strike.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarObjectCollisionTests
{
    private const float G = 9.80665f;
    private static readonly float AvatarMass = JoltCharacter.MassFor(Harness.Run.AvatarSize);   // what JoltCharacter reports: mass from shape and size
    private const float PushCap = 10f;   // [Jolt] AvatarPushMaxSpeed, set to 10 in these runs: the cap rule is what they check (the default is 100)
    private const uint Avatar = Harness.Run.ActorLocalId;

    // pinned: the test holds an invariant only the harness's earlier avatar keeps (see HarnessPins).
    private static RunResult Run(string scenario, double physicsRate, bool pinned = false)
        => Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions
        {
            RateHz = 11.0, PhysicsRateHz = physicsRate, Jolt = { ["AvatarPushMaxSpeed"] = "10" },
            SimulatorAvatars = pinned ? HarnessPins.EarlierAvatar : true,
        });

    private static HarnessPart Box(RunResult r) => r.Parts.Single(p => p.Name == "box");
    private static CollisionWatch AvatarWatch(RunResult r) => r.Watches.Single(w => w.Name == "avatar");

    private static float Level(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // The avatar's level speed between heartbeats, from where it was: what it was seen to move at.
    private static float MostLevelSpeed(RunResult r, double from)
    {
        float most = 0f;
        for (int i = 1; i < r.Samples.Count; i++)
            if (r.Samples[i].T > from)
                most = MathF.Max(most, Level(r.Samples[i].Position - r.Samples[i - 1].Position) / (float)(r.Samples[i].T - r.Samples[i - 1].T));
        return most;
    }

    // ------------------------------------------------------------------ an object thrown at an avatar

    [Theory]
    [InlineData(45.0, 1f)]
    [InlineData(0.0, 1f)]
    [InlineData(45.0, 100f)]
    [InlineData(0.0, 100f)]
    public void A_box_thrown_at_an_avatar_stops_against_it_and_moves_it_no_more_than_their_momentum_allows(double physicsRate, float kg)
    {
        RunResult r = Run(kg < 10f ? "avatar-hit-1kg" : "avatar-hit-100kg", physicsRate, pinned: kg >= 100f && physicsRate == 0.0);
        HarnessPart box = Box(r);
        float side = AvatarHitScenarios.SideFor(kg);

        // Never through: the box's middle stays on the side it came from, at every heartbeat.
        foreach ((double t, Vector3 at, Vector3 _) in box.Trace)
        {
            Sample avatar = r.Samples.First(s => s.T >= t - 1e-9);
            Assert.True(at.X < avatar.Position.X, $"{r.Name}: the box is through the avatar at {t:0.00} s (box x {at.X:0.000}, avatar x {avatar.Position.X:0.000})");
        }

        // The two meet with no bounce: the avatar goes no faster than their common speed, kg x 5 / (80 + kg), and never
        // past the push limit. A 1 kg box barely moves it; a 100 kg box carries it along.
        float common = kg * 5f / (AvatarMass + kg);
        float moved = MostLevelSpeed(r, AvatarHitScenarios.At);
        Assert.True(moved <= MathF.Min(common * 1.1f + 0.2f, PushCap), $"{r.Name}: the avatar moved at up to {moved:0.00} m/s; they meet at {common:0.00}");
        if (kg >= 100f)
            Assert.True(moved > common * 0.3f, $"{r.Name}: the avatar was hardly moved ({moved:0.00} m/s, they meet at {common:0.00})");
        Vector3 start = r.Samples.First(s => s.T >= AvatarHitScenarios.At - 1e-9).Position;
        Assert.True(Level(r.Samples[^1].Position - start) <= common * 1.0f + 0.05f,
            $"{r.Name}: the avatar ended {Level(r.Samples[^1].Position - start):0.00} m from where it stood");
        Assert.InRange(r.Samples[^1].Position.Z, start.Z - 0.02f, start.Z + 0.02f);

        // Stopped against it: once the box has fallen, it is not moving on east faster than the avatar went.
        Assert.True(box.Trace[^1].Velocity.X < 0.2f, $"{r.Name}: the box still goes on at {box.Trace[^1].Velocity}");

        // Both are told, each naming the other: the box's script the avatar, the avatar (its attachments) the box.
        Assert.True(box.Watch.StartsOf(Avatar) >= 1, $"{r.Name}: {box.Watch}");
        Assert.True(AvatarWatch(r).StartsOf(box.LocalId) >= 1, $"{r.Name}: {AvatarWatch(r)}");
        Assert.Subset(new HashSet<uint> { 0u, box.LocalId }, AvatarWatch(r).Touched);   // the land and the box, nothing else
        Assert.Subset(new HashSet<uint> { 0u, Avatar }, box.Watch.Touched);
    }

    // ------------------------------------------------------------------ an avatar walking into an object

    [Theory]
    [InlineData(45.0, 1f)]
    [InlineData(0.0, 1f)]
    [InlineData(45.0, 1000f)]
    [InlineData(0.0, 1000f)]
    public void An_avatar_walking_into_a_box_pushes_a_light_one_along_and_hardly_moves_a_heavy_one(double physicsRate, float kg)
    {
        RunResult r = Run(kg < 10f ? "avatar-walk-1kg" : "avatar-walk-1000kg", physicsRate);
        HarnessPart box = Box(r);
        Vector3 boxStart = box.Trace[0].Position;
        float avatarSpeed = r.Samples.Where(s => s.T > AvatarHitScenarios.At).Max(s => s.HorizontalSpeed);
        float boxSpeed = box.Trace.Max(p => Level(p.Velocity));
        float boxMoved = Level(box.Trace[^1].Position - boxStart);

        // Never faster than the avatar, never launched: the avatar stays on the ground and the box on the ground.
        Assert.True(boxSpeed <= avatarSpeed + 0.05f, $"{r.Name}: the box reached {boxSpeed:0.00} m/s, the avatar {avatarSpeed:0.00}");
        float ground = r.Samples[0].Position.Z;
        Assert.True(r.Samples.Max(s => s.Position.Z) < ground + 0.05f, $"{r.Name}: the avatar rose to {r.Samples.Max(s => s.Position.Z) - ground:0.00} m");
        Assert.True(box.Trace.Max(p => p.Position.Z) < boxStart.Z + 0.05f, $"{r.Name}: the box rose");

        if (kg < 10f)
        {
            // A light box goes along ahead of the avatar, most of the way it walks.
            Assert.True(boxMoved > 8f, $"{r.Name}: the box was pushed only {boxMoved:0.00} m");
            Assert.True(r.Samples[^1].Position.X < box.Trace[^1].Position.X, $"{r.Name}: the avatar got past the box");
        }
        else
        {
            // A heavy one, held by its friction, barely moves, and stops the avatar.
            Assert.True(boxMoved < 0.05f, $"{r.Name}: the heavy box moved {boxMoved:0.000} m");
            float face = boxStart.X - AvatarHitScenarios.SideFor(kg) * 0.5f;
            Assert.True(r.Samples[^1].Position.X < face, $"{r.Name}: the avatar is at {r.Samples[^1].Position.X:0.00}, the box's face at {face:0.00}");
        }
        Assert.True(box.Watch.StartsOf(Avatar) >= 1 && AvatarWatch(r).StartsOf(box.LocalId) >= 1, $"{r.Name}: {box.Watch} / {AvatarWatch(r)}");
    }

    // ------------------------------------------------------------------ standing on an object

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_avatar_stands_on_a_resting_box_without_sinking_jittering_or_riding_it_away(double physicsRate)
    {
        RunResult r = Run("avatar-on-box", physicsRate);
        HarnessPart box = Box(r);
        List<Sample> held = r.Samples.Where(s => s.T >= 1.0 && s.T <= 11.0 + 1e-9).ToList();
        float low = held.Min(s => s.Position.Z), high = held.Max(s => s.Position.Z);
        Assert.True(high - low <= 0.02f, $"{r.Name}: the avatar's height went from {low:0.000} to {high:0.000}");
        Assert.True(Level(held[^1].Position - held[0].Position) < 0.02f, $"{r.Name}: the avatar drifted");
        Assert.True(Level(box.Trace[^1].Position - box.Trace[0].Position) < 0.02f, $"{r.Name}: the box moved");
        Assert.True(AvatarWatch(r).StartsOf(box.LocalId) >= 1, $"{r.Name}: {AvatarWatch(r)}");
    }

    // ------------------------------------------------------------------ an object dropped on an avatar

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_dropped_on_an_avatar_comes_to_rest_and_does_not_push_it_into_the_ground(double physicsRate)
    {
        RunResult r = Run("avatar-box-drop", physicsRate);
        HarnessPart box = Box(r);
        Vector3 stood = r.Samples.First(s => s.T >= AvatarHitScenarios.At - 1e-9).Position;
        Assert.True(r.Samples.Min(s => s.Position.Z) >= stood.Z - 0.02f, $"{r.Name}: the avatar went down to {r.Samples.Min(s => s.Position.Z):0.000} from {stood.Z:0.000}");
        Assert.True(r.Samples.Max(s => Level(s.Position - stood)) < 0.1f, $"{r.Name}: the avatar was moved");

        // At rest for the last second, and never inside the avatar: on its head, or beside it.
        foreach ((double t, Vector3 at, Vector3 v) in box.Trace.Where(p => p.T >= 5.0))
            Assert.True(v.Length() < 0.05f, $"{r.Name}: the box still moves at {t:0.00} s ({v})");
        float head = stood.Z + Harness.Run.AvatarSize.Z * 0.5f;
        foreach ((double t, Vector3 at, Vector3 _) in box.Trace)
            if (Level(at - stood) < 0.3f + 0.25f)
                Assert.True(at.Z > head + 0.25f - 0.05f, $"{r.Name}: the box is in the avatar at {t:0.00} s (z {at.Z:0.000}, head {head:0.000})");

        // The strike, as both are told it: the speed of a 5 m fall, sqrt(2 g h).
        float strike = MathF.Sqrt(2f * G * AvatarHitScenarios.DropHeight);
        Assert.InRange(-box.Watch.Strikes[Avatar], strike * 0.95f, strike * 1.05f);
        Assert.InRange(-AvatarWatch(r).Strikes[box.LocalId], strike * 0.95f, strike * 1.05f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_resting_on_an_avatar_falls_when_the_avatar_walks_away(double physicsRate)
    {
        // avatar-box-drop, and at 4 s, the box at rest on its head, the avatar walks off east.
        Scenario drop = Harness.Harness.Find("avatar-box-drop");
        bool walked = false;
        var sc = new Scenario
        {
            Name = "avatar-box-drop-walk",
            DefaultDuration = _ => 7f,
            Setup = drop.Setup,
            Input = r =>
            {
                drop.Input(r);
                if (!walked && r.Now >= 4.0 - 1e-9)
                {
                    r.Actor.TargetVelocity = new Vector3(AvatarHitScenarios.WalkRequest, 0f, 0f);
                    walked = true;
                }
            },
        };
        RunResult r = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });
        HarnessPart box = Box(r);
        float head = r.Samples[0].Position.Z + Harness.Run.AvatarSize.Z * 0.5f;
        Assert.True(box.Trace.First(p => p.T >= 3.9).Position.Z > head, $"{r.Name}: the box was not on the head before the walk");
        float ground = r.Samples[0].Position.Z - r.Samples[0].Height;
        Assert.InRange(box.Trace[^1].Position.Z, ground + 0.2f, ground + 0.3f);   // on the ground, 0.25 m above it
        Assert.True(r.Samples[^1].Position.X - r.Samples[0].Position.X > 2f, $"{r.Name}: the avatar did not walk off");
    }

    // ------------------------------------------------------------------ the speed core needs for sounds and damage

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_avatar_falling_20_m_tells_core_the_speed_it_lands_at(double physicsRate)
    {
        RunResult r = Run("avatar-fall-20m", physicsRate);
        CollisionWatch w = AvatarWatch(r);
        Assert.Equal(1, w.LandStarts);
        float landing = MathF.Sqrt(2f * G * AvatarHitScenarios.FallHeight);   // 19.8 m/s
        Assert.InRange(-w.Strikes[0], landing * 0.95f, landing * 1.05f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_hitting_an_avatar_at_10_m_s_tells_core_that_speed_on_both_sides(double physicsRate)
    {
        RunResult r = Run("avatar-hit-10ms", physicsRate);
        HarnessPart box = Box(r);
        Assert.InRange(-box.Watch.Strikes[Avatar], 9.5f, 10.5f);
        Assert.InRange(-AvatarWatch(r).Strikes[box.LocalId], 9.5f, 10.5f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_landing_on_the_ground_tells_core_the_speed_it_lands_at(double physicsRate)
    {
        // The box of avatar-box-drop that lands on the head stays there; the walk scenario's boxes are set down. A box
        // dropped from 1 m (the rest-ground scenario): it strikes the ground at sqrt(2 g h), less a step of gravity at most.
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("rest-ground"), new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });
        CollisionWatch w = r.Parts.Single(p => p.Name == "box").Watch;
        float landing = MathF.Sqrt(2f * G * 1f);   // 4.43 m/s
        Assert.InRange(-w.Strikes[0], landing * 0.95f, landing * 1.05f);
    }
}
