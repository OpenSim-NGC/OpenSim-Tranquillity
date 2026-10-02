/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using ProtoBuf;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A restored script gets its permission grant back. The core zeroes a script item's grant whenever it starts the script
/// (SceneObjectPartInventory.CreateScriptInstance), so the grant comes from the saved state. From this simulator's state
/// database (a restart) it comes back whole when the object's owner is the owner noted with it. From state carried inside
/// an object, input from outside the simulator, only what a silent llRequestPermissions would give at that moment comes
/// back (the granter wears the object or sits on it); a granter not here yet leaves it waiting as a claim. No
/// run_time_permissions is posted. llResetScript, an owner change and a new llRequestPermissions clear the saved grant
/// and any claim. The SL wiki says nothing on a grant across a restart, rez or crossing.
/// </summary>
// Runs in parallel: each test has its own harnesses, avatars, items and assets, and rows under random item ids in the
// state database every harness shares (as the other saved-state classes do); nothing process-wide is changed.
public class GrantRestoreTests
{
    private const int Debit = 0x2, TakeControls = 0x4, TriggerAnimation = 0x10, SilentEstate = 0x4000;
    private const int RegionStart = 0, NewRez = 1;
    private const string Phlox = "InWorldz.Phlox";

    /// <summary>Driven over channel 7: "take", "ask KEY MASK", "reset". A touch reports the grant.</summary>
    private const string Seat = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                string cmd = llList2String(w, 0);
                if (cmd == ""take"") { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""took""); }
                else if (cmd == ""ask"") llRequestPermissions(llList2Key(w, 1), (integer)llList2String(w, 2));
                else if (cmd == ""reset"") llResetScript();
            }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
            control(key id, integer l, integer e) { llSay(0, ""ctl""); }
        }";

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void Command(SchedulerHarness h, string msg, string expect = null)
    {
        int before = expect == null ? 0 : h.Said.Count(s => s == expect);
        h.Scene.SimChat(msg, ChatTypeEnum.Region, 7, h.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        if (expect != null)
            Assert.True(h.PumpUntil(() => h.Said.Count(s => s == expect) > before), "no '" + expect + "' after '" + msg + "': " + SavedStateRig.SaidText(h));
        else
            h.PumpUntilIdle(TimeSpan.FromSeconds(5));
    }

    /// <summary>A touch, and what the script says its grant is.</summary>
    private static string Report(SchedulerHarness h, UUID item)
    {
        int before = h.Said.Count(s => s.StartsWith("perms=", StringComparison.Ordinal));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s.StartsWith("perms=", StringComparison.Ordinal)) > before), SavedStateRig.SaidText(h));
        return h.Said.Last(s => s.StartsWith("perms=", StringComparison.Ordinal));
    }

    private static LSLSystemAPI Api(SchedulerHarness h, UUID item)
        => ((Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(SavedStateRig.Exe(h), "m_Apis"))[item];

    private static bool HasControlRecord(SchedulerHarness h, UUID item)
        => ((Interpreter)h.InterpreterFor(item)).ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control);

    private static void SetOwner(SceneObjectGroup g, UUID owner)
    {
        foreach (SceneObjectPart p in g.Parts) p.OwnerID = owner;
    }

    /// <summary>
    /// Run the seat script in a first region, owned by <paramref name="owner"/>, with <paramref name="setUp"/> giving it
    /// its grant, and save it as a region stop does (the unload save). The row is in the shared state database.
    /// </summary>
    private static void SaveInFirstRegion(UUID owner, UUID asset, UUID item, Action<SchedulerHarness, TaskInventoryItem> setUp)
    {
        using var h1 = new SchedulerHarness();
        SetOwner(h1.Prim.ParentGroup, owner);
        var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "seat", Seat);
        Assert.True(h1.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
        h1.Prim.ParentGroup.ResumeScripts();
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        setUp(h1, inv);
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
        h1.SaveState(item);
        SavedStateRig.WaitForWrites(h1);
    }

    /// <summary>
    /// A region restart: a new region whose object, owned by <paramref name="owner"/>, holds the same item, started by the
    /// core's region-start path (CreateScriptInstances, which zeroes the item's grant); the row restores it.
    /// </summary>
    private static SchedulerHarness Restart(UUID owner, UUID asset, UUID item, Action<SchedulerHarness> before = null)
    {
        var h2 = new SchedulerHarness();
        SetOwner(h2.Prim.ParentGroup, owner);
        before?.Invoke(h2);
        var inv = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item, asset, "seat", Seat);
        inv.PermsGranter = UUID.Random();   // whatever the region database held; the core zeroes it at the start
        inv.PermsMask = Debit;
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null), "the script did not load");
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh
        return h2;
    }

    private static ScenePresence SitOn(Scene scene, UUID agent, SceneObjectGroup seat)
    {
        ScenePresence sp = scene.GetScenePresence(agent) ?? SceneHelpers.AddScenePresence(scene, agent);
        sp.AbsolutePosition = seat.AbsolutePosition + new Vector3(1, 1, 0);   // within sit range
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.RootPart.UUID, Vector3.Zero);
        Assert.Equal(seat, sp.ParentPart?.ParentGroup);
        return sp;
    }

    /// <summary>
    /// The seat script's state as an object would carry it, with the grant fields set as <paramref name="forge"/> says,
    /// handed to a new object owned by <paramref name="owner"/> and started by the core as a rez. The source script is
    /// removed first, so commands on channel 7 reach only the new one.
    /// </summary>
    private static UUID RezWithCarried(SchedulerHarness h, UUID owner, Action<SerializedRuntimeState> forge,
                                       bool withControlRecord, out SceneObjectGroup copy)
    {
        var asset = UUID.Random();
        var source = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, source, asset, "source", Seat);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(source, 0, false, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        var interp = (Interpreter)h.InterpreterFor(source);
        if (withControlRecord)
            lock (interp.ScriptState.EventQueueLock)
                interp.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] = new object[] { 1, 1, 0 };
        SerializedRuntimeState st = StateManager.Decode(StateManager.CaptureBlob(interp));
        forge(st);
        byte[] blob;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
        SavedStateRig.PostRemove(h, h.Prim, source);
        h.Prim.Inventory.RemoveInventoryItem(source);
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));

        copy = SceneHelpers.AddSceneObject(h.Scene, "Example Copy", owner);
        copy.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(4, 0, 0);
        var item = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, copy.RootPart, item, asset, "copy", Seat);
        SavedStateRig.States(h).Carry(item, asset, blob);
        h.ClearSaid(item);
        Assert.Equal(1, copy.CreateScriptInstances(0, true, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the rezzed script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h.Said);   // restored from the carried state, not started fresh
        return item;
    }

    private static void Forge(SerializedRuntimeState st, UUID granter, int mask, UUID? owner)
    {
        st.PermsGranter = granter.ToString();
        st.GrantedPermsMask = mask;
        st.PermsOwner = owner?.ToString();
    }

    private static void Arrive(Scene scene, ScenePresence sp) => scene.EventManager.TriggerOnMakeRootAgent(sp);

    // ── a restart: the row comes back whole for the same owner ───────────────

    [Fact]
    public void ARestartGivesBackDebitFromTheOwnerWithNoRunTimePermissionsEvent()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) => { inv.PermsGranter = owner; inv.PermsMask = Debit; });

        using var h2 = Restart(owner, asset, item);
        var inv2 = h2.Prim.Inventory.GetInventoryItem(item);
        Assert.Equal(Debit, inv2.PermsMask);
        Assert.Equal(owner, inv2.PermsGranter);
        Assert.Equal("perms=" + Debit + " key=" + owner, Report(h2, item));
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("rtp=", StringComparison.Ordinal));
    }

    [Fact]
    public void ARestartUnderAnotherOwnerGivesNothingBack()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) => { inv.PermsGranter = owner; inv.PermsMask = Debit; });

        using var h2 = Restart(UUID.Random(), asset, item);
        var inv2 = h2.Prim.Inventory.GetInventoryItem(item);
        Assert.Equal(0, inv2.PermsMask);
        Assert.Equal(UUID.Zero, inv2.PermsGranter);
        Assert.Equal("perms=0 key=" + UUID.Zero, Report(h2, item));
    }

    [Fact]
    public void ARestartGivesASeatScriptItsGrantAndControlsBackFromAnAvatarWhoIsHere()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random(), driver = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) =>
        {
            SceneHelpers.AddScenePresence(h.Scene, driver);
            inv.PermsGranter = driver;
            inv.PermsMask = TakeControls;
            Command(h, "take", "took");
            Assert.True(h.Scene.GetScenePresence(driver).HasScriptControls(item));
        });

        ScenePresence sp = null;
        using var h2 = Restart(owner, asset, item, h => sp = SceneHelpers.AddScenePresence(h.Scene, driver));
        var inv2 = h2.Prim.Inventory.GetInventoryItem(item);
        Assert.Equal(TakeControls, inv2.PermsMask);
        Assert.Equal(driver, inv2.PermsGranter);
        Assert.True(h2.PumpUntil(() => sp.HasScriptControls(item)), "the controls were not taken again");
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("rtp=", StringComparison.Ordinal));
    }

    [Fact]
    public void ARestartWithTheAvatarAwayGivesTheGrantBackAndTheControlsWaitForIt()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random(), driver = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) =>
        {
            SceneHelpers.AddScenePresence(h.Scene, driver);
            inv.PermsGranter = driver;
            inv.PermsMask = TakeControls;
            Command(h, "take", "took");
        });

        using var h2 = Restart(owner, asset, item);
        var inv2 = h2.Prim.Inventory.GetInventoryItem(item);
        Assert.Equal(TakeControls, inv2.PermsMask);
        Assert.Equal(driver, inv2.PermsGranter);
        Assert.True(HasControlRecord(h2, item), "the controls' record went while the grant is held");

        var sp = SitOn(h2.Scene, driver, h2.Prim.ParentGroup);
        Arrive(h2.Scene, sp);
        Assert.True(h2.PumpUntil(() => sp.HasScriptControls(item)), "the arriving avatar's controls were not taken");
    }

    [Fact]
    public void ARestartGivesTheSilentEstateBitBackWithItsRecord()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) =>
        {
            inv.PermsGranter = owner;
            inv.PermsMask = SilentEstate;
            var interp = (Interpreter)h.InterpreterFor(item);
            lock (interp.ScriptState.EventQueueLock)
                interp.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.SilentEstateManagement] = new object[] { 1 };
        });

        using var h2 = Restart(owner, asset, item);
        Assert.Equal(SilentEstate, h2.Prim.Inventory.GetInventoryItem(item).PermsMask);
        Assert.True(((Interpreter)h2.InterpreterFor(item)).ScriptState.MiscAttributes
            .ContainsKey((int)RuntimeState.MiscAttr.SilentEstateManagement));
    }

    // ── old rows and old carried states ──────────────────────────────────────

    /// <summary>A row written before the owner was noted (tag 28) restores the script with no grant, whatever 16 and 17 hold.</summary>
    [Fact]
    public void AnOldRowRestoresWithNoGrant()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) => { inv.PermsGranter = owner; inv.PermsMask = Debit; });
        var row = SavedStateRig.Row(item)!.Value;
        SerializedRuntimeState st = StateManager.Decode(row.Blob);
        Assert.Equal(owner.ToString(), st.PermsOwner);
        st.PermsOwner = null;   // as every earlier build wrote it
        using (var ms = new MemoryStream())
        {
            Serializer.Serialize(ms, st);
            SavedStateRig.PutRow(item, asset, ms.ToArray(), row.SavedAt);
        }

        using var h2 = Restart(owner, asset, item);
        Assert.Equal(0, h2.Prim.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal("perms=0 key=" + UUID.Zero, Report(h2, item));
    }

    [Fact]
    public void AnOldCarriedStateRestoresWithNoGrant()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        var sp = SitOn(h.Scene, owner, h.Prim.ParentGroup);
        var item = RezWithCarried(h, owner, st => Forge(st, owner, TakeControls, null), false, out var copy);
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.False(Api(h, item).HasGrantClaim);
    }

    /// <summary>The older contract (no tag 28) reads a state this build writes: protobuf skips the unknown field.</summary>
    [ProtoContract]
    public class OlderState
    {
        [ProtoMember(1, IsRequired = true)] public int IP;
        [ProtoMember(2, IsRequired = true)] public int LSLState;
        [ProtoMember(9, IsRequired = true)] public RuntimeState.Status RunState;
        [ProtoMember(16)] public string PermsGranter;
        [ProtoMember(17)] public int GrantedPermsMask;
        [ProtoMember(27)] public string BytecodeIdentity;
    }

    [Fact]
    public void AnOlderBuildReadsAStateThatNotesAGrant()
    {
        var st = new SerializedRuntimeState { LSLState = 3, BytecodeIdentity = "b" };
        Forge(st, UUID.Random(), TakeControls, UUID.Random());
        byte[] blob;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
        using var read = new MemoryStream(blob);
        var older = Serializer.Deserialize<OlderState>(read);
        Assert.Equal(3, older.LSLState);
        Assert.Equal("b", older.BytecodeIdentity);
        Assert.Equal(TakeControls, older.GrantedPermsMask);
    }

    // ── carried state: only what a silent re-request would give ──────────────

    [Fact]
    public void ForgedCarriedDebitFromTheOwnerIsNotRestored()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        SceneHelpers.AddScenePresence(h.Scene, owner);   // here, not wearing it and not seated on it
        var item = RezWithCarried(h, owner, st => Forge(st, owner, Debit, owner), false, out var copy);
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal("perms=0 key=" + UUID.Zero, Report(h, item));
        Assert.False(Api(h, item).HasGrantClaim);
    }

    [Fact]
    public void ForgedCarriedControlsFromAnAvatarHereButNotSeatedAreNotRestored()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, other = UUID.Random();
        var sp = SceneHelpers.AddScenePresence(h.Scene, other);
        var item = RezWithCarried(h, owner, st => Forge(st, other, TakeControls, owner), true, out var copy);
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.False(sp.HasScriptControls(item));
        Assert.False(HasControlRecord(h, item));
        Assert.False(Api(h, item).HasGrantClaim);
    }

    [Fact]
    public void ForgedCarriedControlsFromAnAbsentAvatarAreNotRestoredWhenItArrivesUnseated()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, other = UUID.Random();
        var item = RezWithCarried(h, owner, st => Forge(st, other, TakeControls, owner), true, out var copy);
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.True(Api(h, item).HasGrantClaim);   // waiting, acting on nothing

        var sp = SceneHelpers.AddScenePresence(h.Scene, other);
        Arrive(h.Scene, sp);
        h.PumpUntilIdle(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.False(sp.HasScriptControls(item));
    }

    /// <summary>A take and rez by the same owner: a grant that needs a dialog (debit) does not come back from the carried state.</summary>
    [Fact]
    public void ATakeAndRezBySameOwnerDoesNotBringDebitBack()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID;
        SceneHelpers.AddScenePresence(h.Scene, owner);
        var asset = UUID.Random();
        var item = UUID.Random();
        var inv = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, asset, "seat", Seat);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        inv.PermsGranter = owner;
        inv.PermsMask = Debit | TriggerAnimation;

        // Taken as AsyncSceneObjectGroupDeleter does (ToOriginalXmlFormat), and rezzed again with new ids.
        string xml = OpenSim.Region.Framework.Scenes.Serialization.SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
        var copy = OpenSim.Region.Framework.Scenes.Serialization.SceneObjectSerializer.FromOriginalXmlFormat(xml);
        copy.ResetIDs();
        copy.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(4, 0, 0);
        h.Scene.AddNewSceneObject(copy, false);
        TaskInventoryItem rezzed = copy.RootPart.Inventory.GetInventoryItems().Single();
        h.ClearSaid(item);
        copy.CreateScriptInstances(0, true, Phlox, NewRez);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(rezzed.ItemID) != null), "the rezzed script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h.Said);
        Assert.Equal(0, rezzed.PermsMask & Debit);
        Assert.Equal(0, rezzed.PermsMask);   // the owner neither wears it nor sits on it: nothing silent to give
    }

    // ── what clears the saved grant and the claim ────────────────────────────

    [Fact]
    public void LlResetScriptClearsTheRestoredGrantAndTheNextRowHoldsNone()
    {
        UUID owner = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        SaveInFirstRegion(owner, asset, item, (h, inv) => { inv.PermsGranter = owner; inv.PermsMask = Debit; });
        using (var h2 = Restart(owner, asset, item))
        {
            Assert.Equal(Debit, h2.Prim.Inventory.GetInventoryItem(item).PermsMask);
            Command(h2, "reset", "entry");
            Assert.Equal(0, h2.Prim.Inventory.GetInventoryItem(item).PermsMask);
            h2.SaveState(item);
            SavedStateRig.WaitForWrites(h2);
        }
        SerializedRuntimeState row = StateManager.Decode(SavedStateRig.Row(item)!.Value.Blob);
        Assert.Equal(0, row.GrantedPermsMask);
        Assert.Null(row.PermsOwner);
        using var h3 = Restart(owner, asset, item);
        Assert.Equal(0, h3.Prim.Inventory.GetInventoryItem(item).PermsMask);
    }

    [Fact]
    public void LlResetScriptClearsAWaitingClaimAndItsRecord()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, driver = UUID.Random();
        var item = RezWithCarried(h, owner, st => Forge(st, driver, TakeControls, owner), true, out var copy);
        Assert.True(Api(h, item).HasGrantClaim);
        Assert.True(HasControlRecord(h, item));   // waits with its claim

        Command(h, "reset", "entry");
        Assert.False(Api(h, item).HasGrantClaim);
        Assert.False(HasControlRecord(h, item));
        var sp = SitOn(h.Scene, driver, copy);
        Arrive(h.Scene, sp);
        h.PumpUntilIdle(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.False(sp.HasScriptControls(item));
    }

    [Fact]
    public void ANewPermissionAnswerReplacesAWaitingClaim()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, driver = UUID.Random(), other = UUID.Random();
        var item = RezWithCarried(h, owner, st => Forge(st, driver, TakeControls, owner), true, out var copy);
        Assert.True(Api(h, item).HasGrantClaim);

        // Another avatar seated on it is asked, and answers silently.
        SitOn(h.Scene, other, copy);
        Command(h, "ask " + other + " " + TriggerAnimation, "rtp=" + TriggerAnimation);
        Assert.False(Api(h, item).HasGrantClaim);
        Assert.False(HasControlRecord(h, item));

        var sp = SitOn(h.Scene, driver, copy);
        Arrive(h.Scene, sp);
        h.PumpUntilIdle(TimeSpan.FromSeconds(3));
        var inv = copy.RootPart.Inventory.GetInventoryItem(item);
        Assert.Equal(other, inv.PermsGranter);
        Assert.Equal(TriggerAnimation, inv.PermsMask);
        Assert.False(sp.HasScriptControls(item));
    }

    [Fact]
    public void AClaimMetBySeatingGivesTheSilentBitsAndTheControls()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, driver = UUID.Random();
        var item = RezWithCarried(h, owner, st => Forge(st, driver, TakeControls | TriggerAnimation | Debit, owner), true, out var copy);
        Assert.True(Api(h, item).HasGrantClaim);
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);

        var sp = SitOn(h.Scene, driver, copy);
        Arrive(h.Scene, sp);
        Assert.True(h.PumpUntil(() => sp.HasScriptControls(item)), "the claim's controls were not taken");
        var inv = copy.RootPart.Inventory.GetInventoryItem(item);
        Assert.Equal(driver, inv.PermsGranter);
        Assert.Equal(TakeControls | TriggerAnimation, inv.PermsMask);   // debit needs a dialog: asked again
        Assert.False(Api(h, item).HasGrantClaim);
        Assert.DoesNotContain(h.Said, s => s.StartsWith("rtp=", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOwnerChangeClearsAWaitingClaim()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, driver = UUID.Random();
        var item = RezWithCarried(h, owner, st => Forge(st, driver, TakeControls, owner), true, out var copy);
        Assert.True(Api(h, item).HasGrantClaim);

        UUID buyer = UUID.Random();
        copy.SetOwner(buyer, UUID.Zero);
        copy.RootPart.Inventory.ChangeInventoryOwner(buyer);
        Assert.True(h.PumpUntil(() => !Api(h, item).HasGrantClaim), "the owner change left the claim waiting");
        var sp = SitOn(h.Scene, driver, copy);
        Arrive(h.Scene, sp);
        h.PumpUntilIdle(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.False(sp.HasScriptControls(item));
    }
}
