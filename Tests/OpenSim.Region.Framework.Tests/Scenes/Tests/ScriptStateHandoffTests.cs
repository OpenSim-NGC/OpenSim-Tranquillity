/*
 * Copyright (c) Legion Builds
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
using System.Xml;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Script state travels with an object when it crosses a region border, when an avatar's attachments teleport
    /// (SceneObjectGroup.SetState) and when it is rezzed or attached from saved object XML (rez path). A region can
    /// run more than one script engine; the state must reach the engine that owns each script, and unreadable saved
    /// state must never stop an object from arriving.
    ///
    /// The engines here are stand-ins: like YEngine, each accepts only a state tagged with its own name and refuses any
    /// other without side effects. No script is compiled or run.
    /// </summary>
    /// <remarks>
    /// These tests share no process-wide state, so they run in parallel with the rest.
    /// </remarks>
    public class ScriptStateHandoffTests : OpenSimTestCase
    {
        private const string DefaultEngineName = "YEngine";
        private const string OtherEngineName = "OtherEngine";
        private const string Source = "default { state_entry() { } }";

        private static readonly UUID Owner = new UUID("00000000-0000-0000-0000-100000000001");

        private TestScene SetUpScene(params StateEngine[] engines)
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Startup").Set("DefaultScriptEngine", DefaultEngineName);

            TestScene scene = new SceneHelpers().SetupScene("state handoff", TestHelpers.ParseTail(0x77), 1000, 1000, config);
            SceneHelpers.SetupSceneModules(scene, config, engines.Cast<object>().ToArray());
            return scene;
        }

        private static SceneObjectGroup AddObject(Scene scene, int parts = 1)
        {
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(parts, Owner);
            scene.AddNewSceneObject(sog, false);
            return sog;
        }

        /// <summary>Adds a script to the part and makes the engine the one that runs it (it will report a state for it).</summary>
        private static TaskInventoryItem AddScriptRunBy(Scene scene, SceneObjectPart part, StateEngine engine)
        {
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(scene.AssetService, part, "script", Source);
            engine.Runs(item.ItemID);
            return item;
        }

        // ---- SetState: crossing and attachment teleport -----------------------------------------------------------

        [Fact]
        public void AStateReachesTheNonDefaultEngineThatOwnsTheScript()
        {
            StateEngine defaultEngine = new(DefaultEngineName);
            StateEngine other = new(OtherEngineName);
            TestScene scene = SetUpScene(defaultEngine, other);
            SceneObjectGroup sog = AddObject(scene);
            TaskInventoryItem item = AddScriptRunBy(scene, sog.RootPart, other);

            string snapshot = sog.GetStateSnapshot();
            snapshot.Should().NotBeEmpty();
            sog.SetState(snapshot, scene);

            other.Taken.Select(t => t.itemID).Should().Equal(item.ItemID);
            defaultEngine.Taken.Should().BeEmpty();
        }

        [Fact]
        public void TheDefaultEngineStillGetsItsOwnStateWhenAnotherEngineIsLoaded()
        {
            StateEngine defaultEngine = new(DefaultEngineName);
            StateEngine other = new(OtherEngineName);
            // Registered the other way round so that the default engine is not first in the region's list.
            TestScene scene = SetUpScene(other, defaultEngine);
            SceneObjectGroup sog = AddObject(scene, 2);
            TaskInventoryItem ownedByDefault = AddScriptRunBy(scene, sog.Parts[0], defaultEngine);
            TaskInventoryItem ownedByOther = AddScriptRunBy(scene, sog.Parts[1], other);

            sog.SetState(sog.GetStateSnapshot(), scene);

            defaultEngine.Taken.Select(t => t.itemID).Should().Equal(ownedByDefault.ItemID);
            other.Taken.Select(t => t.itemID).Should().Equal(ownedByOther.ItemID);
        }

        [Fact]
        public void TheDefaultEngineIsOfferedEachStateBeforeTheOthers()
        {
            StateEngine defaultEngine = new(DefaultEngineName) { AcceptsAnyState = true };
            StateEngine other = new(OtherEngineName) { AcceptsAnyState = true };
            TestScene scene = SetUpScene(other, defaultEngine);
            SceneObjectGroup sog = AddObject(scene);
            AddScriptRunBy(scene, sog.RootPart, other);

            sog.SetState(sog.GetStateSnapshot(), scene);

            // Both would take it; once an engine has taken a state, no other is offered it.
            defaultEngine.Taken.Should().HaveCount(1);
            other.Taken.Should().BeEmpty();
        }

        [Fact]
        public void ARegionWithOneEngineIsUnchanged()
        {
            StateEngine only = new(DefaultEngineName);
            TestScene scene = SetUpScene(only);
            SceneObjectGroup sog = AddObject(scene);
            TaskInventoryItem item = AddScriptRunBy(scene, sog.RootPart, only);

            sog.SetState(sog.GetStateSnapshot(), scene);

            only.Taken.Select(t => t.itemID).Should().Equal(item.ItemID);
            only.Taken[0].xml.Should().Contain("Engine=\"" + DefaultEngineName + "\"");
        }

        [Fact]
        public void ALoneEngineThatIsNotTheDefaultStillGetsTheState()
        {
            StateEngine only = new(OtherEngineName);
            TestScene scene = SetUpScene(only);
            SceneObjectGroup sog = AddObject(scene);
            TaskInventoryItem item = AddScriptRunBy(scene, sog.RootPart, only);

            sog.SetState(sog.GetStateSnapshot(), scene);

            only.Taken.Select(t => t.itemID).Should().Equal(item.ItemID);
        }

        [Fact]
        public void AStateWithoutAValidItemIdDoesNotCostTheOtherScriptsTheirState()
        {
            StateEngine other = new(OtherEngineName);
            TestScene scene = SetUpScene(new StateEngine(DefaultEngineName), other);
            SceneObjectGroup sog = AddObject(scene);
            UUID good = UUID.Random();

            string xml =
                "<ScriptData><ScriptStates>" +
                "<State UUID=\"not-a-uuid\" Engine=\"" + OtherEngineName + "\"><ScriptState/></State>" +
                "<!-- a comment is no state -->" +
                "<State UUID=\"" + good + "\" Engine=\"" + OtherEngineName + "\"><ScriptState/></State>" +
                "</ScriptStates></ScriptData>";

            Action set = () => sog.SetState(xml, scene);

            set.Should().NotThrow();
            other.Taken.Select(t => t.itemID).Should().Equal(good);
        }

        [Fact]
        public void AnEngineThatThrowsIsSkippedAndTheOtherScriptsStillGetTheirState()
        {
            // The default engine is offered every state first and throws from SetXMLState each time.
            StateEngine throwing = new(DefaultEngineName) { ThrowOnSet = true };
            StateEngine other = new(OtherEngineName);
            TestScene scene = SetUpScene(throwing, other);
            SceneObjectGroup sog = AddObject(scene, 2);
            TaskInventoryItem first = AddScriptRunBy(scene, sog.Parts[0], other);
            TaskInventoryItem second = AddScriptRunBy(scene, sog.Parts[1], other);
            string snapshot = sog.GetStateSnapshot();

            Action set = () => sog.SetState(snapshot, scene);

            set.Should().NotThrow();
            throwing.Offered.Should().Be(2);
            other.Taken.Select(t => t.itemID).Should().BeEquivalentTo(new[] { first.ItemID, second.ItemID });
        }

        // ---- rez and attach: saved state in the object XML ---------------------------------------------------------

        private static string SavedState(UUID id, string engine) =>
            "<State Engine=\"" + engine + "\" UUID=\"" + id + "\"><ScriptState/></State>";

        private static void SetSavedState(SceneObjectGroup sog, params (UUID id, string content)[] states)
        {
            XmlDocument doc = new XmlDocument();
            XmlElement root = doc.CreateElement("GroupScriptStates");
            doc.AppendChild(root);
            foreach ((UUID id, string content) in states)
            {
                XmlElement e = doc.CreateElement("SavedScriptState");
                e.SetAttribute("UUID", id.ToString());
                e.InnerXml = content;
                root.AppendChild(e);
            }
            sog.LoadScriptState(doc);
        }

        [Fact]
        public void MalformedSavedStateOfOneScriptDoesNotFailTheRezOrCostTheOthersTheirState()
        {
            StateEngine defaultEngine = new(DefaultEngineName);
            StateEngine other = new(OtherEngineName);
            TestScene scene = SetUpScene(defaultEngine, other);
            SceneObjectGroup sog = AddObject(scene, 3);

            TaskInventoryItem broken = TaskInventoryHelpers.AddScript(scene.AssetService, sog.Parts[0], "broken", Source);
            TaskInventoryItem ownedByDefault = TaskInventoryHelpers.AddScript(scene.AssetService, sog.Parts[1], "a", Source);
            TaskInventoryItem ownedByOther = TaskInventoryHelpers.AddScript(scene.AssetService, sog.Parts[2], "b", Source);

            // The ids the states were saved under, as a rezzed copy of the object carries them.
            UUID savedBroken = UUID.Random(), savedDefault = UUID.Random(), savedOther = UUID.Random();
            broken.LoadedItemID = savedBroken;
            ownedByDefault.LoadedItemID = savedDefault;
            ownedByOther.LoadedItemID = savedOther;

            // Text where a State element belongs: the object XML around it is fine, the state is not.
            SetSavedState(sog,
                (savedBroken, "this is not xml"),
                (savedDefault, SavedState(savedDefault, DefaultEngineName)),
                (savedOther, SavedState(savedOther, OtherEngineName)));

            Action rez = () => sog.CreateScriptInstances(0, false, DefaultEngineName, 0);

            rez.Should().NotThrow();
            defaultEngine.Taken.Select(t => t.itemID).Should().Equal(ownedByDefault.ItemID);
            other.Taken.Select(t => t.itemID).Should().Equal(ownedByOther.ItemID);
        }

        [Fact]
        public void MalformedSavedStateStartsThatScriptFresh()
        {
            StateEngine only = new(DefaultEngineName);
            TestScene scene = SetUpScene(only);
            SceneObjectGroup sog = AddObject(scene);
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(scene.AssetService, sog.RootPart, "broken", Source);
            UUID saved = UUID.Random();
            item.LoadedItemID = saved;
            SetSavedState(sog, (saved, "this is not xml"));

            bool started = sog.RootPart.Inventory.CreateScriptInstance(item, 0, false, DefaultEngineName, 0);

            started.Should().BeTrue();
            only.Taken.Should().BeEmpty();
        }

        // ---- stand-in engine ---------------------------------------------------------------------------------------

        /// <summary>
        /// A script engine that reports a state for the scripts it runs and takes only a state tagged with its own name,
        /// as YEngine does (it refuses any other before it writes anything).
        /// </summary>
        private sealed class StateEngine : IScriptModule
        {
            private readonly string m_name;
            private readonly HashSet<UUID> m_running = new();

            public StateEngine(string name) { m_name = name; }

            /// <summary>Take any state offered, whoever it is tagged for.</summary>
            public bool AcceptsAnyState { get; init; }

            /// <summary>Throw from SetXMLState, as an engine with a fault of its own would.</summary>
            public bool ThrowOnSet { get; init; }

            /// <summary>How many times SetXMLState was called.</summary>
            public int Offered { get; private set; }

            /// <summary>The states this engine took: the item id they were given for, and the XML.</summary>
            public List<(UUID itemID, string xml)> Taken { get; } = new();

            public void Runs(UUID itemID) => m_running.Add(itemID);

            public string Name => m_name;
            public string ScriptEngineName => m_name;
            public Type ReplaceableInterface => null;
            public void Initialise(Nini.Config.IConfigSource source) { }
            public void Close() { }
            public void AddRegion(Scene scene) { scene.StackModuleInterface<IScriptModule>(this); }
            public void RemoveRegion(Scene scene) { }
            public void RegionLoaded(Scene scene) { }

            public string GetXMLState(UUID itemID) =>
                m_running.Contains(itemID)
                    ? "<State Engine=\"" + m_name + "\" UUID=\"" + itemID + "\"><ScriptState/></State>"
                    : string.Empty;

            public bool SetXMLState(UUID itemID, string xml)
            {
                Offered++;
                if (ThrowOnSet)
                    throw new InvalidOperationException("engine fault");
                XmlDocument doc = new XmlDocument();
                try { doc.LoadXml(xml); }
                catch (XmlException) { return false; }
                if (!AcceptsAnyState && doc.DocumentElement?.GetAttribute("Engine") != m_name)
                    return false;
                Taken.Add((itemID, xml));
                return true;
            }

            public event ScriptRemoved OnScriptRemoved { add { } remove { } }
            public event ObjectRemoved OnObjectRemoved { add { } remove { } }
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
    }
}
