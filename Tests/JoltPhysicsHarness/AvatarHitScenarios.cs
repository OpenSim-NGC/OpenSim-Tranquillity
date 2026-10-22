/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Avatars and physical objects meeting: a box thrown at a standing avatar, an avatar walking into a box, an avatar
// standing on a box, a box dropped on an avatar, and an avatar falling to the ground; a box thrown at a flying avatar, a
// flying avatar flying into a wall and into a light post, an avatar standing up inside a cube, an avatar sitting down on a
// physical seat, and a heavy linkset thrown fast at a standing and at a flying avatar.
//
// Second Life documents (wiki.secondlife.com): "You can push physical object by walking or flying your avatar into them"
// and "Make a physical object drag it into another physical object or your own avatar. The target object or avatar
// should move" (Push); "Residents take damage from collisions with physical objects" (Damage); collision_start is
// "Triggered when task starts colliding with another task", and land_collision_start fires "when a physical object or
// attached avatar starts colliding with land". How far each moves is not documented; the module follows ubODE, where an
// avatar is a body that meets an object with no bounce.
//
// Each box is a prim with a collision script (its CollisionWatch), and so is the avatar (ScenePresence subscribes every
// root avatar); the watches record each collider's hardest strike, the RelativeSpeed core reads for collision sounds and
// impact damage. The box's trace is kept each heartbeat; the run's samples are the avatar's.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class AvatarHitScenarios
{
    /// <summary>When a box is thrown or dropped, or the avatar starts walking (s).</summary>
    public const float At = 1f;
    public const float X = 170f, Y = 60f;
    /// <summary>The walk request ScenePresence sends at speed modifier 1 (m/s).</summary>
    public const float WalkRequest = 4.096f;
    public const float FallHeight = 20f;
    public const float DropHeight = 5f;

    /// <summary>The side of the cube used for a box of this mass: densities stay within what a prim may have.</summary>
    public static float SideFor(float kg) => kg <= 20f ? 0.5f : kg <= 150f ? 0.8f : 1.7f;

    // A physical box of `kg` kilograms (SceneObjectPart.Density x 0.01 is its density in kg/m3), with a collision script,
    // its trace kept.
    private static HarnessPart Box(Run r, Vector3 size, Vector3 position, float kg)
    {
        HarnessPart box = PhantomScenarios.AddPart(r, "box", size, position, true, false, false, true);
        box.Actor.Density = kg / (size.X * size.Y * size.Z) / 0.01f;
        box.Trace = new List<(double, Vector3, Vector3)>();
        return box;
    }

    // The avatar, with a collision subscription as ScenePresence gives it.
    private static void Watch(Run r)
    {
        var w = new CollisionWatch("avatar");
        r.Watches.Add(w);
        w.Attach(r.Actor, r);
    }

    private static void Track(Run r)
    {
        foreach (HarnessPart p in r.Parts)
            if (p.Trace != null && p.Actor != null)
                p.Trace.Add((r.Now, p.Actor.Position, p.Actor.Velocity));
    }

    // A box of `kg` thrown level at `speed` at the avatar's middle, from the west, starting 0.75 m from its side.
    public static Scenario Thrown(string name, float kg, float speed) => new()
    {
        Name = name,
        Description = $"A {SideFor(kg)} m box of {kg} kg is thrown east at {speed} m/s at the middle of an avatar standing on level ground, at {At} s.",
        DefaultDuration = _ => 4f,
        Setup = r =>
        {
            r.AddAvatar(X, Y);
            Watch(r);
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= At - 1e-9)
            {
                float side = SideFor(kg);
                Vector3 at = r.Actor.Position;
                HarnessPart box = Box(r, new Vector3(side, side, side), new Vector3(at.X - 0.75f - side * 0.5f, at.Y, at.Z), kg);
                box.Actor.Velocity = new Vector3(speed, 0f, 0f);
                r.Stage = 1;
            }
            Track(r);
        },
    };

    // A box of `kg` thrown level at `speed` at the middle of an avatar flying (still) 10 m above level ground, from the west.
    private static Scenario ThrownAtFlying(string name, float kg, float speed) => new()
    {
        Name = name,
        Description = $"A {SideFor(kg)} m box of {kg} kg is thrown east at {speed} m/s at the middle of an avatar flying still 10 m above level ground, at {At} s.",
        DefaultDuration = _ => 5f,
        Setup = r =>
        {
            r.AddAvatarAt(new Vector3(X, Y, r.GroundAt(X, Y) + 10f), true);
            Watch(r);
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= At - 1e-9)
            {
                float side = SideFor(kg), gap = speed > 10f ? 2f : 0.75f;
                Vector3 at = r.Actor.Position;
                HarnessPart box = Box(r, new Vector3(side, side, side), new Vector3(at.X - gap - side * 0.5f, at.Y, at.Z), kg);
                box.Actor.Velocity = new Vector3(speed, 0f, 0f);
                r.Stage = 1;
            }
            Track(r);
        },
    };

    // An avatar flying east (16.384 m/s asked) 0.6 m above where it would stand, into a fixed wall or a 0.6 x 0.6 x 2.2 m
    // post of `kg` standing on the ground 8 m ahead.
    private static Scenario FlownInto(string name, float kg) => new()
    {
        Name = name,
        Description = kg > 0f
            ? $"An avatar flies east 0.6 m above level ground into a 0.6 x 0.6 x 2.2 m post of {kg} kg standing 8 m ahead, from {At} s."
            : $"An avatar flies east 0.6 m above level ground into a fixed 1 x 4 x 3 m wall 8 m ahead, from {At} s.",
        DefaultDuration = _ => 4f,
        Setup = r =>
        {
            float g = r.GroundAt(X, Y);
            if (kg > 0f)
                Box(r, new Vector3(0.6f, 0.6f, 2.2f), new Vector3(X, Y, g + 1.11f), kg);
            else
                PhantomScenarios.AddPart(r, "wall", new Vector3(1f, 4f, 3f), new Vector3(X + 0.5f, Y, g + 1.5f), false, false, false, true);
            r.AddAvatarAt(new Vector3(X - 8f, Y, g + r.AvatarStandHalf + 0.6f), true);
            Watch(r);
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= At - 1e-9)
            {
                r.Actor.TargetVelocity = new Vector3(WalkRequest * 4f, 0f, 0f);
                r.Stage = 1;
            }
            Track(r);
        },
    };

    /// <summary>The local id of a thrown linkset's root; its parts take the next two.</summary>
    public const uint LinksetRootId = 1501;

    // A physical linkset of three 0.6 m cubes in a row, 100 kg in all, thrown west at `speed` at the middle of an avatar
    // standing on level ground or flying 10 m above it, from 3 m away. The parts are added as core adds a linkset's parts.
    private static Scenario LinksetThrown(string name, float speed, bool flying) => new()
    {
        Name = name,
        Description = $"A three-part linkset of 0.6 m cubes, 100 kg in all, is thrown west at {speed} m/s at the middle of an avatar {(flying ? "flying still 10 m above" : "standing on")} level ground, at {At} s.",
        DefaultDuration = _ => 4f,
        Setup = r =>
        {
            if (flying)
                r.AddAvatarAt(new Vector3(X, Y, r.GroundAt(X, Y) + 10f), true);
            else
                r.AddAvatar(X, Y);
            Watch(r);
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= At - 1e-9)
            {
                const float Side = 0.6f;
                var size = new Vector3(Side, Side, Side);
                float density = 100f / 3f / (Side * Side * Side) / 0.01f;
                Vector3 at = r.Actor.Position, mid = new Vector3(at.X + 3f + Side * 1.5f, at.Y, at.Z);
                PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid, Quaternion.Identity, true, LinksetRootId, null, density);
                r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid - new Vector3(Side, 0f, 0f), Quaternion.Identity, true, LinksetRootId + 1, root, density);
                r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid + new Vector3(Side, 0f, 0f), Quaternion.Identity, true, LinksetRootId + 2, root, density);
                root.Velocity = new Vector3(-speed, 0f, 0f);
                r.Stage = 1;
            }
        },
    };

    // An avatar walking east into a box of `kg` resting on level ground 3 m ahead.
    private static Scenario Walked(string name, float kg) => new()
    {
        Name = name,
        Description = $"An avatar walks east on level ground ({WalkRequest} m/s asked) from {At} s into a {SideFor(kg)} m box of {kg} kg resting 3 m ahead.",
        DefaultDuration = _ => 6f,
        Setup = r =>
        {
            float side = SideFor(kg);
            r.AddAvatar(X - 5f, Y);
            Watch(r);
            Box(r, new Vector3(side, side, side), new Vector3(X - 2f + side * 0.5f, Y, r.GroundAt(X, Y) + side * 0.5f + 0.01f), kg);
        },
        Input = r =>
        {
            if (r.Stage == 0 && r.Now >= At - 1e-9)
            {
                r.Actor.TargetVelocity = new Vector3(WalkRequest, 0f, 0f);
                r.Stage = 1;
            }
            Track(r);
        },
    };

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Thrown("avatar-hit-1kg", 1f, 5f),
        Thrown("avatar-hit-100kg", 100f, 5f),
        Thrown("avatar-hit-10ms", 1f, 10f),
        Walked("avatar-walk-1kg", 1f),
        Walked("avatar-walk-1000kg", 1000f),
        new()
        {
            Name = "avatar-on-box",
            Description = "An avatar arrives standing on a 2 x 2 x 0.5 m box of 20 kg resting on level ground, and stands 12 s.",
            DefaultDuration = _ => 12f,
            Setup = r =>
            {
                float ground = r.GroundAt(X, Y);
                Box(r, new Vector3(2f, 2f, 0.5f), new Vector3(X, Y, ground + 0.26f), 20f);
                r.AddAvatarAt(new Vector3(X, Y, ground + 0.5f + r.AvatarStandHalf + 0.01f), false);
                Watch(r);
            },
            Input = Track,
        },
        new()
        {
            Name = "avatar-box-drop",
            Description = $"A 0.5 m box of 10 kg is let fall at {At} s from {DropHeight} m above the head of an avatar standing on level ground.",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                r.AddAvatar(X, Y);
                Watch(r);
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    float head = r.Actor.Position.Z + r.ActorSize.Z * 0.5f;
                    Box(r, new Vector3(0.5f, 0.5f, 0.5f), new Vector3(X, Y, head + DropHeight + 0.25f), 10f);
                    r.Stage = 1;
                }
                Track(r);
            },
        },
        new()
        {
            Name = "avatar-fall-20m",
            Description = $"An avatar, not flying, arrives with its feet {FallHeight} m above level ground and falls to it.",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                r.AddAvatarAt(new Vector3(X, Y, r.GroundAt(X, Y) + FallHeight + r.AvatarStandHalf), false);
                Watch(r);
            },
        },
        ThrownAtFlying("avatar-fly-hit-1kg", 1f, 5f),
        ThrownAtFlying("avatar-fly-hit-100kg", 100f, 5f),
        ThrownAtFlying("avatar-fly-hit-20ms", 1f, 20f),
        FlownInto("avatar-fly-wall", 0f),
        FlownInto("avatar-fly-post", 1f),
        new()
        {
            Name = "avatar-stand-up-inside",
            Description = $"An avatar is added at {At} s at the middle of a fixed 2 m cube standing on level ground, as ScenePresence.StandUp places one with no clearance check.",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                HarnessPart cube = PhantomScenarios.AddPart(r, "cube", new Vector3(2f, 2f, 2f), new Vector3(X, Y, r.GroundAt(X, Y) + 1.01f), false, false, false, true);
                r.Actor = cube.Actor;   // until the avatar arrives
                r.ActorSize = cube.Size;
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    r.AddAvatarAt(new Vector3(X, Y, r.GroundAt(X, Y) + 1f), false);
                    Watch(r);
                    r.Stage = 1;
                }
            },
        },
        new()
        {
            Name = "avatar-sit",
            Description = $"An avatar standing on a 2 x 2 x 0.5 m seat of 50 kg leaves physics at {At} s, as core takes a seated avatar out (ScenePresence.RemoveFromPhysicalScene), and a 0.3 m box of 1 kg is let fall from 2.5 m above where it was.",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                float ground = r.GroundAt(X, Y);
                Box(r, new Vector3(2f, 2f, 0.5f), new Vector3(X, Y, ground + 0.26f), 50f);
                r.AddAvatarAt(new Vector3(X, Y, ground + 0.51f + r.AvatarStandHalf + 0.01f), false);
                Watch(r);
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    Vector3 sat = r.Actor.Position;
                    r.PhysicsScene.RemoveAvatar(r.Actor);
                    r.Actor = r.Parts[0].Actor;
                    r.ActorSize = r.Parts[0].Size;
                    Box(r, new Vector3(0.3f, 0.3f, 0.3f), new Vector3(sat.X, sat.Y, sat.Z + 2.5f), 1f);
                    r.Stage = 1;
                }
                Track(r);
            },
        },
        LinksetThrown("avatar-linkset-20ms", 20f, false),
        LinksetThrown("avatar-fly-linkset-20ms", 20f, true),
    };
}
