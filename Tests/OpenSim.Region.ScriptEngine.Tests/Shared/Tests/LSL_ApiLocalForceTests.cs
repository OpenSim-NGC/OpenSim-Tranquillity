/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// What llSetForce and llSetTorque hand the physical prim's actor. The Second Life wiki
    /// (https://wiki.secondlife.com/wiki/LlSetForce, https://wiki.secondlife.com/wiki/LlSetTorque): with local TRUE the vector is
    /// in the object's own axes, with FALSE in the region's. The actor gets the vector in region axes, already turned by the
    /// object's rotation when the script gave a local one, together with the fact that the script gave it as local, so an
    /// engine can go on turning it with the object (<see cref="PhysicsActor.SetScriptForce"/>,
    /// <see cref="PhysicsActor.SetScriptTorque"/>). The tests use an object turned a quarter turn about z, where the object's
    /// x axis is the region's y axis.
    /// </summary>
    // Each test builds its own scene, group and stand-in actor and touches no process-wide state, so the class runs in parallel.
    public class LSL_ApiLocalForceTests
    {
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

        // The object's rotation: a quarter turn about the region's z axis.
        private static readonly Quaternion QuarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        private readonly Recorder m_actor = new();
        private readonly LSL_Api m_api;

        public LSL_ApiLocalForceTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");

            Scene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup group = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "force-", 0x10);
            group.RootPart.PhysActor = m_actor;

            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(config);
            engine.AddRegion(scene);

            m_api = new LSL_Api();
            m_api.Initialize(engine, group.RootPart, null);
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.True((expected - actual).Length() < 1e-4f, $"expected {expected}, got {actual}");
        }

        [Fact]
        public void A_local_force_reaches_the_actor_along_the_objects_own_axes()
        {
            m_api.llSetForce(new LSL_Vector(10.0, 0.0, 0.0), 1);

            // The object's x axis points along the region's y axis.
            AssertVector(new Vector3(0f, 10f, 0f), m_actor.Force);
            Assert.True(m_actor.ForceLocal);
            Assert.Equal(1, m_actor.ForceCalls);
        }

        [Fact]
        public void A_force_that_is_not_local_reaches_the_actor_along_the_regions_axes()
        {
            m_api.llSetForce(new LSL_Vector(10.0, 0.0, 0.0), 0);

            AssertVector(new Vector3(10f, 0f, 0f), m_actor.Force);
            Assert.False(m_actor.ForceLocal);
            Assert.Equal(1, m_actor.ForceCalls);
        }

        [Fact]
        public void A_local_torque_reaches_the_actor_along_the_objects_own_axes()
        {
            m_api.llSetTorque(new LSL_Vector(0.0, 5.0, 0.0), 1);

            // The object's y axis points along the region's negative x axis.
            AssertVector(new Vector3(-5f, 0f, 0f), m_actor.Torque);
            Assert.True(m_actor.TorqueLocal);
            Assert.Equal(1, m_actor.TorqueCalls);
        }

        [Fact]
        public void A_torque_that_is_not_local_reaches_the_actor_along_the_regions_axes()
        {
            m_api.llSetTorque(new LSL_Vector(0.0, 5.0, 0.0), 0);

            AssertVector(new Vector3(0f, 5f, 0f), m_actor.Torque);
            Assert.False(m_actor.TorqueLocal);
            Assert.Equal(1, m_actor.TorqueCalls);
        }

        [Fact]
        public void llSetForceAndTorque_hands_the_actor_both_with_the_same_local_flag()
        {
            m_api.llSetForceAndTorque(new LSL_Vector(1.0, 0.0, 0.0), new LSL_Vector(0.0, 1.0, 0.0), 1);

            AssertVector(new Vector3(0f, 1f, 0f), m_actor.Force);
            AssertVector(new Vector3(-1f, 0f, 0f), m_actor.Torque);
            Assert.True(m_actor.ForceLocal);
            Assert.True(m_actor.TorqueLocal);
        }

        [Fact]
        public void llGetForce_and_llGetTorque_read_back_the_region_axes_vector()
        {
            m_api.llSetForce(new LSL_Vector(10.0, 0.0, 0.0), 1);
            m_api.llSetTorque(new LSL_Vector(0.0, 5.0, 0.0), 1);

            LSL_Vector force = m_api.llGetForce();
            LSL_Vector torque = m_api.llGetTorque();
            AssertVector(new Vector3(0f, 10f, 0f), new Vector3((float)force.x, (float)force.y, (float)force.z));
            AssertVector(new Vector3(-5f, 0f, 0f), new Vector3((float)torque.x, (float)torque.y, (float)torque.z));
        }
    }
}
