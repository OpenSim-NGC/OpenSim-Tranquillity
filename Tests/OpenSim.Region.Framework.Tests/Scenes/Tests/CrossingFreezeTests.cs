/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An object that crosses a region border is frozen before anything about it is read for the other region: its
/// scripts, the scripts of the attachments of the avatars that cross with it (seated or standing), and the avatars'
/// own input. Whatever is read after that is the final state, and an object that stays gives all of it back. Two
/// regions run in one process, each with a script engine that records what it is asked.
/// </summary>
public class CrossingFreezeTests : OpenSimTestCase
{
    private static readonly Vector3 Start = new(128, 10, 2);

    // the object's origin leaves the region by a tad
    private static readonly Vector3 Target = new(128, -0.05f, 2);

    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    private static readonly UUID OwnerId = TestHelpers.ParseTail(0x1);

    private TestScene m_sceneA;
    private TestScene m_sceneB;
    private RecordingEngine m_engineA;
    private RecordingEngine m_engineB;
    private SceneObjectGroup m_platform;

    public CrossingFreezeTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void SetupTwoRegions(int parts = 2, bool banOwnerInDestination = false, uint regionX = 1000, uint regionY = 1000)
    {
        EntityTransferModule etmA = new();
        EntityTransferModule etmB = new();
        LocalSimulationConnectorModule lscm = new();
        m_engineA = new RecordingEngine();
        m_engineB = new RecordingEngine();

        IConfigSource config = new IniConfigSource();
        IConfig modulesConfig = config.AddConfig("Modules");
        modulesConfig.Set("EntityTransferModule", etmA.Name);
        modulesConfig.Set("SimulationServices", lscm.Name);
        IConfig entityTransferConfig = config.AddConfig("EntityTransfer");
        entityTransferConfig.Set("wait_for_callback", false);

        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), regionX, regionY);
        m_sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), regionX, regionY - 1);
        m_sceneA.EnableStandingRiderCrossing = true;
        m_sceneB.EnableStandingRiderCrossing = true;

        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(m_sceneA, config, new CapabilitiesModule(), etmA, m_engineA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, new CapabilitiesModule(), etmB, m_engineB);

        if (banOwnerInDestination)
            m_sceneB.RegionInfo.EstateSettings.AddBan(new EstateBan { BannedUserID = OwnerId });

        m_platform = SceneHelpers.AddSceneObject(m_sceneA, parts, OwnerId, "", 0x2);
        m_platform.RootPart.Scale = new Vector3(8f, 8f, 0.5f);
        m_platform.AbsolutePosition = Start;
    }

    private static TaskInventoryItem AddScript(Scene scene, SceneObjectPart part, RecordingEngine engine)
    {
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(scene.AssetService, part, "script", "default { state_entry() { } }");
        engine.Runs(item.ItemID);
        return item;
    }

    private ScenePresence AddAvatar(int userTail, Vector3 position)
    {
        UUID userId = TestHelpers.ParseTail(userTail);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        acd.circuitcode = (uint)(1000 + userTail);
        TestClient tc = new(acd, m_sceneA);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, new List<TestClient>());
        ScenePresence sp = SceneHelpers.AddScenePresence(m_sceneA, tc, acd);
        sp.AbsolutePosition = position;
        return sp;
    }

    /// <summary>The avatar wears an object with one script, which the engine of the avatar's region runs.</summary>
    private TaskInventoryItem Wear(ScenePresence sp)
    {
        SceneObjectGroup worn = SceneHelpers.CreateSceneObject(1, sp.UUID, "worn", 0x50);
        TaskInventoryItem item = AddScript(m_sceneA, worn.RootPart, m_engineA);
        sp.AddAttachment(worn);
        return item;
    }

    private ScenePresence AddStandingRider(int userTail, Vector3 offset)
    {
        ScenePresence sp = AddAvatar(userTail, Start + offset);
        sp.RideOn.Update(0, m_platform.UUID, true);
        sp.RideOn.Update(RideOnLimits.StartMs, m_platform.UUID, true);
        Assert.True(sp.RideOn.IsLinked);
        return sp;
    }

    private ScenePresence AddSeatedRider(int userTail)
    {
        ScenePresence sp = AddAvatar(userTail, Start);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, m_platform.UUID, Vector3.Zero);
        Assert.True(sp.IsSatOnObject);
        return sp;
    }

    private void Cross(ScenePresence rider = null, Vector3? carriedAt = null)
    {
        m_sceneA.Update(4);
        m_sceneB.Update(4);
        // reads the scenes make on their own (backups) before the crossing do not count
        m_engineA.Watching = true;
        m_engineB.Watching = true;
        // the engine carries a standing avatar along with what it stands on: do the same here
        if (carriedAt is not null)
            rider.AbsolutePosition = Target + carriedAt.Value;
        if (rider is null)
            m_platform.AbsolutePosition = Target;
        else
        {
            m_sceneA.SceneGraph.UpdatePrimGroupPosition(m_platform.LocalId, Target, rider.ControllingClient);
        }
        m_sceneA.Update(4);
        m_sceneB.Update(4);
    }

    // ---- the object's own scripts ----------------------------------------------------------------------------------

    [Fact]
    public void The_scripts_of_every_part_are_suspended_before_their_state_is_read_for_the_other_region()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(parts: 2);
        TaskInventoryItem s1 = AddScript(m_sceneA, m_platform.Parts[0], m_engineA);
        TaskInventoryItem s2 = AddScript(m_sceneA, m_platform.Parts[1], m_engineA);

        Cross();

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Empty(m_engineA.ReadWhileRunning);
        Assert.Equal(new[] { s1.ItemID, s2.ItemID }.OrderBy(i => i), m_engineA.Suspended.OrderBy(i => i));

        // what the other region was given is what the frozen scripts reported, nothing newer
        // (the state is re-serialised on its way, so the report number is what identifies it)
        Assert.Equal(2, m_engineB.Taken.Count);
        foreach (string xml in m_engineB.Taken)
        {
            string number = System.Text.RegularExpressions.Regex.Match(xml, "Seq=\"(\\d+)\"").Groups[1].Value;
            Assert.Contains(m_engineA.Reported, reported => reported.Contains($"Seq=\"{number}\""));
        }

        // the old region's copies stay frozen until they are removed: nothing resumed them
        Assert.Empty(m_engineA.Resumed);
    }

    [Fact]
    public void An_object_that_stays_because_the_other_region_refuses_it_gets_its_scripts_back()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(parts: 2, banOwnerInDestination: true);
        TaskInventoryItem s1 = AddScript(m_sceneA, m_platform.Parts[0], m_engineA);
        TaskInventoryItem s2 = AddScript(m_sceneA, m_platform.Parts[1], m_engineA);

        Cross();

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.Equal(new[] { s1.ItemID, s2.ItemID }.OrderBy(i => i), m_engineA.Suspended.OrderBy(i => i));
        Assert.Equal(m_engineA.Suspended.OrderBy(i => i), m_engineA.Resumed.OrderBy(i => i));
        Assert.Empty(m_engineA.StillSuspended);
    }

    [Fact]
    public void An_object_with_nowhere_to_go_is_never_frozen()
    {
        TestHelpers.InMethod();
        // coordinates no other test uses, so that no region of another test (the grid data is shared) lies to the east
        SetupTwoRegions(parts: 1, regionX: 4500, regionY: 4500);
        AddScript(m_sceneA, m_platform.RootPart, m_engineA);

        // out of the east edge, where no region lies
        m_sceneA.Update(4);
        m_platform.AbsolutePosition = new Vector3(256.05f, 128, 2);
        m_sceneA.Update(4);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Empty(m_engineA.Suspended);
    }

    // ---- standing riders -------------------------------------------------------------------------------------------

    [Fact]
    public void A_standing_rider_is_frozen_with_its_attachment_scripts_before_anything_is_read()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(parts: 1);
        AddScript(m_sceneA, m_platform.RootPart, m_engineA);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        TaskInventoryItem worn = Wear(rider);

        bool? inTransit = null, off = null, wornSuspended = null, inputIgnored = null;
        m_engineA.OnFirstRead = () =>
        {
            inTransit = rider.IsInTransit;
            off = rider.PhysicsActor is null;
            wornSuspended = m_engineA.Suspended.Contains(worn.ItemID);

            AgentUpdateArgs move = new()
            {
                BodyRotation = Quaternion.Identity,
                ControlFlags = (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS
            };
            rider.HandleAgentUpdate(rider.ControllingClient, move);
            inputIgnored = (rider.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) == 0;
        };

        Cross(rider, StandingOffset);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(inTransit, "the rider was not in transit when the state was read");
        Assert.True(off, "the rider was still in the physics scene when the state was read");
        Assert.True(wornSuspended, "the rider's attachment script was still running when the state was read");
        Assert.True(inputIgnored, "the rider's input still counted when the state was read");
    }

    [Fact]
    public void A_standing_rider_left_in_the_old_region_gets_its_attachment_scripts_back()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(parts: 1, banOwnerInDestination: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        TaskInventoryItem worn = Wear(rider);

        Cross(rider, StandingOffset);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Contains(worn.ItemID, m_engineA.Suspended);
        Assert.Contains(worn.ItemID, m_engineA.Resumed);
        Assert.DoesNotContain(worn.ItemID, m_engineA.StillSuspended);
        Assert.False(rider.IsInTransit);
        Assert.False(rider.IsChildAgent);
        Assert.NotNull(rider.PhysicsActor);
    }

    // ---- seated riders get the same freeze -------------------------------------------------------------------------

    [Fact]
    public void A_seated_rider_is_frozen_with_its_attachment_scripts_before_anything_is_read()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(parts: 1);
        AddScript(m_sceneA, m_platform.RootPart, m_engineA);
        ScenePresence sitter = AddSeatedRider(0x11);
        TaskInventoryItem worn = Wear(sitter);

        bool? inTransit = null, wornSuspended = null, inputIgnored = null;
        m_engineA.OnFirstRead = () =>
        {
            inTransit = sitter.IsInTransit;
            wornSuspended = m_engineA.Suspended.Contains(worn.ItemID);

            AgentUpdateArgs move = new()
            {
                BodyRotation = Quaternion.Identity,
                ControlFlags = (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS
            };
            sitter.HandleAgentUpdate(sitter.ControllingClient, move);
            inputIgnored = (sitter.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) == 0;
        };

        Cross(sitter);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(inTransit, "the seated rider was not in transit when the state was read");
        Assert.True(wornSuspended, "the seated rider's attachment script was still running when the state was read");
        Assert.True(inputIgnored, "the seated rider's input still counted when the state was read");
    }

    [Fact]
    public void A_seated_rider_of_an_object_that_stays_is_thawed_and_still_sitting()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(parts: 1, banOwnerInDestination: true);
        ScenePresence sitter = AddSeatedRider(0x11);
        TaskInventoryItem worn = Wear(sitter);

        Cross(sitter);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Contains(worn.ItemID, m_engineA.Suspended);
        Assert.DoesNotContain(worn.ItemID, m_engineA.StillSuspended);
        Assert.False(sitter.IsInTransit);
        Assert.False(sitter.IsChildAgent);
        Assert.True(sitter.IsSatOnObject);
    }

    // ---- stand-in engine -------------------------------------------------------------------------------------------

    /// <summary>
    /// A script engine that runs the scripts it is told about: it reports a state for each (numbered, so the
    /// receiving region's copy can be traced to the report it came from), and notes every suspend, resume and state
    /// read, and whether a state was read from a script that was not suspended.
    /// </summary>
    private sealed class RecordingEngine : IScriptModule
    {
        private readonly HashSet<UUID> m_running = new();
        private readonly HashSet<UUID> m_suspended = new();
        private int m_sequence;

        /// <summary>Called once, the first time any state is read.</summary>
        public Action OnFirstRead { get; set; }

        /// <summary>Reads are only noted from the moment the test arms this.</summary>
        public bool Watching { get; set; }

        /// <summary>Every item suspended, in order.</summary>
        public List<UUID> Suspended { get; } = new();

        /// <summary>Every item resumed, in order.</summary>
        public List<UUID> Resumed { get; } = new();

        /// <summary>The items suspended now.</summary>
        public IEnumerable<UUID> StillSuspended => m_suspended.ToArray();

        /// <summary>The states this engine reported.</summary>
        public List<string> Reported { get; } = new();

        /// <summary>The items whose state was read while the script was not suspended.</summary>
        public List<UUID> ReadWhileRunning { get; } = new();

        /// <summary>The states this engine was given.</summary>
        public List<string> Taken { get; } = new();

        public void Runs(UUID itemID) => m_running.Add(itemID);

        public string Name => "RecordingEngine";
        public string ScriptEngineName => "RecordingEngine";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene) { scene.StackModuleInterface<IScriptModule>(this); }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        public string GetXMLState(UUID itemID)
        {
            if (!m_running.Contains(itemID))
                return string.Empty;

            if (!Watching)
                return $"<State Engine=\"RecordingEngine\" UUID=\"{itemID}\"><ScriptState Seq=\"0\"/></State>";

            if (OnFirstRead is not null)
            {
                Action first = OnFirstRead;
                OnFirstRead = null;
                first();
            }

            if (!m_suspended.Contains(itemID))
                ReadWhileRunning.Add(itemID);

            string xml = $"<State Engine=\"RecordingEngine\" UUID=\"{itemID}\"><ScriptState Seq=\"{++m_sequence}\"/></State>";
            Reported.Add(xml);
            return xml;
        }

        public bool SetXMLState(UUID itemID, string xml)
        {
            Taken.Add(xml);
            return true;
        }

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
