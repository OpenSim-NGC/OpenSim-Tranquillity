/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A grant from an Experience ends when its avatar enters a parcel where the Experience cannot run. SL wiki
/// experience_permissions_denied, "The experience can no longer run": "The agent has moved to a parcel where the
/// experience cannot run." The script's item loses the grant and the controls it took, and gets
/// experience_permissions_denied with XP_ERROR_NOT_PERMITTED_LAND (17). Where the Experience still runs nothing changes,
/// and a grant that did not come from an Experience is left alone.
/// </summary>
// Runs in parallel: each test has its own harness, avatar, items and Experience module on its own scene.
public class ExperienceWithdrawnTests
{
    private const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const int RegionStart = 0;
    private const string Phlox = "InWorldz.Phlox";

    /// <summary>Driven over channel 7: "xp KEY", "ask KEY MASK", "take". A touch reports the grant.</summary>
    private const string Game = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                string cmd = llList2String(w, 0);
                if (cmd == ""xp"") llRequestExperiencePermissions(llList2Key(w, 1), """");
                else if (cmd == ""ask"") llRequestPermissions(llList2Key(w, 1), (integer)llList2String(w, 2));
                else if (cmd == ""take"") { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""took""); }
            }
            experience_permissions(key a) { llSay(0, ""xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""xpdenied="" + (string)a + "" "" + (string)r); }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
        }";

    /// <summary>One Experience the visitor allowed; the estate's allowed, key and blocked lists can change.</summary>
    internal class ChangingEstate : DispatchProxy
    {
        public UUID Experience, Visitor;
        public readonly List<UUID> Allowed = new(), Trusted = new(), Blocked = new();

        public static ChangingEstate Create(UUID experience, UUID visitor, out IExperienceModule module)
        {
            module = Create<IExperienceModule, ChangingEstate>();
            var stub = (ChangingEstate)(object)module;
            stub.Experience = experience;
            stub.Visitor = visitor;
            stub.Allowed.Add(experience);
            return stub;
        }

        protected override object Invoke(MethodInfo m, object[] a)
        {
            lock (Allowed)
            {
                if (m.Name == nameof(IExperienceModule.GetExperiencePermission))
                    return (UUID)a[0] == Visitor && (UUID)a[1] == Experience ? ExperiencePermission.Allowed : ExperiencePermission.None;
                if (m.Name == nameof(IExperienceModule.GetEstateAllowedExperiences)) return Allowed.ToArray();
                if (m.Name == nameof(IExperienceModule.GetEstateKeyExperiences)) return Trusted.ToArray();
                if (m.Name == nameof(IExperienceModule.GetEstateBlockedExperiences)) return Blocked.ToArray();
            }
            var rt = m.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H = new();
        public readonly UUID Experience = UUID.Random(), Visitor = UUID.Random(), Item = UUID.Random();
        public readonly ChangingEstate Estate;
        public readonly ScenePresence Sp;

        public Rig()
        {
            Estate = ChangingEstate.Create(Experience, Visitor, out IExperienceModule module);
            H.Scene.RegisterModuleInterface(module);
            Sp = SceneHelpers.AddScenePresence(H.Scene, Visitor);
            var inv = TaskInventoryHelpers.AddScript(H.Scene.AssetService, H.Prim, Item, UUID.Random(), "game", Game);
            inv.ExperienceID = Experience;
            Assert.True(H.Prim.Inventory.CreateScriptInstance(Item, 0, false, Phlox, RegionStart));
            H.Prim.ParentGroup.ResumeScripts();
            Assert.True(H.PumpUntil(() => H.Said.Contains("entry")), SavedStateRig.SaidText(H));
        }

        public void Command(string msg, string expect)
        {
            int before = H.Said.Count(s => s == expect);
            H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s == expect) > before), "no '" + expect + "' after '" + msg + "': " + SavedStateRig.SaidText(H));
        }

        public string Report()
        {
            int before = H.Said.Count(s => s.StartsWith("perms=", StringComparison.Ordinal));
            H.PostTouch(Item);
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s.StartsWith("perms=", StringComparison.Ordinal)) > before), SavedStateRig.SaidText(H));
            return H.Said.Last(s => s.StartsWith("perms=", StringComparison.Ordinal));
        }

        public void EnterParcel() => H.Scene.EventManager.TriggerAvatarEnteringNewParcel(Sp, 1, H.Scene.RegionInfo.RegionID);

        public void Dispose() => H.Dispose();
    }

    private static string Denied(UUID agent) => "xpdenied=" + agent + " 17";

    [Fact]
    public void TheGrantEndsWhenTheAvatarEntersAParcelWhereTheExperienceIsNoLongerAllowed()
    {
        using var r = new Rig();
        r.Command("xp " + r.Visitor, "xp=" + r.Visitor);
        Assert.Equal("perms=" + ExperiencePerms + " key=" + r.Visitor, r.Report());

        lock (r.Estate.Allowed) r.Estate.Allowed.Clear();
        r.EnterParcel();

        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(Denied(r.Visitor))), SavedStateRig.SaidText(r.H));
        Assert.Equal("perms=0 key=" + UUID.Zero, r.Report());
        Assert.Single(r.H.Said, s => s.StartsWith("xpdenied=", StringComparison.Ordinal));
        Assert.Equal(0, r.H.Prim.Inventory.GetInventoryItem(r.Item).PermsMask);
    }

    [Fact]
    public void TheGrantEndsWhenTheEstateBlocksTheExperience()
    {
        using var r = new Rig();
        r.Command("xp " + r.Visitor, "xp=" + r.Visitor);

        lock (r.Estate.Allowed) r.Estate.Blocked.Add(r.Experience);
        r.EnterParcel();

        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(Denied(r.Visitor))), SavedStateRig.SaidText(r.H));
        Assert.Equal("perms=0 key=" + UUID.Zero, r.Report());
    }

    [Fact]
    public void ControlsTakenUnderTheGrantAreReleased()
    {
        using var r = new Rig();
        r.Command("xp " + r.Visitor, "xp=" + r.Visitor);
        r.Command("take", "took");
        Assert.True(r.H.PumpUntil(() => r.Sp.HasScriptControls(r.Item)), "the controls were never taken");

        lock (r.Estate.Allowed) r.Estate.Allowed.Clear();
        r.EnterParcel();

        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(Denied(r.Visitor))), SavedStateRig.SaidText(r.H));
        Assert.False(r.Sp.HasScriptControls(r.Item), "the controls are still taken");
    }

    [Fact]
    public void WhereTheExperienceStillRunsTheGrantStays()
    {
        using var r = new Rig();
        r.Command("xp " + r.Visitor, "xp=" + r.Visitor);

        r.EnterParcel();
        // A trusted Experience that is no longer on the allowed list still runs here.
        lock (r.Estate.Allowed) { r.Estate.Allowed.Clear(); r.Estate.Trusted.Add(r.Experience); }
        r.EnterParcel();
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("xpdenied=", StringComparison.Ordinal));
        Assert.Equal("perms=" + ExperiencePerms + " key=" + r.Visitor, r.Report());
    }

    [Fact]
    public void AGrantThatDidNotComeFromTheExperienceIsLeftAlone()
    {
        using var r = new Rig();
        // The same avatar's grant, with the same bits, given otherwise than by the Experience (not noted as its grant).
        TaskInventoryItem item = r.H.Prim.Inventory.GetInventoryItem(r.Item);
        lock (r.H.Prim.TaskInventory) { item.PermsGranter = r.Visitor; item.PermsMask = ExperiencePerms; }

        lock (r.Estate.Allowed) r.Estate.Allowed.Clear();
        r.EnterParcel();
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("xpdenied=", StringComparison.Ordinal));
        Assert.Equal("perms=" + ExperiencePerms + " key=" + r.Visitor, r.Report());
    }
}
