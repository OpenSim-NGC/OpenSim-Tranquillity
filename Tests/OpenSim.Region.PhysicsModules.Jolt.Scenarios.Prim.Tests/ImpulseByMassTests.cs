/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// llGetMass, llGetObjectMass and llApplyImpulse together, through the harness at the 11 Hz heartbeat, with [Jolt]
/// PhysicsStepRate 45 and with one physics step per heartbeat. In both script engines llGetMass and llGetObjectMass return
/// SceneObjectGroup.GetMass, the sum of every part's actor Mass, and llApplyImpulse goes through
/// SceneObjectPart.ApplyImpulse (a local impulse turned by the calling prim's region rotation) to
/// SceneObjectGroup.applyImpulse, which hands it to the root's actor as a non-push AddForce. The SL wiki: llApplyImpulse
/// applies an impulse to the object (wiki.secondlife.com/wiki/LlApplyImpulse), and an impulse is mass x the change in velocity, so an impulse of the reported mass x 2 m/s
/// gives an object at rest a speed of 2 m/s. The objects float (buoyancy 1), so nothing but the impulse moves them. Serial
/// with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ImpulseByMassTests
{
    public enum Case { Prim, ResizedPrim, PrimReadNonPhysical, Linkset }

    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private const float X = 128f, Y = 128f;
    private const uint RootId = 1801;
    private const float DeltaV = 2f;
    // The child the second script is in: the last part, turned 90 degrees about the vertical on the object.
    private static readonly Quaternion ChildTurn = Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 2f);

    // SceneObjectGroup.GetMass: llGetMass from a script in any part, and llGetObjectMass of any part's key.
    private static float ObjectMass(IEnumerable<PhysicsActor> parts) => parts.Sum(p => p.Mass);

    private readonly record struct Outcome(float Mass, float RootBodyMass, Vector3 VelocityAfter, Vector3 Direction);

    private static Outcome Shoot(Case c, double physicsHz, bool fromChild, bool local)
    {
        var parts = new List<PhysicsActor>();
        float mass = 0f, bodyMass = 0f;
        Vector3 direction = Vector3.UnitX, after = Vector3.Zero;
        int stage = 0;
        var sc = new Scenario
        {
            Name = "impulse-by-mass",
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                float z = r.GroundAt(X, Y) + 20f;
                bool physical = c != Case.PrimReadNonPhysical;
                // The object is turned 30 degrees about the vertical, so a local impulse is not along a region axis.
                Quaternion rot = Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 6f);
                PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X, Y, z), rot, physical, RootId);
                parts.Add(root);
                if (c == Case.Linkset)
                {
                    float[] offsets = { -4f, -2f, 2f, 4f };
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        Quaternion partRot = i == offsets.Length - 1 ? ChildTurn * rot : rot;
                        parts.Add(r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X, Y, z) + new Vector3(offsets[i], 0f, 0f) * rot,
                                            partRot, true, RootId + 1 + (uint)i, root));
                    }
                }
                root.Buoyancy = 1f;
                r.Actor = root;
            },
            Input = r =>
            {
                PhysicsActor root = parts[0];
                switch (stage)
                {
                    case 0 when r.Now >= 0.3:
                        stage = 1;
                        if (c == Case.ResizedPrim)
                            root.Size = new Vector3(2f, 2f, 2f);   // the build tool's or llSetScale's resize
                        if (c == Case.PrimReadNonPhysical)
                        {
                            // The script reads the mass while the prim is not physical, then turns it physical.
                            mass = ObjectMass(parts);
                            root.IsPhysical = true;
                            root.Buoyancy = 1f;
                        }
                        break;
                    case 1 when r.Now >= 0.8:
                        stage = 2;
                        if (c != Case.PrimReadNonPhysical)
                            mass = ObjectMass(parts);
                        bodyMass = ((JoltPrim)root).BodyMass;
                        // The calling prim's region rotation turns a local impulse (SceneObjectPart.ApplyImpulse).
                        // A child's is the root's rotation and its own on the object (SceneObjectPart.GetWorldRotation).
                        Quaternion caller = fromChild ? ChildTurn * root.Orientation : root.Orientation;
                        direction = local ? Vector3.UnitX * caller : Vector3.UnitX;
                        root.AddForce(direction * (mass * DeltaV), false);   // SceneObjectGroup.applyImpulse
                        break;
                    case 2:
                        // One heartbeat later: the body's 5 % a second damping has taken under 0.5 % off.
                        stage = 3;
                        after = root.Velocity;
                        break;
                }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        Assert.Equal(3, stage);
        return new Outcome(mass, bodyMass, after, direction);
    }

    // What llGetMass and llGetObjectMass report: volume x density, in lindograms (1 m cube: 10).
    [Theory]
    [InlineData(Case.Prim, 45.0, 10f)]
    [InlineData(Case.Prim, 0.0, 10f)]
    [InlineData(Case.ResizedPrim, 45.0, 80f)]
    [InlineData(Case.ResizedPrim, 0.0, 80f)]
    [InlineData(Case.PrimReadNonPhysical, 45.0, 10f)]
    [InlineData(Case.PrimReadNonPhysical, 0.0, 10f)]
    [InlineData(Case.Linkset, 45.0, 50f)]
    [InlineData(Case.Linkset, 0.0, 50f)]
    public void The_reported_mass_is_the_object_mass_the_engine_moves(Case c, double physicsHz, float expected)
    {
        Outcome o = Shoot(c, physicsHz, fromChild: false, local: false);
        Assert.Equal(expected, o.Mass, 2);
        Assert.Equal(expected, o.RootBodyMass, 2);
    }

    // An impulse of the reported mass x 2 m/s gives a speed of 2 m/s, within 2 percent, in the impulse's direction.
    [Theory]
    [InlineData(Case.Prim, 45.0, false)]
    [InlineData(Case.Prim, 0.0, false)]
    [InlineData(Case.Prim, 45.0, true)]
    [InlineData(Case.Prim, 0.0, true)]
    [InlineData(Case.ResizedPrim, 45.0, false)]
    [InlineData(Case.ResizedPrim, 0.0, false)]
    [InlineData(Case.PrimReadNonPhysical, 45.0, false)]
    [InlineData(Case.PrimReadNonPhysical, 0.0, false)]
    [InlineData(Case.Linkset, 45.0, false)]
    [InlineData(Case.Linkset, 0.0, false)]
    [InlineData(Case.Linkset, 45.0, true)]
    [InlineData(Case.Linkset, 0.0, true)]
    public void An_impulse_of_mass_times_two_metres_a_second_gives_two_metres_a_second(Case c, double physicsHz, bool local)
    {
        Outcome o = Shoot(c, physicsHz, fromChild: false, local: local);
        AssertSpeed(o);
    }

    // The same from a script in a child prim of the linkset (turned 90 degrees on the object), local and region axes.
    [Theory]
    [InlineData(45.0, false)]
    [InlineData(0.0, false)]
    [InlineData(45.0, true)]
    [InlineData(0.0, true)]
    public void An_impulse_from_a_script_in_a_child_prim_gives_two_metres_a_second(double physicsHz, bool local)
    {
        Outcome o = Shoot(Case.Linkset, physicsHz, fromChild: true, local: local);
        AssertSpeed(o);
    }

    // llGetObjectMass of the linkset (any part's key) equals llGetMass from a script in the root and in a child: core
    // answers all three with SceneObjectGroup.GetMass, the sum of the parts. Each part reports its own share.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void The_object_mass_of_a_linkset_equals_llGetMass(double physicsHz)
    {
        var parts = new List<PhysicsActor>();
        float[] shares = null;
        float objectMass = 0f, bodyMass = 0f;
        var sc = new Scenario
        {
            Name = "linkset-mass",
            DefaultDuration = _ => 1f,
            Setup = r =>
            {
                float z = r.GroundAt(X, Y) + 0.51f;
                PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X, Y, z), Quaternion.Identity, true, RootId);
                parts.Add(root);
                float[] offsets = { -4f, -2f, 2f, 4f };
                for (int i = 0; i < offsets.Length; i++)
                    parts.Add(r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X + offsets[i], Y, z), Quaternion.Identity, true,
                                        RootId + 1 + (uint)i, root));
                r.Actor = root;
            },
            Input = r =>
            {
                if (r.Now < 0.5 || shares != null)
                    return;
                shares = parts.Select(p => p.Mass).ToArray();
                objectMass = ObjectMass(parts);
                bodyMass = ((JoltPrim)parts[0]).BodyMass;
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });

        Assert.NotNull(shares);
        foreach (float share in shares)
            Assert.Equal(10f, share, 2);
        Assert.Equal(50f, objectMass, 2);
        Assert.Equal(objectMass, bodyMass, 2);
    }

    private static void AssertSpeed(Outcome o)
    {
        float speed = o.VelocityAfter.Length();
        Assert.True(MathF.Abs(speed - DeltaV) <= 0.02f * DeltaV, $"speed {speed:0.####} m/s after an impulse of {o.Mass:0.###} x {DeltaV} (velocity {o.VelocityAfter})");
        float along = Vector3.Dot(o.VelocityAfter, o.Direction) / speed;
        Assert.True(along > 0.999f, $"moving along {o.VelocityAfter}, not {o.Direction}");
    }
}
