/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A permission request still waiting for its answer ends with a reset, an owner change, and the script leaving the
/// region (an unload: a crossing, a derez). SL wiki llRequestExperiencePermissions: "Outstanding permission requests will
/// be lost if the script is de-rezzed, moved to another region, or reset." The llRequestPermissions page says nothing
/// on a waiting request; its only rule on two requests is "If a script makes two permission requests, whichever response
/// is last is considered the granted permissions", so the same rule is applied to it. An answer that arrives after the
/// request ended installs nothing and posts no event; the Experience request's timeout is stopped with it.
/// </summary>
// Runs in parallel: each test has its own harness or regions (7380, 7380) and (7380, 7379), used by no other test;
// avatars, items and Experience modules are its own. Nothing process-wide is changed.
public class PendingPermissionRequestTests
{
    private const int TriggerAnimation = 0x10, Experience = 0x2000;
    private const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const string Phlox = "InWorldz.Phlox";

    /// <summary>Driven over channel 7: "ask KEY MASK", "xp KEY", "reset".</summary>
    private const string Asker = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                string cmd = llList2String(w, 0);
                if (cmd == ""ask"") { llRequestPermissions(llList2Key(w, 1), (integer)llList2String(w, 2)); llSay(0, ""asked""); }
                else if (cmd == ""xp"") { llRequestExperiencePermissions(llList2Key(w, 1), """"); llSay(0, ""asked""); }
                else if (cmd == ""reset"") llResetScript();
            }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
            experience_permissions(key a) { llSay(0, ""xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""xpdenied="" + (string)a + "" "" + (string)r); }
        }";

    /// <summary>The estate allows the Experience; nobody has answered it yet, so a request asks. Every write is recorded.</summary>
    internal class RecordingEstate : ExperienceWithdrawnTests.ChangingEstate
    {
        public readonly List<string> Writes = new();

        public static RecordingEstate Create(UUID experience, out IExperienceModule module)
        {
            module = Create<IExperienceModule, RecordingEstate>();
            var stub = (RecordingEstate)(object)module;
            stub.Experience = experience;
            stub.Visitor = UUID.Random();   // nobody this test asks
            stub.Allowed.Add(experience);
            return stub;
        }

        protected override object Invoke(MethodInfo m, object[] a)
        {
            if (m.Name.StartsWith("Set", StringComparison.Ordinal))
                lock (Writes) Writes.Add(m.Name);
            return base.Invoke(m, a);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H = new();
        public readonly UUID Exp = UUID.Random(), Item = UUID.Random();
        public readonly RecordingEstate Estate;
        public readonly TestClient Client;

        public Rig()
        {
            Estate = RecordingEstate.Create(Exp, out IExperienceModule module);
            H.Scene.RegisterModuleInterface(module);
            Client = (TestClient)SceneHelpers.AddScenePresence(H.Scene, UUID.Random()).ControllingClient;
            var inv = TaskInventoryHelpers.AddScript(H.Scene.AssetService, H.Prim, Item, UUID.Random(), "asker", Asker);
            inv.ExperienceID = Exp;
            Assert.True(H.Prim.Inventory.CreateScriptInstance(Item, 0, false, Phlox, 0));
            H.Prim.ParentGroup.ResumeScripts();
            Assert.True(H.PumpUntil(() => H.Said.Contains("entry")), SavedStateRig.SaidText(H));
        }

        public void Command(string msg, string expect)
        {
            int before = H.Said.Count(s => s == expect);
            H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(H.PumpUntil(() => H.Said.Count(s => s == expect) > before), "no '" + expect + "' after '" + msg + "': " + SavedStateRig.SaidText(H));
        }

        /// <summary>The request is sent, and the dialog reached the avatar.</summary>
        public void Ask(bool experience)
        {
            int asked = Client.ScriptQuestions.Count;
            Command(experience ? "xp " + Client.AgentId : "ask " + Client.AgentId + " " + TriggerAnimation, "asked");
            Assert.True(H.PumpUntil(() => Client.ScriptQuestions.Count > asked), "no question was sent");
            Assert.True(Waiting(Api), "the request is not waiting");
        }

        public LSLSystemAPI Api => ((Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(SavedStateRig.Exe(H), "m_Apis"))[Item];

        public void Answer(bool experience)
            => Client.FireScriptAnswer(H.Prim.UUID, Item, experience ? Experience : TriggerAnimation);

        public void Dispose() => H.Dispose();
    }

    /// <summary>The API still holds a request waiting for an answer (either kind).</summary>
    private static bool Waiting(LSLSystemAPI api)
        => SavedStateRig.Field(api, "m_waitingForScriptAnswer") != null
           || ((ICollection)SavedStateRig.Field(api, "m_pendingExpPerms")).Count != 0;

    private static bool Answered(IEnumerable<string> said)
        => said.Any(s => s.StartsWith("rtp=", StringComparison.Ordinal) || s.StartsWith("xp", StringComparison.Ordinal));

    /// <summary>The late answer is given; nothing may come of it within the window.</summary>
    private static void AnswerLate(Rig r, bool experience)
    {
        r.H.ClearSaid(r.Item);
        r.Answer(experience);
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));   // a window for what must NOT happen
        Assert.False(Answered(r.H.Said), SavedStateRig.SaidText(r.H));
        Assert.Equal(0, r.H.Prim.Inventory.GetInventoryItem(r.Item).PermsMask);
        lock (r.Estate.Writes) Assert.Empty(r.Estate.Writes);
    }

    // ── a normal answer ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAnswerToAWaitingRequestIsTaken(bool experience)
    {
        using var r = new Rig();
        r.Ask(experience);
        r.Answer(experience);
        string expect = experience ? "xp=" + r.Client.AgentId : "rtp=" + TriggerAnimation;
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(expect)), SavedStateRig.SaidText(r.H));
        Assert.Equal(experience ? ExperiencePerms : TriggerAnimation, r.H.Prim.Inventory.GetInventoryItem(r.Item).PermsMask);
        Assert.False(Waiting(r.Api));
    }

    // ── a reset ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALateAnswerAfterAResetInstallsNothing(bool experience)
    {
        using var r = new Rig();
        r.Ask(experience);
        r.Command("reset", "entry");
        AnswerLate(r, experience);
        Assert.False(Waiting(r.Api), "the reset left the request waiting");
    }

    // ── an owner change ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALateAnswerAfterAnOwnerChangeInstallsNothing(bool experience)
    {
        using var r = new Rig();
        r.Ask(experience);
        // The owner change's own end of permissions forgets the Control record: that says it has run.
        var interp = (Interpreter)r.H.InterpreterFor(r.Item);
        lock (interp.ScriptState.EventQueueLock)
            interp.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] = new object[] { 1, 1, 0 };
        UUID buyer = UUID.Random();
        r.H.Prim.ParentGroup.SetOwner(buyer, UUID.Zero);
        r.H.Prim.Inventory.ChangeInventoryOwner(buyer);
        Assert.True(r.H.PumpUntil(() => !interp.ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control)),
            "the owner change did not reach the script");
        AnswerLate(r, experience);
        Assert.False(Waiting(r.Api), "the owner change left the request waiting");
    }

    // ── a crossing ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALateAnswerAfterACrossingInstallsNothingOnEitherSide(bool experience)
    {
        var (a, b) = VehicleCrossingStateTests.TwoRegions(7380);
        using var ra = a;
        using var rb = b;
        UUID exp = UUID.Random();
        var estateA = RecordingEstate.Create(exp, out IExperienceModule moduleA);
        a.Scene.RegisterModuleInterface(moduleA);
        var estateB = RecordingEstate.Create(exp, out IExperienceModule moduleB);
        b.Scene.RegisterModuleInterface(moduleB);
        var client = (TestClient)SceneHelpers.AddScenePresence(a.Scene, UUID.Random()).ControllingClient;

        var sog = SceneHelpers.AddSceneObject(a.Scene, "Example Vehicle", UUID.Random());
        sog.AbsolutePosition = new Vector3(128, 3, 30);
        UUID objectId = sog.UUID, item = UUID.Random();
        var inv = TaskInventoryHelpers.AddScript(a.Scene.AssetService, sog.RootPart, item, UUID.Random(), "asker", Asker);
        inv.ExperienceID = exp;
        sog.CreateScriptInstances(0, true, a.Engine.Name, 1);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => a.Heard("entry")), a.Text());

        int asked = client.ScriptQuestions.Count;
        a.Scene.SimChat(experience ? "xp " + client.AgentId : "ask " + client.AgentId + " " + TriggerAnimation,
            ChatTypeEnum.Region, 7, sog.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => client.ScriptQuestions.Count > asked), "no question was sent: " + a.Text());
        LSLSystemAPI apiA = VehicleCrossingStateTests.Api(a, item);
        Assert.True(VehicleCrossingStateTests.WaitFor(() => Waiting(apiA)), "the request is not waiting");

        sog.UpdateGroupPosition(new Vector3(128, -5, 30));
        Assert.True(VehicleCrossingStateTests.WaitFor(() => b.Scene.GetSceneObjectGroup(objectId) != null), "the object did not reach region B");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => VehicleCrossingStateTests.Api(b, item) != null), "the script did not start in region B");
        Assert.True(VehicleCrossingStateTests.WaitFor(() => VehicleCrossingStateTests.Api(a, item) == null), "the script is still loaded in region A");
        Assert.False(Waiting(apiA), "the crossing left the request waiting in region A");

        lock (a.Said) a.Said.Clear();
        lock (b.Said) b.Said.Clear();
        client.FireScriptAnswer(objectId, item, experience ? Experience : TriggerAnimation);
        System.Threading.Thread.Sleep(2000);   // a window for what must NOT happen; both regions' schedulers run on their own threads
        lock (a.Said) Assert.False(Answered(a.Said), a.Text());
        lock (b.Said) Assert.False(Answered(b.Said), b.Text());
        Assert.Equal(0, b.Scene.GetSceneObjectGroup(objectId).RootPart.Inventory.GetInventoryItem(item).PermsMask);
        lock (estateA.Writes) Assert.Empty(estateA.Writes);
        lock (estateB.Writes) Assert.Empty(estateB.Writes);
    }
}
