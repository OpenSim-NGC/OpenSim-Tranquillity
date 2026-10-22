/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The soft link of a standing avatar to the object it stands on (<see cref="SoftLink"/>), fed with the contact
/// reports that every physics engine gives the avatar (<see cref="ContactPoint"/> keyed by the touched object's local
/// id, with <see cref="ContactPoint.CharacterFeet"/> set for the feet). The tests use no engine and no wall clock:
/// each report carries the time it is fed at.
/// </summary>
public class SoftLinkTests : OpenSimTestCase
{
    private static readonly UUID GroupA = UUID.Random();
    private static readonly UUID GroupB = UUID.Random();

    // the rider crossing feature is off unless a region switches it on
    private static TestScene NewScene()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        scene.EnableStandingRiderCrossing = true;
        return scene;
    }

    private static SoftLink Fed(params (double t, UUID g)[] reports)
    {
        SoftLink link = new();
        foreach ((double t, UUID g) in reports)
            link.Update(t, g, true);
        return link;
    }

    [Fact]
    public void A_link_starts_after_the_start_time_and_not_before()
    {
        SoftLink link = new();
        link.Update(0, GroupA, true);
        link.Update(100, GroupA, true);
        link.Update(RideOnLimits.StartMs - 1, GroupA, true);
        Assert.False(link.IsLinked);
        link.Update(RideOnLimits.StartMs, GroupA, true);
        Assert.True(link.IsLinked);
        Assert.Equal(GroupA, link.GroupID);
    }

    [Fact]
    public void A_gap_in_the_contact_restarts_the_start_time()
    {
        SoftLink link = Fed((0, GroupA), (200, GroupA), (300, UUID.Zero), (400, GroupA), (600, GroupA));
        Assert.False(link.IsLinked);
        link.Update(400 + RideOnLimits.StartMs, GroupA, true);
        Assert.True(link.IsLinked);
    }

    [Fact]
    public void A_link_survives_a_short_gap_and_breaks_at_the_break_time()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        double t = RideOnLimits.StartMs;
        link.Update(t + 100, UUID.Zero, true);
        link.Update(t + RideOnLimits.BreakMs - 1, UUID.Zero, true);
        Assert.True(link.IsLinked);
        link.Update(t + RideOnLimits.BreakMs, UUID.Zero, true);
        Assert.False(link.IsLinked);
        Assert.Equal(UUID.Zero, link.GroupID);
    }

    [Fact]
    public void Contact_again_within_the_break_time_keeps_the_link()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        double t = RideOnLimits.StartMs;
        link.Update(t + 900, UUID.Zero, true);
        link.Update(t + 950, GroupA, true);
        link.Update(t + 1900, UUID.Zero, true);
        Assert.True(link.IsLinked);
        link.Update(t + 950 + RideOnLimits.BreakMs, UUID.Zero, true);
        Assert.False(link.IsLinked);
    }

    [Fact]
    public void A_stretch_with_no_reports_is_no_information_and_does_not_break_the_link()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        double t = RideOnLimits.StartMs;

        // reports stop for 1500 ms while the avatar stays on the object, then resume without a feet contact (the
        // first frame after an engine's body wakes up): the silence does not count
        link.Update(t + 1500, UUID.Zero, true);

        Assert.True(link.IsLinked);
        Assert.Equal(GroupA, link.GroupID);
    }

    [Fact]
    public void Reports_that_resume_without_the_object_break_the_link_after_the_break_time()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        double t = RideOnLimits.StartMs + 1500;
        link.Update(t, UUID.Zero, true);

        for (double dt = 100; dt < RideOnLimits.BreakMs; dt += 100)
            link.Update(t + dt, UUID.Zero, true);
        Assert.True(link.IsLinked);

        link.Update(t + RideOnLimits.BreakMs, UUID.Zero, true);
        Assert.False(link.IsLinked);
    }

    [Fact]
    public void A_silence_does_not_hide_a_report_without_the_object_before_it()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        double t = RideOnLimits.StartMs;
        link.Update(t + 100, UUID.Zero, true);
        link.Update(t + 200, UUID.Zero, true);
        link.Update(t + 200 + 1500, UUID.Zero, true);
        Assert.True(link.IsLinked);

        // 200 ms of reports without the object came before the silence, 800 ms more after it make the break time
        link.Update(t + 200 + 1500 + RideOnLimits.BreakMs - 200 - 1, UUID.Zero, true);
        Assert.True(link.IsLinked);
        link.Update(t + 200 + 1500 + RideOnLimits.BreakMs - 200, UUID.Zero, true);
        Assert.False(link.IsLinked);
    }

    [Fact]
    public void An_avatar_that_cannot_stand_loses_the_link_at_once()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        Assert.True(link.IsLinked);
        link.Update(RideOnLimits.StartMs + 10, GroupA, false);
        Assert.False(link.IsLinked);
    }

    [Fact]
    public void Another_object_takes_over_only_after_the_start_time()
    {
        SoftLink link = Fed((0, GroupA), (RideOnLimits.StartMs, GroupA));
        double t = RideOnLimits.StartMs;
        link.Update(t + 100, GroupB, true);
        link.Update(t + 100 + RideOnLimits.StartMs - 1, GroupB, true);
        Assert.Equal(GroupA, link.GroupID);
        link.Update(t + 100 + RideOnLimits.StartMs, GroupB, true);
        Assert.Equal(GroupB, link.GroupID);
    }

    // ---- through a scene presence: which of the engine's reports count ----

    private static ContactPoint Feet(float z) => new(new Vector3(0, 0, z), new Vector3(0, 0, -1), 0f, true);
    private static ContactPoint Side(float z) => new(new Vector3(0, 0, z), new Vector3(1, 0, 0), 0f, false);

    private static void Feed(ScenePresence sp, double t, Dictionary<uint, ContactPoint> contacts)
        => sp.UpdateSoftLink(contacts, t);

    private static void StandFor(ScenePresence sp, uint id, ContactPoint contact)
    {
        Feed(sp, 0, new() { { id, contact } });
        Feed(sp, RideOnLimits.StartMs, new() { { id, contact } });
    }

    [Fact]
    public void Feet_on_the_root_part_link_the_avatar_to_the_object()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);

        StandFor(sp, so.RootPart.LocalId, Feet(1f));

        Assert.True(sp.RideOn.IsLinked);
        Assert.Equal(so.UUID, sp.RideOn.GroupID);
    }

    [Fact]
    public void Feet_on_a_child_part_link_the_avatar_to_the_whole_object()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 3, UUID.Random(), "Rider", 0x10);
        SceneObjectPart child = so.Parts[2];
        Assert.NotEqual(so.RootPart.LocalId, child.LocalId);

        StandFor(sp, child.LocalId, Feet(1f));

        Assert.Equal(so.UUID, sp.RideOn.GroupID);
    }

    [Fact]
    public void A_side_contact_does_not_link()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);

        StandFor(sp, so.RootPart.LocalId, Side(1f));

        Assert.False(sp.RideOn.IsLinked);
    }

    [Fact]
    public void Terrain_alone_does_not_link()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));

        StandFor(sp, 0, Feet(21f));

        Assert.False(sp.RideOn.IsLinked);
    }

    [Fact]
    public void The_lowest_feet_contact_decides_so_ground_below_the_object_does_not_link()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
        Dictionary<uint, ContactPoint> both = new() { { 0, Feet(20f) }, { so.RootPart.LocalId, Feet(20.2f) } };

        Feed(sp, 0, both);
        Feed(sp, RideOnLimits.StartMs, both);

        Assert.False(sp.RideOn.IsLinked);
    }

    [Fact]
    public void An_object_that_is_gone_does_not_link()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));

        StandFor(sp, 987654u, Feet(1f));

        Assert.False(sp.RideOn.IsLinked);
    }

    [Fact]
    public void Flying_breaks_the_link_at_once()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
        StandFor(sp, so.RootPart.LocalId, Feet(1f));
        Assert.True(sp.RideOn.IsLinked);

        sp.Flying = true;
        Feed(sp, RideOnLimits.StartMs + 10, new() { { so.RootPart.LocalId, Feet(1f) } });

        Assert.False(sp.RideOn.IsLinked);
    }

    [Fact]
    public void The_physics_collision_update_feeds_the_link_from_the_presences_clock()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
        double now = 1000;
        sp.RideOnClock = () => now;
        Dictionary<uint, ContactPoint> contacts = new() { { so.RootPart.LocalId, Feet(1f) } };

        sp.PhysicsCollisionUpdate(new CollisionEventUpdate(contacts));
        Assert.False(sp.RideOn.IsLinked);
        now += RideOnLimits.StartMs;
        sp.PhysicsCollisionUpdate(new CollisionEventUpdate(contacts));

        Assert.True(sp.RideOn.IsLinked);
        Assert.Equal(so.UUID, sp.RideOn.GroupID);
    }

    [Fact]
    public void A_presence_in_transit_has_no_link()
    {
        TestScene scene = NewScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
        double now = 0;
        sp.RideOnClock = () => now;
        Dictionary<uint, ContactPoint> contacts = new() { { so.RootPart.LocalId, Feet(1f) } };
        sp.PhysicsCollisionUpdate(new CollisionEventUpdate(contacts));
        now += RideOnLimits.StartMs;
        sp.PhysicsCollisionUpdate(new CollisionEventUpdate(contacts));
        Assert.True(sp.RideOn.IsLinked);

        sp.IsInTransit = true;
        sp.PhysicsCollisionUpdate(new CollisionEventUpdate(contacts));

        Assert.False(sp.RideOn.IsLinked);
    }
}
