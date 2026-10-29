/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The harness's PumpUntilIdle is idle only once the loader's compile thread has handed back every script it was given.
/// The loader's WorkIsPending (PhloxScriptLoader.HasPendingWork) does not count a compile still running there, so a
/// slow compile let PumpUntilIdle report idle with the script not yet loaded, and a test then read a script with no
/// interpreter. The compile is held here with the loader's own per-instance hook (BeforeCompileForTest).
/// No process-wide state: runs in parallel.
/// </summary>
public class HarnessIdleWaitsForCompileTests
{
    [Fact]
    public void PumpUntilIdleIsNotIdleWhileACompileIsRunning()
    {
        using var h = new SchedulerHarness();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ((global::Phlox.ScriptEngine.PhloxScriptLoader)h.Loader).BeforeCompileForTest = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(60));
        };
        try
        {
            var item = h.RezScript("default { state_entry() { llSay(0, \"loaded\"); } }");
            Assert.True(h.PumpUntil(() => entered.IsSet), "the compile never started");

            // A window that proves something does NOT happen: while the compile is held, idle is never reported.
            Assert.False(h.PumpUntilIdle(TimeSpan.FromMilliseconds(500)), "idle reported with a compile still running");
            Assert.Null(h.InterpreterFor(item));

            release.Set();
            Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(30)), "never idle after the compile finished");
            Assert.NotNull(h.InterpreterFor(item));
        }
        finally
        {
            release.Set();
        }
    }
}
