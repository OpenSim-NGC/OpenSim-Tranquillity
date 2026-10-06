/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Who takes a script's snapshot and who writes it. As in Halcyon: the scheduler marks a script after every timeslice and
/// captures it between slices when the state thread asks; the state thread only writes, and the scheduler never waits on
/// the database. At shutdown the scheduler stops before the final save. An unload keeps the row, except when the script
/// item itself left its prim.
/// </summary>
// "phlox-state": one test holds StateManager's process-wide writer lock, which every engine's writes take.
[Collection("phlox-state")]
public class StateSaveThreadingTests
{
    private readonly ITestOutputHelper _out;
    public StateSaveThreadingTests(ITestOutputHelper o) => _out = o;

    private const string UpSrc = "integer g = 1; default { state_entry() { g = 2; llSay(0, \"up\"); } }";

    /// <summary>
    /// The state thread never serializes a running script itself: with the scheduler not running, no row is written
    /// however long the state thread waits; the next scheduler pass takes the snapshot and the row follows.
    /// </summary>
    [Fact]
    public void TheSnapshotIsTakenOnTheSchedulerThreadNotTheStateThread()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(UpSrc, UUID.Random(), UUID.Random());
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));

        var sm = SavedStateRig.States(h);
        int selfBefore = (int)SavedStateRig.Member(sm, "SelfCaptures");
        Thread.Sleep(3500);   // more than a flush interval with the scheduler idle: proves the state thread takes no snapshot itself
        Assert.Equal(selfBefore, (int)SavedStateRig.Member(sm, "SelfCaptures"));

        Assert.True(h.PumpUntil(() => (int)SavedStateRig.Member(sm, "SchedulerCaptures") > 0 && SavedStateRig.Row(item) != null,
            TimeSpan.FromSeconds(15)), "no snapshot and row after the scheduler ran");
    }

    /// <summary>
    /// A script in the middle of an event (here asleep in llSleep) is saved as it is at shutdown, with the globals the
    /// event has already set, and finishes the event after the restart. Only event ends used to mark a script, so the
    /// row held the state from before the event and the event was lost.
    /// </summary>
    [Fact]
    public void AScriptAsleepMidEventIsSavedAsItIsAndFinishesAfterTheRestart()
    {
        const string src =
            "integer g = 1; default { state_entry() { llSay(0, \"up\"); } " +
            "touch_start(integer n) { g = 5; llSay(0, \"b\"); llSleep(2.0); llSay(0, \"done g=\" + (string)g); } }";
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(src, asset, item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
            h1.PumpFor(TimeSpan.FromSeconds(3));   // the first save after state_entry
            h1.PostTouch(item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("b") && h1.RunStateOf(item) == "Sleeping"), SavedStateRig.SaidText(h1));
            h1.StopRegionAsTheSimulatorDoes();
        }
        using var h2 = new SchedulerHarness();
        h2.RezScript(src, asset, item);
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("done g=5"), TimeSpan.FromSeconds(8)), SavedStateRig.SaidText(h2) + " " + h2.StatusOf(item));
    }

    /// <summary>
    /// An unload does not write on the scheduler thread: with the database's writer lock held elsewhere, the pass that
    /// unloads the script returns at once, and the row is written once the lock is free.
    /// </summary>
    [Fact]
    public void AnUnloadDoesNotWaitForTheDatabase()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(UpSrc, UUID.Random(), UUID.Random());
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));

        object writerLock = typeof(StateManager).GetField("s_WriterLock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        using var held = new ManualResetEventSlim(false);
        var holder = new Thread(() => { lock (writerLock) { held.Set(); Thread.Sleep(3000); } }) { IsBackground = true };
        holder.Start();
        held.Wait();

        SavedStateRig.PostRemove(h, h.Prim, item);   // a derez-style unload: the item stays in the prim
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5 && SavedStateRig.Exe(h).IsLoaded(item); i++) h.PumpOnce();
        sw.Stop();
        _out.WriteLine($"unload pass took {sw.ElapsedMilliseconds} ms");
        Assert.False(SavedStateRig.Exe(h).IsLoaded(item));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"the unload waited {sw.ElapsedMilliseconds} ms for the database");

        holder.Join();
        SavedStateRig.WaitForWrites(h);
        Assert.NotNull(SavedStateRig.Row(item));
    }

    /// <summary>
    /// Region shutdown stops the script scheduler before the final save (Halcyon MasterScheduler.Stop joins its thread
    /// first), so the save is of scripts that are no longer running.
    /// </summary>
    [Fact]
    public void ShutdownStopsTheSchedulerBeforeTheFinalSave()
    {
        using var h = new SchedulerHarness();
        h.RezScript(UpSrc, UUID.Random(), UUID.Random());
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));

        // Run the master scheduler's own thread again (the harness stopped it to pump by hand).
        var ms = SavedStateRig.Field(h.Engine, "m_MasterScheduler");
        ms.GetType().GetField("m_Stop", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ms, false);
        ms.GetType().GetMethod("Start")!.Invoke(ms, null);
        var thread = (Thread)SavedStateRig.Field(ms, "m_Thread");
        Assert.True(thread.IsAlive);

        try
        {
            h.Engine.GetType().GetMethod("OnShutdown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(h.Engine, null);
            Assert.False(thread.IsAlive, "the scheduler thread was still running after the shutdown save");
            Assert.Null(h.StateManagerOf());
        }
        finally
        {
            ms.GetType().GetMethod("StopThread", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(ms, null);
        }
    }

    /// <summary>
    /// A script item deleted from its prim (the prim stays) can never be restored again: its row goes, as Halcyon and
    /// YEngine delete theirs on removal. A derez keeps it until state travels with objects.
    /// </summary>
    [Fact]
    public void RemovingTheScriptItemDeletesItsRowAndADerezKeepsIt()
    {
        using var h = new SchedulerHarness();
        var removed = h.RezScript(UpSrc, UUID.Random(), UUID.Random());
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));
        h.SaveState(removed);
        Assert.NotNull(SavedStateRig.Row(removed));
        h.Prim.Inventory.RemoveInventoryItem(removed);   // the core removes the item, then raises OnRemoveScript
        Assert.True(h.PumpUntil(() => !SavedStateRig.Exe(h).IsLoaded(removed)));
        SavedStateRig.WaitForWrites(h);
        Assert.Null(SavedStateRig.Row(removed));

        // A derez: the item stays in its prim, the object leaves the scene.
        var sog = OpenSim.Tests.Common.SceneHelpers.AddSceneObject(h.Scene, "derezzed", UUID.Random());
        var kept = SavedStateRig.Rez(h, sog.RootPart, UpSrc, UUID.Random(), UUID.Random(), 0, false, 0);
        Assert.True(h.PumpUntil(() => h.Said.Count >= 2), SavedStateRig.SaidText(h));
        h.Scene.DeleteSceneObject(sog, false);
        Assert.True(h.PumpUntil(() => !SavedStateRig.Exe(h).IsLoaded(kept)));
        SavedStateRig.WaitForWrites(h);
        Assert.NotNull(SavedStateRig.Row(kept));
    }

    /// <summary>
    /// [InWorldz.Phlox] StateRowMaxAgeDays: a row not saved or loaded for that long, of a script not loaded in this
    /// simulator, is deleted; a loaded script's row is refreshed and kept however old it was.
    /// </summary>
    [Fact]
    public void ThePurgeDeletesOldRowsOfScriptsNotLoadedAndKeepsLoadedOnes()
    {
        using var h = new SchedulerHarness();
        long longAgo = DateTimeOffset.UtcNow.AddDays(-100).ToUnixTimeSeconds();
        var orphan = UUID.Random();
        SavedStateRig.PutRow(orphan, UUID.Random(), new byte[] { 0x08, 0x01 }, longAgo);

        var live = h.RezScript(UpSrc, UUID.Random(), UUID.Random());
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));
        Assert.True(h.PumpUntil(() => SavedStateRig.Row(live) != null, TimeSpan.FromSeconds(15)), "the live script was never saved");
        SavedStateRig.SetSavedAt(live, longAgo);

        var sm = SavedStateRig.States(h);
        long cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var purged = SavedStateRig.Member(sm, "PurgeOldRows", cutoff);
        _out.WriteLine($"purged {purged}");
        Assert.Null(SavedStateRig.Row(orphan));
        var liveRow = SavedStateRig.Row(live);
        Assert.NotNull(liveRow);
        Assert.True(liveRow!.Value.SavedAt > cutoff, "the loaded script's row was not refreshed");
    }

    /// <summary>The purge is off unless an operator sets StateRowMaxAgeDays: it was never there before.</summary>
    [Fact]
    public void ThePurgeIsOffByDefault()
    {
        using var h = new SchedulerHarness();
        Assert.Equal(0, (int)SavedStateRig.Member(SavedStateRig.States(h), "StateRowMaxAgeDays"));
        using var h2 = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("StateRowMaxAgeDays", "90"));
        Assert.Equal(90, (int)SavedStateRig.Member(SavedStateRig.States(h2), "StateRowMaxAgeDays"));
    }
}
