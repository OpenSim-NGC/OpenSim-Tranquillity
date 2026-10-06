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
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llPushObject (SL wiki LlPushObject):
/// - local: "if TRUE uses the local axis of target, if FALSE uses the region axis." A local push on an avatar is turned
///   by the avatar's rotation, on an object by the object's; the pusher's own rotation plays no part.
/// - "ang_impulse is ignored when applying to agents or their attachments."
/// </summary>
// Calls the API directly on the harness prims; no clock and no process-wide state, so the class runs in parallel.
public class PushObjectTests
{
    private static LSLSystemAPI Api(SchedulerHarness h, SceneObjectPart p) => new LSLSystemAPI(h.Engine, p, p.LocalId, UUID.Random());

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    /// <summary>
    /// Records each push. It keeps the position and rotation it is given, because a part or an avatar with a physics
    /// actor reads its own from the actor.
    /// </summary>
    private sealed class Recorder : NullPhysicsActor
    {
        public readonly List<Vector3> Forces = new(), AngularForces = new();
        private Vector3 m_position;
        private Quaternion m_orientation = Quaternion.Identity;
        public override bool IsPhysical { get => true; set { } }
        public override Vector3 Position { get => m_position; set => m_position = value; }
        public override Quaternion Orientation { get => m_orientation; set => m_orientation = value; }
        public override void AddForce(Vector3 force, bool pushforce) { lock (Forces) Forces.Add(force); }
        public override void AddAngularForce(Vector3 force, bool pushforce) { lock (AngularForces) AngularForces.Add(force); }
    }

    private static void SetAvatarActor(ScenePresence sp, PhysicsActor pa)
        => typeof(ScenePresence).GetProperty("PhysicsActor")!.GetSetMethod(true)!.Invoke(sp, new object[] { pa });

    private static readonly Quaternion PusherTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
    private static readonly Quaternion TargetTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(Math.PI / 2));
    private static readonly Vector3 Impulse = new(3, 0, 0);

    /// <summary>The harness prim, turned by <see cref="PusherTurn"/>, at <paramref name="pos"/>.</summary>
    private static SceneObjectPart Pusher(SchedulerHarness h, Vector3 pos)
    {
        h.Prim.ParentGroup.AbsolutePosition = pos;
        h.Prim.ParentGroup.UpdateGroupRotationR(PusherTurn);
        return h.Prim;
    }

    /// <summary>An avatar at <paramref name="pos"/>, turned by <paramref name="rot"/>, whose physics records each push.</summary>
    private static (ScenePresence Sp, Recorder Actor) Avatar(SchedulerHarness h, Vector3 pos, Quaternion rot)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var actor = new Recorder();
        SetAvatarActor(sp, actor);
        sp.AbsolutePosition = pos;
        sp.Rotation = rot;
        actor.Position = pos;
        return (sp, actor);
    }

    /// <summary>A physical object at <paramref name="pos"/>, turned by <paramref name="rot"/>, recording each push.</summary>
    private static (SceneObjectGroup Sog, Recorder Actor) Target(SchedulerHarness h, Vector3 pos, Quaternion rot)
    {
        var sog = SceneHelpers.AddSceneObject(h.Scene, "target", UUID.Random());
        var actor = new Recorder { Position = pos, Orientation = rot };
        sog.RootPart.PhysActor = actor;
        sog.AbsolutePosition = pos;
        sog.UpdateGroupRotationR(rot);
        return (sog, actor);
    }

    [Fact]
    public void ALocalPushOnAnAvatarUsesTheAvatarsAxes()
    {
        using var h = new SchedulerHarness();
        var pusher = Pusher(h, new Vector3(100, 100, 25));
        var (sp, actor) = Avatar(h, new Vector3(102, 100, 25), TargetTurn);
        Api(h, pusher).llPushObject(sp.UUID.ToString(), Impulse, Vector3.Zero, 1);
        Assert.Single(actor.Forces);
        Near(Impulse * TargetTurn, actor.Forces[0]);
    }

    [Fact]
    public void ARegionAxisPushOnAnAvatarIsNotTurned()
    {
        using var h = new SchedulerHarness();
        var pusher = Pusher(h, new Vector3(100, 100, 25));
        var (sp, actor) = Avatar(h, new Vector3(102, 100, 25), TargetTurn);
        Api(h, pusher).llPushObject(sp.UUID.ToString(), Impulse, Vector3.Zero, 0);
        Assert.Single(actor.Forces);
        Near(Impulse, actor.Forces[0]);
    }

    [Fact]
    public void ALocalPushOnAnObjectUsesTheObjectsAxes()
    {
        using var h = new SchedulerHarness();
        var pusher = Pusher(h, new Vector3(100, 100, 25));
        var (target, actor) = Target(h, new Vector3(102, 100, 25), TargetTurn);
        Api(h, pusher).llPushObject(target.UUID.ToString(), Impulse, Vector3.Zero, 1);
        Assert.Single(actor.Forces);
        Near(Impulse * TargetTurn, actor.Forces[0]);
    }

    [Fact]
    public void ARegionAxisPushOnAnObjectIsNotTurned()
    {
        using var h = new SchedulerHarness();
        var pusher = Pusher(h, new Vector3(100, 100, 25));
        var (target, actor) = Target(h, new Vector3(102, 100, 25), TargetTurn);
        Api(h, pusher).llPushObject(target.UUID.ToString(), Impulse, Vector3.Zero, 0);
        Assert.Single(actor.Forces);
        Near(Impulse, actor.Forces[0]);
    }

    [Fact]
    public void AnAngularImpulseOnAnAvatarIsIgnored()
    {
        using var h = new SchedulerHarness();
        var pusher = Pusher(h, new Vector3(100, 100, 25));
        var (sp, actor) = Avatar(h, new Vector3(102, 100, 25), TargetTurn);
        Api(h, pusher).llPushObject(sp.UUID.ToString(), Impulse, new Vector3(0, 0, 5), 1);
        Assert.Single(actor.Forces);
        Assert.Empty(actor.AngularForces);
    }
}
