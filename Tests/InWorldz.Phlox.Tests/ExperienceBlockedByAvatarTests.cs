/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Serialization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An avatar blocking an Experience ends the grants that avatar gave to that Experience's scripts. SL wiki
/// experience_permissions_denied, "When the experience can no longer run": "The agent has blocked the experience from the
/// experience profile." The core's ExperienceModule clears the item's grant, posts experience_permissions_denied with
/// XP_ERROR_NOT_PERMITTED (4) and raises EventManager.OnExperiencePermissionsRevoked; Phlox then ends what it keeps for the
/// grant as it does when the land ends it (the controls taken, the grant noted for the saved state, a request still
/// waiting), and tells the script nothing more. Grants under other Experiences and other avatars' grants stay.
/// </summary>
// Runs in parallel: each test has its own harnesses, avatars, items, assets and Experience module on its own scene, and
// rows under random item ids in the state database every harness shares; nothing process-wide is changed.
public class ExperienceBlockedByAvatarTests
{
    /// <summary>SL's list: TAKE_CONTROLS | TRIGGER_ANIMATION | ATTACH | TRACK_CAMERA | CONTROL_CAMERA | TELEPORT.</summary>
    private const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const int RegionStart = 0;
    private const int PermissionExperience = 0x2000;
    private const string Phlox = "InWorldz.Phlox";

    /// <summary>Driven over channel 7 as "NAME xp KEY" and "NAME take"; a touch reports the grant. NAME is replaced.</summary>
    private const string Game = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""NAME entry""); }
            touch_start(integer t) { llSay(0, ""NAME perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                if (llList2String(w, 0) != ""NAME"") return;
                string cmd = llList2String(w, 1);
                if (cmd == ""xp"") llRequestExperiencePermissions(llList2Key(w, 2), """");
                else if (cmd == ""take"") { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""NAME took""); }
            }
            experience_permissions(key a) { llSay(0, ""NAME xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""NAME xpdenied="" + (string)a + "" "" + (string)r); }
        }";

    /// <summary>IExperienceService stand-in: every permission change is stored, no avatar has one at login.</summary>
    public class StubExperienceService : DispatchProxy
    {
        public static IExperienceService Create() => DispatchProxy.Create<IExperienceService, StubExperienceService>();

        protected override object Invoke(MethodInfo m, object[] a)
        {
            if (m.Name == nameof(IExperienceService.FetchExperiencePermissions)) return new Dictionary<UUID, bool>();
            if (m.Name == nameof(IExperienceService.UpdateExperiencePermissions)) return true;
            Type rt = m.ReturnType;
            if (rt.IsArray) return Array.CreateInstance(rt.GetElementType()!, 0);
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    /// <summary>
    /// A region with the core's ExperienceModule and two Experiences the estate allows, X and Y; avatars A and B allowed
    /// both. Script "s1" is in X, "s2" in Y, "s3" in X.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly ExperienceModule Module = new();
        public readonly UUID X, Y, A, B;
        public readonly ScenePresence SpA, SpB;
        public readonly Dictionary<string, (UUID Item, UUID Asset, UUID Experience)> Scripts = new();

        public Rig(UUID x = default, UUID y = default, UUID a = default, UUID b = default, UUID owner = default,
                   Dictionary<string, (UUID Item, UUID Asset, UUID Experience)> restore = null)
        {
            X = x.IsZero() ? UUID.Random() : x;
            Y = y.IsZero() ? UUID.Random() : y;
            A = a.IsZero() ? UUID.Random() : a;
            B = b.IsZero() ? UUID.Random() : b;
            H = new SchedulerHarness();
            if (!owner.IsZero())
                foreach (SceneObjectPart p in H.Prim.ParentGroup.Parts) p.OwnerID = owner;

            H.Scene.RegisterModuleInterface<IExperienceService>(StubExperienceService.Create());
            H.Scene.RegionInfo.EstateSettings.AllowedExperiences = new[] { X, Y };
            var config = new IniConfigSource();
            config.AddConfig("Experience").Set("Enabled", "true");
            // After the engine's RegionLoaded, so the module's list of script modules holds Phlox, as on a region.
            SceneHelpers.SetupSceneModules(H.Scene, config, Module);

            SpA = SceneHelpers.AddScenePresence(H.Scene, A);
            SpB = SceneHelpers.AddScenePresence(H.Scene, B);
            foreach (UUID agent in new[] { A, B })
                foreach (UUID exp in new[] { X, Y })
                    Assert.True(Module.SetExperiencePermissions(agent, exp, true));

            if (restore == null)
            {
                Add("s1", X, UUID.Random(), UUID.Random());
                Add("s2", Y, UUID.Random(), UUID.Random());
                Add("s3", X, UUID.Random(), UUID.Random());
                foreach (string name in Scripts.Keys)
                    Assert.True(H.Prim.Inventory.CreateScriptInstance(Scripts[name].Item, 0, false, Phlox, RegionStart));
                H.Prim.ParentGroup.ResumeScripts();
                foreach (string name in Scripts.Keys)
                    Assert.True(H.PumpUntil(() => H.Said.Contains(name + " entry")), SavedStateRig.SaidText(H));
            }
            else
            {
                foreach (var kv in restore)
                {
                    TaskInventoryItem inv = Add(kv.Key, kv.Value.Experience, kv.Value.Item, kv.Value.Asset);
                    inv.PermsGranter = UUID.Random();   // whatever the region database held; the core zeroes it at the start
                    inv.PermsMask = 0x2;
                }
                Assert.Equal(restore.Count, H.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
                H.Prim.ParentGroup.ResumeScripts();
                foreach (var kv in restore)
                    Assert.True(H.PumpUntil(() => H.InterpreterFor(kv.Value.Item) != null), "the script did not load");
                H.PumpUntilIdle(TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(H.Said, s => s.EndsWith(" entry", StringComparison.Ordinal));   // restored, not started fresh
            }
        }

        private TaskInventoryItem Add(string name, UUID experience, UUID item, UUID asset)
        {
            var inv = TaskInventoryHelpers.AddScript(H.Scene.AssetService, H.Prim, item, asset, name, Game.Replace("NAME", name));
            inv.ExperienceID = experience;
            Scripts[name] = (item, asset, experience);
            return inv;
        }

        public UUID Item(string name) => Scripts[name].Item;

        public void Command(string msg, string expect)
        {
            int before = H.Said.Count(s => s == expect);
            H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s == expect) > before), "no '" + expect + "' after '" + msg + "': " + SavedStateRig.SaidText(H));
        }

        public void Grant(string name, UUID agent) => Command(name + " xp " + agent, name + " xp=" + agent);

        public string Report(string name)
        {
            string prefix = name + " perms=";
            int before = H.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));
            H.PostTouch(Item(name));
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal)) > before), SavedStateRig.SaidText(H));
            return H.Said.Last(s => s.StartsWith(prefix, StringComparison.Ordinal));
        }

        public int Denials(string name) => H.Said.Count(s => s.StartsWith(name + " xpdenied=", StringComparison.Ordinal));

        public void Dispose() => H.Dispose();
    }

    private static string Perms(string name, int mask, UUID key) => name + " perms=" + mask + " key=" + key;

    /// <summary>s1 (X) and s2 (Y) hold A's grant and took A's controls; s3 (X) holds B's grant and took B's controls.</summary>
    private static void GrantAll(Rig r)
    {
        r.Grant("s1", r.A);
        r.Grant("s2", r.A);
        r.Grant("s3", r.B);
        r.Command("s1 take", "s1 took");
        r.Command("s2 take", "s2 took");
        r.Command("s3 take", "s3 took");
        Assert.True(r.H.PumpUntil(() => r.SpA.HasScriptControls(r.Item("s1")) && r.SpA.HasScriptControls(r.Item("s2"))
                                         && r.SpB.HasScriptControls(r.Item("s3"))), "the controls were never taken");
    }

    private static void Block(Rig r, UUID agent, UUID experience)
        => Assert.True(r.Module.SetExperiencePermissions(agent, experience, false));

    private static void InvokeEngineHandler(SchedulerHarness h, UUID partId, UUID itemId, UUID granter, UUID experience, int mask, int reason)
    {
        MethodInfo handler = typeof(PhloxEngine).GetMethod("OnExperiencePermissionsRevoked", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handler);
        handler!.Invoke(h.Engine, new object[] { partId, itemId, granter, experience, mask, reason });
    }

    [Fact]
    public void ABlockEndsOnlyThatAvatarsGrantUnderThatExperience()
    {
        using var r = new Rig();
        GrantAll(r);

        Block(r, r.A, r.X);

        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains("s1 xpdenied=" + r.A + " 4")), SavedStateRig.SaidText(r.H));
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(Perms("s1", 0, UUID.Zero), r.Report("s1"));
        Assert.False(r.SpA.HasScriptControls(r.Item("s1")), "s1 still holds A's controls");
        Assert.True(Api(r.H, r.Item("s1")).HoldsExperienceGrantFrom(r.A) == false, "s1 still notes A's Experience grant");

        // A's grant under the other Experience, and B's grant under the blocked one, stay with their controls.
        Assert.Equal(Perms("s2", ExperiencePerms, r.A), r.Report("s2"));
        Assert.Equal(Perms("s3", ExperiencePerms, r.B), r.Report("s3"));
        Assert.True(r.SpA.HasScriptControls(r.Item("s2")), "s2 lost A's controls");
        Assert.True(r.SpB.HasScriptControls(r.Item("s3")), "s3 lost B's controls");
        Assert.Equal(1, r.Denials("s1"));
        Assert.Equal(0, r.Denials("s2"));
        Assert.Equal(0, r.Denials("s3"));
    }

    [Fact]
    public void TheEndedGrantStaysEndedAfterARegionStopAndStart()
    {
        UUID owner = UUID.Random();
        Dictionary<string, (UUID, UUID, UUID)> scripts;
        UUID x, y, a, b;
        using (var r = new Rig(owner: owner))
        {
            GrantAll(r);
            Block(r, r.A, r.X);
            Assert.True(r.H.PumpUntil(() => r.H.Said.Contains("s1 xpdenied=" + r.A + " 4")), SavedStateRig.SaidText(r.H));
            r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
            scripts = new(r.Scripts);
            (x, y, a, b) = (r.X, r.Y, r.A, r.B);
            r.H.StopRegionAsTheSimulatorDoes();
        }

        SerializedRuntimeState row1 = StateManager.Decode(SavedStateRig.Row(scripts["s1"].Item1)!.Value.Blob);
        Assert.Equal(0, row1.GrantedPermsMask);

        using var r2 = new Rig(x, y, a, b, owner, scripts);
        Assert.Equal(Perms("s1", 0, UUID.Zero), r2.Report("s1"));
        Assert.Equal(Perms("s2", ExperiencePerms, a), r2.Report("s2"));
        Assert.Equal(Perms("s3", ExperiencePerms, b), r2.Report("s3"));
        Assert.Equal(0, r2.H.Prim.Inventory.GetInventoryItem(scripts["s1"].Item1).PermsMask);
    }

    [Fact]
    public void AWaitingRequestForThatAvatarGetsTheDenialOnce()
    {
        using var r = new Rig();
        r.Grant("s1", r.A);
        // A forgets the Experience in the profile, so the script's new request asks A again and waits.
        var client = (TestClient)r.SpA.ControllingClient;
        Assert.True(r.Module.ForgetExperiencePermissions(r.A, r.X));
        int asked = client.ScriptQuestions.Count;
        r.H.Scene.SimChat("s1 xp " + r.A, ChatTypeEnum.Region, 7, r.H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(r.H.PumpUntil(() => client.ScriptQuestions.Count > asked), "no Experience question was sent");

        Block(r, r.A, r.X);
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains("s1 xpdenied=" + r.A + " 4")), SavedStateRig.SaidText(r.H));
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        // The dialog is then closed without accepting: the request was already answered.
        client.FireScriptAnswer(r.H.Prim.UUID, r.Item("s1"), 0);
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        Assert.Equal(1, r.Denials("s1"));
        Assert.Equal(Perms("s1", 0, UUID.Zero), r.Report("s1"));
    }

    [Fact]
    public void TheSignalForAnAvatarWithNoGrantOrDuringARegionStopDoesNothing()
    {
        using var r = new Rig();
        GrantAll(r);
        UUID stranger = UUID.Random();
        SceneHelpers.AddScenePresence(r.H.Scene, stranger);

        // The signal names an avatar who gave s1 nothing: A's grant and controls stay.
        InvokeEngineHandler(r.H, r.H.Prim.UUID, r.Item("s1"), stranger, r.X, ExperiencePerms, 4);
        // And an item this engine does not run.
        InvokeEngineHandler(r.H, r.H.Prim.UUID, UUID.Random(), r.A, r.X, ExperiencePerms, 4);
        r.H.Scene.EventManager.TriggerExperiencePermissionsRevoked(r.H.Prim.UUID, r.Item("s1"), stranger, r.X, ExperiencePerms, 4);
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(Perms("s1", ExperiencePerms, r.A), r.Report("s1"));
        Assert.True(r.SpA.HasScriptControls(r.Item("s1")), "s1 lost A's controls");
        Assert.Equal(0, r.Denials("s1"));

        // During the region's stop: nothing changes and nothing throws.
        r.H.StopRegionAsTheSimulatorDoes();
        InvokeEngineHandler(r.H, r.H.Prim.UUID, r.Item("s1"), r.A, r.X, ExperiencePerms, 4);
        r.H.Scene.EventManager.TriggerExperiencePermissionsRevoked(r.H.Prim.UUID, r.Item("s1"), r.A, r.X, ExperiencePerms, 4);
        SerializedRuntimeState row = StateManager.Decode(SavedStateRig.Row(r.Item("s1"))!.Value.Blob);
        Assert.Equal(ExperiencePerms, row.GrantedPermsMask);
        Assert.Equal(r.A.ToString(), row.PermsGranter);
    }

    private static LSLSystemAPI Api(SchedulerHarness h, UUID item)
        => ((Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(SavedStateRig.Exe(h), "m_Apis"))[item];
}
