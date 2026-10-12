/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script held for a crossing has events queued, and the event that ends the hold is still in the scheduler's incoming
/// queue when the region stops. The final pass over that queue ends the hold, which resumes the script's queued events
/// as an ordinary pass would. The saved state is restored in a new region: the script handles each queued event once,
/// in the order they were queued, and is then waiting, not held, with an empty queue.
/// </summary>
// Runs in parallel: each test has its own harnesses and random item ids in the shared state database.
public class CrossingEndAtStopTests
{
    private const string Reader = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            link_message(integer s, integer n, string m, key k) { llSay(0, ""lm "" + m); }
        }";

    private static void Link(SchedulerHarness h, UUID item, string m)
        => h.Engine.PostScriptEvent(item, "link_message", new object[] { 0, 0, m, UUID.Zero.ToString() });

    private static SchedulerHarness Region(UUID item, UUID script)
    {
        var h = new SchedulerHarness();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, script, "reader", Reader);
        Assert.Equal(1, h.Prim.ParentGroup.CreateScriptInstances(0, false, "InWorldz.Phlox", 0));
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the script did not load");
        return h;
    }

    [Theory]
    [InlineData(0)]   // the end alone is in the incoming queue
    [InlineData(1)]   // an event posted while held is in the same batch, ahead of the end
    public void QueuedEventsOfAHeldScriptRunOnceAndInOrderAfterARestoreWhenTheEndIsTakenInTheFinalPass(int unpumped)
    {
        UUID item = UUID.Random(), script = UUID.Random();
        SchedulerHarness h1 = Region(item, script);
        try
        {
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
            var exe = SavedStateRig.Exe(h1);
            exe.RequestCrossingHold(h1.Prim.ParentGroup, true);
            Assert.True(h1.PumpUntil(() => (exe.GetStatus(item).LocalDisable ?? "").Contains("CrossingWait")), "not held");
            for (int i = 1; i <= 3; i++) Link(h1, item, i.ToString());
            Assert.True(h1.PumpUntil(() => exe.GetStatus(item).QueuedEvents >= 3), "queued " + exe.GetStatus(item).QueuedEvents);
            h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain(h1.Said, s => s.StartsWith("lm "));   // held: nothing ran

            if (unpumped == 1) Link(h1, item, "4");
            exe.RequestCrossingHold(h1.Prim.ParentGroup, false);        // the end, not yet taken
            Assert.Equal(unpumped + 1, exe.PendingEventCount);
            h1.StopRegionAsTheSimulatorDoes();
            Assert.DoesNotContain(h1.Said, s => s.StartsWith("lm "));   // the stopped scheduler ran nothing
            var saved = global::Phlox.ScriptEngine.StateManager.Decode(SavedStateRig.Row(item)!.Value.Blob).ToRuntimeState();
            // The saved row: the hold is over, and every event is still queued once (none dropped, none doubled).
            Assert.Equal(RuntimeState.LocalDisableFlag.None, saved.LocalDisable);
            Assert.Equal(3 + unpumped, saved.EventQueue.Count);
        }
        finally { h1.Dispose(); }

        int expected = 3 + unpumped;
        using var h2 = Region(item, script);
        Assert.True(h2.PumpUntil(() => h2.Said.Count(s => s.StartsWith("lm ")) >= expected, TimeSpan.FromSeconds(15)),
            "the queued events did not all run: " + SavedStateRig.SaidText(h2));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(3));
        string[] ran = h2.Said.Where(s => s.StartsWith("lm ")).ToArray();
        Assert.Equal(Enumerable.Range(1, expected).Select(i => "lm " + i).ToArray(), ran);   // once each, in order
        Assert.DoesNotContain("entry", h2.Said);                                              // restored, not started fresh

        var exe2 = SavedStateRig.Exe(h2);
        Assert.Equal("Waiting", h2.RunStateOf(item));
        Assert.DoesNotContain("CrossingWait", exe2.GetStatus(item).LocalDisable ?? "");
        Assert.Equal(0, exe2.GetStatus(item).QueuedEvents);

        // And it is a working script: a new event runs.
        Link(h2, item, "after");
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("lm after")), SavedStateRig.SaidText(h2));
    }
}
