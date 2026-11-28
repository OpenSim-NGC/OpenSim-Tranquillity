/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Linq;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// An object YEngine rezzes from a script gets STATUS_DIE_AT_EDGE only from the [EntityTransfer]
    /// ScriptRezzedObjectsDieAtEdge setting, as an object Phlox rezzes does (both go through Scene.RezObject), and
    /// never the stored flag, which would also delete it when it crosses into a neighbouring region.
    /// </summary>
    public class LSL_ApiRezDieAtEdgeTests : OpenSimTestCase
    {
        private sealed class Rig
        {
            public required Scene Scene;
            public required SceneObjectGroup Rezzer;
            public required LSL_Api Api;

            public SceneObjectGroup Rezzed()
            {
                return Scene.GetSceneObjectGroups().Single(g => g.RezzerID == Rezzer.RootPart.UUID);
            }
        }

        public LSL_ApiRezDieAtEdgeTests()
        {
            // The rez functions finish on the work pool; run that inline. The assembly runs one test at a time.
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        }

        public override void Dispose()
        {
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
            base.Dispose();
        }

        private static Rig Build(bool scriptRezzedObjectsDieAtEdge)
        {
            MockScriptEngine engine = new MockScriptEngine();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), engine);
            scene.ScriptRezzedObjectsDieAtEdge = scriptRezzedObjectsDieAtEdge;

            UUID owner = TestHelpers.ParseTail(0x1);
            SceneObjectGroup rezzer = SceneHelpers.AddSceneObject(scene, 1, owner, "rezzer-", 0x100);
            rezzer.AbsolutePosition = new Vector3(128, 128, 20);
            TaskInventoryHelpers.AddSceneObject(scene.AssetService, rezzer.RootPart, "child", TestHelpers.ParseTail(0x200), owner);
            TaskInventoryItem script = TaskInventoryHelpers.AddScript(scene.AssetService, rezzer.RootPart);

            LSL_Api api = new LSL_Api();
            api.Initialize(engine, rezzer.RootPart, script);
            return new Rig { Scene = scene, Rezzer = rezzer, Api = api };
        }

        private static readonly LSL_Vector Offset = new LSL_Vector(0, 0, 1);

        private static SceneObjectGroup RezWithLlRezObject(Rig r)
        {
            r.Api.llRezObject("child", r.Api.llGetPos() + Offset, new LSL_Vector(0, 0, 0), new LSL_Rotation(0, 0, 0, 1), 0);
            return r.Rezzed();
        }

        private static SceneObjectGroup RezWithLlRezObjectWithParams(Rig r)
        {
            r.Api.llRezObjectWithParams("child",
                new LSL_List(ScriptBaseClass.REZ_POS, Offset, ScriptBaseClass.TRUE, ScriptBaseClass.FALSE));
            return r.Rezzed();
        }

        [Fact]
        public void llRezObject_with_the_setting_off_rezzes_an_object_without_die_at_edge()
        {
            TestHelpers.InMethod();
            Rig r = Build(false);

            SceneObjectGroup so = RezWithLlRezObject(r);
            Assert.False(so.RootPart.DIE_AT_EDGE);
            Assert.False(so.DieAtVoidEdge);
            Assert.False(so.RootPart.GetDieAtEdge());
        }

        [Fact]
        public void llRezObject_with_the_setting_on_rezzes_an_object_that_dies_at_a_void_edge_only()
        {
            TestHelpers.InMethod();
            Rig r = Build(true);

            SceneObjectGroup so = RezWithLlRezObject(r);
            Assert.True(so.DieAtVoidEdge);
            Assert.False(so.RootPart.DIE_AT_EDGE);   // the stored flag also acts at a crossing into a neighbour
            Assert.True(so.RootPart.GetDieAtEdge());   // what llGetStatus(STATUS_DIE_AT_EDGE) answers
        }

        [Fact]
        public void llRezObjectWithParams_with_the_setting_off_rezzes_an_object_without_die_at_edge()
        {
            TestHelpers.InMethod();
            Rig r = Build(false);

            SceneObjectGroup so = RezWithLlRezObjectWithParams(r);
            Assert.False(so.RootPart.DIE_AT_EDGE);
            Assert.False(so.DieAtVoidEdge);
            Assert.False(so.RootPart.GetDieAtEdge());
        }

        [Fact]
        public void llRezObjectWithParams_with_the_setting_on_rezzes_an_object_that_dies_at_a_void_edge_only()
        {
            TestHelpers.InMethod();
            Rig r = Build(true);

            SceneObjectGroup so = RezWithLlRezObjectWithParams(r);
            Assert.True(so.DieAtVoidEdge);
            Assert.False(so.RootPart.DIE_AT_EDGE);
            Assert.True(so.RootPart.GetDieAtEdge());
        }
    }
}
