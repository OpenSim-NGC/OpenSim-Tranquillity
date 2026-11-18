/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A reset of a script whose Running flag is off (the viewer's Running box unticked, llSetScriptState FALSE) resets it
/// and leaves it stopped: its globals and state go back to the start and state_entry waits until the script is set
/// running again, then runs once. That is how the stock script engine (YEngine, XMRInstRun.ResetLocked) treats it:
/// the reset posts state_entry and leaves the running flag alone. The viewer only sends ScriptReset and
/// SetScriptRunning (Second Life viewer, llpreviewscript.cpp LLLiveLSLEditor::onReset and onRunningCheckboxClicked);
/// what the region does with them is the server's rule. A running script that is reset restarts as before.
/// </summary>
// Touches no process-wide state: each test has its own harness, so the class runs in parallel.
public class StoppedScriptResetTests
{
    private readonly ITestOutputHelper _out;
    public StoppedScriptResetTests(ITestOutputHelper o) => _out = o;

    // Its first item in the prim is "script1" (SchedulerHarness names items in rez order).
    private const string Target = @"
        integer n;
        default {
            state_entry() { n = n + 1; llSay(0, ""up "" + (string)n); }
            touch_start(integer t) { n = n + 10; llSay(0, ""n "" + (string)n); }
        }";

    private const string Resetter = @"
        default {
            touch_start(integer t) { llResetOtherScript(""script1""); llSay(0, ""reset sent""); }
        }";

    private static int Ups(SchedulerHarness h) => h.Said.Count(s => s.StartsWith("up "));
    private static string Log(SchedulerHarness h) => string.Join(" | ", h.Said);

    private static global::InWorldz.Phlox.VM.RuntimeState StateOf(SchedulerHarness h, UUID itemId)
        => ((global::InWorldz.Phlox.VM.Interpreter)h.InterpreterFor(itemId)).ScriptState;

    /// <summary>The global n holds a value after the touch, and is null again once a reset has been applied.</summary>
    private static bool GlobalsCleared(SchedulerHarness h, UUID itemId) => StateOf(h, itemId).Globals.All(g => g is null);

    /// <summary>Rez the target, let state_entry run, touch it so its global moves on, then untick Running.</summary>
    private static UUID RezTouchedAndStopped(SchedulerHarness h)
    {
        var itemId = h.RezScript(Target);
        Assert.True(h.PumpUntil(() => h.Said.Contains("up 1")));
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n 11")));
        Assert.False(GlobalsCleared(h, itemId));

        h.Scene.EventManager.TriggerStopScript(h.Prim.LocalId, itemId);   // the viewer's Running box unticked
        Assert.True(h.PumpUntil(() => !h.Engine.GetScriptState(itemId)));
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.False(h.IsOnRunQueue(itemId), "stopped, but still on the run queue");
        return itemId;
    }

    /// <summary>After a reset of a stopped script: its state is fresh, it stays stopped and state_entry does not run.</summary>
    private void AssertResetAndStillStopped(SchedulerHarness h, UUID itemId)
    {
        Assert.True(h.PumpUntil(() => GlobalsCleared(h, itemId)), "the globals did not stay reset (state_entry ran, or the reset never came): " + Log(h));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which state_entry must NOT run
        _out.WriteLine("after reset: said=[" + Log(h) + "]");

        Assert.Equal(1, Ups(h));                                                   // no state_entry from the reset
        Assert.False(h.Engine.GetScriptState(itemId));                             // the Running box stays unticked
        Assert.False(h.IsOnRunQueue(itemId));
        Assert.Equal(0, StateOf(h, itemId).LSLState);                              // back in the default state
    }

    /// <summary>Tick Running: the state_entry the reset queued runs, once, from the fresh globals.</summary>
    private void AssertStartRunsStateEntryOnce(SchedulerHarness h, UUID itemId)
    {
        h.Scene.EventManager.TriggerStartScript(h.Prim.LocalId, itemId);
        Assert.True(h.PumpUntil(() => Ups(h) >= 2), "state_entry never ran after Running was ticked: " + Log(h));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a second state_entry must NOT run
        _out.WriteLine("after start: said=[" + Log(h) + "]");

        Assert.Equal(2, Ups(h));
        Assert.Equal("up 1", h.Said.Last(s => s.StartsWith("up ")));   // the globals were reset: n counted from 0
        Assert.True(h.Engine.GetScriptState(itemId));
    }

    [Fact]
    public void ViewerResetOfAStoppedScriptResetsItAndLeavesItStopped()
    {
        using var h = new SchedulerHarness();
        var itemId = RezTouchedAndStopped(h);

        h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, itemId);   // the viewer's Reset (Scene.ProcessScriptReset)
        AssertResetAndStillStopped(h, itemId);
    }

    [Fact]
    public void ARunningScriptStartedAfterAResetWhileStoppedRunsStateEntryOnce()
    {
        using var h = new SchedulerHarness();
        var itemId = RezTouchedAndStopped(h);

        h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, itemId);
        AssertResetAndStillStopped(h, itemId);
        AssertStartRunsStateEntryOnce(h, itemId);
    }

    [Fact]
    public void ViewerResetOfARunningScriptStillRestartsIt()
    {
        using var h = new SchedulerHarness();
        var itemId = h.RezScript(Target);
        Assert.True(h.PumpUntil(() => h.Said.Contains("up 1")));
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n 11")));

        h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, itemId);
        Assert.True(h.PumpUntil(() => Ups(h) >= 2), "a running script did not restart: " + Log(h));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a second restart must NOT happen
        _out.WriteLine("said=[" + Log(h) + "]");

        Assert.Equal(2, Ups(h));
        Assert.Equal("up 1", h.Said.Last(s => s.StartsWith("up ")));
        Assert.True(h.Engine.GetScriptState(itemId));
    }

    [Fact]
    public void LlResetOtherScriptOfAStoppedScriptLeavesItStoppedUntilItIsStarted()
    {
        using var h = new SchedulerHarness();
        var itemId = RezTouchedAndStopped(h);
        var resetterId = h.RezScript(Resetter);

        h.PostTouch(resetterId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("reset sent")));
        AssertResetAndStillStopped(h, itemId);
        AssertStartRunsStateEntryOnce(h, itemId);
    }
}
