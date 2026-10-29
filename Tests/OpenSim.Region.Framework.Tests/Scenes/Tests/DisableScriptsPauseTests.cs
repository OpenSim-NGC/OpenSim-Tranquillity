/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// "Disable scripts" is a pause: the scene asks every engine, through IScriptModule, to suspend each script and
/// removes none; allowing scripts again resumes the scripts that were suspended and starts no second copy of them.
/// The engine here records what it is asked. No process-wide state: classes using it run in parallel.
/// </summary>
public class DisableScriptsPauseTests : OpenSimTestCase
{
    private TestScene m_scene;
    private PausingEngine m_engine;

    private void Setup()
    {
        m_engine = new PausingEngine();
        SceneHelpers sh = new();
        m_scene = sh.SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, new IniConfigSource(), m_engine);
    }

    private TaskInventoryItem AddScript(SceneObjectGroup group, int part = 0)
    {
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(m_scene.AssetService, group.Parts[part], "script", "default { state_entry() { } }");
        m_engine.Runs(item.ItemID);
        return item;
    }

    [Fact]
    public void DisablingSuspendsEveryScriptAndRemovesNone()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup a = SceneHelpers.AddSceneObject(m_scene, 2, TestHelpers.ParseTail(1), "a", 0x10);
        SceneObjectGroup b = SceneHelpers.AddSceneObject(m_scene, 1, TestHelpers.ParseTail(1), "b", 0x20);
        TaskInventoryItem s1 = AddScript(a, 0);
        TaskInventoryItem s2 = AddScript(a, 1);
        TaskInventoryItem s3 = AddScript(b);

        m_scene.ScriptsEnabled = false;

        Assert.Equal(new[] { s1.ItemID, s2.ItemID, s3.ItemID }.OrderBy(i => i), m_engine.StillSuspended.OrderBy(i => i));
        Assert.Empty(m_engine.Removed);
        Assert.True(m_engine.HasScript(s1.ItemID, out _));
        Assert.True(m_engine.HasScript(s2.ItemID, out _));
        Assert.True(m_engine.HasScript(s3.ItemID, out _));
    }

    [Fact]
    public void AllowingAgainResumesTheSuspendedScriptsAndStartsNoSecondCopy()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup a = SceneHelpers.AddSceneObject(m_scene, 1, TestHelpers.ParseTail(1), "a", 0x10);
        TaskInventoryItem s1 = AddScript(a);
        m_scene.ScriptsEnabled = false;

        m_scene.ScriptsEnabled = true;

        Assert.Empty(m_engine.StillSuspended);
        Assert.Equal(new[] { s1.ItemID }, m_engine.Resumed.Distinct());
        Assert.Empty(m_engine.Started);
        Assert.Empty(m_engine.Removed);
    }

    [Fact]
    public void ASwitchSetToTheValueItHasAsksNoEngineAnything()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup a = SceneHelpers.AddSceneObject(m_scene, 1, TestHelpers.ParseTail(1), "a", 0x10);
        AddScript(a);

        m_scene.ScriptsEnabled = true;

        Assert.Empty(m_engine.Suspended);
        Assert.Empty(m_engine.Resumed);
        Assert.Empty(m_engine.Started);
    }

    [Fact]
    public void AScriptSuspendedForAnotherReasonIsNotResumedByAllowingScripts()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup a = SceneHelpers.AddSceneObject(m_scene, 2, TestHelpers.ParseTail(1), "a", 0x10);
        TaskInventoryItem held = AddScript(a, 0);
        TaskInventoryItem other = AddScript(a, 1);
        // e.g. held by a border crossing: the engine already has it suspended when scripts are disabled
        m_engine.SuspendScript(held.ItemID);
        m_engine.Suspended.Clear();

        m_scene.ScriptsEnabled = false;
        m_scene.ScriptsEnabled = true;

        Assert.Equal(new[] { held.ItemID }, m_engine.StillSuspended);
        Assert.Equal(new[] { other.ItemID }, m_engine.Resumed.Distinct());
    }

    [Fact]
    public void AScriptNoEngineHoldsIsStartedWhenScriptsAreAllowedAgain()
    {
        TestHelpers.InMethod();
        Setup();
        SceneObjectGroup a = SceneHelpers.AddSceneObject(m_scene, 1, TestHelpers.ParseTail(1), "a", 0x10);
        m_scene.ScriptsEnabled = false;
        // it came into the region while scripts were off, so no engine was ever given it
        TaskInventoryItem late = TaskInventoryHelpers.AddScript(m_scene.AssetService, a.RootPart, "late", "default { state_entry() { } }");

        m_scene.ScriptsEnabled = true;

        Assert.Equal(new[] { late.ItemID }, m_engine.Started);
    }

    [Fact]
    public void TheViewersDebugRequestPausesAndResumesTheScripts()
    {
        TestHelpers.InMethod();
        Setup();
        EstateManagementModule estate = new();
        SceneHelpers.SetupSceneModules(m_scene, new IniConfigSource(), estate);
        SceneObjectGroup a = SceneHelpers.AddSceneObject(m_scene, 1, TestHelpers.ParseTail(1), "a", 0x10);
        TaskInventoryItem s1 = AddScript(a);
        UUID agent = TestHelpers.ParseTail(2);
        UUID invoice = UUID.Random();

        DebugRequest(estate, invoice, agent, true);

        Assert.True(m_scene.RegionInfo.RegionSettings.DisableScripts);
        Assert.False(m_scene.ScriptsEnabled);
        Assert.Equal(new[] { s1.ItemID }, m_engine.StillSuspended);
        Assert.Empty(m_engine.Removed);

        DebugRequest(estate, invoice, agent, false);

        Assert.False(m_scene.RegionInfo.RegionSettings.DisableScripts);
        Assert.True(m_scene.ScriptsEnabled);
        Assert.Empty(m_engine.StillSuspended);
        Assert.Empty(m_engine.Started);
    }

    // the handler the viewer's Region/Estate debug request reaches, which is private
    private static void DebugRequest(EstateManagementModule estate, UUID invoice, UUID agent, bool disableScripts)
        => typeof(EstateManagementModule).GetMethod("HandleEstateDebugRegionRequest", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(estate, new object[] { null, invoice, agent, disableScripts, false, false });

    /// <summary>An engine that keeps every script it is given, suspended or not, and records what it is asked.</summary>
    private sealed class PausingEngine : IScriptModule
    {
        private readonly HashSet<UUID> m_running = new();
        private readonly HashSet<UUID> m_suspended = new();

        public List<UUID> Suspended { get; } = new();
        public List<UUID> Resumed { get; } = new();
        public List<UUID> Removed { get; } = new();
        public List<UUID> Started { get; } = new();
        public IEnumerable<UUID> StillSuspended => m_suspended.ToArray();

        public void Runs(UUID itemID) => m_running.Add(itemID);

        public string Name => "PausingEngine";
        public string ScriptEngineName => "PausingEngine";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene)
        {
            scene.StackModuleInterface<IScriptModule>(this);
            scene.EventManager.OnRezScript += OnRezScript;
            scene.EventManager.OnRemoveScript += (localID, itemID) => { Removed.Add(itemID); m_running.Remove(itemID); };
        }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        private void OnRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
        {
            Started.Add(itemID);
            m_running.Add(itemID);
        }

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => true;

        public bool SuspendScript(UUID itemID)
        {
            if (!m_running.Contains(itemID))
                return false;
            m_suspended.Add(itemID);
            Suspended.Add(itemID);
            return true;
        }

        public bool ResumeScript(UUID itemID)
        {
            if (!m_running.Contains(itemID))
                return false;
            m_suspended.Remove(itemID);
            Resumed.Add(itemID);
            return true;
        }

        public event ScriptRemoved OnScriptRemoved { add { } remove { } }
        public event ObjectRemoved OnObjectRemoved { add { } remove { } }
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool HasScript(UUID itemID, out bool running) { running = m_running.Contains(itemID); return running; }
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
