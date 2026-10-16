/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Copyright (c) Legion Builds
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


using System.Collections.Generic;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using Xunit;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// What llCastRay hands the physics engine and what a script gets back. The Second Life wiki
    /// (https://wiki.secondlife.com/wiki/LlCastRay) gives RC_MAX_HITS a maximum of 256, says that phantom and
    /// volume-detect objects are detected when RC_DETECT_PHANTOM is set to any nonzero value. A hit is left as OpenSim
    /// has always returned it: the key, the position as a string, the link number if asked for, then the normal if asked
    /// for (the wiki's order differs; existing scripts read this one).
    /// </summary>
    public class LSL_ApiCastRayTests
    {
        /// <summary>An engine that filters by type and counts as the real ones do, over hits it is given, and records the filter.</summary>
        private sealed class FilteringScene : OpenSim.Region.PhysicsModules.BasicPhysics.BasicScene
        {
            public readonly List<(float depth, uint id)> Hits = new();
            public RayFilterFlags? AskedFilter;

            public override bool SupportsRaycastWorldFiltered() => true;

            public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int Count, RayFilterFlags filter)
            {
                AskedFilter = filter;
                return Hits.OrderBy(h => h.depth).Take(Count)
                    .Select(h => new ContactResult { ConsumerID = h.id, Depth = h.depth, Pos = new Vector3(10, 10, 50 - h.depth), Normal = Vector3.UnitZ })
                    .ToList();
            }
        }

        private readonly Scene m_scene;
        private readonly FilteringScene m_physics = new();
        private readonly SceneObjectGroup m_group;
        private readonly LSL_Api m_api;

        public LSL_ApiCastRayTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");

            m_scene = new SceneHelpers().SetupScene();
            m_scene.PhysicsScene = m_physics;
            m_group = SceneHelpers.AddSceneObject(m_scene, 3, UUID.Random(), "cast-", 0x10);

            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(config);
            engine.AddRegion(m_scene);

            m_api = new LSL_Api();
            m_api.Initialize(engine, m_group.RootPart, null);
        }

        private static LSL_List Options(params int[] pairs) => new LSL_List(pairs.Select(p => (object)new LSL_Integer(p)).ToArray());

        private LSL_List Cast(LSL_List options)
            => m_api.llCastRay(new LSL_Vector(10, 10, 50), new LSL_Vector(10, 10, 0), options);

        private static int Status(LSL_List result) => ((LSL_Integer)result.Data[result.Length - 1]).value;

        [Fact]
        public void ACastForMoreThanSixteenHitsGetsThemUpTo256()
        {
            for (int i = 1; i <= 300; i++) m_physics.Hits.Add((i * 0.1f, 0));

            LSL_List result = Cast(Options(ScriptBaseClass.RC_MAX_HITS, 300));

            Assert.Equal(256, Status(result));
        }

        [Fact]
        public void TheHostGroupsOwnHitsTakeNoPlaceAmongTheHitsAsked()
        {
            // The ray crosses the three prims of the script's own linkset, then the ground.
            float depth = 1;
            foreach (SceneObjectPart part in m_group.Parts) m_physics.Hits.Add((depth++, part.LocalId));
            m_physics.Hits.Add((depth, 0));

            LSL_List result = Cast(Options(ScriptBaseClass.RC_MAX_HITS, 1, ScriptBaseClass.RC_REJECT_TYPES, ScriptBaseClass.RC_REJECT_HOSTGROUP));

            Assert.Equal(1, Status(result));
        }

        [Fact]
        public void TheHostPrimsOwnHitTakesNoPlaceAmongTheHitsAsked()
        {
            m_physics.Hits.Add((1, m_group.RootPart.LocalId));
            m_physics.Hits.Add((2, 0));
            m_physics.Hits.Add((3, 0));

            LSL_List result = Cast(Options(ScriptBaseClass.RC_MAX_HITS, 2, ScriptBaseClass.RC_REJECT_TYPES, ScriptBaseClass.RC_REJECT_HOST));

            Assert.Equal(2, Status(result));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(-1, true)]
        [InlineData(4, true)]
        public void PhantomsAreAskedForWhenTheOptionIsNonzero(int value, bool asked)
        {
            m_physics.Hits.Add((1, 0));

            Cast(Options(ScriptBaseClass.RC_DETECT_PHANTOM, value));

            Assert.Equal(asked, (m_physics.AskedFilter.Value & RayFilterFlags.LSLPhantom) == RayFilterFlags.LSLPhantom);
        }

        [Fact]
        public void AHitIsTheKeyThePositionAsAStringTheLinkNumberThenTheNormal()
        {
            // Existing OpenSim scripts read a hit in this order, so it is left as it is (it differs from the wiki's).
            m_physics.Hits.Add((2, m_group.RootPart.LocalId));

            LSL_List result = Cast(Options(ScriptBaseClass.RC_MAX_HITS, 1,
                ScriptBaseClass.RC_DATA_FLAGS, ScriptBaseClass.RC_GET_LINK_NUM | ScriptBaseClass.RC_GET_NORMAL));

            Assert.Equal(5, result.Length);
            Assert.Equal(m_group.RootPart.UUID.ToString(), result.Data[0].ToString());
            Assert.IsType<LSL_String>(result.Data[1]);
            Assert.Equal(new Vector3(10, 10, 48).ToString(), result.Data[1].ToString());
            Assert.IsType<LSL_Integer>(result.Data[2]);
            Assert.IsType<LSL_Vector>(result.Data[3]);
            Assert.Equal(1, Status(result));
        }

        [Fact]
        public void AHitWithNoDataFlagsIsTheKeyThenThePositionAsAString()
        {
            m_physics.Hits.Add((2, 0));

            LSL_List result = Cast(Options(ScriptBaseClass.RC_MAX_HITS, 1));

            Assert.Equal(3, result.Length);
            Assert.IsType<LSL_String>(result.Data[0]);
            Assert.IsType<LSL_String>(result.Data[1]);
            Assert.Equal(1, Status(result));
        }
    }
}
