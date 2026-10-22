/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Who a collision event names, in every pairing of a prim, a physical linkset and an avatar. Core names whatever local id
/// the engine hands it, by part (SceneObjectPart.CreateColliderArgs: GetSceneObjectPart(localId)), and runs the handler of
/// the part whose actor the update was sent to, with that part's link number, passing it to the root's script by its own
/// rule (SceneObjectPart.SendCollisionEvent). So naming is the engine's.
///
/// Second Life documents no rule for llDetectedKey or llDetectedName when the other object is a linkset. llCollisionFilter's
/// example (wiki.secondlife.com/wiki/LlCollisionFilter) has a filter on "Post" detect a child prim named "Object" of an
/// object named "Post", and not a prim named "Post" of an object named "Object": the filter matches the object's name, and
/// core checks the filter against the part it names. ubODE names the other side by its root
/// (ODEScene.Collision_accounting_events: p1.ParentActor.m_baseLocalID) and sends the update to the part touched. Its
/// ParentActor is the root only for a part core has linked, which it does for a physical linkset alone
/// (SceneObjectGroup.LinkToGroup), so a non-physical linkset's part is named as itself.
///
/// So here: the part touched receives the update, and it names the other object by its root when that is a physical
/// linkset, by itself when it is a single prim or a non-physical linkset's part, and an avatar by the avatar.
/// Each case runs at an 11 Hz heartbeat with one physics step per heartbeat and with [Jolt] PhysicsStepRate 45.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class CollisionNamingTests
{
    private const uint Avatar = Harness.Run.ActorLocalId;
    private const uint OtherAvatar = 1100;
    private const uint BoxId = 1500;
    private const uint RootA = 1501, WestA = 1502, EastA = 1503;
    private const uint RootB = 1601, WestB = 1602, EastB = 1603;
    private const float X = 170f, Y = 60f;
    private const float Side = 0.6f;
    private const double At = 1.0;

    private static RunResult Run(Scenario sc, double physicsHz)
        => Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });

    private static CollisionWatch Watch(Run r, PhysicsActor pa, string name)
    {
        var w = new CollisionWatch(name);
        r.Watches.Add(w);
        w.Attach(pa, r);
        return w;
    }

    private sealed class Linkset
    {
        public PhysicsActor Root, West, East;
        public CollisionWatch RootWatch, WestWatch, EastWatch;
        public uint RootId, WestId, EastId;
        public IEnumerable<CollisionWatch> Watches => new[] { RootWatch, WestWatch, EastWatch };
    }

    // Three 0.6 m cubes in a row along X, the root in the middle, each part with a collision script. A physical linkset is
    // linked as core links one (child.link(root)); a non-physical one is not linked in the engine, as core does not link it.
    private static Linkset AddLinkset(Run r, Vector3 mid, bool physical, uint rootId, float kg = 30f)
    {
        var size = new Vector3(Side, Side, Side);
        float density = kg / 3f / (Side * Side * Side) / 0.01f;
        var l = new Linkset { RootId = rootId, WestId = rootId + 1, EastId = rootId + 2 };
        l.Root = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid, Quaternion.Identity, physical, l.RootId, null, density);
        l.West = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid - new Vector3(Side, 0f, 0f), Quaternion.Identity, physical, l.WestId,
            physical ? l.Root : null, density);
        l.East = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid + new Vector3(Side, 0f, 0f), Quaternion.Identity, physical, l.EastId,
            physical ? l.Root : null, density);
        l.RootWatch = Watch(r, l.Root, $"part {l.RootId}");
        l.WestWatch = Watch(r, l.West, $"part {l.WestId}");
        l.EastWatch = Watch(r, l.East, $"part {l.EastId}");
        return l;
    }

    private static PhysicsActor AddBox(Run r, Vector3 size, Vector3 position, float kg, uint id)
    {
        bool physical = kg > 0f;
        PhysicsActor pa = r.AddPart(PrimitiveBaseShape.CreateBox(), size, position, Quaternion.Identity, physical, id);
        if (physical)
            pa.Density = kg / (size.X * size.Y * size.Z) / 0.01f;
        return pa;
    }

    private static string Describe(IEnumerable<CollisionWatch> ws) => string.Join(" / ", ws.Select(w => w.ToString()));

    // ------------------------------------------------------------------ prim against prim

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_box_dropped_on_a_single_prim_and_the_prim_name_each_other(double physicsHz)
    {
        CollisionWatch boxWatch = null, slabWatch = null;
        var sc = new Scenario
        {
            Name = "naming-prim-prim",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                PhysicsActor slab = AddBox(r, new Vector3(2f, 2f, 0.5f), new Vector3(X, Y, g + 0.26f), 0f, RootA);
                r.Actor = slab;
                slabWatch = Watch(r, slab, "slab");
                PhysicsActor box = AddBox(r, new Vector3(0.3f, 0.3f, 0.3f), new Vector3(X, Y, g + 1.5f), 1f, BoxId);
                boxWatch = Watch(r, box, "box");
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(boxWatch.StartsOf(RootA) >= 1 && slabWatch.StartsOf(BoxId) >= 1, $"{res.Name}: {boxWatch} / {slabWatch}");
        Assert.Equal(new HashSet<uint> { RootA }, boxWatch.Touched);
        Assert.Equal(new HashSet<uint> { BoxId }, slabWatch.Touched);
    }

    // ------------------------------------------------------------------ linkset against prim

    // A 0.3 m box of 1 kg let fall onto the east part of a physical linkset resting on the ground: the east part is told of
    // the box, and the box of the linkset by its root, never by the part it landed on.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_box_dropped_on_a_linkset_part_names_the_linkset_by_its_root_and_the_part_touched_hears_of_the_box(double physicsHz)
    {
        Linkset l = null;
        CollisionWatch boxWatch = null;
        var sc = new Scenario
        {
            Name = "naming-box-on-linkset",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                l = AddLinkset(r, new Vector3(X, Y, g + Side * 0.5f + 0.01f), true, RootA);
                r.Actor = l.Root;
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    Vector3 east = l.Root.Position + new Vector3(Side, 0f, 0f);
                    PhysicsActor box = AddBox(r, new Vector3(0.3f, 0.3f, 0.3f), east + new Vector3(0f, 0f, 1.2f), 1f, BoxId);
                    boxWatch = Watch(r, box, "box");
                    r.Stage = 1;
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(boxWatch.StartsOf(RootA) >= 1, $"{res.Name}: {boxWatch}");
        Assert.Subset(new HashSet<uint> { 0u, RootA }, boxWatch.Touched);   // the land, and the linkset by its root
        Assert.True(l.EastWatch.StartsOf(BoxId) >= 1, $"{res.Name}: {Describe(l.Watches)}");
        Assert.Equal(0, l.RootWatch.StartsOf(BoxId));
        Assert.Equal(0, l.WestWatch.StartsOf(BoxId));
    }

    // A physical linkset let fall onto a fixed platform: the platform is told of the linkset by its root, and the parts that
    // touch it are told of the platform.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_linkset_landing_on_a_prim_is_named_by_its_root(double physicsHz)
    {
        Linkset l = null;
        CollisionWatch platformWatch = null;
        const uint PlatformId = 1700;
        var sc = new Scenario
        {
            Name = "naming-linkset-on-prim",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                PhysicsActor platform = AddBox(r, new Vector3(4f, 4f, 0.5f), new Vector3(X, Y, g + 1.25f), 0f, PlatformId);
                platformWatch = Watch(r, platform, "platform");
                l = AddLinkset(r, new Vector3(X, Y, g + 1.5f + Side * 0.5f + 0.5f), true, RootA);
                r.Actor = l.Root;
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(platformWatch.StartsOf(RootA) >= 1, $"{res.Name}: {platformWatch}");
        Assert.Equal(new HashSet<uint> { RootA }, platformWatch.Touched);
        Assert.True(l.Watches.Count(w => w.StartsOf(PlatformId) >= 1) >= 1, $"{res.Name}: {Describe(l.Watches)}");
        foreach (CollisionWatch w in l.Watches)
            Assert.Subset(new HashSet<uint> { 0u, PlatformId }, w.Touched);
    }

    // ------------------------------------------------------------------ linkset against linkset

    // Linkset B let fall square onto linkset A resting on the ground: each part of A is told of B by B's root, and each part
    // of B of A by A's root.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void Two_linksets_name_each_other_by_their_roots(double physicsHz)
    {
        Linkset a = null, b = null;
        var sc = new Scenario
        {
            Name = "naming-linkset-on-linkset",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                a = AddLinkset(r, new Vector3(X, Y, g + Side * 0.5f + 0.01f), true, RootA);
                b = AddLinkset(r, new Vector3(X, Y, g + Side * 1.5f + 0.5f), true, RootB);
                r.Actor = b.Root;
            },
        };
        RunResult res = Run(sc, physicsHz);

        string all = Describe(a.Watches.Concat(b.Watches));
        Assert.True(a.Watches.Count(w => w.StartsOf(RootB) >= 1) >= 1, $"{res.Name}: {all}");
        Assert.True(b.Watches.Count(w => w.StartsOf(RootA) >= 1) >= 1, $"{res.Name}: {all}");
        foreach (CollisionWatch w in a.Watches)
            Assert.Subset(new HashSet<uint> { 0u, RootB }, w.Touched);   // the land, and the other by its root
        foreach (CollisionWatch w in b.Watches)
            Assert.Subset(new HashSet<uint> { 0u, RootA }, w.Touched);
    }

    // ------------------------------------------------------------------ a non-physical linkset

    // A box let fall onto the east part of a non-physical linkset: core does not link a non-physical linkset in the engine
    // (it calls link() only when the root is physical), so its parts are separate prims to it, as to ubODE, and each is
    // named as itself.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_box_dropped_on_a_non_physical_linkset_part_names_that_part(double physicsHz)
    {
        Linkset l = null;
        CollisionWatch boxWatch = null;
        var sc = new Scenario
        {
            Name = "naming-box-on-fixed-linkset",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                l = AddLinkset(r, new Vector3(X, Y, g + Side * 0.5f + 0.01f), false, RootA);
                PhysicsActor box = AddBox(r, new Vector3(0.3f, 0.3f, 0.3f), l.East.Position + new Vector3(0f, 0f, 1.2f), 1f, BoxId);
                boxWatch = Watch(r, box, "box");
                r.Actor = box;
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(boxWatch.StartsOf(EastA) >= 1, $"{res.Name}: {boxWatch}");
        Assert.Equal(new HashSet<uint> { EastA }, boxWatch.Touched);
        Assert.True(l.EastWatch.StartsOf(BoxId) >= 1, $"{res.Name}: {Describe(l.Watches)}");
    }

    // ------------------------------------------------------------------ an avatar against a linkset

    // An avatar walking into the west part of a heavy physical linkset resting on the ground: the part touched is told of the
    // avatar, and the avatar of the linkset by its root, as a box dropped on it is.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_walking_into_a_linkset_names_it_by_its_root_and_the_part_touched_hears_of_the_avatar(double physicsHz)
    {
        Linkset l = null;
        CollisionWatch avatarWatch = null;
        var sc = new Scenario
        {
            Name = "naming-avatar-into-linkset",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                l = AddLinkset(r, new Vector3(X + 2f, Y, g + Side * 0.5f + 0.01f), true, RootA, 300f);
                r.AddAvatar(X - 1f, Y);
                avatarWatch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    r.Actor.TargetVelocity = new Vector3(4.096f, 0f, 0f);
                    r.Stage = 1;
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        Assert.True(avatarWatch.StartsOf(RootA) >= 1, $"{res.Name}: {avatarWatch}");
        Assert.Subset(new HashSet<uint> { 0u, RootA }, avatarWatch.Touched);   // the land, and the linkset by its root
        Assert.True(l.WestWatch.StartsOf(Avatar) >= 1, $"{res.Name}: {Describe(l.Watches)}");
        Assert.Equal(0, l.RootWatch.StartsOf(Avatar));
        Assert.Equal(0, l.EastWatch.StartsOf(Avatar));
    }

    // ------------------------------------------------------------------ an avatar against an avatar

    // Two avatars walking at each other: each is told of the other avatar.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void Two_avatars_name_each_other(double physicsHz)
    {
        CollisionWatch watch = null, otherWatch = null;
        PhysicsActor other = null;
        var sc = new Scenario
        {
            Name = "naming-avatar-avatar",
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                float g = r.GroundAt(X, Y);
                other = r.PhysicsScene.AddAvatar(OtherAvatar, "Other User", new Vector3(X + 1.5f, Y, g + r.AvatarStandHalf + 0.01f), r.AvatarBox, 0f, false);
                otherWatch = Watch(r, other, "other avatar");
                r.AddAvatar(X - 1.5f, Y);
                watch = Watch(r, r.Actor, "avatar");
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    r.Actor.TargetVelocity = new Vector3(2f, 0f, 0f);
                    other.TargetVelocity = new Vector3(-2f, 0f, 0f);
                    r.Stage = 1;
                }
            },
        };
        RunResult res = Run(sc, physicsHz);

        string where = $"avatar at {res.Samples[^1].Position.X:0.000}, other at {other.Position.X:0.000}";
        Assert.True(watch.StartsOf(OtherAvatar) >= 1 && otherWatch.StartsOf(Avatar) >= 1, $"{res.Name}: {watch} / {otherWatch}; {where}");
        Assert.Subset(new HashSet<uint> { 0u, OtherAvatar }, watch.Touched);
        Assert.Subset(new HashSet<uint> { 0u, Avatar }, otherWatch.Touched);
    }
}
