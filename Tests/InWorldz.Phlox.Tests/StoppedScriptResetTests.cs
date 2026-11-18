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
/// llResetOtherScript is the exception: on a script that is not running it does nothing, and the script is not reset
/// when it is set running again (https://wiki.secondlife.com/wiki/LlResetOtherScript, Caveats). A script stopped by a
/// runtime error is not covered by that rule: llResetOtherScript is how SL restarts one (the crash monitor example on
/// https://wiki.secondlife.com/wiki/LlGetScriptState), since llSetScriptState "Cannot be used to restart a script that
/// has encountered a run-time error" (https://wiki.secondlife.com/wiki/LlSetScriptState, Caveats).
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
    private void AssertStartRunsStateEntryOnce(SchedulerHarness h, UUID itemId, int upsBefore = 1)
    {
        h.Scene.EventManager.TriggerStartScript(h.Prim.LocalId, itemId);
        Assert.True(h.PumpUntil(() => Ups(h) >= upsBefore + 1), "state_entry never ran after Running was ticked: " + Log(h));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a second state_entry must NOT run
        _out.WriteLine("after start: said=[" + Log(h) + "]");

        Assert.Equal(upsBefore + 1, Ups(h));
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

    /// <summary>
    /// A reset deletes the script's saved state, and a stopped script never runs again to get itself saved. The reset
    /// state, still stopped and with its state_entry waiting, has to be saved by the reset itself, or a region restart
    /// loads the script fresh and running. The region stop is the simulator's own path (only scripts marked changed are
    /// saved), as in TerminatedScriptStaysStoppedTests.
    /// </summary>
    [Fact]
    public void AStoppedScriptThatWasResetStaysStoppedAcrossARegionRestart()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(Target, assetId, itemId);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up 1")));
            h1.PostTouch(itemId);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("n 11")));
            h1.Scene.EventManager.TriggerStopScript(h1.Prim.LocalId, itemId);
            Assert.True(h1.PumpUntil(() => !h1.Engine.GetScriptState(itemId)));
            h1.Scene.EventManager.TriggerScriptReset(h1.Prim.LocalId, itemId);
            AssertResetAndStillStopped(h1, itemId);
            h1.StopRegionAsTheSimulatorDoes();
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(Target, assetId, itemId);   // the item as the region DB presents it: Running flag at its default, true
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(itemId) != null));
        h2.PumpFor(TimeSpan.FromSeconds(1));      // a window in which state_entry must NOT run
        _out.WriteLine("after restart: said=[" + Log(h2) + "]");

        Assert.Equal(0, Ups(h2));
        Assert.False(h2.Engine.GetScriptState(itemId));
        Assert.False(h2.IsOnRunQueue(itemId));

        AssertStartRunsStateEntryOnce(h2, itemId, upsBefore: 0);
    }

    /// <summary>
    /// The SL wiki, llResetOtherScript, Caveats: "If the script is not running, this call has no effect, even after the
    /// script is set running again." The stopped script keeps its globals, gets no state_entry, and when it is started it
    /// carries on from where it stopped.
    /// </summary>
    [Fact]
    public void LlResetOtherScriptOfAStoppedScriptHasNoEffectEvenAfterItIsStarted()
    {
        using var h = new SchedulerHarness();
        var itemId = RezTouchedAndStopped(h);
        var resetterId = h.RezScript(Resetter);

        h.PostTouch(resetterId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("reset sent")));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a reset must NOT be applied
        _out.WriteLine("after llResetOtherScript: said=[" + Log(h) + "]");

        Assert.False(GlobalsCleared(h, itemId), "llResetOtherScript reset a script that was not running");
        Assert.False(h.Engine.GetScriptState(itemId));
        Assert.False(h.IsOnRunQueue(itemId));
        Assert.Equal(1, Ups(h));

        h.Scene.EventManager.TriggerStartScript(h.Prim.LocalId, itemId);
        Assert.True(h.PumpUntil(() => h.Engine.GetScriptState(itemId)));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a held-back state_entry must NOT run
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n 21")), "the started script did not carry on from its own globals: " + Log(h));
        _out.WriteLine("after start: said=[" + Log(h) + "]");

        Assert.Equal(1, Ups(h));
        Assert.DoesNotContain("n 10", h.Said);
    }

    /// <summary>llResetOtherScript on a running script resets it: state_entry runs again from fresh globals.</summary>
    [Fact]
    public void LlResetOtherScriptOfARunningScriptRestartsIt()
    {
        using var h = new SchedulerHarness();
        var itemId = h.RezScript(Target);
        Assert.True(h.PumpUntil(() => h.Said.Contains("up 1")));
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n 11")));
        var resetterId = h.RezScript(Resetter);

        h.PostTouch(resetterId);
        Assert.True(h.PumpUntil(() => Ups(h) >= 2), "a running script was not reset: " + Log(h));
        h.PumpFor(TimeSpan.FromSeconds(1));   // a window in which a second restart must NOT happen
        _out.WriteLine("said=[" + Log(h) + "]");

        Assert.Equal(2, Ups(h));
        Assert.Equal("up 1", h.Said.Last(s => s.StartsWith("up ")));
        Assert.True(h.Engine.GetScriptState(itemId));
    }

    /// <summary>
    /// A script stopped by a runtime error is restarted by llResetOtherScript: its state_entry runs again. The SL wiki's
    /// crash monitor (llGetScriptState) restarts crashed scripts this way.
    /// </summary>
    [Fact]
    public void LlResetOtherScriptOfACrashedScriptRestartsIt()
    {
        // osSetRot is VeryHigh; at VeryLow the call is denied and the denial ends the script with a runtime error
        const string crasher = @"default {
            state_entry() { llSay(0, ""crasher up""); osSetRot(llGetKey(), <0,0,0,1>); llSay(0, ""after""); }
        }";
        using var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "VeryLow"));
        var itemId = h.RezScript(crasher);   // "script1", the item the resetter names
        Assert.True(h.PumpUntil(() => h.RunStateOf(itemId) == "Killed"), "the script did not crash: " + Log(h));
        Assert.False(h.Engine.GetScriptState(itemId));
        var resetterId = h.RezScript(Resetter);

        h.PostTouch(resetterId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("reset sent")));
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s == "crasher up") >= 2), "llResetOtherScript did not restart a crashed script: " + Log(h));
        _out.WriteLine("said=[" + Log(h) + "]");

        Assert.Equal(2, h.Said.Count(s => s == "crasher up"));
        Assert.DoesNotContain("after", h.Said);
    }
}
