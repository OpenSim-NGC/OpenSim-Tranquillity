/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Object and region queries. Script memory from the VM's MemInfo (Halcyon EngineInterface.GetFreeMemory /
/// GetUsedMemory); llGetScriptState from the engine (Halcyon EngineInterface.GetScriptState). llGetObjectDetails' script
/// totals (Halcyon GetPartScriptTotal), the seven flags SL lists past OBJECT_TEMP_ON_REZ, OBJECT_UNKNOWN_DETAIL (-1) for
/// an unknown flag (SL wiki llGetObjectDetails), a seated avatar's OBJECT_ROOT and the part's costs (YEngine).
/// llRequestSimulatorData's SL values for an unknown region; llGetEnv's keys; llGetAgentList's NULL_KEY for an agent
/// in god mode (SL wiki llGetAgentList); iwGetAgentList's details and box (Halcyon :14360-14454). llDie in an
/// attachment and after llDie; rez errors; iwRezPrim; iwLinkStandTarget saving.
/// </summary>
// No test reaches a network service. No process-wide state: the class runs in parallel.
public class ObjectRegionQueryTests
{
    private const int OBJECT_RUNNING_SCRIPT_COUNT = 9, OBJECT_TOTAL_SCRIPT_COUNT = 10, OBJECT_SCRIPT_MEMORY = 11,
        OBJECT_SCRIPT_TIME = 12, OBJECT_STREAMING_COST = 15, OBJECT_PHYSICS_COST = 16, OBJECT_CHARACTER_TIME = 17,
        OBJECT_ROOT = 18, OBJECT_PATHFINDING_TYPE = 20, OBJECT_RENDER_WEIGHT = 24, OBJECT_HOVER_HEIGHT = 25,
        OBJECT_BODY_SHAPE_TYPE = 26, OBJECT_LAST_OWNER_ID = 27, OBJECT_CLICK_ACTION = 28,
        IW_OBJECT_SCRIPT_MEMORY_USED = 10001, OBJECT_NAME = 1, OBJECT_POS = 3;
    private const int AGENT_LIST_REGION = 4;

    // ---------------------------------------------------------------- memory, script state

    [Fact]
    public void MemoryComesFromTheVm()
    {
        using var r = new ApiCallRig(source: "list big; default { state_entry() { integer i; for (i = 0; i < 200; ++i) big += [\"0123456789\"]; } }");
        int used = r.State.MemInfo.MemoryUsed;
        Assert.True(used > 0);
        Assert.Equal(used, r.Api.llGetUsedMemory());
        Assert.Equal(MemoryInfo.MAX_MEMORY - used, r.Api.llGetFreeMemory());
    }

    [Fact]
    public void ScriptStateFollowsLlSetScriptState()
    {
        using var r = new ApiCallRig();
        UUID other = r.H.RezScript("default { state_entry() { } }");
        string name = r.H.Prim.Inventory.GetInventoryItem(other).Name;
        Assert.True(r.H.PumpUntil(() => r.H.RunStateOf(other) == "Waiting", TimeSpan.FromSeconds(30)));
        Assert.Equal(1, r.Api.llGetScriptState(name));

        r.Api.llSetScriptState(name, 0);
        Assert.True(r.H.PumpUntil(() => !r.H.Engine.GetScriptState(other), TimeSpan.FromSeconds(10)));
        Assert.Equal(0, r.Api.llGetScriptState(name));
        Assert.False(r.H.Prim.Inventory.GetInventoryItem(other).ScriptRunning);   // the Running flag is saved too
    }

    // ---------------------------------------------------------------- llGetObjectDetails

    [Fact]
    public void ScriptTotalsCountRunningScriptsAndMemory()
    {
        using var r = new ApiCallRig();
        UUID stopped = r.H.RezScript("default { state_entry() { } }");
        Assert.True(r.H.PumpUntil(() => r.H.RunStateOf(stopped) == "Waiting", TimeSpan.FromSeconds(30)));
        r.Api.llSetScriptState(r.H.Prim.Inventory.GetInventoryItem(stopped).Name, 0);
        Assert.True(r.H.PumpUntil(() => !r.H.Engine.GetScriptState(stopped), TimeSpan.FromSeconds(10)));

        var d = r.Api.llGetObjectDetails(r.H.Prim.UUID.ToString(), ApiCallRig.L(OBJECT_RUNNING_SCRIPT_COUNT,
            OBJECT_TOTAL_SCRIPT_COUNT, OBJECT_SCRIPT_MEMORY, IW_OBJECT_SCRIPT_MEMORY_USED, OBJECT_SCRIPT_TIME));
        Assert.Equal(1, d.Data[0]);
        Assert.Equal(2, d.Data[1]);
        Assert.Equal(2 * MemoryInfo.MAX_MEMORY, d.Data[2]);
        Assert.True((int)d.Data[3] > 0);
        Assert.IsType<float>(d.Data[4]);
    }

    [Fact]
    public void AnAvatarsScriptTotalsAreItsAttachments()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        SceneObjectGroup g = r.H.Prim.ParentGroup;
        g.AttachedAvatar = sp.UUID;
        g.IsAttachment = true;
        sp.AddAttachment(g);
        var d = r.Api.llGetObjectDetails(sp.UUID.ToString(), ApiCallRig.L(OBJECT_RUNNING_SCRIPT_COUNT, OBJECT_TOTAL_SCRIPT_COUNT));
        Assert.Equal(1, d.Data[0]);
        Assert.Equal(1, d.Data[1]);
    }

    [Fact]
    public void TheSevenLaterFlagsAndAnUnknownOneOnAnObject()
    {
        using var r = new ApiCallRig();
        UUID last = UUID.Random();
        r.H.Prim.ParentGroup.LastOwnerID = last;
        r.H.Prim.ClickAction = 3;
        var d = r.Api.llGetObjectDetails(r.H.Prim.UUID.ToString(), ApiCallRig.L(OBJECT_CHARACTER_TIME,
            OBJECT_PATHFINDING_TYPE, OBJECT_RENDER_WEIGHT, OBJECT_HOVER_HEIGHT, OBJECT_BODY_SHAPE_TYPE,
            OBJECT_LAST_OWNER_ID, OBJECT_CLICK_ACTION, 9999));
        Assert.Equal(new object[] { 0f, 0, 0, 0f, -1f, last.ToString(), 3, -1 }, d.Data);
    }

    [Fact]
    public void TheSevenLaterFlagsAndAnUnknownOneOnAnAvatar()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        var d = r.Api.llGetObjectDetails(sp.UUID.ToString(), ApiCallRig.L(OBJECT_CHARACTER_TIME,
            OBJECT_PATHFINDING_TYPE, OBJECT_RENDER_WEIGHT, OBJECT_HOVER_HEIGHT, OBJECT_LAST_OWNER_ID,
            OBJECT_CLICK_ACTION, 9999));
        Assert.Equal(new object[] { 0f, 1, -1, 0f, UUID.Zero.ToString(), 0, -1 }, d.Data);
        Assert.IsType<float>(r.Api.llGetObjectDetails(sp.UUID.ToString(), ApiCallRig.L(OBJECT_BODY_SHAPE_TYPE)).Data[0]);
    }

    [Fact]
    public void ASeatedAvatarsRootIsTheSeat()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        SceneObjectGroup seat = SceneHelpers.AddSceneObject(r.H.Scene, 2, UUID.Random(), "seat", 0x30);
        sp.ParentPart = seat.Parts.First(p => p != seat.RootPart);
        sp.ParentID = sp.ParentPart.LocalId;
        var d = r.Api.llGetObjectDetails(sp.UUID.ToString(), ApiCallRig.L(OBJECT_ROOT));
        Assert.Equal(seat.RootPart.UUID.ToString(), d.Data[0]);
    }

    [Fact]
    public void CostsAreThePartsCosts()
    {
        using var r = new ApiCallRig();
        var d = r.Api.llGetObjectDetails(r.H.Prim.UUID.ToString(), ApiCallRig.L(OBJECT_STREAMING_COST, OBJECT_PHYSICS_COST));
        Assert.Equal(r.H.Prim.StreamingCost, d.Data[0]);
        Assert.Equal(r.H.Prim.PhysicsCost, d.Data[1]);
        Assert.NotEqual(0f, (float)d.Data[0] + (float)d.Data[1]);
    }

    // ---------------------------------------------------------------- llRequestSimulatorData, llGetEnv

    [Theory]
    [InlineData("DATA_SIM_STATUS", "unknown")]
    [InlineData("DATA_SIM_RATING", "UNKNOWN")]
    public void AnUnknownRegionAnswersSlsValues(string code, string expected)
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { llRequestSimulatorData(\"No Such Region\", " + code + "); }"
            + " dataserver(key q, string d) { llSay(0, \"sim=\" + d); } }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("sim=")), TimeSpan.FromSeconds(30)));
        Assert.Contains("sim=" + expected, h.Said);
    }

    [Fact]
    public void GetEnvKeysAreCaseInsensitiveAndReal()
    {
        using var r = new ApiCallRig();
        Scene s = r.H.Scene;
        int started = r.Api.llGetUnixTime() - 600;
        typeof(Scene).GetField("m_unixStartTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(s, started);
        Assert.Equal(started.ToString(), r.Api.llGetEnv("REGION_START_TIME"));
        Assert.Equal(started.ToString(), r.Api.llGetEnv("region_start_time"));
        Assert.Equal(s.Frame.ToString(), r.Api.llGetEnv("frame_number"));   // the test scene runs no frames
        Assert.Equal("Phlox", r.Api.llGetEnv("script_engine"));
        Assert.Equal(s.RegionInfo.RegionSizeX.ToString(), r.Api.llGetEnv("region_size_x"));
        Assert.Equal(s.RegionInfo.RegionSizeY.ToString(), r.Api.llGetEnv("region_size_y"));
        Assert.Equal("", r.Api.llGetEnv("inworldz"));
        Assert.Equal("", r.Api.llGetEnv("halcyon"));
        Assert.Equal("20", r.Api.llGetEnv("chat_range"));
        Assert.InRange(int.Parse(r.Api.llGetEnv("region_up_time")), 598, 700);
    }

    // ---------------------------------------------------------------- agent lists

    [Fact]
    public void AnAgentInGodModeIsANullKeyInLlGetAgentList()
    {
        using var r = new ApiCallRig();
        ScenePresence god = r.AddAvatar();
        ScenePresence plain = r.AddAvatar();
        god.IsViewerUIGod = true;
        var list = r.Api.llGetAgentList(AGENT_LIST_REGION, ApiCallRig.L()).Data.Select(o => o.ToString()).ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(UUID.Zero.ToString(), list);
        Assert.Contains(plain.UUID.ToString(), list);
        Assert.DoesNotContain(god.UUID.ToString(), list);
        // iwGetAgentList keeps hiding gods.
        var iw = r.Api.iwGetAgentList(AGENT_LIST_REGION, Vector3.Zero, Vector3.Zero, ApiCallRig.L()).Data.Select(o => o.ToString()).ToList();
        Assert.Equal(new[] { plain.UUID.ToString() }, iw);
    }

    [Fact]
    public void IwGetAgentListReturnsTheRequestedDetails()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        var d = r.Api.iwGetAgentList(AGENT_LIST_REGION, Vector3.Zero, Vector3.Zero, ApiCallRig.L(OBJECT_NAME, OBJECT_POS));
        Assert.Equal(2, d.Length);
        Assert.Equal(sp.Name, d.Data[0]);
        Assert.Equal(sp.AbsolutePosition, d.Data[1]);
    }

    [Fact]
    public void IwGetAgentListBoxNeedsBothCornersTakesEitherOrderAndWildcardsAZeroAxis()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        sp.AbsolutePosition = new Vector3(100, 100, 30);
        string id = sp.UUID.ToString();
        int Count(Vector3 a, Vector3 b) => r.Api.iwGetAgentList(AGENT_LIST_REGION, a, b, ApiCallRig.L()).Data.Count(o => o.ToString() == id);

        Assert.Equal(1, Count(Vector3.Zero, new Vector3(50, 50, 50)));                     // one corner zero: no box
        Assert.Equal(1, Count(new Vector3(120, 120, 40), new Vector3(90, 90, 20)));        // corners swapped
        Assert.Equal(1, Count(new Vector3(90, 90, 0), new Vector3(120, 120, 0)));          // z 0/0: any height
        Assert.Equal(0, Count(new Vector3(1, 1, 1), new Vector3(50, 50, 50)));
    }

    // ---------------------------------------------------------------- llDie

    [Fact]
    public void LlDieInAnAttachmentDoesNothing()
    {
        using var h = new SchedulerHarness();
        SceneObjectGroup g = h.Prim.ParentGroup;
        g.IsAttachment = true;
        g.AttachedAvatar = UUID.Random();
        h.RezScript("default { state_entry() { llDie(); llSay(0, \"still here\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("still here"), TimeSpan.FromSeconds(30)));
        Assert.False(g.IsDeleted);
    }

    [Fact]
    public void NothingRunsAfterLlDie()
    {
        using var h = new SchedulerHarness();
        UUID item = h.RezScript("default { state_entry() { llSay(0, \"before\"); llDie(); llSay(0, \"after\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("before"), TimeSpan.FromSeconds(30)));
        Assert.True(h.PumpUntil(() => h.Prim.ParentGroup.IsDeleted, TimeSpan.FromSeconds(10)));
        h.Pump(50);
        Assert.DoesNotContain("after", h.Said);
    }

    // ---------------------------------------------------------------- rez errors (Halcyon iwRezAt :3155-3245), iwRezPrim

    [Fact]
    public void AMissingItemShoutsAndSleeps()
    {
        using var r = new ApiCallRig();
        var (ms, ret) = r.Accounted(api => api.iwRezAt("nothing", 0, r.H.Prim.AbsolutePosition, Vector3.Zero, Quaternion.Identity, 0));
        Assert.Equal(UUID.Zero.ToString(), ret);
        Assert.Equal(100, ms);
        Assert.Contains(r.Errors, e => e.Contains("Unable to create requested object. Inventory item 'nothing' not found."));
    }

    [Fact]
    public void AnItemThatIsNotAnObjectHasItsOwnTextAndNoSleep()
    {
        using var r = new ApiCallRig();
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = UUID.Random(), Name = "a note",
            Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard,
        }, false);
        var (ms, _) = r.Accounted(api => api.iwRezAt("a note", 0, r.H.Prim.AbsolutePosition, Vector3.Zero, Quaternion.Identity, 0));
        Assert.Equal(0, ms);
        Assert.Contains(r.Errors, e => e.Contains("Inventory item 'a note' is something other than an object."));
    }

    [Fact]
    public void TooFarHasNoSleep()
    {
        using var r = new ApiCallRig();
        var (ms, _) = r.Accounted(api => api.iwRezAt("x", 0, r.H.Prim.AbsolutePosition + new Vector3(20, 0, 0), Vector3.Zero, Quaternion.Identity, 0));
        Assert.Equal(0, ms);
        Assert.Contains(r.Errors, e => e.Contains("Position exceeds 10m distance limit."));
    }

    [Fact]
    public void IwRezPrimSaysItIsNotImplemented()
    {
        using var r = new ApiCallRig();
        string ret = r.Api.iwRezPrim(ApiCallRig.L(), ApiCallRig.L(), ApiCallRig.L(), r.H.Prim.AbsolutePosition, Vector3.Zero, Quaternion.Identity, 0);
        Assert.Equal(UUID.Zero.ToString(), ret);
        Assert.Contains(r.Errors, e => e.Contains("Command not implemented: iwRezPrim"));
    }

    // ---------------------------------------------------------------- iwLinkStandTarget

    [Fact]
    public void AStandTargetIsSaved()
    {
        using var r = new ApiCallRig();
        r.H.Prim.ParentGroup.HasGroupChanged = false;
        r.Api.iwLinkStandTarget(1, new Vector3(1, 0, 0), Quaternion.Identity);
        Assert.Equal(new Vector3(1, 0, 0), r.H.Prim.StandOffset);
        Assert.True(r.H.Prim.ParentGroup.HasGroupChanged);
    }
}
