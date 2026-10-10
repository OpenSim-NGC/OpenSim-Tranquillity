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
}
