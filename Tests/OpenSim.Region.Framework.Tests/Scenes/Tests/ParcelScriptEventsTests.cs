/*
 * Copyright (c) Contributors, http://opensimulator.org/
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

using System.Collections;
using System.Diagnostics;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

using Xunit.Abstractions;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// The scene events and the start-time check that let a script engine enforce No Scripts parcels live:
    /// EventManager.OnGroupCrossedToNewParcel, EventManager.OnObjectOwnerOrGroupChanged, and
    /// DefaultPermissionsModule's parcel rule deferring to an engine that implements IParcelScriptPolicyEngine.
    /// </summary>
    /// <remarks>
    /// No avatar is added to any of these scenes, so every test also covers an empty region.
    /// </remarks>
    public class ParcelScriptEventsTests
    {
        private static readonly UUID ParcelOwner = new UUID("00000000-0000-0000-0000-100000000001");
        private static readonly UUID OtherUser = new UUID("00000000-0000-0000-0000-100000000002");
        private static readonly UUID ThirdUser = new UUID("00000000-0000-0000-0000-100000000003");
        private static readonly UUID GroupA = new UUID("00000000-0000-0000-8888-000000000001");
        private static readonly UUID GroupB = new UUID("00000000-0000-0000-8888-000000000002");

        private readonly ITestOutputHelper m_output;

        private TestScene m_scene;

        /// <summary>West half, x in [0, 128). No outside or group scripts.</summary>
        private ILandObject m_west;

        /// <summary>East half, x in [128, 256).</summary>
        private ILandObject m_east;

        private readonly List<(SceneObjectGroup sog, ILandObject oldParcel, ILandObject newParcel)> m_crossings = new();
        private readonly List<(SceneObjectGroup sog, UUID oldOwner, UUID newOwner, UUID oldGroup, UUID newGroup)> m_ownerChanges = new();

        public ParcelScriptEventsTests(ITestOutputHelper output)
        {
            m_output = output;
        }

        private void SetUpScene(params object[] extraModules)
        {
            TestHelpers.InMethod();

            IConfigSource config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("serverside_object_permissions", true);

            m_scene = new SceneHelpers().SetupScene("parcels", TestHelpers.ParseTail(0x77), 1000, 1000, config);

            LandManagementModule lmm = new LandManagementModule();
            List<object> modules = new() { lmm, new DefaultPermissionsModule() };
            modules.AddRange(extraModules);
            SceneHelpers.SetupSceneModules(m_scene, config, modules.ToArray());

            int half = (int)Constants.RegionSize / 2;

            ILandObject west = new LandObject(ParcelOwner, false, m_scene);
            west.LandData.Name = "west";
            west.SetLandBitmap(west.GetSquareLandBitmap(0, 0, half, (int)Constants.RegionSize));
            m_west = lmm.AddLandObject(west);
            m_west.LandData.Flags &= ~(uint)(ParcelFlags.AllowOtherScripts | ParcelFlags.AllowGroupScripts);

            ILandObject east = new LandObject(ParcelOwner, false, m_scene);
            east.LandData.Name = "east";
            east.SetLandBitmap(east.GetSquareLandBitmap(half, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            m_east = lmm.AddLandObject(east);

            m_scene.EventManager.OnGroupCrossedToNewParcel += (sog, oldParcel, newParcel) => m_crossings.Add((sog, oldParcel, newParcel));
            m_scene.EventManager.OnObjectOwnerOrGroupChanged +=
                (sog, oldOwner, newOwner, oldGroup, newGroup) => m_ownerChanges.Add((sog, oldOwner, newOwner, oldGroup, newGroup));
        }

        private int m_nextTail = 0x1000;

        private SceneObjectGroup AddObject(Vector3 pos, UUID owner, int parts = 1)
        {
            // distinct part UUIDs for every object (the helper's default tail is shared)
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(parts, owner, "o", m_nextTail);
            m_nextTail += 16;
            sog.AbsolutePosition = pos;
            m_scene.AddNewSceneObject(sog, false);
            return sog;
        }

        #region object crossed to a new parcel

        [Fact]
        public void RezRaisesTheFirstParcelWithNoOldParcel()
        {
            SetUpScene();

            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);

            m_crossings.Should().HaveCount(1);
            m_crossings[0].sog.Should().BeSameAs(sog);
            m_crossings[0].oldParcel.Should().BeNull();
            m_crossings[0].newParcel.LandData.LocalID.Should().Be(m_west.LandData.LocalID);
        }

        [Fact]
        public void TheSetterRaisesAMoveAcrossAParcelBoundary()
        {
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            m_crossings.Clear();

            sog.AbsolutePosition = new Vector3(200, 64, 25);

            m_crossings.Should().HaveCount(1);
            m_crossings[0].sog.Should().BeSameAs(sog);
            m_crossings[0].oldParcel.LandData.LocalID.Should().Be(m_west.LandData.LocalID);
            m_crossings[0].newParcel.LandData.LocalID.Should().Be(m_east.LandData.LocalID);

            sog.AbsolutePosition = new Vector3(10, 64, 25);

            m_crossings.Should().HaveCount(2);
            m_crossings[1].oldParcel.LandData.LocalID.Should().Be(m_east.LandData.LocalID);
            m_crossings[1].newParcel.LandData.LocalID.Should().Be(m_west.LandData.LocalID);
        }

        [Fact]
        public void MovesWithinOneParcelRaiseNothing()
        {
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            m_crossings.Clear();

            sog.AbsolutePosition = new Vector3(100, 20, 25);
            sog.AbsolutePosition = new Vector3(1, 250, 40);
            sog.AbsolutePosition = new Vector3(127, 127, 25);

            m_crossings.Should().BeEmpty();
        }

        [Fact]
        public void APhysicsMoveAcrossAParcelBoundaryIsRaised()
        {
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            PhysicsActor pa = sog.RootPart.PhysActor;
            pa.Should().NotBeNull();
            // BasicPhysics does not simulate bodies, so wire the terse-update request the way
            // SceneObjectPart does for a physical prim (SceneObjectPart.ApplyPhysics / DoPhysicsPropertyUpdate).
            pa.OnRequestTerseUpdate += sog.RootPart.PhysicsRequestingTerseUpdate;
            m_crossings.Clear();

            // The physics engine moves the body without going through the group's position setter,
            // then asks for a terse update, as ubODE and BulletS do.
            pa.Position = new Vector3(100, 64, 25);
            pa.RequestPhysicsterseUpdate();
            m_crossings.Should().BeEmpty();

            pa.Position = new Vector3(190, 64, 25);
            pa.RequestPhysicsterseUpdate();

            m_crossings.Should().HaveCount(1);
            m_crossings[0].sog.Should().BeSameAs(sog);
            m_crossings[0].oldParcel.LandData.LocalID.Should().Be(m_west.LandData.LocalID);
            m_crossings[0].newParcel.LandData.LocalID.Should().Be(m_east.LandData.LocalID);
        }

        [Fact]
        public void AnObjectNotInTheSceneRaisesNothing()
        {
            SetUpScene();

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, OtherUser);
            sog.AbsolutePosition = new Vector3(64, 64, 25);
            sog.AbsolutePosition = new Vector3(200, 64, 25);

            m_crossings.Should().BeEmpty();
        }

        [Fact]
        public void AThousandObjectsMovingWithinTheirParcelsRaiseNothing()
        {
            SetUpScene();

            const int objects = 1000;
            const int movesEach = 10;
            SceneObjectGroup[] sogs = new SceneObjectGroup[objects];
            for (int i = 0; i < objects; i++)
            {
                float x = (i % 2 == 0) ? 20 + (i % 90) : 150 + (i % 90);
                sogs[i] = AddObject(new Vector3(x, 10 + (i % 230), 25), OtherUser);
            }
            m_crossings.Should().HaveCount(objects);
            m_crossings.Clear();

            Stopwatch sw = Stopwatch.StartNew();
            for (int m = 1; m <= movesEach; m++)
            {
                for (int i = 0; i < objects; i++)
                {
                    Vector3 p = sogs[i].AbsolutePosition;
                    p.Y = 10 + ((i + m * 7) % 230);
                    sogs[i].AbsolutePosition = p;
                }
            }
            sw.Stop();

            m_crossings.Should().BeEmpty();
            m_output.WriteLine(
                "{0} objects x {1} moves within their parcels: {2} events, {3:F1} ms total, {4:F3} us per move (whole setter)",
                objects, movesEach, m_crossings.Count, sw.Elapsed.TotalMilliseconds,
                sw.Elapsed.TotalMilliseconds * 1000.0 / (objects * movesEach));

            // the part this change adds to every move: one land lookup and one compare
            sw.Restart();
            for (int m = 0; m < movesEach; m++)
                for (int i = 0; i < objects; i++)
                    sogs[i].CheckParcelCrossing();
            sw.Stop();

            m_crossings.Should().BeEmpty();
            m_output.WriteLine(
                "CheckParcelCrossing alone, {0} calls: {1:F1} ms total, {2:F3} us per call",
                objects * movesEach, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds * 1000.0 / (objects * movesEach));
        }

        #endregion

        #region object owner or group changed

        [Fact]
        public void SetOwnerIdRaisesTheOwnerChange()
        {
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser, 2);
            sog.SetGroup(GroupA, null);
            m_ownerChanges.Clear();

            sog.SetOwnerId(ThirdUser);

            m_ownerChanges.Should().ContainSingle();
            var e = m_ownerChanges[0];
            e.sog.Should().BeSameAs(sog);
            e.oldOwner.Should().Be(OtherUser);
            e.newOwner.Should().Be(ThirdUser);
            e.oldGroup.Should().Be(GroupA);
            e.newGroup.Should().Be(GroupA);
        }

        [Fact]
        public void DeedToGroupRaisesTheOwnerChange()
        {
            // Scene.ObjectOwner's deed branch is sog.SetOwnerId(groupID).
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            sog.SetGroup(GroupA, null);
            m_ownerChanges.Clear();

            sog.SetOwnerId(GroupA);

            m_ownerChanges.Should().ContainSingle();
            m_ownerChanges[0].oldOwner.Should().Be(OtherUser);
            m_ownerChanges[0].newOwner.Should().Be(GroupA);
            m_ownerChanges[0].newGroup.Should().Be(GroupA);
        }

        [Fact]
        public void SetOwnerRaisesOwnerAndGroupTogether()
        {
            // Buy as original (BuySellModule) and llAttachToAvatarTemp use SetOwner.
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser, 3);
            sog.SetGroup(GroupA, null);
            m_ownerChanges.Clear();

            sog.SetOwner(ThirdUser, GroupB);

            m_ownerChanges.Should().ContainSingle();
            var e = m_ownerChanges[0];
            e.oldOwner.Should().Be(OtherUser);
            e.newOwner.Should().Be(ThirdUser);
            e.oldGroup.Should().Be(GroupA);
            e.newGroup.Should().Be(GroupB);
        }

        [Fact]
        public void SetGroupRaisesTheGroupChange()
        {
            // The ObjectGroup packet and the god "set owner" path use SetGroup.
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            m_ownerChanges.Clear();

            sog.SetGroup(GroupB, null);

            m_ownerChanges.Should().ContainSingle();
            var e = m_ownerChanges[0];
            e.oldOwner.Should().Be(OtherUser);
            e.newOwner.Should().Be(OtherUser);
            e.oldGroup.Should().Be(UUID.Zero);
            e.newGroup.Should().Be(GroupB);
        }

        [Fact]
        public void NoChangeAndObjectsNotInTheSceneRaiseNothing()
        {
            SetUpScene();
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            sog.SetGroup(GroupA, null);
            m_ownerChanges.Clear();

            sog.SetOwnerId(OtherUser);
            sog.SetGroup(GroupA, null);
            sog.SetOwner(OtherUser, GroupA);
            m_ownerChanges.Should().BeEmpty();

            // Rez paths set the owner and group before the object is added.
            SceneObjectGroup loose = SceneHelpers.CreateSceneObject(1, OtherUser);
            loose.SetGroup(GroupB, null);
            loose.SetOwnerId(ThirdUser);
            m_ownerChanges.Should().BeEmpty();

        }

        [Fact]
        public void ADuplicateRecordsItsOwnFirstParcel()
        {
            // SceneGraph.DuplicateObject copies the group (MemberwiseClone), may change its owner before it is in
            // the scene, and adds it without going through AddSceneObject.
            SetUpScene();
            // duplication is an agent action, so this test needs the agent present
            SceneHelpers.AddScenePresence(m_scene, ParcelOwner);
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), ParcelOwner);
            m_crossings.Clear();
            m_ownerChanges.Clear();

            SceneObjectGroup copy = m_scene.SceneGraph.DuplicateObject(
                sog.LocalId, new Vector3(100, 0, 0), ParcelOwner, GroupB, Quaternion.Identity, false);

            copy.Should().NotBeNull();
            copy.Should().NotBeSameAs(sog);
            m_ownerChanges.Should().BeEmpty();
            m_crossings.Should().ContainSingle();
            m_crossings[0].sog.Should().BeSameAs(copy);
            m_crossings[0].oldParcel.Should().BeNull();
            m_crossings[0].newParcel.LandData.LocalID.Should().Be(m_east.LandData.LocalID);

            // the copy is tracked from now on
            copy.AbsolutePosition = new Vector3(20, 64, 25);
            m_crossings.Should().HaveCount(2);
            m_crossings[1].sog.Should().BeSameAs(copy);
        }

        #endregion

        #region start-time parcel check

        private const string PlainScript = "default { state_entry() { } }";

        private (SceneObjectPart part, TaskInventoryItem item) AddScriptedObjectOnNoScriptLand(string source = PlainScript)
        {
            SceneObjectGroup sog = AddObject(new Vector3(64, 64, 25), OtherUser);
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(m_scene.AssetService, sog.RootPart, "s", source);
            return (sog.RootPart, item);
        }

        [Fact]
        public void WithNoEngineNoScriptLandIsRefusedAsToday()
        {
            SetUpScene();
            var (part, item) = AddScriptedObjectOnNoScriptLand();

            m_scene.Permissions.CanRunScript(item, part).Should().BeFalse();
            m_scene.Permissions.CanRunScript(item, part, false).Should().BeFalse();
            part.Inventory.CreateScriptInstance(item, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeFalse();        }

        [Fact]
        public void AnEngineWithoutTheInterfaceIsRefusedAsToday()
        {
            PlainEngine engine = new PlainEngine("YEngine");
            SetUpScene(engine);
            m_scene.DefaultScriptEngine.Should().Be("YEngine");
            var (part, item) = AddScriptedObjectOnNoScriptLand();

            part.Inventory.CreateScriptInstance(item, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeFalse();
            engine.Rezzed.Should().BeEmpty();

            // The parcel owner's own object still runs, as today.
            SceneObjectGroup own = AddObject(new Vector3(64, 64, 25), ParcelOwner);
            TaskInventoryItem ownItem = TaskInventoryHelpers.AddScript(m_scene.AssetService, own.RootPart, "s", PlainScript);
            own.RootPart.Inventory.CreateScriptInstance(ownItem, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeTrue();
            engine.Rezzed.Should().ContainSingle().Which.Should().Be(ownItem.ItemID);
        }

        [Fact]
        public void AnEngineThatEnforcesParcelRulesGetsTheScript()
        {
            PolicyEngine engine = new PolicyEngine("YEngine", enforces: true);
            SetUpScene(engine);
            var (part, item) = AddScriptedObjectOnNoScriptLand();

            m_scene.Permissions.CanRunScript(item, part, true).Should().BeTrue();
            part.Inventory.CreateScriptInstance(item, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeTrue();
            engine.Rezzed.Should().ContainSingle().Which.Should().Be(item.ItemID);
        }

        [Fact]
        public void AnEngineThatDeclaresFalseIsRefusedAsToday()
        {
            PolicyEngine engine = new PolicyEngine("YEngine", enforces: false);
            SetUpScene(engine);
            var (part, item) = AddScriptedObjectOnNoScriptLand();

            part.Inventory.CreateScriptInstance(item, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeFalse();
            engine.Rezzed.Should().BeEmpty();
        }

        [Fact]
        public void TheEngineIsResolvedPerScript()
        {
            // Default engine YEngine without the interface, plus an engine that enforces the rules itself.
            PlainEngine yengine = new PlainEngine("YEngine");
            PolicyEngine policy = new PolicyEngine("Policy", enforces: true);
            SetUpScene(yengine, policy);

            // Default engine: refused, as today.
            var (part1, item1) = AddScriptedObjectOnNoScriptLand();
            part1.Inventory.CreateScriptInstance(item1, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeFalse();

            // Names the policy engine on its first line: the policy engine decides.
            var (part2, item2) = AddScriptedObjectOnNoScriptLand("//Policy:lsl\n" + PlainScript);
            part2.Inventory.CreateScriptInstance(item2, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeTrue();
            policy.Rezzed.Should().ContainSingle().Which.Should().Be(item2.ItemID);

            // Names an engine that is not loaded: falls back to the default engine, refused.
            var (part3, item3) = AddScriptedObjectOnNoScriptLand("//Missing:lsl\n" + PlainScript);
            part3.Inventory.CreateScriptInstance(item3, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeFalse();
        }

        [Fact]
        public void AScriptNamingAPlainEngineIsRefusedWhenThePolicyEngineIsDefault()
        {
            PolicyEngine policy = new PolicyEngine("YEngine", enforces: true);
            PlainEngine other = new PlainEngine("Other");
            SetUpScene(policy, other);

            var (part1, item1) = AddScriptedObjectOnNoScriptLand();
            part1.Inventory.CreateScriptInstance(item1, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeTrue();

            var (part2, item2) = AddScriptedObjectOnNoScriptLand("//Other:lsl\n" + PlainScript);
            part2.Inventory.CreateScriptInstance(item2, 0, false, m_scene.DefaultScriptEngine, 0).Should().BeFalse();
        }

        /// <summary>A script engine stub that does not implement IParcelScriptPolicyEngine (as YEngine).</summary>
        private class PlainEngine : IScriptModule
        {
            private readonly string m_name;
            private Scene m_scene;
            public readonly List<UUID> Rezzed = new();

            public PlainEngine(string name) { m_name = name; }

            public string Name => m_name;
            public Type ReplaceableInterface => null;
            public void Initialise(IConfigSource source) { }
            public void Close() { }
            public void AddRegion(Scene scene)
            {
                m_scene = scene;
                scene.StackModuleInterface<IScriptModule>(this);
                scene.EventManager.OnRezScript += OnRezScript;
            }
            public void RemoveRegion(Scene scene) { scene.EventManager.OnRezScript -= OnRezScript; }
            public void RegionLoaded(Scene scene) { }

            private void OnRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
            {
                // Same choice as YEngine: the engine named on the first line if loaded, else the default.
                string wanted = engine;
                if (script.StartsWith("//"))
                {
                    int colon = script.IndexOf(':');
                    if (colon > 2)
                    {
                        string named = script[2..colon].Trim();
                        foreach (IScriptModule m in m_scene.RequestModuleInterfaces<IScriptModule>())
                            if (m.ScriptEngineName == named)
                                wanted = named;
                    }
                }
                if (wanted == m_name)
                    Rezzed.Add(itemID);
            }

            public event ScriptRemoved OnScriptRemoved { add { } remove { } }
            public event ObjectRemoved OnObjectRemoved { add { } remove { } }
            public string ScriptEngineName => m_name;
            public string GetXMLState(UUID itemID) => string.Empty;
            public bool SetXMLState(UUID itemID, string xml) => false;
            public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
            public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
            public bool SuspendScript(UUID itemID) => false;
            public bool ResumeScript(UUID itemID) => false;
            public ArrayList GetScriptErrors(UUID itemID) => new ArrayList();
            public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
            public bool GetScriptState(UUID itemID) => false;
            public void SaveAllState() { }
            public void StartProcessing() { }
            public float GetScriptExecutionTime(List<UUID> itemIDs) => 0f;
            public int GetScriptsMemory(List<UUID> itemIDs) => 0;
            public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => new();
            public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
            {
                totaltime = 0f;
                totalmemory = 0f;
                return new List<ScriptTopStatsData>();
            }
        }

        /// <summary>A script engine stub that declares whether it enforces parcel script rules itself.</summary>
        private class PolicyEngine : PlainEngine, IParcelScriptPolicyEngine
        {
            public PolicyEngine(string name, bool enforces) : base(name) { EnforcesParcelScriptRules = enforces; }
            public bool EnforcesParcelScriptRules { get; }
        }

        #endregion
    }
}
