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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Physical status and read-outs:
/// - llSetStatus STATUS_ROTATE_X/Y/Z use the SL bits 0x002, 0x004, 0x008 (SL wiki llSetStatus), the same bits core
///   and every physics engine read from SceneObjectPart.RotationAxisLocks (SceneObjectGroup.axisSelect). Status is
///   an object attribute (SL: "all prims in an object share the same status"), so the locks are the root's, from
///   any prim of the object, and llGetStatus reads them back.
/// - llApplyImpulse drops an impulse with a NaN or infinite component: the 20000 cap's length test is false for a
///   NaN and turns an infinity into a NaN, which would otherwise reach the body or the wearer.
/// - llGetForce returns the force llSetForce stored on the object's root (SceneObjectPart.Force), in region axes, as
///   YEngine does, so it does not depend on the physics engine keeping the force it was given.
/// - llGetAccel is the object's acceleration, ZERO_VECTOR in a child prim or an attachment (SL wiki llGetAccel);
///   llGetOmega is the root's angular velocity, a physical spin or a llTargetOmega (SL: "Returns the omega of the
///   root if called in a child prim"); llGetTorque returns the torque llSetTorque stored on the root.
/// - llGetVel in an attachment returns the wearer's velocity, as YEngine (ScenePresence.GetWorldVelocity) and
///   Halcyon (SceneObjectPart.GetWearerVelocity) do; the SL wiki page says nothing about attachments.
/// </summary>
// Calls the API directly on the harness prims; no clock and no process-wide state, so the class runs in parallel.
public class PhysicsStatusAndReadoutTests
{
    private const int STATUS_ROTATE_X = 2, STATUS_ROTATE_Y = 4, STATUS_ROTATE_Z = 8;

    private static LSLSystemAPI Api(SchedulerHarness h, SceneObjectPart p) => new LSLSystemAPI(h.Engine, p, p.LocalId, UUID.Random());

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    /// <summary>
    /// A physical body as an engine reports it. Force and torque are not kept (NullPhysicsActor reads them back as
    /// zero), as on an engine that applies them and does not store them.
    /// </summary>
    private sealed class Body : NullPhysicsActor
    {
        public byte Locks;
        public int LockCalls;
        public Vector3 Vel, Accel, Spin;
        public readonly List<Vector3> Forces = new();
        public override bool IsPhysical { get => true; set { } }
        public override void LockAngularMotion(byte axislocks) { Locks = axislocks; LockCalls++; }
        public override Vector3 Velocity { get => Vel; set => Vel = value; }
        public override Vector3 Acceleration { get => Accel; set { } }
        public override Vector3 RotationalVelocity { get => Spin; set => Spin = value; }
        public override Quaternion Orientation { get; set; } = Quaternion.Identity;
        public override void AddForce(Vector3 force, bool pushforce) { lock (Forces) Forces.Add(force); }
    }

    private static SceneObjectPart AddChild(SchedulerHarness h, Vector3 offset)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "child", h.Prim.OwnerID));
        var child = h.Prim.ParentGroup.Parts.Single(p => p != h.Prim);
        child.OffsetPosition = offset;
        return child;
    }

    private static void SetAvatarActor(ScenePresence sp, PhysicsActor pa)
        => typeof(ScenePresence).GetProperty("PhysicsActor")!.GetSetMethod(true)!.Invoke(sp, new object[] { pa });

    /// <summary>Wear the harness object on a new avatar at <paramref name="pos"/>.</summary>
    private static ScenePresence Wear(SchedulerHarness h, Vector3 pos)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.AbsolutePosition = pos;
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sp.AddAttachment(sog);
        return sp;
    }

    // ---- llSetStatus / llGetStatus rotation axes ----

    [Theory]
    [InlineData(STATUS_ROTATE_X, 0x02)]
    [InlineData(STATUS_ROTATE_Y, 0x04)]
    [InlineData(STATUS_ROTATE_Z, 0x08)]
    public void SetStatusLocksTheAxisBitTheEnginesRead(int status, int bit)
    {
        using var h = new SchedulerHarness();
        var body = new Body();
        h.Prim.PhysActor = body;
        Api(h, h.Prim).llSetStatus(status, 0);
        Assert.Equal((byte)bit, h.Prim.RotationAxisLocks);
        Assert.Equal((byte)bit, body.Locks);
    }

    [Fact]
    public void SetStatusTrueFreesOnlyTheNamedAxis()
    {
        using var h = new SchedulerHarness();
        var body = new Body();
        h.Prim.PhysActor = body;
        var api = Api(h, h.Prim);
        api.llSetStatus(STATUS_ROTATE_X | STATUS_ROTATE_Y | STATUS_ROTATE_Z, 0);
        Assert.Equal((byte)0x0E, body.Locks);
        api.llSetStatus(STATUS_ROTATE_X, 1);
        Assert.Equal((byte)0x0C, h.Prim.RotationAxisLocks);
        Assert.Equal((byte)0x0C, body.Locks);
    }

    [Fact]
    public void SetStatusFromAChildLocksTheRootsAxes()
    {
        using var h = new SchedulerHarness();
        var body = new Body();
        h.Prim.PhysActor = body;
        var child = AddChild(h, new Vector3(1, 0, 0));
        Api(h, child).llSetStatus(STATUS_ROTATE_Z, 0);
        Assert.Equal((byte)0x08, h.Prim.RotationAxisLocks);
        Assert.Equal((byte)0x08, body.Locks);
        Assert.Equal((byte)0, child.RotationAxisLocks);
    }

    [Fact]
    public void GetStatusReadsTheRootsAxisLocks()
    {
        using var h = new SchedulerHarness();
        var child = AddChild(h, new Vector3(1, 0, 0));
        h.Prim.ParentGroup.SetAxisRotation(STATUS_ROTATE_X, 0);   // as YEngine's llSetStatus does
        foreach (var part in new[] { h.Prim, child })
        {
            var api = Api(h, part);
            Assert.Equal(0, api.llGetStatus(STATUS_ROTATE_X));
            Assert.Equal(1, api.llGetStatus(STATUS_ROTATE_Y));
            Assert.Equal(1, api.llGetStatus(STATUS_ROTATE_Z));
        }
    }

    // ---- llApplyImpulse with a value that is not a number ----

    public static IEnumerable<object[]> NotFiniteImpulses => new[]
    {
        new object[] { new Vector3(float.NaN, 0, 0) },
        new object[] { new Vector3(0, 0, float.PositiveInfinity) },
        new object[] { new Vector3(1, float.NegativeInfinity, float.NaN) },
    };

    [Theory]
    [MemberData(nameof(NotFiniteImpulses))]
    public void ApplyImpulseThatIsNotFiniteDoesNotReachThePhysicalObject(Vector3 impulse)
    {
        using var h = new SchedulerHarness();
        var body = new Body();
        h.Prim.PhysActor = body;
        h.Prim.AddFlag(PrimFlags.Physics);
        Api(h, h.Prim).llApplyImpulse(impulse, 0);
        Assert.Empty(body.Forces);
    }

    [Theory]
    [MemberData(nameof(NotFiniteImpulses))]
    public void ApplyImpulseThatIsNotFiniteDoesNotPushTheWearer(Vector3 impulse)
    {
        using var h = new SchedulerHarness();
        var sp = Wear(h, new Vector3(100, 100, 30));
        var avatar = new Body();
        SetAvatarActor(sp, avatar);
        Api(h, h.Prim).llApplyImpulse(impulse, 0);
        Assert.Empty(avatar.Forces);
    }

    [Fact]
    public void ApplyImpulseStillCapsAFiniteImpulseAt20000()
    {
        using var h = new SchedulerHarness();
        var body = new Body();
        h.Prim.PhysActor = body;
        h.Prim.AddFlag(PrimFlags.Physics);
        Api(h, h.Prim).llApplyImpulse(new Vector3(30000, 0, 0), 0);
        Assert.Single(body.Forces);
        Near(new Vector3(20000, 0, 0), body.Forces[0], 0.5f);
    }

    // ---- llGetForce ----

    [Fact]
    public void GetForceReturnsTheForceSetWhenTheEngineDoesNotKeepIt()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body();
        h.Prim.AddFlag(PrimFlags.Physics);
        var api = Api(h, h.Prim);
        api.llSetForce(new Vector3(4, 5, 6), 0);
        Near(new Vector3(4, 5, 6), api.llGetForce());
    }

    [Fact]
    public void GetForceInAChildReturnsTheObjectsForce()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body();
        h.Prim.AddFlag(PrimFlags.Physics);
        var child = AddChild(h, new Vector3(1, 0, 0));
        Api(h, h.Prim).llSetForce(new Vector3(0, 0, 9), 0);
        Near(new Vector3(0, 0, 9), Api(h, child).llGetForce());
    }

    [Fact]
    public void GetForceAfterALocalForceReturnsItInRegionAxes()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body();
        h.Prim.AddFlag(PrimFlags.Physics);
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
        h.Prim.ParentGroup.UpdateGroupRotationR(quarter);
        var api = Api(h, h.Prim);
        api.llSetForce(new Vector3(3, 0, 0), 1);
        Near(new Vector3(3, 0, 0) * quarter, api.llGetForce());
    }

    [Fact]
    public void GetForceAfterAZeroForceIsZero()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body();
        h.Prim.AddFlag(PrimFlags.Physics);
        var api = Api(h, h.Prim);
        api.llSetForce(new Vector3(4, 5, 6), 0);
        api.llSetForce(Vector3.Zero, 0);
        Assert.Equal(Vector3.Zero, api.llGetForce());
    }

    // ---- llGetAccel, llGetOmega, llGetTorque ----

    [Fact]
    public void GetAccelReturnsThePhysicalObjectsAcceleration()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body { Accel = new Vector3(1, 2, -3) };
        h.Prim.AddFlag(PrimFlags.Physics);
        Near(new Vector3(1, 2, -3), Api(h, h.Prim).llGetAccel());
    }

    [Fact]
    public void GetAccelInAChildIsZero()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body { Accel = new Vector3(1, 2, -3) };
        h.Prim.AddFlag(PrimFlags.Physics);
        var child = AddChild(h, new Vector3(1, 0, 0));
        Assert.Equal(Vector3.Zero, Api(h, child).llGetAccel());   // SL: "Returns ZERO_VECTOR in child prims"
    }

    [Fact]
    public void GetAccelInAnAttachmentIsZero()
    {
        using var h = new SchedulerHarness();
        var sp = Wear(h, new Vector3(100, 100, 30));
        SetAvatarActor(sp, new Body { Accel = new Vector3(0, 0, 5) });
        h.Prim.PhysActor = new Body { Accel = new Vector3(0, 0, 5) };
        Assert.Equal(Vector3.Zero, Api(h, h.Prim).llGetAccel());   // SL: "Returns ZERO_VECTOR in attachments"
    }

    [Fact]
    public void GetOmegaReturnsThePhysicalObjectsSpin()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body { Spin = new Vector3(0, 0, 2) };
        h.Prim.AddFlag(PrimFlags.Physics);
        Near(new Vector3(0, 0, 2), Api(h, h.Prim).llGetOmega());
    }

    [Fact]
    public void GetOmegaInAChildReturnsTheRootsSpin()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body { Spin = new Vector3(0, 0, 2) };
        h.Prim.AddFlag(PrimFlags.Physics);
        var child = AddChild(h, new Vector3(1, 0, 0));
        Near(new Vector3(0, 0, 2), Api(h, child).llGetOmega());   // SL: "Returns the omega of the root"
    }

    [Fact]
    public void GetOmegaOfANonPhysicalObjectReturnsItsTargetOmega()
    {
        using var h = new SchedulerHarness();
        var api = Api(h, h.Prim);
        api.llTargetOmega(new Vector3(0, 0, 1), 1.5f, 1f);
        Near(new Vector3(0, 0, 1.5f), api.llGetOmega());
    }

    [Fact]
    public void GetTorqueReturnsTheTorqueSetWhenTheEngineDoesNotKeepIt()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body();
        h.Prim.AddFlag(PrimFlags.Physics);
        var api = Api(h, h.Prim);
        api.llSetTorque(new Vector3(0, 7, 0), 0);
        Near(new Vector3(0, 7, 0), api.llGetTorque());
    }

    [Fact]
    public void GetTorqueInAChildReturnsTheObjectsTorque()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body();
        h.Prim.AddFlag(PrimFlags.Physics);
        var child = AddChild(h, new Vector3(1, 0, 0));
        Api(h, h.Prim).llSetForceAndTorque(Vector3.Zero, new Vector3(1, 0, 0), 0);
        Near(new Vector3(1, 0, 0), Api(h, child).llGetTorque());
    }

    // ---- llGetVel ----

    [Fact]
    public void GetVelInAnAttachmentReturnsTheWearersVelocity()
    {
        using var h = new SchedulerHarness();
        var sp = Wear(h, new Vector3(100, 100, 30));
        SetAvatarActor(sp, new Body { Vel = new Vector3(3, -1, 0) });
        h.Prim.PhysActor = new Body { Vel = new Vector3(0, 0, 9) };
        Near(new Vector3(3, -1, 0), Api(h, h.Prim).llGetVel());
    }

    [Fact]
    public void GetVelInAnAttachmentsChildReturnsTheWearersVelocity()
    {
        using var h = new SchedulerHarness();
        var child = AddChild(h, new Vector3(0.2f, 0, 0));
        var sp = Wear(h, new Vector3(100, 100, 30));
        SetAvatarActor(sp, new Body { Vel = new Vector3(0, 4, 0) });
        Near(new Vector3(0, 4, 0), Api(h, child).llGetVel());
    }

    [Fact]
    public void GetVelOfAnUnattachedPhysicalObjectIsItsOwnAsBefore()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Body { Vel = new Vector3(0, 0, 9) };
        h.Prim.AddFlag(PrimFlags.Physics);
        Near(new Vector3(0, 0, 9), Api(h, h.Prim).llGetVel());
    }
}
