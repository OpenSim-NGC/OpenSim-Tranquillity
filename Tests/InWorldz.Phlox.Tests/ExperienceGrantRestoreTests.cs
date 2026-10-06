/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Linq;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using ProtoBuf;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A grant from an Experience (llRequestExperiencePermissions) is held by the script item as SL's list, granted by the
/// agent, and noted as that Experience's. After a restore it comes back by the rules other grants follow: from this
/// simulator's state database whole, when the object's owner is the owner noted; from carried state only when
/// llRequestExperiencePermissions would grant it now with no dialog (the script is still in that Experience, the
/// Experience is allowed here, the granter is here and still allows it), and a granter not here yet leaves it waiting as a
/// claim, decided when that avatar arrives anywhere in the region. No run_time_permissions and no experience_permissions
/// is posted by a restore. The script's Experience is its item's: a grant noted with an Experience the item does not name
/// ends at the restore, with no event. A grant from this simulator's state database whose Experience the land no longer
/// lets run ends at the restore, with one experience_permissions_denied.
/// </summary>
// Runs in parallel: each test has its own harnesses, avatars, items, assets and Experience module (registered on its own
// scene), and rows under random item ids in the state database every harness shares; nothing process-wide is changed.
public class ExperienceGrantRestoreTests
{
    /// <summary>SL's list: TAKE_CONTROLS | TRIGGER_ANIMATION | ATTACH | TRACK_CAMERA | CONTROL_CAMERA | TELEPORT.</summary>
    private const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const int Debit = 0x2, TakeControls = 0x4;
    private const int RegionStart = 0, NewRez = 1;
    private const string Phlox = "InWorldz.Phlox";

    /// <summary>Driven over channel 7: "xp KEY", "ask KEY MASK", "reset". A touch reports the grant.</summary>
    private const string Game = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                string cmd = llList2String(w, 0);
                if (cmd == ""xp"") llRequestExperiencePermissions(llList2Key(w, 1), """");
                else if (cmd == ""ask"") llRequestPermissions(llList2Key(w, 1), (integer)llList2String(w, 2));
                else if (cmd == ""reset"") llResetScript();
            }
            experience_permissions(key a) { llSay(0, ""xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""xpdenied="" + (string)r); }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
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

    private static string Report(SchedulerHarness h, UUID item)
    {
        int before = h.Said.Count(s => s.StartsWith("perms=", StringComparison.Ordinal));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s.StartsWith("perms=", StringComparison.Ordinal)) > before), SavedStateRig.SaidText(h));
        return h.Said.Last(s => s.StartsWith("perms=", StringComparison.Ordinal));
    }

    private static string Perms(int mask, UUID key) => "perms=" + mask + " key=" + key;

    /// <summary>No permission event of either kind was posted.</summary>
    private static void NoPermissionEvents(SchedulerHarness h)
    {
        Assert.DoesNotContain(h.Said, s => s.StartsWith("rtp=", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Said, s => s.StartsWith("xp=", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Said, s => s.StartsWith("xpdenied=", StringComparison.Ordinal));
    }

    private static LSLSystemAPI Api(SchedulerHarness h, UUID item)
        => ((System.Collections.Generic.Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(SavedStateRig.Exe(h), "m_Apis"))[item];

    private static void SetOwner(SceneObjectGroup g, UUID owner)
    {
        foreach (SceneObjectPart p in g.Parts) p.OwnerID = owner;
    }

    /// <summary>The region knows one Experience, allowed in its estate and granted by <paramref name="grantedBy"/>.</summary>
    private static void Region(SchedulerHarness h, UUID experience, UUID grantedBy)
        => h.Scene.RegisterModuleInterface<IExperienceModule>(OneExperience.Create(experience, grantedBy));

    private static SerializedRuntimeState RowState(UUID item) => StateManager.Decode(SavedStateRig.Row(item)!.Value.Blob);

    /// <summary>
    /// The game script, compiled into <paramref name="experience"/>, runs in a first region owned by
    /// <paramref name="owner"/>; <paramref name="visitor"/>, who allowed the Experience, is asked and grants silently. The
    /// script is saved as a region stop does.
    /// </summary>
    private static void GrantAndSave(UUID owner, UUID visitor, UUID experience, UUID asset, UUID item, Action<SchedulerHarness> after = null)
    {
        using var h1 = new SchedulerHarness();
        SetOwner(h1.Prim.ParentGroup, owner);
        Region(h1, experience, visitor);
        SceneHelpers.AddScenePresence(h1.Scene, visitor);
        var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "game", Game);
        inv.ExperienceID = experience;
        Assert.True(h1.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
        h1.Prim.ParentGroup.ResumeScripts();
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        Command(h1, "xp " + visitor, "xp=" + visitor);
        after?.Invoke(h1);
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
        h1.SaveState(item);
        SavedStateRig.WaitForWrites(h1);
    }

    /// <summary>A region restart: a new region holding the same item, started by the core's region-start path.</summary>
    private static SchedulerHarness Restart(UUID owner, UUID asset, UUID item, UUID experience)
    {
        var h2 = new SchedulerHarness();
        SetOwner(h2.Prim.ParentGroup, owner);
        var inv = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item, asset, "game", Game);
        inv.ExperienceID = experience;
        inv.PermsGranter = UUID.Random();   // whatever the region database held; the core zeroes it at the start
        inv.PermsMask = Debit;
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null), "the script did not load");
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh
        return h2;
    }

    /// <summary>
    /// The game script's state as an object would carry it, its grant fields set by <paramref name="forge"/>, handed to a
    /// new object owned by <paramref name="owner"/> whose script item is compiled into <paramref name="itemExperience"/>,
    /// and started by the core as a rez.
    /// </summary>
    private static UUID RezWithCarried(SchedulerHarness h, UUID owner, UUID itemExperience, Action<SerializedRuntimeState> forge,
                                       out SceneObjectGroup copy)
    {
        var asset = UUID.Random();
        var source = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, source, asset, "source", Game);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(source, 0, false, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        SerializedRuntimeState st = StateManager.Decode(StateManager.CaptureBlob((Interpreter)h.InterpreterFor(source)));
        forge(st);
        byte[] blob;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
        SavedStateRig.PostRemove(h, h.Prim, source);
        h.Prim.Inventory.RemoveInventoryItem(source);
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));

        copy = SceneHelpers.AddSceneObject(h.Scene, "Example Copy", owner);
        copy.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(4, 0, 0);
        var item = UUID.Random();
        var inv = TaskInventoryHelpers.AddScript(h.Scene.AssetService, copy.RootPart, item, asset, "copy", Game);
        inv.ExperienceID = itemExperience;
        SavedStateRig.States(h).Carry(item, asset, blob);
        h.ClearSaid(item);
        Assert.Equal(1, copy.CreateScriptInstances(0, true, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the rezzed script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h.Said);   // restored from the carried state, not started fresh
        return item;
    }

    private static void Forge(SerializedRuntimeState st, UUID granter, int mask, UUID owner, UUID? experience)
    {
        st.PermsGranter = granter.ToString();
        st.GrantedPermsMask = mask;
        st.PermsOwner = owner.ToString();
        st.PermsExperience = experience?.ToString();
    }

    private static int MaskOf(SceneObjectGroup g, UUID item) => g.RootPart.Inventory.GetInventoryItem(item).PermsMask;

    // ── the grant itself ─────────────────────────────────────────────────────

    [Fact]
    public void AnExperienceGrantGivesTheScriptSlsListFromTheAgent()
    {
        using var h = new SchedulerHarness();
        UUID experience = UUID.Random(), visitor = UUID.Random(), item = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var inv = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, UUID.Random(), "game", Game);
        inv.ExperienceID = experience;
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));

        Command(h, "xp " + visitor, "xp=" + visitor);
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h, item));
    }

    // ── a restart: the row comes back whole for the same owner ───────────────

    [Fact]
    public void ARestartGivesAnExperienceGrantBackWholeWithNoEvent()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item);
        SerializedRuntimeState row = RowState(item);
        Assert.Equal(experience.ToString(), row.PermsExperience);
        Assert.Equal(visitor.ToString(), row.PermsGranter);
        Assert.Equal(ExperiencePerms, row.GrantedPermsMask);

        // The region after the restart has no Experience module at all: a row is trusted and restored whole.
        using var h2 = Restart(owner, asset, item, experience);
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h2, item));
        NoPermissionEvents(h2);

        // Still noted as the Experience's grant: the next row says so again.
        h2.SaveState(item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Equal(experience.ToString(), RowState(item).PermsExperience);
    }

    [Fact]
    public void ARestartUnderAnotherOwnerGivesNoExperienceGrantBack()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item);

        using var h2 = Restart(UUID.Random(), asset, item, experience);
        Assert.Equal(Perms(0, UUID.Zero), Report(h2, item));
        NoPermissionEvents(h2);
    }

    // ── carried state: only what the Experience would grant now with no dialog ─

    [Fact]
    public void CarriedExperienceGrantComesBackWhenTheExperienceWouldGrantItSilently()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);   // here, not seated on the object and not wearing it
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.Equal(ExperiencePerms, MaskOf(copy, item));
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h, item));
        Assert.False(Api(h, item).HasGrantClaim);
        NoPermissionEvents(h);
    }

    [Fact]
    public void CarriedExperienceGrantWaitsForItsGranterAndTheirArrivalAnywhereDecidesIt()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.True(Api(h, item).HasGrantClaim);

        var sp = SceneHelpers.AddScenePresence(h.Scene, visitor);   // arrives, neither seated on it nor wearing it
        h.Scene.EventManager.TriggerOnMakeRootAgent(sp);
        Assert.True(h.PumpUntil(() => MaskOf(copy, item) == ExperiencePerms), "the claim was not decided on arrival");
        Assert.False(Api(h, item).HasGrantClaim);
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h, item));
        NoPermissionEvents(h);
    }

    /// <summary>
    /// A carried grant is held to the decision llRequestExperiencePermissions makes: an Experience the estate blocks, one
    /// it neither allows nor trusts, and one the granter has blocked give nothing back and leave no claim.
    /// </summary>
    [Theory]
    [InlineData("blocked in the region")]
    [InlineData("not allowed in the region")]
    [InlineData("blocked by the avatar")]
    public void CarriedExperienceGrantIsNotRestoredWhereTheExperienceWouldBeDenied(string why)
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        var estate = ExperienceWithdrawnTests.ChangingEstate.Create(experience, visitor, out IExperienceModule module);
        h.Scene.RegisterModuleInterface(module);
        if (why == "blocked in the region") estate.Blocked.Add(experience);
        else if (why == "not allowed in the region") estate.Allowed.Clear();
        else estate.VisitorBlocked = true;
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.False(Api(h, item).HasGrantClaim, why);
        Assert.Equal(Perms(0, UUID.Zero), Report(h, item));
        NoPermissionEvents(h);
    }

    [Fact]
    public void ForgedCarriedExperienceTheScriptIsNotInRestoresNothing()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random(), named = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        // The state names an Experience the script item is not compiled into.
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, named), out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.False(Api(h, item).HasGrantClaim);
        NoPermissionEvents(h);
    }

    [Fact]
    public void ForgedCarriedExperienceNamedByAScriptInNoExperienceRestoresNothing()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var item = RezWithCarried(h, owner, UUID.Zero, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.False(Api(h, item).HasGrantClaim);
    }

    [Fact]
    public void ForgedCarriedExperienceFromAGranterWhoHasNotAllowedItRestoresNothing()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, UUID.Random());   // someone else allowed it; the visitor never did
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.Equal(Perms(0, UUID.Zero), Report(h, item));
        Assert.False(Api(h, item).HasGrantClaim);
        NoPermissionEvents(h);
    }

    [Fact]
    public void ForgedExperienceClaimFromAGranterWhoHasNotAllowedItRestoresNothingOnArrival()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, UUID.Random());
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.True(Api(h, item).HasGrantClaim);   // waiting, acting on nothing

        var sp = SceneHelpers.AddScenePresence(h.Scene, visitor);
        h.Scene.EventManager.TriggerOnMakeRootAgent(sp);
        Assert.True(h.PumpUntil(() => !Api(h, item).HasGrantClaim), "the claim was not decided on arrival");
        Assert.Equal(0, MaskOf(copy, item));
        NoPermissionEvents(h);
    }

    [Fact]
    public void ForgedCarriedExperienceGrantHoldingDebitGivesOnlyTheExperiencesList()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms | Debit, owner, experience), out var copy);
        Assert.Equal(ExperiencePerms, MaskOf(copy, item));
    }

    // ── the script's Experience is its item's, never the state's ─────────────

    private static SerializedPostedEvent QueuedExperienceGrant(UUID agent)
        => SerializedPostedEvent.FromPostedEvent(new PostedEvent
        {
            EventType = SupportedEventList.Events.EXPERIENCE_PERMISSIONS,
            Args = new object[] { agent.ToString() }
        });

    /// <summary>
    /// Carried state names an Experience and a grant from it while the script item names none (an object whose item lost
    /// its Experience on the way here, or forged state): the granter is not here yet, and no claim is left waiting for them.
    /// </summary>
    [Fact]
    public void CarriedExperienceGrantForAnItemInNoExperienceLeavesNoClaim()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        var item = RezWithCarried(h, owner, UUID.Zero, st => Forge(st, visitor, ExperiencePerms, owner, experience), out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.False(Api(h, item).HasGrantClaim);
        Assert.False(Api(h, item).HasExperienceClaimFor(visitor));

        var sp = SceneHelpers.AddScenePresence(h.Scene, visitor);   // arrives: nothing was waiting for them
        h.Scene.EventManager.TriggerOnMakeRootAgent(sp);
        h.PumpUntilIdle(TimeSpan.FromSeconds(3));
        Assert.Equal(0, MaskOf(copy, item));
        Assert.Equal(Perms(0, UUID.Zero), Report(h, item));
        NoPermissionEvents(h);
    }

    /// <summary>
    /// Carried state for an item in no Experience, its granter here, with an experience_permissions event still on its
    /// queue: no grant, and the event is not delivered.
    /// </summary>
    [Fact]
    public void CarriedExperienceGrantAndQueuedGrantEventForAnItemInNoExperienceGiveNothing()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var item = RezWithCarried(h, owner, UUID.Zero, st =>
        {
            Forge(st, visitor, ExperiencePerms, owner, experience);
            st.EventQueue = new[] { QueuedExperienceGrant(visitor) };
        }, out var copy);
        Assert.Equal(0, MaskOf(copy, item));
        Assert.False(Api(h, item).HasGrantClaim);
        Assert.Equal(Perms(0, UUID.Zero), Report(h, item));
        NoPermissionEvents(h);
    }

    /// <summary>The same carried state for an item that names that Experience keeps both the grant and the queued event.</summary>
    [Fact]
    public void CarriedExperienceGrantAndQueuedGrantEventForAnItemInThatExperienceAreKept()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        SceneHelpers.AddScenePresence(h.Scene, visitor);
        var item = RezWithCarried(h, owner, experience, st =>
        {
            Forge(st, visitor, ExperiencePerms, owner, experience);
            st.EventQueue = new[] { QueuedExperienceGrant(visitor) };
        }, out var copy);
        Assert.True(h.PumpUntil(() => h.Said.Contains("xp=" + visitor)), "the queued event was not delivered: " + SavedStateRig.SaidText(h));
        Assert.Equal(ExperiencePerms, MaskOf(copy, item));
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h, item));
        Assert.Equal(1, h.Said.Count(s => s == "xp=" + visitor));
    }

    /// <summary>
    /// The engine's own row for a script whose item still names the Experience, saved by a real region stop (Scene.Close,
    /// the final save after the scene is emptied), gives the grant back whole on the next start, still that Experience's.
    /// </summary>
    [Fact]
    public void ARowForAnItemThatStillNamesTheExperienceSurvivesARegionStopAndStart()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SetOwner(h1.Prim.ParentGroup, owner);
            Region(h1, experience, visitor);
            SceneHelpers.AddScenePresence(h1.Scene, visitor);
            var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "game", Game);
            inv.ExperienceID = experience;
            Assert.True(h1.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
            h1.Prim.ParentGroup.ResumeScripts();
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
            Command(h1, "xp " + visitor, "xp=" + visitor);
            h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
            h1.StopRegionAsTheSimulatorDoes();
        }
        Assert.Equal(experience.ToString(), RowState(item).PermsExperience);

        using var h2 = Restart(owner, asset, item, experience);
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h2, item));
        NoPermissionEvents(h2);
        h2.SaveState(item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Equal(experience.ToString(), RowState(item).PermsExperience);
    }

    /// <summary>A row noting an Experience the item no longer names gives nothing back and says nothing.</summary>
    [Fact]
    public void ARowForAnItemThatNoLongerNamesTheExperienceGivesNoGrantBack()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item);

        using var h2 = Restart(owner, asset, item, UUID.Zero);
        Assert.Equal(Perms(0, UUID.Zero), Report(h2, item));
        Assert.False(Api(h2, item).HasGrantClaim);
        NoPermissionEvents(h2);
        h2.SaveState(item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Null(RowState(item).PermsGranter);
        Assert.Null(RowState(item).PermsExperience);
    }

    // ── a restart onto land where the Experience can no longer run ───────────

    /// <summary>
    /// A region restart whose region has an Experience module that answers as <paramref name="estate"/> does. The game
    /// script's grant came from <paramref name="experience"/>; its granter is not in the region.
    /// </summary>
    private static SchedulerHarness RestartWithEstate(UUID owner, UUID asset, UUID item, UUID experience, Action<ExperienceWithdrawnTests.ChangingEstate> estate, UUID visitor)
    {
        var h2 = new SchedulerHarness();
        SetOwner(h2.Prim.ParentGroup, owner);
        var state = ExperienceWithdrawnTests.ChangingEstate.Create(experience, visitor, out IExperienceModule module);
        estate(state);
        h2.Scene.RegisterModuleInterface(module);
        var inv = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item, asset, "game", Game);
        inv.ExperienceID = experience;
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null), "the script did not load");
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);
        return h2;
    }

    [Fact]
    public void ARestartWhereTheLandStillAllowsTheExperienceKeepsItsGrantSilently()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item);

        using var h2 = RestartWithEstate(owner, asset, item, experience, _ => { }, visitor);
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h2, item));
        NoPermissionEvents(h2);
    }

    /// <summary>
    /// SL wiki experience_permissions_denied, "The experience can no longer run": the grant ends, and the script is told
    /// once with XP_ERROR_NOT_PERMITTED_LAND (17), at the start, not when its granter next moves.
    /// </summary>
    [Theory]
    [InlineData("no longer allowed")]
    [InlineData("blocked by the estate")]
    public void ARestartWhereTheLandNoLongerLetsTheExperienceRunEndsItsGrantWithOneDenial(string why)
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item);

        using var h2 = RestartWithEstate(owner, asset, item, experience, e =>
        {
            if (why == "no longer allowed") e.Allowed.Clear();
            else e.Blocked.Add(experience);
        }, visitor);
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("xpdenied=17")), why + ": " + SavedStateRig.SaidText(h2));
        Assert.Equal(Perms(0, UUID.Zero), Report(h2, item));
        Assert.Equal(0, h2.Prim.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal(1, h2.Said.Count(s => s.StartsWith("xpdenied=", StringComparison.Ordinal)));
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("xp=", StringComparison.Ordinal));

        // The ended grant is not in the next row either.
        h2.SaveState(item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Null(RowState(item).PermsGranter);
    }

    // ── what ends the note ───────────────────────────────────────────────────

    [Fact]
    public void ANewLlRequestPermissionsEndsTheExperienceNote()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        // The visitor is asked for TRIGGER_ANIMATION, already held; a dialog goes to them and is not answered.
        GrantAndSave(owner, visitor, experience, asset, item, h1 => Command(h1, "ask " + visitor + " 16"));
        SerializedRuntimeState row = RowState(item);
        Assert.Equal(visitor.ToString(), row.PermsGranter);
        Assert.Null(row.PermsExperience);
    }

    [Fact]
    public void LlResetScriptLeavesNoExperienceGrantInTheNextRow()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item, h1 =>
        {
            h1.ClearSaid(item);
            Command(h1, "reset", "entry");
        });
        SerializedRuntimeState row = RowState(item);
        Assert.Null(row.PermsGranter);
        Assert.Equal(0, row.GrantedPermsMask);
        Assert.Null(row.PermsExperience);
    }

    // ── old rows, old carried states, older builds ───────────────────────────

    /// <summary>A row written before tag 29 holds a grant with no Experience noted: it restores whole, as any grant.</summary>
    [Fact]
    public void AnOldRowWithNoExperienceTagRestoresAsAnyGrant()
    {
        UUID owner = UUID.Random(), visitor = UUID.Random(), experience = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        GrantAndSave(owner, visitor, experience, asset, item);
        var saved = SavedStateRig.Row(item)!.Value;
        SerializedRuntimeState st = StateManager.Decode(saved.Blob);
        st.PermsExperience = null;   // as every earlier build wrote it
        using (var ms = new MemoryStream())
        {
            Serializer.Serialize(ms, st);
            SavedStateRig.PutRow(item, asset, ms.ToArray(), saved.SavedAt);
        }

        using var h2 = Restart(owner, asset, item, experience);
        Assert.Equal(Perms(ExperiencePerms, visitor), Report(h2, item));
        NoPermissionEvents(h2);
        h2.SaveState(item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Null(RowState(item).PermsExperience);
    }

    /// <summary>
    /// A carried state written before tag 29 holds a grant with no Experience noted: it is an ordinary grant, a claim the
    /// granter meets only by sitting on the object or wearing it, not by arriving elsewhere in the region.
    /// </summary>
    [Fact]
    public void AnOldCarriedStateWithNoExperienceTagRestoresAsAnyGrant()
    {
        using var h = new SchedulerHarness();
        UUID owner = h.Prim.OwnerID, visitor = UUID.Random(), experience = UUID.Random();
        Region(h, experience, visitor);
        var item = RezWithCarried(h, owner, experience, st => Forge(st, visitor, ExperiencePerms, owner, null), out var copy);
        Assert.True(Api(h, item).HasGrantClaim);
        Assert.False(Api(h, item).HasExperienceClaimFor(visitor));

        var sp = SceneHelpers.AddScenePresence(h.Scene, visitor);   // arrives, neither seated on it nor wearing it
        h.Scene.EventManager.TriggerOnMakeRootAgent(sp);
        h.PumpUntilIdle(TimeSpan.FromSeconds(3));
        Assert.Equal(0, MaskOf(copy, item));
        Assert.True(Api(h, item).HasGrantClaim);   // still waiting for a seat or a wearer, as before
        NoPermissionEvents(h);
    }

    /// <summary>
    /// A grant the land ended (its avatar entered a parcel where the Experience can no longer run) stays ended across a
    /// real region stop (Scene.Close) and start: the row holds no grant, and the restored script holds none.
    /// </summary>
    [Fact]
    public void AGrantTheLandEndedStaysEndedAfterARegionStopAndStart()
    {
        UUID owner = UUID.Random(), experience = UUID.Random(), visitor = UUID.Random(), asset = UUID.Random(), item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SetOwner(h1.Prim.ParentGroup, owner);
            var estate = ExperienceWithdrawnTests.ChangingEstate.Create(experience, visitor, out IExperienceModule module);
            h1.Scene.RegisterModuleInterface(module);
            ScenePresence sp = SceneHelpers.AddScenePresence(h1.Scene, visitor);
            var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "game", Game);
            inv.ExperienceID = experience;
            Assert.True(h1.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
            h1.Prim.ParentGroup.ResumeScripts();
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
            Command(h1, "xp " + visitor, "xp=" + visitor);

            lock (estate.Allowed) estate.Allowed.Clear();
            h1.Scene.EventManager.TriggerAvatarEnteringNewParcel(sp, 1, h1.Scene.RegionInfo.RegionID);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("xpdenied=17")), SavedStateRig.SaidText(h1));
            h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
            h1.StopRegionAsTheSimulatorDoes();
        }
        SerializedRuntimeState row = RowState(item);
        Assert.Equal(0, row.GrantedPermsMask);
        Assert.True(string.IsNullOrEmpty(row.PermsGranter), "the row holds a granter");
        Assert.True(string.IsNullOrEmpty(row.PermsExperience), "the row notes an Experience");

        using var h2 = Restart(owner, asset, item, experience);
        Assert.Equal(Perms(0, UUID.Zero), Report(h2, item));
        NoPermissionEvents(h2);
    }

    /// <summary>The older contract (no tag 29) reads a state this build writes: protobuf skips the unknown field.</summary>
    [ProtoContract]
    public class OlderState
    {
        [ProtoMember(1, IsRequired = true)] public int IP;
        [ProtoMember(2, IsRequired = true)] public int LSLState;
        [ProtoMember(9, IsRequired = true)] public RuntimeState.Status RunState;
        [ProtoMember(16)] public string PermsGranter;
        [ProtoMember(17)] public int GrantedPermsMask;
        [ProtoMember(27)] public string BytecodeIdentity;
        [ProtoMember(28)] public string PermsOwner;
    }

    [Fact]
    public void AnOlderBuildReadsAStateThatNotesAnExperienceGrant()
    {
        UUID granter = UUID.Random(), owner = UUID.Random();
        var st = new SerializedRuntimeState { LSLState = 3, BytecodeIdentity = "b" };
        Forge(st, granter, ExperiencePerms, owner, UUID.Random());
        byte[] blob;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
        using var read = new MemoryStream(blob);
        var older = Serializer.Deserialize<OlderState>(read);
        Assert.Equal(3, older.LSLState);
        Assert.Equal("b", older.BytecodeIdentity);
        Assert.Equal(granter.ToString(), older.PermsGranter);
        Assert.Equal(ExperiencePerms, older.GrantedPermsMask);
        Assert.Equal(owner.ToString(), older.PermsOwner);
    }
}
