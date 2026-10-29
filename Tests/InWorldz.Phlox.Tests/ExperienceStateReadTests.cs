/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Linq;
using System.Threading;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using ProtoBuf;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A grant a script already holds from an Experience ends when the engine's background read of that Experience's state
/// finds it disabled by its owner or suspended: the grant and the controls it took go, and the script is told once with
/// experience_permissions_denied and the state's code, XP_ERROR_EXPERIENCE_DISABLED (8) or XP_ERROR_EXPERIENCE_SUSPENDED
/// (9), 8 when both are set (SL wiki llGetExperienceErrorMessage). The read runs off the scheduler thread, through the
/// core's Experience lookup; a lookup the engine makes for a script call that finds the same ends the grants too. The
/// calls that use a grant ask nothing. Here the read is started directly (ReadExperienceStatesNow); its interval is
/// tested on the engine's clock in <see cref="ExperienceStateReadTimingTests"/>.
/// </summary>
// Runs in parallel: each test has its own harness, avatar, Experience service stand-in and Experience module on its own
// scene, and rows under random item ids in the state database every harness shares; nothing process-wide is changed.
public class ExperienceStateReadTests
{
    /// <summary>SL's list: TAKE_CONTROLS | TRIGGER_ANIMATION | ATTACH | TRACK_CAMERA | CONTROL_CAMERA | TELEPORT.</summary>
    internal const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const int TakeControls = 0x4;
    internal const int Disabled = (int)ExperienceFlags.Disabled;
    internal const int Suspended = (int)ExperienceFlags.Suspended;
    private const int RegionStart = 0, NewRez = 1;
    private const string Phlox = "InWorldz.Phlox";

    /// <summary>Driven over channel 7 as "NAME command ..."; a touch reports the grant. Every line starts with NAME.</summary>
    private const string Game = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""NAME entry""); }
            touch_start(integer t) { llSay(0, ""NAME perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                if (llList2String(w, 0) != ""NAME"") return;
                string cmd = llList2String(w, 1);
                if (cmd == ""xp"") llRequestExperiencePermissions(llList2Key(w, 2), """");
                else if (cmd == ""ask"") llRequestPermissions(llList2Key(w, 2), (integer)llList2String(w, 3));
                else if (cmd == ""take"") { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""NAME took""); }
                else if (cmd == ""details"") llSay(0, ""NAME details="" + llList2CSV(llGetExperienceDetails(NULL_KEY)));
            }
            experience_permissions(key a) { llSay(0, ""NAME xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""NAME xpdenied="" + (string)a + "" "" + (string)r); }
            run_time_permissions(integer p) { llSay(0, ""NAME rtp="" + (string)p); }
        }";

    internal static string Source(string name) => Game.Replace("NAME", name);

    /// <summary>
    /// A region with the core's ExperienceModule over a service that knows Experiences X and Y, both allowed in the
    /// estate; avatar A is here and has allowed both, so a request from a script in either is granted with no dialog.
    /// </summary>
    internal sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly ExperienceModule Module = new();
        public readonly ExperienceStateTests.StateService Service;
        public readonly UUID X = UUID.Random(), Y = UUID.Random(), A = UUID.Random();
        public readonly ScenePresence Sp;
        private readonly System.Collections.Generic.Dictionary<string, UUID> m_items = new();

        public Rig(SchedulerHarness h = null)
        {
            H = h ?? new SchedulerHarness();
            Service = ExperienceStateTests.StateService.Create(Info(X));
            Service.Others[Y] = Info(Y);
            H.Scene.RegisterModuleInterface<IExperienceService>((IExperienceService)(object)Service);
            H.Scene.RegionInfo.EstateSettings.AllowedExperiences = new[] { X, Y };
            var config = new IniConfigSource();
            config.AddConfig("Experience").Set("Enabled", "true");
            SceneHelpers.SetupSceneModules(H.Scene, config, Module);
            Sp = SceneHelpers.AddScenePresence(H.Scene, A);
            Assert.True(Module.SetExperiencePermissions(A, X, true));
            Assert.True(Module.SetExperiencePermissions(A, Y, true));
        }

        private static ExperienceInfo Info(UUID id) => new()
        {
            public_id = id, owner_id = UUID.Random(), group_id = UUID.Random(), name = "Example Experience", properties = 0
        };

        public PhloxExecutionScheduler Exe => SavedStateRig.Exe(H);

        /// <summary>X's state at the service from now on.</summary>
        public int XProperties { set => Service.Info.properties = value; }

        /// <summary>The game script as <paramref name="name"/>, compiled into <paramref name="experience"/>, started.</summary>
        public UUID Script(string name, UUID experience)
        {
            UUID item = UUID.Random();
            var inv = TaskInventoryHelpers.AddScript(H.Scene.AssetService, H.Prim, item, UUID.Random(), name, Source(name));
            inv.ExperienceID = experience;
            Assert.True(H.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, RegionStart));
            H.Prim.ParentGroup.ResumeScripts();
            Assert.True(H.PumpUntil(() => H.Said.Contains(name + " entry")), SavedStateRig.SaidText(H));
            m_items[name] = item;
            return item;
        }

        public UUID Item(string name) => m_items[name];

        public void Say(string msg, string expect)
        {
            int before = H.Said.Count(s => s == expect);
            H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s == expect) > before), "no '" + expect + "' after '" + msg + "': " + SavedStateRig.SaidText(H));
        }

        /// <summary><paramref name="name"/> asks A through its Experience and is granted.</summary>
        public void Grant(string name) => Say(name + " xp " + A, name + " xp=" + A);

        public string Report(string name)
        {
            string prefix = name + " perms=";
            int before = H.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));
            H.PostTouch(Item(name));
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal)) > before), SavedStateRig.SaidText(H));
            return H.Said.Last(s => s.StartsWith(prefix, StringComparison.Ordinal));
        }

        public string Perms(string name, int mask, UUID key) => name + " perms=" + mask + " key=" + key;

        public string Denied(string name, int code) => name + " xpdenied=" + A + " " + code;

        public int Denials(string name) => H.Said.Count(s => s.StartsWith(name + " xpdenied=", StringComparison.Ordinal));

        public int MaskOf(string name) => H.Prim.Inventory.GetInventoryItem(Item(name)).PermsMask;

        /// <summary>
        /// The game script's state as an object would carry it, holding A's grant from X, handed to a new object of the
        /// same owner whose script item is in X, and started by the core as a rez. A is here and allows X, so the grant
        /// comes back.
        /// </summary>
        public (SceneObjectGroup Copy, UUID Item) CarryIn(string name)
        {
            UUID owner = H.Prim.OwnerID;
            var asset = UUID.Random();
            var source = UUID.Random();
            TaskInventoryHelpers.AddScript(H.Scene.AssetService, H.Prim, source, asset, name, Source(name));
            Assert.True(H.Prim.Inventory.CreateScriptInstance(source, 0, false, Phlox, NewRez));
            Assert.True(H.PumpUntil(() => H.Said.Contains(name + " entry")), SavedStateRig.SaidText(H));
            H.PumpUntilIdle(TimeSpan.FromSeconds(2));
            SerializedRuntimeState st = StateManager.Decode(StateManager.CaptureBlob((Interpreter)H.InterpreterFor(source)));
            st.PermsGranter = A.ToString();
            st.GrantedPermsMask = ExperiencePerms;
            st.PermsOwner = owner.ToString();
            st.PermsExperience = X.ToString();
            byte[] blob;
            using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
            SavedStateRig.PostRemove(H, H.Prim, source);
            H.Prim.Inventory.RemoveInventoryItem(source);
            H.PumpUntilIdle(TimeSpan.FromSeconds(2));

            var copy = SceneHelpers.AddSceneObject(H.Scene, "Example Copy", owner);
            copy.AbsolutePosition = H.Prim.AbsolutePosition + new Vector3(4, 0, 0);
            var item = UUID.Random();
            var inv = TaskInventoryHelpers.AddScript(H.Scene.AssetService, copy.RootPart, item, asset, "copy", Source(name));
            inv.ExperienceID = X;
            SavedStateRig.States(H).Carry(item, asset, blob);
            Assert.Equal(1, copy.CreateScriptInstances(0, true, Phlox, NewRez));
            Assert.True(H.PumpUntil(() => H.InterpreterFor(item) != null), "the rezzed script did not load");
            H.PumpUntilIdle(TimeSpan.FromSeconds(5));
            Assert.Equal(ExperiencePerms, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
            return (copy, item);
        }

        /// <summary>One read of the held Experiences' state, started now, run to its end.</summary>
        public void Read()
        {
            int before = Service.Lookups;
            Exe.ReadExperienceStatesNow();
            Assert.True(H.PumpUntil(() => Service.Lookups > before && !Exe.ExperienceStateReadRunning),
                "no read finished; lookups " + before + " -> " + Service.Lookups);
            H.PumpUntilIdle(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Service.Gate?.Set();
            H.Dispose();
        }
    }

    [Theory]
    [InlineData(Suspended, 9)]
    [InlineData(Disabled, 8)]
    [InlineData(Disabled | Suspended, 8)]
    public void AHeldGrantEndsAtTheNextReadAfterItsExperienceCannotRunAndTheScriptIsToldOnce(int properties, int code)
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");
        Assert.Equal(r.Perms("s1", ExperiencePerms, r.A), r.Report("s1"));

        r.XProperties = properties;
        r.Read();
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", code))), SavedStateRig.SaidText(r.H));
        Assert.Equal(0, r.MaskOf("s1"));
        Assert.Equal(r.Perms("s1", 0, UUID.Zero), r.Report("s1"));

        // A later read finds no grant from it and tells nothing more.
        r.Exe.ReadExperienceStatesNow();
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(1, r.Denials("s1"));
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("s1 rtp=", StringComparison.Ordinal));

        // What the engine keeps for the grant went with it: the next row holds no grant.
        r.H.SaveState(r.Item("s1"));
        SavedStateRig.WaitForWrites(r.H);
        SerializedRuntimeState row = StateManager.Decode(SavedStateRig.Row(r.Item("s1"))!.Value.Blob);
        Assert.Null(row.PermsGranter);
        Assert.Null(row.PermsExperience);
    }

    [Fact]
    public void AScriptThatMakesNoCallAfterItsGrantStillLosesItAndItsControls()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");
        r.Say("s1 take", "s1 took");
        Assert.True(r.H.PumpUntil(() => r.Sp.HasScriptControls(r.Item("s1"))), "the controls were never taken");

        // From here the script calls nothing: only the read can end its grant.
        r.XProperties = Suspended;
        r.Read();
        Assert.True(r.H.PumpUntil(() => !r.Sp.HasScriptControls(r.Item("s1"))), "the controls are still taken");
        Assert.Equal(0, r.MaskOf("s1"));
        Assert.Equal(1, r.Denials("s1"));
        Assert.Contains(r.Denied("s1", 9), r.H.Said);
    }

    [Fact]
    public void AnEnabledExperiencesGrantIsUntouchedByARead()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");

        r.Read();
        r.Read();
        Assert.Equal(r.Perms("s1", ExperiencePerms, r.A), r.Report("s1"));
        Assert.Equal(0, r.Denials("s1"));
    }

    [Fact]
    public void AnOrdinaryGrantAndAGrantFromAnotherExperienceAreUntouched()
    {
        using var r = new Rig();
        r.Script("sx", r.X);
        r.Script("sy", r.Y);
        r.Script("so", UUID.Zero);
        r.Grant("sx");
        r.Grant("sy");
        var client = (TestClient)r.Sp.ControllingClient;
        int questions = client.ScriptQuestions.Count;
        r.H.Scene.SimChat("so ask " + r.A + " " + TakeControls, ChatTypeEnum.Region, 7, r.H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(r.H.PumpUntil(() => client.ScriptQuestions.Count > questions), "no question for the ordinary request");
        client.FireScriptAnswer(r.H.Prim.UUID, r.Item("so"), TakeControls);
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains("so rtp=" + TakeControls)), SavedStateRig.SaidText(r.H));

        r.XProperties = Suspended;
        r.Read();
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("sx", 9))), SavedStateRig.SaidText(r.H));
        Assert.Equal(r.Perms("sx", 0, UUID.Zero), r.Report("sx"));
        Assert.Equal(r.Perms("sy", ExperiencePerms, r.A), r.Report("sy"));
        Assert.Equal(r.Perms("so", TakeControls, r.A), r.Report("so"));
        Assert.Equal(0, r.Denials("sy"));
        Assert.Equal(0, r.Denials("so"));
    }

    [Fact]
    public void AFailedReadChangesNothingAndTheNextReadStillWorks()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");

        r.XProperties = Suspended;
        r.Service.LookupFails = true;
        r.Read();
        Assert.Equal(r.Perms("s1", ExperiencePerms, r.A), r.Report("s1"));
        Assert.Equal(0, r.Denials("s1"));

        r.Service.LookupFails = false;
        r.Read();
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", 9))), SavedStateRig.SaidText(r.H));
        Assert.Equal(1, r.Denials("s1"));
    }

    [Fact]
    public void ASlowReadDoesNotStartASecondOne()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");

        int before = r.Service.Lookups;
        r.Service.Gate = new ManualResetEventSlim(false);
        r.Exe.ReadExperienceStatesNow();
        Assert.True(r.H.PumpUntil(() => r.Service.Lookups == before + 1), "the read never asked");
        Assert.True(r.Exe.ExperienceStateReadRunning);

        // Asked again while the first is still waiting on the service: no second read starts.
        r.Exe.ReadExperienceStatesNow();
        r.H.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(before + 1, r.Service.Lookups);

        // The slow read answers that X is suspended; that ends the grant, once.
        r.XProperties = Suspended;
        r.Service.Gate.Set();
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", 9))), SavedStateRig.SaidText(r.H));
        Assert.True(r.H.PumpUntil(() => !r.Exe.ExperienceStateReadRunning), "the read never finished");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(1, r.Denials("s1"));
    }

    [Fact]
    public void AReadAfterASlowOneFinishedStartsAgain()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");

        r.Service.Gate = new ManualResetEventSlim(false);
        int before = r.Service.Lookups;
        r.Exe.ReadExperienceStatesNow();
        Assert.True(r.H.PumpUntil(() => r.Service.Lookups == before + 1), "the read never asked");
        r.Service.Gate.Set();
        Assert.True(r.H.PumpUntil(() => !r.Exe.ExperienceStateReadRunning), "the read never finished");

        r.XProperties = Suspended;
        r.Read();
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", 9))), SavedStateRig.SaidText(r.H));
    }

    [Fact]
    public void AGrantCarriedInWithAnObjectIsCovered()
    {
        using var r = new Rig();
        var (copy, item) = r.CarryIn("src");

        r.XProperties = Suspended;
        r.Read();
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("src", 9))), SavedStateRig.SaidText(r.H));
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal(1, r.Denials("src"));
    }

    [Fact]
    public void ALookupForAScriptCallThatFindsTheExperienceSuspendedEndsTheGrantsHeldFromIt()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Script("s2", r.X);
        r.Grant("s1");

        // No read: s2's llGetExperienceDetails is the lookup that sees X suspended.
        r.XProperties = Suspended;
        r.Say("s2 details", "s2 details=Example Experience, " + r.Service.Info.owner_id + ", " + r.X + ", 9, experience is suspended, " + r.Service.Info.group_id);
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", 9))), SavedStateRig.SaidText(r.H));
        Assert.Equal(0, r.MaskOf("s1"));
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(1, r.Denials("s1"));
        Assert.Equal(0, r.Denials("s2"));
    }

    [Fact]
    public void ARefusedRequestEndsTheGrantTheScriptAlreadyHoldsFromThatExperience()
    {
        using var r = new Rig();
        UUID b = UUID.Random();
        SceneHelpers.AddScenePresence(r.H.Scene, b);
        Assert.True(r.Module.SetExperiencePermissions(b, r.X, true));
        r.Script("s1", r.X);
        r.Grant("s1");

        // The script asks B; the request's lookup sees X suspended: the request is refused, and A's grant ends.
        r.XProperties = Suspended;
        r.Say("s1 xp " + b, "s1 xpdenied=" + b + " 9");
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", 9))), SavedStateRig.SaidText(r.H));
        Assert.Equal(r.Perms("s1", 0, UUID.Zero), r.Report("s1"));
    }

    [Fact]
    public void UsingAHeldGrantAsksTheServiceNothing()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        int before = r.Service.Lookups;
        r.XProperties = Suspended;
        r.Say("s1 take", "s1 took");
        Assert.Equal(r.Perms("s1", ExperiencePerms, r.A), r.Report("s1"));
        Assert.True(r.Sp.HasScriptControls(r.Item("s1")));
        Assert.Equal(before, r.Service.Lookups);
    }

    // ── a region stop ──

    [Fact]
    public void NoReadStartsOnceTheRegionStops()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        r.XProperties = Suspended;
        r.H.StopRegionAsTheSimulatorDoes();
        int before = r.Service.Lookups;
        r.Exe.ReadExperienceStatesNow();
        r.H.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(before, r.Service.Lookups);
        Assert.False(r.Exe.ExperienceStateReadRunning);
        Assert.Equal(0, r.Denials("s1"));
        Assert.Equal(ExperiencePerms, r.MaskOf("s1"));
    }

    [Fact]
    public void AReadInFlightAtTheStopEndsNoGrantAfterTheFinalSaveAndLeavesNothingRunning()
    {
        using var r = new Rig();
        r.Script("s1", r.X);
        r.Grant("s1");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        // A read is waiting on the service when the region stops; the service then answers that X is suspended.
        r.Service.Gate = new ManualResetEventSlim(false);
        int before = r.Service.Lookups;
        r.Exe.ReadExperienceStatesNow();
        Assert.True(r.H.PumpUntil(() => r.Service.Lookups == before + 1), "the read never asked");
        r.XProperties = Suspended;
        r.H.StopRegionAsTheSimulatorDoes();
        Assert.Equal(r.X.ToString(), StateManager.Decode(SavedStateRig.Row(r.Item("s1"))!.Value.Blob).PermsExperience);

        r.Service.Gate.Set();
        Assert.True(r.H.PumpUntil(() => !r.Exe.ExperienceStateReaderAlive), "the reader is still running");
        r.H.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(0, r.Denials("s1"));
        Assert.Equal(ExperiencePerms, r.MaskOf("s1"));
        Assert.Equal(r.X.ToString(), StateManager.Decode(SavedStateRig.Row(r.Item("s1"))!.Value.Blob).PermsExperience);
    }

    [Fact]
    public void AReadInFlightAtTheStopMakesNoFurtherLookup()
    {
        using var r = new Rig();
        r.Script("sx", r.X);
        r.Script("sy", r.Y);
        r.Grant("sx");
        r.Grant("sy");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        // Two Experiences to read; the region stops while the first lookup waits.
        r.Service.Gate = new ManualResetEventSlim(false);
        int before = r.Service.Lookups;
        r.Exe.ReadExperienceStatesNow();
        Assert.True(r.H.PumpUntil(() => r.Service.Lookups == before + 1), "the read never asked");
        r.H.StopRegionAsTheSimulatorDoes();
        r.Service.Gate.Set();
        Assert.True(r.H.PumpUntil(() => !r.Exe.ExperienceStateReaderAlive), "the reader is still running");
        Assert.Equal(before + 1, r.Service.Lookups);
    }
}
