/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Positions, rotations, scale and forces:
/// - llSetScale does nothing on a physical prim (SL wiki llSetScale: "Does not work on physical prims."); otherwise
///   it still rounds each component into [0.01, 64.0].
/// - llGetPos and PRIM_POSITION of a child prim in an attachment: its offset turned by the wearer's rotation plus the
///   wearer's position (Halcyon GetSLCompatiblePosition).
/// - llSetRot in a child prim is offset by the root's rotation (SL: "If the prim is not the root prim it is offset by
///   the root's rotation"), and the rotation is normalised; llSetLocalRot normalises too.
/// - llApplyImpulse from an attachment pushes the wearer (Halcyon and YEngine); llApplyRotationalImpulse from one
///   does nothing (SL: "It does not work on attachments.").
/// - llMoveToTarget keeps a target with no region beyond the edge inside the region (Halcyon).
/// - llLookAt points +Z at the target with +Y level and +X below the horizon (SL: "keeping its forward axis
///   (positive x) below the horizon"); straight above or below keeps the current turn.
/// - llEdgeOfWorld: TRUE for a zero direction ("TRUE is always returned"), and whatever the direction's length.
/// </summary>
// Calls the API directly on the harness prims; no clock and no process-wide state, so the class runs in parallel.
public class PositionRotationForceTests
{
    private const int PRIM_POSITION = 6;

    private static LSLSystemAPI Api(SchedulerHarness h, SceneObjectPart p) => new LSLSystemAPI(h.Engine, p, p.LocalId, UUID.Random());

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static void Near(Quaternion expected, Quaternion actual, float tolerance = 0.001f)
        => Assert.True(Math.Abs(Quaternion.Dot(expected, actual)) > 1f - tolerance, $"expected {expected}, got {actual}");

    private sealed class Recorder : NullPhysicsActor
    {
        public readonly List<Vector3> Forces = new(), AngularForces = new();
        public Vector3? Target;
        public override bool IsPhysical { get => true; set { } }
        public override void AddForce(Vector3 force, bool pushforce) { lock (Forces) Forces.Add(force); }
        public override void AddAngularForce(Vector3 force, bool pushforce) { lock (AngularForces) AngularForces.Add(force); }
        public override Vector3 PIDTarget { set => Target = value; }
    }

    private static void SetAvatarActor(ScenePresence sp, PhysicsActor pa)
        => typeof(ScenePresence).GetProperty("PhysicsActor")!.GetSetMethod(true)!.Invoke(sp, new object[] { pa });

    /// <summary>Wear the harness object on a new avatar at <paramref name="pos"/>, turned by <paramref name="rot"/>.</summary>
    private static ScenePresence Wear(SchedulerHarness h, Vector3 pos, Quaternion rot)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.AbsolutePosition = pos;
        sp.Rotation = rot;
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sp.AddAttachment(sog);
        return sp;
    }

    private static SceneObjectPart AddChild(SchedulerHarness h, Vector3 offset)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "child", h.Prim.OwnerID));
        var child = h.Prim.ParentGroup.Parts.Single(p => p != h.Prim);
        child.OffsetPosition = offset;
        return child;
    }

    private static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));

    // ---- llSetScale ----

    [Fact]
    public void SetScaleDoesNothingOnAPhysicalPrim()
    {
        using var h = new SchedulerHarness();
        Vector3 before = h.Prim.Scale;
        h.Prim.AddFlag(PrimFlags.Physics);
        Assert.True(h.Prim.ParentGroup.UsesPhysics);
        Api(h, h.Prim).llSetScale(new Vector3(2, 3, 4));
        Assert.Equal(before, h.Prim.Scale);
    }

    [Fact]
    public void SetScaleOnANonPhysicalPrimStillRoundsIntoTheSlRange()
    {
        using var h = new SchedulerHarness();
        Api(h, h.Prim).llSetScale(new Vector3(100, 0.001f, 2));
        Near(new Vector3(64, 0.01f, 2), h.Prim.Scale);
    }

    // ---- attachment child position ----

    [Fact]
    public void AnAttachmentChildsPositionFollowsTheWearersTurn()
    {
        using var h = new SchedulerHarness();
        var child = AddChild(h, new Vector3(1, 0, 0));
        Wear(h, new Vector3(100, 100, 30), Quarter);
        var expected = new Vector3(100, 101, 30);
        Near(expected, Api(h, child).llGetPos());
        var got = Api(h, h.Prim).llGetLinkPrimitiveParams(2, new LSLList(new object[] { PRIM_POSITION }));
        Near(expected, (Vector3)got.Data[0]);
    }

    // ---- llSetRot / llSetLocalRot ----

    [Fact]
    public void SetRotInAChildIsOffsetByTheRootsRotation()
    {
        using var h = new SchedulerHarness();
        var child = AddChild(h, new Vector3(1, 0, 0));
        h.Prim.ParentGroup.UpdateGroupRotationR(Quarter);
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        Api(h, child).llSetRot(rot);
        Near(Quarter * rot, child.RotationOffset);
    }

    [Fact]
    public void SetRotNormalisesTheRotation()
    {
        using var h = new SchedulerHarness();
        Api(h, h.Prim).llSetRot(new Quaternion(0, 0, 2, 2));
        Assert.Equal(1f, h.Prim.ParentGroup.GroupRotation.Length(), 3);
    }

    [Fact]
    public void SetLocalRotNormalisesTheRotation()
    {
        using var h = new SchedulerHarness();
        var child = AddChild(h, new Vector3(1, 0, 0));
        Api(h, child).llSetLocalRot(new Quaternion(0, 0, 3, 3));
        Assert.Equal(1f, child.RotationOffset.Length(), 3);
    }

    // ---- impulses from an attachment ----

    [Fact]
    public void ApplyImpulseFromAnAttachmentPushesTheWearer()
    {
        using var h = new SchedulerHarness();
        var sp = Wear(h, new Vector3(100, 100, 30), Quaternion.Identity);
        var actor = new Recorder();
        SetAvatarActor(sp, actor);
        Api(h, h.Prim).llApplyImpulse(new Vector3(5, 0, 0), 0);
        Assert.Single(actor.Forces);
        Near(new Vector3(5, 0, 0), actor.Forces[0]);
    }

    [Fact]
    public void ApplyRotationalImpulseFromAnAttachmentDoesNothing()
    {
        using var h = new SchedulerHarness();
        var sp = Wear(h, new Vector3(100, 100, 30), Quaternion.Identity);
        var actor = new Recorder();
        SetAvatarActor(sp, actor);
        h.Prim.PhysActor = new Recorder();
        Api(h, h.Prim).llApplyRotationalImpulse(new Vector3(0, 0, 5), 0);
        Assert.Empty(actor.AngularForces);
        Assert.Empty(((Recorder)h.Prim.PhysActor).AngularForces);
    }

    // ---- llMoveToTarget ----

    [Fact]
    public void MoveToTargetKeepsATargetWithNoRegionBeyondTheEdgeInside()
    {
        using var h = new SchedulerHarness();
        var actor = new Recorder();
        h.Prim.PhysActor = actor;
        h.Prim.AddFlag(PrimFlags.Physics);
        Api(h, h.Prim).llMoveToTarget(new Vector3(300, -20, 25), 1f);
        Assert.NotNull(actor.Target);
        var t = actor.Target!.Value;
        Assert.InRange(t.X, 255f, 256f);
        Assert.InRange(t.Y, 0f, 1f);
        Assert.Equal(25f, t.Z);
    }

    [Fact]
    public void MoveToTargetInsideTheRegionIsUnchanged()
    {
        using var h = new SchedulerHarness();
        var actor = new Recorder();
        h.Prim.PhysActor = actor;
        h.Prim.AddFlag(PrimFlags.Physics);
        Api(h, h.Prim).llMoveToTarget(new Vector3(30, 40, 25), 1f);
        Near(new Vector3(30, 40, 25), actor.Target!.Value);
    }

    // ---- llLookAt ----

    [Fact]
    public void LookAtPointsUpAtTheTargetAndKeepsTheLeftAxisLevel()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(128, 128, 25);
        var target = new Vector3(138, 138, 30);
        Api(h, h.Prim).llLookAt(target, 1f, 1f);
        var r = h.Prim.ParentGroup.GroupRotation;
        Vector3 targetDirection = target - new Vector3(128, 128, 25);
        Near(Vector3.Normalize(in targetDirection), Vector3.UnitZ * r);
        Assert.InRange((Vector3.UnitY * r).Z, -0.001f, 0.001f);
        Assert.True((Vector3.UnitX * r).Z <= 0.001f, "forward axis above the horizon: " + (Vector3.UnitX * r));
    }

    [Fact]
    public void LookAtStraightUpKeepsTheCurrentTurn()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(128, 128, 25);
        h.Prim.ParentGroup.UpdateGroupRotationR(Quarter);
        Api(h, h.Prim).llLookAt(new Vector3(128, 128, 40), 1f, 1f);
        Near(Quarter, h.Prim.ParentGroup.GroupRotation);
    }

    // ---- llEdgeOfWorld ----

    [Fact]
    public void EdgeOfWorldIsTrueForAZeroDirection()
    {
        using var h = new SchedulerHarness();
        Assert.Equal(1, Api(h, h.Prim).llEdgeOfWorld(new Vector3(128, 128, 25), Vector3.Zero));
    }

    [Fact]
    public void EdgeOfWorldDoesNotDependOnTheDirectionsLength()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        Assert.Equal(1, api.llEdgeOfWorld(new Vector3(128, 128, 25), new Vector3(0.1f, 0, 0)));
        Assert.Equal(1, api.llEdgeOfWorld(new Vector3(128, 128, 25), new Vector3(0, -50f, 7)));
    }
}
