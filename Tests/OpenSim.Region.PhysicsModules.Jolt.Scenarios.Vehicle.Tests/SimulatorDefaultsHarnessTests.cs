/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The harness's --sim-defaults option (HarnessOptions.SimulatorDefaults): it builds a prim and an avatar with the values
// the simulator hands the engine for a new one, and changes nothing else.
//
// What the simulator hands it: SceneObjectPart.AddToPhysics sets, on every new prim's actor, the material (a new part is
// wood; SOPMaterialData gives wood friction 0.6 and restitution 0.5), then density 1000, gravity multiplier 1, the
// material's friction and restitution again, and buoyancy 0, and on a root rotation locks 0. ScenePresence.
// AddToPhysicalScene hands AddAvatar the appearance's box, which for the default appearance is 0.45 x 0.6 x 2.1 m
// (AvatarAppearance.SetSize adds 0.2 m to the 1.9 m height), then subscribes it to collisions every 100 ms.
//
// The harness without the option gives a prim no material, so its body keeps the backend's friction 0.6 and restitution
// 0, and an avatar a 1.9 m box with no subscription. It sets density 1000 on a physical prim only, so a fixed one reports
// the mass of the backend's own density, 1000 kg/m3 where the simulator's 1000 is 10 kg/m3 (JoltPrim's
// DensityScaleFactor 0.01): 100 times the mass. Gravity multiplier and buoyancy are the same either way. So with the
// option a prim differs in its restitution, and a fixed one in the mass it reports; an avatar in its height and its
// subscription.
//
// A vehicle sets its own contact friction and restitution 0 while it is one (JoltPrim.ApplyVehicleBodyParams), contacts
// multiply two restitutions, and the terrain keeps restitution 0: so a car, and a box on the ground, run the same with
// the option, and a ball shot at a fixed wall comes back off it only with the option.
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class SimulatorDefaultsHarnessTests
{
    private const uint PartId = 2001, ChildId = 1002;

    // Everything about an actor the option must leave alone, read right after the scenario's setup.
    private readonly record struct Shape(Vector3 Position, Quaternion Orientation, Vector3 Size, bool Physical, bool Phantom,
                                         Vector3 Velocity, Vector3 RotationalVelocity, int VehicleType, float Density, float GravModifier,
                                         float Buoyancy, float Friction);

    private static Shape Read(PhysicsActor a)
        => new(a.Position, a.Orientation, a.Size, a.IsPhysical, a.Phantom, a.Velocity, a.RotationalVelocity, a.VehicleType,
               a.Density, a.GravModifier, a.Buoyancy, a.Friction);

    // The prims each way the harness builds one: its own box with a linked child, a fixed box, a further prim, a sphere,
    // a part of the phantom scenarios, and a row prim of the ray cast scenarios' kind. Returns what each looked like
    // after setup, its restitution and its mass.
    private static Dictionary<string, (Shape shape, float restitution, float mass)> BuildPrims(bool simulatorDefaults)
    {
        var seen = new Dictionary<string, (Shape, float, float)>();
        void Note(string name, PhysicsActor a) => seen[name] = (Read(a), a.Restitution, a.Mass);

        var boxes = new Scenario
        {
            Name = "sim-defaults-prims",
            DefaultDuration = _ => 0.1f,
            Setup = r =>
            {
                PhysicsActor box = r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 100f, Course.Ground + 0.52f), Quaternion.Identity);
                PhysicsActor child = r.AddChildBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(100.75f, 100f, Course.Ground + 0.27f), Quaternion.Identity, ChildId);
                PhysicsActor wall = r.AddOtherBox(new Vector3(1f, 4f, 3f), new Vector3(110f, 100f, Course.Ground + 1.5f), Quaternion.Identity, false);
                PhysicsActor part = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), new Vector3(120f, 100f, Course.Ground + 0.52f),
                                              Quaternion.Identity, true, PartId);
                HarnessPart slab = PhantomScenarios.AddPart(r, "slab", new Vector3(3f, 3f, 0.5f), new Vector3(130f, 100f, Course.Ground + 3f),
                                                            false, false, false, false);
                PhysicsActor row = r.AsSimulatorAdds(r.PhysicsScene.AddPrimShape("row", PrimitiveBaseShape.CreateBox(), new Vector3(140f, 100f, Course.Ground + 1f),
                                                                                 new Vector3(0.25f, 0.5f, 0.5f), Quaternion.Identity, false, 3000u));
                Note("box", box);
                Note("child", child);
                Note("wall", wall);
                Note("part", part);
                Note("slab", slab.Actor);
                Note("row", row);
            },
        };
        Harness.Harness.Run(boxes, new HarnessOptions { RateHz = 45.0, PhysicsRateHz = 0, SimulatorDefaults = simulatorDefaults });

        var sphere = new Scenario
        {
            Name = "sim-defaults-sphere",
            DefaultDuration = _ => 0.1f,
            Setup = r => Note("sphere", r.AddSphere(0.2f, new Vector3(100f, 100f, Course.Ground + 1f))),
        };
        Harness.Harness.Run(sphere, new HarnessOptions { RateHz = 45.0, PhysicsRateHz = 0, SimulatorDefaults = simulatorDefaults });
        return seen;
    }

    [Fact]
    public void The_option_is_on_by_default_and_each_half_can_be_turned_off()
    {
        Assert.True(new HarnessOptions().SimulatorDefaults);
        var o = new HarnessOptions { SimulatorAvatars = false };
        Assert.True(o.SimulatorPrims);
        Assert.False(o.SimulatorDefaults);
    }

    // Every prim the harness builds gets the wood restitution 0.5 with the option and keeps the backend's 0 without it;
    // a fixed one reports a hundredth of the mass it reports without; its friction is 0.6 and its density, gravity
    // multiplier and buoyancy 1000, 1 and 0 either way, and nothing else about it changes.
    [Fact]
    public void A_prim_gets_the_simulators_restitution_and_density_and_nothing_else_changes()
    {
        Dictionary<string, (Shape shape, float restitution, float mass)> off = BuildPrims(false);
        Dictionary<string, (Shape shape, float restitution, float mass)> on = BuildPrims(true);

        Assert.Equal(new[] { "box", "child", "part", "row", "slab", "sphere", "wall" }, on.Keys.OrderBy(k => k));
        Assert.Equal(off.Keys.OrderBy(k => k), on.Keys.OrderBy(k => k));
        foreach (string name in on.Keys)
        {
            Assert.True(0f == off[name].restitution, $"{name}: restitution without the option {off[name].restitution}");
            Assert.True(0.5f == on[name].restitution, $"{name}: restitution with the option {on[name].restitution}");
            Assert.Equal(off[name].shape, on[name].shape);
            Shape s = on[name].shape;
            // Physical: 1000 x 0.01 kg/m3 either way (a welded child reports its own part's). Fixed: the backend's
            // 1000 kg/m3 without the option.
            float volume = s.Size.X * s.Size.Y * s.Size.Z * (name == "sphere" ? MathF.PI / 6f : 1f);
            Assert.Equal(volume * 10f, on[name].mass, 2);
            Assert.Equal(s.Physical ? volume * 10f : volume * 1000f, off[name].mass, 2);
            Assert.Equal(0.6f, s.Friction);
            Assert.Equal(1000f, s.Density);
            Assert.Equal(1f, s.GravModifier);
            Assert.Equal(0f, s.Buoyancy);
        }
    }

    // An avatar arrives with the default appearance's box, 2.1 m tall, subscribed to collisions, with the option; 1.9 m
    // and not subscribed without it. It stands where it is put, so its centre is 0.1 m higher, half the extra height;
    // nothing else about it changes, but its mass, which follows the box (1.954922 for the 2.1 m default one).
    [Fact]
    public void An_avatar_gets_the_default_appearances_box_and_a_collision_subscription_and_nothing_else_changes()
    {
        (PhysicsActor actor, Vector3 at, bool subscribed, Vector3 size, float mass, bool flying) Arrive(bool simulatorDefaults)
        {
            (PhysicsActor, Vector3, bool, Vector3, float, bool) seen = default;
            var sc = new Scenario
            {
                Name = "sim-defaults-avatar",
                DefaultDuration = _ => 0.1f,
                Setup = r =>
                {
                    PhysicsActor a = r.AddAvatar(170f, 60f);
                    seen = (a, a.Position, a.SubscribedEvents(), a.Size, a.Mass, a.Flying);
                },
            };
            Harness.Harness.Run(sc, new HarnessOptions { RateHz = 45.0, PhysicsRateHz = 0, SimulatorDefaults = simulatorDefaults });
            return seen;
        }

        var off = Arrive(false);
        var on = Arrive(true);

        Assert.Equal(Harness.Run.AvatarSize, off.size);
        Assert.Equal(new Vector3(0.45f, 0.6f, 2.1f), on.size);
        Assert.False(off.subscribed);
        Assert.True(on.subscribed);
        Assert.Equal(off.at.X, on.at.X);
        Assert.Equal(off.at.Y, on.at.Y);
        Assert.Equal(0.1f, on.at.Z - off.at.Z, 4);
        Assert.Equal(JoltCharacter.MassFor(off.size), off.mass, 4);
        Assert.Equal(1.954922f, on.mass, 4);
        Assert.Equal(off.flying, on.flying);
    }

    // A vehicle's own contact material replaces the prim's while it is one, and the ground's restitution is 0, so the test
    // car driven, the car into a fixed wall and into a box, and a box let fall on the ground run exactly the same.
    [Theory]
    [InlineData("testcar", 0.0)]
    [InlineData("testcar", 45.0)]
    [InlineData("crash-wall", 0.0)]
    [InlineData("crash-box", 45.0)]
    [InlineData("drop", 0.0)]
    public void A_vehicle_and_a_box_on_the_ground_run_the_same(string scenario, double physicsRate)
    {
        RunResult off = Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { RateHz = 45.0, PhysicsRateHz = physicsRate });
        RunResult on = Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { RateHz = 45.0, PhysicsRateHz = physicsRate, SimulatorDefaults = true });
        Assert.True(off.Samples.Count > 10);
        Assert.Equal(off.ToCsv(), on.ToCsv());
        Assert.Equal(off.SummaryLine(), on.SummaryLine());
    }

    // A small ball shot at a fixed 10 cm wall: the two restitutions multiply (0.5 x 0.5 with the option, 0 x 0 without),
    // so with the option it comes back off the wall and ends metres short of it; without, it drops at the wall's foot.
    // Neither goes through.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_ball_shot_at_a_fixed_wall_comes_back_off_it(double physicsRate)
    {
        Scenario sc = Harness.Harness.Find("tunnel-wall-10cm");
        RunResult off = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 45.0, PhysicsRateHz = physicsRate, SimulatorDefaults = false });
        RunResult on = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 45.0, PhysicsRateHz = physicsRate, SimulatorDefaults = true });
        Assert.Equal(0, off.Summary.Tunneled);
        Assert.Equal(0, on.Summary.Tunneled);
        Assert.True(on.Summary.End.X < off.Summary.End.X - 5f, $"{on.Name}: ended at x {on.Summary.End.X}, without the option at {off.Summary.End.X}");
    }
}
