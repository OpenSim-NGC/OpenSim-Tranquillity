/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The final save never writes a state that may be changing or that was not copied whole. A part of a script's state
/// that cannot be copied fails the capture (the last saved row stays), never an empty part. When the scheduler's thread
/// did not stop in time (a script held inside a call), the scheduler runs no further script, the script it is still
/// running keeps its last saved row, and every other script is saved.
/// </summary>
// Runs in parallel: each test has its own harness, items and rows under random item ids in the shared state database.
public class ShutdownCaptureTests
{
    private const string Held = @"
        integer g = 1;
        default {
            touch_start(integer t) { g = 2; llSleep(0.05); llSay(0, ""hold""); g = 3; }
        }";

    private const string Other = @"
        integer g = 1;
        default {
            touch_start(integer t) { g = 5; llSay(0, ""b""); }
        }";

    private static object Global0(UUID item)
        => StateManager.Decode(SavedStateRig.Row(item)!.Value.Blob).ToRuntimeState().Globals[0];

    [Fact]
    public void AScriptHeldInsideACallPastTheStopWaitKeepsItsRowAndTheOthersAreSaved()
    {
        using var h = new SchedulerHarness();
        UUID held = h.RezScript(Held), other = h.RezScript(Other);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(held) != null && h.InterpreterFor(other) != null), "the scripts did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        h.Engine.SaveAllState();   // the last good rows: g = 1 for both
        Assert.Equal(1, Global0(held));
        Assert.Equal(1, Global0(other));

        h.PostTouch(other);
        Assert.True(h.PumpUntil(() => h.Said.Contains("b")), SavedStateRig.SaidText(h));   // other: g = 5, unsaved

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        h.Scene.EventManager.OnChatFromWorld += (_, chat) =>
        {
            if (chat.Message != "hold") return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(60));
        };
        h.PostTouch(held);
        var pump = new Thread(() => h.Pump(400)) { IsBackground = true };
        pump.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the script never reached its call");
            // The held script was saved-marked by its llSleep slice with g = 2, and is inside llSay now.
            typeof(PhloxEngine).GetMethod("SaveStateAtStop", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(h.Engine, new object[] { false });
            Assert.Equal(1, Global0(held));    // its last good row stays
            Assert.Equal(5, Global0(other));   // the others are saved
        }
        finally
        {
            release.Set();
            Assert.True(pump.Join(TimeSpan.FromSeconds(60)), "the scheduler thread did not finish");
        }
    }

    /// <summary>A part of the state that cannot be copied fails the capture; it is never saved as an empty part.</summary>
    [Theory]
    [InlineData("Calls")]
    [InlineData("ActiveListens")]
    [InlineData("MiscAttributes")]
    [InlineData("Operands")]
    public void APartThatCannotBeCopiedFailsTheCapture(string part)
    {
        var st = new RuntimeState(4);
        typeof(RuntimeState).GetField(part)!.SetValue(st, null);
        Assert.ThrowsAny<Exception>(() => SerializedRuntimeState.FromRuntimeState(st));
    }

    [Fact]
    public void AWholeStateIsCaptured()
    {
        var st = new RuntimeState(4);
        Assert.NotNull(SerializedRuntimeState.FromRuntimeState(st));
    }
}
