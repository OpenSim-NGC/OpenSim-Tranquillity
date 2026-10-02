/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script's state travels inside its object: the core asks the engine for it when the object is serialized (take,
/// take copy, detach, a crossing, a teleport's attachments) and hands it back before the scripts start. SL: "A script will
/// NOT automatically re-enter the default state state_entry event when the task is rezzed or attached (even by a new
/// owner), nor if the task is moved to another SIM" (wiki, State). State another engine wrote, or that cannot be read,
/// gives a fresh start and never a failed load.
/// </summary>
// Runs in parallel: each test has its own harness, items and assets; nothing process-wide is changed.
public class CarriedStateTests
{
    private const int NewRez = 1;
    private const int DebugChannel = 2147483647;

    private const string Counter = @"
        integer n;
        default {
            state_entry() { n = 5; llSay(0, ""entry""); }
            touch_start(integer t) { n++; llSay(0, ""n="" + (string)n); }
            on_rez(integer p) { llSay(0, ""rez n="" + (string)n + "" sp="" + (string)llGetStartParameter()); }
        }";

    private static UUID Running(SchedulerHarness h, string src, UUID asset, UUID item)
    {
        h.RezScript(src, asset, item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        return item;
    }

    private static void Touch(SchedulerHarness h, UUID item, string expect)
    {
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains(expect)), "no '" + expect + "': " + SavedStateRig.SaidText(h));
    }

    private static string Envelope(string engine, UUID item, UUID asset, string version, string data)
        => $"<State Engine=\"{engine}\" UUID=\"{item}\" Asset=\"{asset}\"" + (version == null ? "" : $" Version=\"{version}\"") +
           $"><ScriptState>{data}</ScriptState></State>";

    // ── take and rez ──────────────────────────────────────────────────────────

    /// <summary>
    /// The object is serialized as a take writes it and rezzed again with new item ids, through the core's own path
    /// (SaveScriptedState, LoadScriptState, ResetIDs, CreateScriptInstances): the script keeps its globals, gets on_rez
    /// with the rez's parameter, and does not run state_entry.
    /// </summary>
    [Fact]
    public void ATakenObjectRezzedAgainKeepsItsScriptsGlobals()
    {
        using var h = new SchedulerHarness();
        var asset = UUID.Random();
        var item = Running(h, Counter, asset, UUID.Random());
        Touch(h, item, "n=6");

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
        Assert.Contains("Engine=\"InWorldz.Phlox\"", xml);

        SceneObjectGroup copy = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        copy.ResetIDs();
        h.Scene.AddNewSceneObject(copy, false);
        UUID newItem = copy.RootPart.Inventory.GetInventoryItems().Single().ItemID;
        Assert.NotEqual(item, newItem);

        h.ClearSaid(item);
        copy.CreateScriptInstances(7, true, h.Engine.Name, NewRez);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("rez"))), SavedStateRig.SaidText(h));
        Assert.Contains("rez n=6 sp=7", h.Said);
        Assert.DoesNotContain("entry", h.Said);
        Touch(h, newItem, "n=7");
    }

    /// <summary>
    /// An object an earlier build serialized carries no Phlox state (its GetXMLState returned nothing). It rezzes and
    /// its script starts fresh, as before.
    /// </summary>
    [Fact]
    public void AnObjectWithoutCarriedStateStartsItsScriptFresh()
    {
        using var h = new SchedulerHarness();
        var item = Running(h, Counter, UUID.Random(), UUID.Random());
        Touch(h, item, "n=6");

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup, false);
        Assert.DoesNotContain("SavedScriptState", xml);
        SceneObjectGroup copy = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        copy.ResetIDs();
        h.Scene.AddNewSceneObject(copy, false);

        h.ClearSaid(item);
        copy.CreateScriptInstances(3, true, h.Engine.Name, NewRez);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry") && h.Said.Any(s => s.StartsWith("rez"))), SavedStateRig.SaidText(h));
        Assert.Contains("rez n=5 sp=3", h.Said);
    }

    /// <summary>The envelope: YEngine's State element, with Phlox's engine name, the item and asset, and a version.</summary>
    [Fact]
    public void TheEnvelopeNamesTheEngineItemAssetAndVersion()
    {
        using var h = new SchedulerHarness();
        var asset = UUID.Random();
        var item = Running(h, Counter, asset, UUID.Random());

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(h.Engine.GetXMLState(item));
        var state = doc.DocumentElement!;
        Assert.Equal("State", state.Name);
        Assert.Equal("InWorldz.Phlox", state.GetAttribute("Engine"));
        Assert.Equal(item.ToString(), state.GetAttribute("UUID"));
        Assert.Equal(asset.ToString(), state.GetAttribute("Asset"));
        Assert.Equal("1", state.GetAttribute("Version"));
        Assert.NotEmpty(Convert.FromBase64String(state["ScriptState"]!.InnerText));

        Assert.Equal(string.Empty, h.Engine.GetXMLState(UUID.Random()));   // not a script here
    }

    /// <summary>A script held because its saved row could not be read carries nothing: its state is not known.</summary>
    [Fact]
    public void AScriptHeldForAnUnreadRowCarriesNothing()
    {
        using var h = new SchedulerHarness();
        var item = Running(h, Counter, UUID.Random(), UUID.Random());
        var interp = (Interpreter)h.InterpreterFor(item);
        interp.ScriptState.LocalDisable |= RuntimeState.LocalDisableFlag.StateLoadFailed;
        Assert.Equal(string.Empty, h.Engine.GetXMLState(item));
    }

    // ── what is refused ───────────────────────────────────────────────────────

    public static TheoryData<string, string> Unreadable() => new()
    {
        { "not xml", "<State Engine=\"InWorldz.Phlox\"" },
        { "YEngine's", Envelope("YEngine", UUID.Zero, UUID.Zero, null, "AAAA") },
        { "XEngine's legacy ScriptState root", "<ScriptState><State>default</State></ScriptState>" },
        { "a newer envelope", Envelope("InWorldz.Phlox", UUID.Zero, UUID.Zero, "2", "CAE=") },
        { "no version", Envelope("InWorldz.Phlox", UUID.Zero, UUID.Zero, null, "CAE=") },
        { "bad base64", Envelope("InWorldz.Phlox", UUID.Zero, UUID.Zero, "1", "!!not base64!!") },
        { "not a saved state", Envelope("InWorldz.Phlox", UUID.Zero, UUID.Zero, "1", Convert.ToBase64String(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })) },
    };

    /// <summary>State Phlox cannot use is refused (the core then asks the next engine) and the script starts fresh.</summary>
    [Theory]
    [MemberData(nameof(Unreadable))]
    public void StatePhloxCannotReadIsRefusedAndTheScriptStartsFresh(string what, string xml)
    {
        using var h = new SchedulerHarness();
        var asset = UUID.Random();
        var item = UUID.Random();
        Assert.False(h.Engine.SetXMLState(item, xml), what);
        Running(h, Counter, asset, item);
        Touch(h, item, "n=6");
        Assert.Equal(0, SavedStateRig.RejectedRows(item));
    }

    /// <summary>
    /// With both engines on one region, each takes only its own: YEngine refuses Phlox's envelope, and a Phlox script
    /// given YEngine's state starts fresh. Nothing is written for either.
    /// </summary>
    [Fact]
    public void EachEngineRefusesTheOthersState()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Running(h, Counter, UUID.Random(), UUID.Random());
        Touch(h, item, "n=6");
        string phlox = h.Engine.GetXMLState(item);
        Assert.NotEmpty(phlox);

        var other = UUID.Random();
        Assert.False(h.YEngine.SetXMLState(other, phlox));
        Assert.False(h.Engine.SetXMLState(other, Envelope("YEngine", other, UUID.Random(), null, "<Snapshot>AAAA</Snapshot>")));
        Assert.True(h.Engine.SetXMLState(other, phlox.Replace(item.ToString(), other.ToString())));
    }

    /// <summary>
    /// A carried state saved for another asset (the script was edited) is dropped and the state database's row is used
    /// as before; with no row the script starts fresh.
    /// </summary>
    [Fact]
    public void CarriedStateForAnotherAssetGivesWayToTheSavedRow()
    {
        var asset = UUID.Random();
        var item = UUID.Random();
        string carried;
        using (var h1 = new SchedulerHarness())
        {
            Running(h1, Counter, asset, item);
            Touch(h1, item, "n=6");
            h1.SaveState(item);
            Touch(h1, item, "n=7");
            carried = h1.Engine.GetXMLState(item).Replace(asset.ToString(), UUID.Random().ToString());
        }
        using var h2 = new SchedulerHarness();
        Assert.True(h2.Engine.SetXMLState(item, carried));
        h2.RezScript(Counter, asset, item);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        Touch(h2, item, "n=7");                    // the row's n=6, plus this touch
        Assert.DoesNotContain("entry", h2.Said);
    }

    /// <summary>
    /// Carried state comes before the state database's row for the same item (a crossing inside one simulator leaves
    /// both), as YEngine's SetXMLState writes the carried state over the state file its load reads.
    /// </summary>
    [Fact]
    public void CarriedStateComesBeforeTheSavedRow()
    {
        var asset = UUID.Random();
        var item = UUID.Random();
        string carried;
        using (var h1 = new SchedulerHarness())
        {
            Running(h1, Counter, asset, item);
            h1.SaveState(item);                    // row: n=5
            Touch(h1, item, "n=6");
            Touch(h1, item, "n=7");
            carried = h1.Engine.GetXMLState(item); // carried: n=7
        }
        using var h2 = new SchedulerHarness();
        Assert.True(h2.Engine.SetXMLState(item, carried));
        h2.RezScript(Counter, asset, item);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        Touch(h2, item, "n=8");
    }

    // ── threads ───────────────────────────────────────────────────────────────

    private static void SetMasterRunning(SchedulerHarness h, bool running)
    {
        var ms = SavedStateRig.Field(h.Engine, "m_MasterScheduler");
        ms.GetType().GetField("m_Stop", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ms, !running);
    }

    /// <summary>
    /// From a region thread while the scheduler runs, the capture is taken on the scheduler thread and the caller waits
    /// for it (Halcyon EngineInterface.GetXMLState: RequestStateData, WaitForData).
    /// </summary>
    [Fact]
    public void ARegionThreadGetsTheStateCapturedOnTheSchedulerThread()
    {
        using var h = new SchedulerHarness();
        var item = Running(h, Counter, UUID.Random(), UUID.Random());
        Touch(h, item, "n=6");
        SetMasterRunning(h, true);
        try
        {
            Task<string> asked = Task.Run(() => h.Engine.GetXMLState(item));
            Assert.True(h.PumpUntil(() => asked.IsCompleted, TimeSpan.FromSeconds(20)), "the capture was never answered");
            Assert.Contains("InWorldz.Phlox", asked.Result);

            var other = UUID.Random();
            Assert.True(h.Engine.SetXMLState(other, asked.Result.Replace(item.ToString(), other.ToString())));
        }
        finally { SetMasterRunning(h, false); }
    }

    /// <summary>A scheduler that does not answer in time: the object travels without the state, and the caller goes on.</summary>
    [Fact]
    public void ACaptureNotAnsweredInTimeCarriesNothing()
    {
        using var h = new SchedulerHarness();
        var item = Running(h, Counter, UUID.Random(), UUID.Random());
        h.Engine.GetType().GetField("CarriedStateTimeoutMs", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Engine, 200);
        SetMasterRunning(h, true);
        // Asked from a region thread; nothing pumps the scheduler, so the 200 ms must run out.
        try { Assert.Equal(string.Empty, OnOwnThread(() => h.Engine.GetXMLState(item))); }
        finally { SetMasterRunning(h, false); }
    }

    /// <summary>
    /// Runs <paramref name="ask"/> on a thread of its own and returns its answer. Not Task.Run(..).Result: a task not yet
    /// started can run inline on the waiting thread, and the test thread is the one that drove the scheduler, so the
    /// engine would take it for the scheduler thread and capture directly.
    /// </summary>
    internal static T OnOwnThread<T>(Func<T> ask)
    {
        T answer = default;
        var t = new System.Threading.Thread(() => answer = ask()) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the asking thread did not finish");
        return answer;
    }

    /// <summary>SaveAllState writes every loaded script's state now, and a region start restores it.</summary>
    [Fact]
    public void SaveAllStateWritesEveryScriptNow()
    {
        var asset = UUID.Random();
        var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            Running(h1, Counter, asset, item);
            Touch(h1, item, "n=6");
            h1.Engine.SaveAllState();
            Assert.NotNull(SavedStateRig.Row(item));
        }
        using var h2 = new SchedulerHarness();
        h2.RezScript(Counter, asset, item);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        Touch(h2, item, "n=7");
    }

    // ── grants put back on a restore ───────────────────────────────────────────

    private const string Taker = @"
        default {
            state_entry() { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""touched""); }
            control(key id, integer l, integer e) { }
        }";

    private const int TakeControls = 0x4;
    private const int SilentEstate = 0x4000;

    /// <summary>Save a script that took controls from a present avatar; the row carries its Control record.</summary>
    private static void SaveTaker(UUID asset, UUID item, Action<Interpreter> alsoRecord = null)
    {
        using var h1 = new SchedulerHarness();
        var sp = SceneHelpers.AddScenePresence(h1.Scene, UUID.Random());
        var invItem = OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, item, asset, "taker", Taker);
        invItem.PermsGranter = sp.UUID;
        invItem.PermsMask = TakeControls;
        SavedStateRig.PostRez(h1, h1.Prim, item, Taker, 0, false, 0);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        var interp = (Interpreter)h1.InterpreterFor(item);
        Assert.True(interp.ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control));
        alsoRecord?.Invoke(interp);
        h1.SaveState(item);
    }

    /// <summary>Restore the taker in a new region with the grant given; returns once the restored script answers a touch.</summary>
    private static SchedulerHarness RestoreTaker(UUID asset, UUID item, UUID granter, int mask, Action<SchedulerHarness> before = null)
    {
        var h2 = new SchedulerHarness();
        before?.Invoke(h2);
        var invItem = OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item, asset, "taker", Taker);
        invItem.PermsGranter = granter;
        invItem.PermsMask = mask;
        SavedStateRig.PostRez(h2, h2.Prim, item, Taker, 0, false, 0);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        Touch(h2, item, "touched");
        Assert.DoesNotContain("entry", h2.Said);
        return h2;
    }

    /// <summary>
    /// A restored script whose grant no longer holds PERMISSION_TAKE_CONTROLS does not take them and says nothing
    /// (Halcyon TakeControlsInternal); llTakeControls's own "not granted" error is for the script's call only.
    /// </summary>
    [Fact]
    public void ARestoreWithoutTheGrantTakesNoControlsAndSaysNothing()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        SaveTaker(asset, item);
        using var h2 = RestoreTaker(asset, item, UUID.Zero, 0);
        Assert.DoesNotContain(h2.SaidOn, s => s.Channel == DebugChannel);
    }

    /// <summary>With the grant and the granter here, a restore takes the controls again.</summary>
    [Fact]
    public void ARestoreWithTheGrantTakesTheControlsAgain()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        SaveTaker(asset, item);
        ScenePresence sp = null;
        using var h2 = RestoreTaker(asset, item, UUID.Zero, 0, h => sp = SceneHelpers.AddScenePresence(h.Scene, UUID.Random()));
        Assert.False(sp.HasScriptControls(item));
        // the same restore with the grant from that avatar
        var item2 = UUID.Random();
        SaveTaker(asset, item2);
        var inv = OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, item2, asset, "taker2", Taker);
        inv.PermsGranter = sp.UUID;
        inv.PermsMask = TakeControls;
        SavedStateRig.PostRez(h2, h2.Prim, item2, Taker, 0, false, 0);
        Assert.True(h2.PumpUntil(() => sp.HasScriptControls(item2)), "the restored script did not take the controls again");
        Assert.DoesNotContain(h2.SaidOn, s => s.Channel == DebugChannel);
    }

    /// <summary>
    /// The saved PERMISSION_SILENT_ESTATE_MANAGEMENT record is put back on the item's grant at a restore (Halcyon
    /// OnScriptInjected, SilentEstateManagement).
    /// </summary>
    [Fact]
    public void ARestorePutsTheSilentEstateBitBack()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        SaveTaker(asset, item, interp =>
            interp.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.SilentEstateManagement] = new object[] { 1 });
        var granter = UUID.Random();
        using var h2 = RestoreTaker(asset, item, granter, TakeControls);
        var inv = h2.Prim.Inventory.GetInventoryItem(item);
        Assert.Equal(TakeControls | SilentEstate, inv.PermsMask);
        Assert.Equal(granter, inv.PermsGranter);
    }

    /// <summary>A record of 0 clears the bit, and a grant left empty loses its granter.</summary>
    [Fact]
    public void ARestoreClearsTheSilentEstateBitTheRecordSaysIsOff()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        SaveTaker(asset, item, interp =>
            interp.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.SilentEstateManagement] = new object[] { 0 });
        using var h2 = RestoreTaker(asset, item, UUID.Random(), SilentEstate);
        var inv = h2.Prim.Inventory.GetInventoryItem(item);
        Assert.Equal(0, inv.PermsMask);
        Assert.Equal(UUID.Zero, inv.PermsGranter);
    }

    /// <summary>
    /// The granter arrives after the restore (a crossing or teleport the core carried no controls for, a login) and
    /// sits on the object: the script takes its controls again (Halcyon OnCrossedAvatarReady ->
    /// OnGroupCrossedAvatarReady). The core sets the seat before it raises OnMakeRootAgent.
    /// </summary>
    [Fact]
    public void TheGranterArrivingSeatedOnTheObjectGetsItsControlsTakenAgain()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        SaveTaker(asset, item);
        var granter = UUID.Random();
        using var h2 = RestoreTaker(asset, item, granter, TakeControls);

        var sp = SceneHelpers.AddScenePresence(h2.Scene, granter);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.False(sp.HasScriptControls(item));   // not seated: arriving alone takes nothing

        sp.AbsolutePosition = h2.Prim.AbsolutePosition + new Vector3(1, 1, 0);   // within sit range
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, h2.Prim.UUID, Vector3.Zero);
        Assert.Equal(h2.Prim.ParentGroup, sp.ParentPart?.ParentGroup);
        h2.Scene.EventManager.TriggerOnMakeRootAgent(sp);
        Assert.True(h2.PumpUntil(() => sp.HasScriptControls(item)), "the controls were not taken again");
        Assert.DoesNotContain(h2.SaidOn, s => s.Channel == DebugChannel);
    }

    /// <summary>Another avatar sitting down takes nothing: only the granter's controls are re-taken.</summary>
    [Fact]
    public void AnotherAvatarArrivingSeatedTakesNothing()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        SaveTaker(asset, item);
        using var h2 = RestoreTaker(asset, item, UUID.Random(), TakeControls);
        var sp = SceneHelpers.AddScenePresence(h2.Scene, UUID.Random());
        sp.AbsolutePosition = h2.Prim.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, h2.Prim.UUID, Vector3.Zero);
        Assert.Equal(h2.Prim.ParentGroup, sp.ParentPart?.ParentGroup);
        h2.Scene.EventManager.TriggerOnMakeRootAgent(sp);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.False(sp.HasScriptControls(item));
    }
}
