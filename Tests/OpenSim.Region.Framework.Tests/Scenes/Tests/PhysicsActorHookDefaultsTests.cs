/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The optional members of <see cref="PhysicsActor"/> and <see cref="PhysicsScene"/> that only some engines use do nothing,
/// or answer what every engine did before they existed, on an engine that does not override them. The basic engine's
/// actors stand for such an engine: they override none of these members.
/// </summary>
public class PhysicsActorHookDefaultsTests
{
    private static readonly Vector3 AvatarSize = new(0.45f, 0.6f, 1.9f);
    private static readonly Vector3 Place = new(128f, 128f, 30f);

    private static PhysicsActor Avatar(PhysicsScene engine, bool allowOutside = false)
        => engine.AddAvatar(1, "Test User", Place, AvatarSize, 0f, false, allowOutside);

    private static PhysicsActor Prim(PhysicsScene engine, bool physical = true)
        => engine.AddPrimShape("prim", PrimitiveBaseShape.CreateBox(), Place, Vector3.One, Quaternion.Identity, physical, 2);

    /// <summary>A basic avatar actor that keeps the force and torque it is given, and counts the calls to <c>ReleaseHold()</c>,
    /// so that the defaults that are written in terms of other members can be seen using them.</summary>
    private sealed class Recording : BasicActor
    {
        public Recording() : base(AvatarSize) { }
        public override Vector3 Force { get; set; }
        public override Vector3 Torque { get; set; }
        public int Releases;
        public override void ReleaseHold() { Releases++; }
    }

    [Fact]
    public void Every_flag_is_false_on_an_actor_that_does_not_override_it()
    {
        PhysicsScene engine = new BasicScene();

        foreach (PhysicsActor actor in new[] { Avatar(engine), Prim(engine), Prim(engine, physical: false) })
        {
            Assert.False(actor.AllowOutsideRegion);
            Assert.False(actor.WaitsBeforeCrossingBack);
            Assert.False(actor.AllowsVehicleGrab);
            Assert.False(actor.VehicleTypeStopsTargets);
            Assert.False(actor.CarriesTargetsAcrossCrossing);
            Assert.False(actor.PhysicsCostIsShapeCount);
        }
    }

    [Fact]
    public void Holding_an_actor_and_letting_it_go_changes_nothing()
    {
        PhysicsScene engine = new BasicScene();
        PhysicsActor prim = Prim(engine);
        prim.Velocity = new Vector3(3f, 0f, 0f);

        prim.HoldStill();
        Assert.Equal(Place, prim.Position);
        Assert.Equal(new Vector3(3f, 0f, 0f), prim.Velocity);

        prim.ReleaseHold();
        prim.ReleaseHold(2.5f);
        prim.HoldStill();
        prim.ReleaseHold(0f);
        Assert.Equal(Place, prim.Position);
        Assert.Equal(new Vector3(3f, 0f, 0f), prim.Velocity);
    }

    [Fact]
    public void Releasing_a_hold_after_a_time_does_what_releasing_it_does()
    {
        var actor = new Recording();

        actor.ReleaseHold(4f);
        actor.ReleaseHold(0f);

        Assert.Equal(2, actor.Releases);
    }

    [Fact]
    public void Keeping_an_actor_inside_the_border_does_nothing()
    {
        PhysicsScene engine = new BasicScene();
        PhysicsActor prim = Prim(engine);
        prim.Velocity = new Vector3(0f, 5f, 0f);

        prim.KeepInsideBorder(true);
        prim.KeepInsideBorder(false);

        Assert.Equal(Place, prim.Position);
        Assert.Equal(new Vector3(0f, 5f, 0f), prim.Velocity);
        Assert.False(prim.WaitsBeforeCrossingBack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_script_force_and_torque_set_the_force_and_torque(bool local)
    {
        var actor = new Recording();

        actor.SetScriptForce(new Vector3(1f, 2f, 3f), local);
        actor.SetScriptTorque(new Vector3(4f, 5f, 6f), local);

        Assert.Equal(new Vector3(1f, 2f, 3f), actor.Force);
        Assert.Equal(new Vector3(4f, 5f, 6f), actor.Torque);

        actor.SetScriptForce(Vector3.Zero, local);
        actor.SetScriptTorque(Vector3.Zero, local);

        Assert.Equal(Vector3.Zero, actor.Force);
        Assert.Equal(Vector3.Zero, actor.Torque);
    }

    [Fact]
    public void A_script_force_on_an_engine_that_takes_no_forces_is_ignored_as_the_force_setter_is()
    {
        PhysicsScene engine = new BasicScene();
        PhysicsActor prim = Prim(engine);

        prim.SetScriptForce(new Vector3(1f, 2f, 3f), true);
        prim.SetScriptTorque(new Vector3(4f, 5f, 6f), false);

        Assert.Equal(prim.Force, Vector3.Zero);
        Assert.Equal(prim.Torque, Vector3.Zero);
    }

    [Fact]
    public void Setting_the_hover_repel_flag_is_ignored()
    {
        PhysicsScene engine = new BasicScene();
        PhysicsActor prim = Prim(engine);

        prim.PIDHoverRepel = true;
        prim.PIDHoverActive = true;
        prim.PIDHoverRepel = false;

        Assert.False(prim.PIDHoverActive);
    }

    [Fact]
    public void Raising_a_complexity_error_with_nobody_listening_is_silent()
    {
        PhysicsScene engine = new BasicScene();
        PhysicsActor prim = Prim(engine);

        prim.RaiseComplexityError("(300 of 256 convex hulls)");
    }

    [Fact]
    public void A_complexity_error_reaches_the_listener_with_its_detail()
    {
        PhysicsScene engine = new BasicScene();
        PhysicsActor prim = Prim(engine);
        var heard = new List<string>();
        prim.OnComplexityError += heard.Add;

        prim.RaiseComplexityError("(300 of 256 convex hulls)");

        Assert.Equal(new[] { "(300 of 256 convex hulls)" }, heard);
    }

    [Fact]
    public void The_default_overload_makes_the_actor_and_sets_the_flag()
    {
        PhysicsScene engine = new BasicScene();

        PhysicsActor carried = engine.AddAvatar(1, "Test User", Place, AvatarSize, 0f, false, true);
        PhysicsActor ordinary = engine.AddAvatar(2, "Test User", Place, AvatarSize, 0f, false, false);

        Assert.True(carried.AllowOutsideRegion);
        Assert.False(ordinary.AllowOutsideRegion);
    }
}
