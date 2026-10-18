/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What llSetForce and llSetTorque hand the physical prim's actor. The Second Life wiki
/// (https://wiki.secondlife.com/wiki/LlSetForce, https://wiki.secondlife.com/wiki/LlSetTorque): with local TRUE the vector is
/// in the object's own axes, with FALSE in the region's. The actor gets the vector in region axes, already turned by the
/// object's rotation when the script gave a local one, together with the fact that the script gave it as local
/// (<see cref="PhysicsActor.SetScriptForce"/>, <see cref="PhysicsActor.SetScriptTorque"/>). The object is turned a quarter
/// turn about z, where its x axis is the region's y axis.
/// </summary>
// Calls the API directly on the harness prim; no clock and no process-wide state, so the class runs in parallel.
public class LocalForceFlagTests
{
    private static readonly Quaternion QuarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

    private sealed class Recorder : NullPhysicsActor
    {
        public Vector3 Force, Torque;
        public bool ForceLocal, TorqueLocal;
        public int ForceCalls, TorqueCalls;
        public override bool IsPhysical { get => true; set { } }
        // A root prim reads its rotation from its actor.
        public override Quaternion Orientation { get; set; } = QuarterTurn;

        public override void SetScriptForce(Vector3 force, bool local)
        {
            Force = force;
            ForceLocal = local;
            ForceCalls++;
        }

        public override void SetScriptTorque(Vector3 torque, bool local)
        {
            Torque = torque;
            TorqueLocal = local;
            TorqueCalls++;
        }
    }

    private static (SchedulerHarness, Recorder, LSLSystemAPI) Make()
    {
        var h = new SchedulerHarness();
        var actor = new Recorder();
        h.Prim.PhysActor = actor;
        h.Prim.AddFlag(PrimFlags.Physics);
        return (h, actor, new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random()));
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-4f);
    }

    [Theory]
    [InlineData(1, true, 0f, 1f)]
    [InlineData(0, false, 1f, 0f)]
    public void llSetForce_gives_the_actor_region_axes_and_whether_the_script_said_local(int local, bool expectedLocal, float x, float y)
    {
        var (h, actor, api) = Make();
        using (h)
        {
            api.llSetForce(new Vector3(1f, 0f, 0f), local);

            Assert.Equal(1, actor.ForceCalls);
            Assert.Equal(expectedLocal, actor.ForceLocal);
            AssertVector(new Vector3(x, y, 0f), actor.Force);
        }
    }

    [Theory]
    [InlineData(1, true, 0f, 1f)]
    [InlineData(0, false, 1f, 0f)]
    public void llSetTorque_gives_the_actor_region_axes_and_whether_the_script_said_local(int local, bool expectedLocal, float x, float y)
    {
        var (h, actor, api) = Make();
        using (h)
        {
            api.llSetTorque(new Vector3(1f, 0f, 0f), local);

            Assert.Equal(1, actor.TorqueCalls);
            Assert.Equal(expectedLocal, actor.TorqueLocal);
            AssertVector(new Vector3(x, y, 0f), actor.Torque);
        }
    }

    [Fact]
    public void llGetForce_and_llGetTorque_read_back_the_region_axes_vector()
    {
        var (h, _, api) = Make();
        using (h)
        {
            api.llSetForceAndTorque(new Vector3(1f, 0f, 0f), new Vector3(0f, 2f, 0f), 1);

            AssertVector(new Vector3(0f, 1f, 0f), api.llGetForce());
            AssertVector(new Vector3(-2f, 0f, 0f), api.llGetTorque());
        }
    }
}
