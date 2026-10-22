/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// What llSetHoverHeight and llGroundRepel hand the physical prim's actor. The Second Life wiki
    /// (https://wiki.secondlife.com/wiki/LlGroundRepel, https://wiki.secondlife.com/wiki/LlSetHoverHeight): the height is
    /// above the ground, or above the higher of the land and the water when water is TRUE. The physics engines add the
    /// ground (or the water) themselves, so the height passes to the actor as the script gave it; llGroundRepel is the hover
    /// plus the repel flag.
    /// </summary>
    // Each test builds its own scene, group and stand-in actor and touches no process-wide state, so the class runs in parallel.
    public class LSL_ApiGroundRepelTests
    {
        private sealed class Recorder : NullPhysicsActor
        {
            public float Height;
            public PIDHoverType Type = (PIDHoverType)(-1);
            public float Tau;
            public bool Repel, Active;
            public override bool IsPhysical { get => true; set { } }
            public override Quaternion Orientation { get; set; } = Quaternion.Identity;
            public override float PIDHoverHeight { set => Height = value; }
            public override PIDHoverType PIDHoverType { set => Type = value; }
            public override float PIDHoverTau { set => Tau = value; }
            public override bool PIDHoverRepel { set => Repel = value; }
            public override bool PIDHoverActive { get => Active; set => Active = value; }
        }

        private readonly Recorder m_actor = new();
        private readonly LSL_Api m_api;

        public LSL_ApiGroundRepelTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");

            Scene scene = new SceneHelpers().SetupScene();
            // Land well above the water, so that an engine given the ground twice would show it in either case.
            for (int x = 0; x < scene.Heightmap.Width; x++)
                for (int y = 0; y < scene.Heightmap.Height; y++)
                    scene.Heightmap[x, y] = 40f;
            SceneObjectGroup group = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "repel-", 0x10);
            group.RootPart.PhysActor = m_actor;

            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(config);
            engine.AddRegion(scene);

            m_api = new LSL_Api();
            m_api.Initialize(engine, group.RootPart, null);
        }

        [Theory]
        [InlineData(0, PIDHoverType.Ground)]
        [InlineData(1, PIDHoverType.GroundAndWater)]
        public void llGroundRepel_hands_the_actor_the_height_as_the_script_gave_it(int water, PIDHoverType expected)
        {
            m_api.llGroundRepel(3.0, water, 1.0);

            Assert.Equal(expected, m_actor.Type);
            Assert.Equal(3f, m_actor.Height);
            Assert.Equal(1f, m_actor.Tau);
            Assert.True(m_actor.Repel);
            Assert.True(m_actor.Active);
        }

        [Theory]
        [InlineData(0, PIDHoverType.Ground)]
        [InlineData(1, PIDHoverType.GroundAndWater)]
        public void llSetHoverHeight_hands_the_actor_the_height_as_the_script_gave_it(int water, PIDHoverType expected)
        {
            m_api.llSetHoverHeight(3.0, water, 1.0);

            Assert.Equal(expected, m_actor.Type);
            Assert.Equal(3f, m_actor.Height);
            Assert.False(m_actor.Repel);
            Assert.True(m_actor.Active);
        }
    }
}
