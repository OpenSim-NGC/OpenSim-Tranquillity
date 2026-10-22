/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A five-prim physical linkset that physics has carried away from where it was linked, made non-physical by a script
/// (llSetStatus(STATUS_PHYSICS, FALSE)) and by the build tool's Physical checkbox, through the harness at the 11 Hz
/// heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. Both script engines call
/// SceneObjectGroup.ScriptSetPhysicsStatus, and the build tool's ObjectFlagUpdate reaches SceneGraph.UpdatePrimFlags; both
/// end in SceneObjectGroup.UpdateFlags, which turns the root non-physical first and then each child
/// (SceneObjectPart.UpdatePrimFlags, DoPhysicsPropertyUpdate: Stop(), IsPhysical = false, delink()). The build tool does it
/// while the object is selected (SceneObjectGroup.IsSelected hands Selected to the root's actor, then every part's). Core
/// moves a physical linkset's parts only through the root's body, so a child's actor is never handed its new place while
/// physics moves the object. Afterwards every part is solid where it now is: an avatar walking into it is stopped, and a
/// ray onto it hits it. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class LinksetPhysicsOffTests
{
    public enum Path { Script, BuildTool }

    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private const float StartX = 100f, StartY = 128f;
    private const uint RootId = 1501, FirstAvatarId = 1601;
    // The parts' places along the root's x axis: five 1 m cubes with 1 m between them, the root in the middle.
    private static readonly float[] Offsets = { 0f, -4f, -2f, 2f, 4f };
    private const float Carry = 20f, Speed = 4f;                  // carried 20 m along x at 4 m/s
    private const float TurnRate = MathF.PI / 10f;                // turned 90 degrees in 5 s
    private const float RequestWalk = 4.096f;                     // the viewer's walk request (AvatarSpeedTests)

    [Theory]
    [InlineData(Path.Script, 45.0)]
    [InlineData(Path.Script, 0.0)]
    [InlineData(Path.BuildTool, 45.0)]
    [InlineData(Path.BuildTool, 0.0)]
    public void A_moved_and_turned_linkset_made_non_physical_is_solid_where_each_part_now_is(Path path, double physicsHz)
    {
        var parts = new List<PhysicsActor>();
        var avatars = new List<PhysicsActor>();
        var centres = new Vector3[5];
        var avatarStartX = new float[5];
        var furthestX = new float[5];
        var xs = new List<(double T, float X)>[5];
        var hitIds = new uint[5];
        var hitTops = new float[5];
        float ground = 0f, carried = 0f, turned = 0f;
        bool linearDone = false, angularDone = false;
        double stoppedAt = double.NaN, selectAt = double.NaN, offAt = double.NaN, deselectAt = double.NaN,
               rayAt = double.NaN, walkAt = double.NaN;
        bool selected = false, off = false, deselected = false, rayed = false, walking = false;

        var sc = new Scenario
        {
            Name = "linkset-physics-off",
            DefaultDuration = _ => 11f,
            Setup = r =>
            {
                ground = r.GroundAt(StartX, StartY);
                float z = ground + 1f;   // the parts span 0.5 m to 1.5 m above the ground: an avatar walks into them
                PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(StartX, StartY, z), Quaternion.Identity, true, RootId);
                parts.Add(root);
                for (int i = 1; i < Offsets.Length; i++)
                    parts.Add(r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(StartX + Offsets[i], StartY, z), Quaternion.Identity,
                                        true, RootId + (uint)i, root));
                r.Actor = root;
                root.Buoyancy = 1f;   // it floats, so only the velocities a script sets move it
            },
            Input = r =>
            {
                PhysicsActor root = parts[0];
                double now = r.Now;
                if (now < 0.5)
                    return;

                // Carried 20 m and turned 90 degrees by physics, as a script's llSetVelocity and llSetAngularVelocity would.
                if (!linearDone)
                {
                    if (root.Position.X - StartX >= Carry) { root.Velocity = Vector3.Zero; linearDone = true; }
                    else root.Velocity = new Vector3(Speed, 0f, 0f);
                }
                if (!angularDone)
                {
                    if (Yaw(root.Orientation) >= MathF.PI / 2f) { root.RotationalVelocity = Vector3.Zero; angularDone = true; }
                    else root.RotationalVelocity = new Vector3(0f, 0f, TurnRate);
                }
                if (!linearDone || !angularDone)
                    return;
                if (double.IsNaN(stoppedAt))
                {
                    stoppedAt = now;
                    selectAt = now + 0.5;
                    offAt = now + 0.8;
                    deselectAt = now + 1.1;
                    rayAt = now + 1.4;
                    walkAt = now + 1.5;
                }

                if (path == Path.BuildTool && !selected && now >= selectAt)
                {
                    selected = true;
                    Select(true, parts);
                }
                if (!off && now >= offAt)
                {
                    off = true;
                    carried = root.Position.X - StartX;
                    turned = Yaw(root.Orientation);
                    // SceneObjectGroup.UpdateFlags: the root, then each child.
                    foreach (PhysicsActor p in parts)
                    {
                        p.Velocity = Vector3.Zero;   // SceneObjectPart.Stop
                        p.IsPhysical = false;
                        p.delink();
                    }
                    for (int i = 0; i < parts.Count; i++)
                        centres[i] = root.Position + new Vector3(Offsets[i], 0f, 0f) * root.Orientation;
                    return;
                }
                if (path == Path.BuildTool && off && !deselected && now >= deselectAt)
                {
                    deselected = true;
                    Select(false, parts);
                }
                if (off && !rayed && now >= rayAt)
                {
                    rayed = true;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        Vector3 c = centres[i];
                        var hits = (List<ContactResult>)r.PhysicsScene.RaycastWorld(new Vector3(c.X, c.Y, ground + 5f), -Vector3.UnitZ, 10f, 1,
                                                                                    RayFilterFlags.AllPrims | RayFilterFlags.land);
                        hitIds[i] = hits.Count > 0 ? hits[0].ConsumerID : uint.MaxValue;
                        hitTops[i] = hits.Count > 0 ? hits[0].Pos.Z : float.NaN;
                    }
                }
                if (rayed && !walking && now >= walkAt)
                {
                    walking = true;
                    // One avatar 3 m in front of each part (the row now runs along y), walking at it along x.
                    for (int i = 0; i < parts.Count; i++)
                    {
                        Vector3 c = centres[i];
                        var start = new Vector3(c.X - 3f, c.Y, r.GroundAt(c.X - 3f, c.Y) + r.AvatarStandHalf + 0.01f);
                        PhysicsActor av = r.PhysicsScene.AddAvatar(FirstAvatarId + (uint)i, "Test User", start, Run.AvatarSize, 0f, false);
                        av.TargetVelocity = new Vector3(RequestWalk, 0f, 0f);
                        avatars.Add(av);
                        avatarStartX[i] = start.X;
                        furthestX[i] = start.X;
                        xs[i] = new List<(double T, float X)>();
                    }
                    return;
                }
                if (walking)
                    for (int i = 0; i < avatars.Count; i++)
                    {
                        furthestX[i] = MathF.Max(furthestX[i], avatars[i].Position.X);
                        xs[i].Add((now, avatars[i].Position.X));
                    }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });

        Assert.True(walking, "the run ended before the avatars walked");
        Assert.InRange(carried, Carry - 0.1f, Carry + 0.6f);
        Assert.InRange(turned * 180f / MathF.PI, 89f, 93f);
        for (int i = 0; i < parts.Count; i++)
        {
            uint id = RootId + (uint)i;
            Assert.True(hitIds[i] == id, $"a ray onto part {id} at {centres[i]} hit {hitIds[i]}");
            Assert.True(MathF.Abs(hitTops[i] - (centres[i].Z + 0.5f)) <= 0.03f, $"a ray onto part {id} met it at z {hitTops[i]:0.###}, not its top {centres[i].Z + 0.5f:0.###}");
            // The near face is 0.5 m before the part's centre: the avatar's centre stops short of it.
            float face = centres[i].X - 0.5f;
            Assert.True(furthestX[i] < face, $"the avatar walked through part {id}: it reached x {furthestX[i]:0.##}, the part's face is at {face:0.##}");
            Assert.True(furthestX[i] - avatarStartX[i] >= 1.5f, $"the avatar at part {id} walked only {furthestX[i] - avatarStartX[i]:0.##} m");
            // Stopped: in the walk's last second it gained under 5 cm, though it still asks to walk at it.
            (double T, float X) last = xs[i][^1];
            (double T, float X) secondBefore = xs[i].Find(p => p.T >= last.T - 1.0 - 1e-9);
            Assert.True(last.X - secondBefore.X < 0.05f, $"the avatar at part {id} still moves on into it ({last.X - secondBefore.X:0.###} m in the last second)");
        }
    }

    // SceneObjectGroup.IsSelected: the root's actor, then every part's (the root's again among them).
    private static void Select(bool selected, List<PhysicsActor> parts)
    {
        parts[0].Selected = selected;
        foreach (PhysicsActor p in parts)
            p.Selected = selected;
    }

    private static float Yaw(Quaternion q)
    {
        Vector3 x = Vector3.UnitX * q;
        return MathF.Atan2(x.Y, x.X);
    }
}
