/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The core asks for an object's script states one script at a time, on a region thread (a take, a detach, a crossing,
/// a teleport's attachments; the take and teleport callers hold a lock while they ask). The wait for the scheduler is
/// bounded per object, not per script: the first script asked for captures all the object's scripts in one pass, and on a
/// timeout the rest travel without state at once.
/// </summary>
// Runs in parallel: each test has its own harness, items and assets; nothing process-wide is changed.
public class ObjectCaptureWaitTests
{
    private const int Scripts = 100;

    private const string Busy = @"
        integer i;
        default { state_entry() { llSay(0, ""entry""); while (TRUE) { i++; } } }";

    private readonly ITestOutputHelper m_out;
    public ObjectCaptureWaitTests(ITestOutputHelper output) { m_out = output; }

    private static SchedulerHarness BusyObject()
    {
        var h = new SchedulerHarness();
        var asset = UUID.Random();
        for (int i = 0; i < Scripts; i++) h.RezScript(Busy, asset, UUID.Random());
        Assert.True(h.PumpUntil(() => CountOf(h, "entry") == Scripts, TimeSpan.FromSeconds(60)), "not all scripts started");
        return h;
    }

    private static int CountOf(SchedulerHarness h, string s) { int n = 0; foreach (var x in h.Said) if (x == s) n++; return n; }

    private static void SetMasterRunning(SchedulerHarness h, bool running)
    {
        var ms = SavedStateRig.Field(h.Engine, "m_MasterScheduler");
        ms.GetType().GetField("m_Stop", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ms, !running);
    }

    private static void SetTimeout(SchedulerHarness h, int ms)
        => h.Engine.GetType().GetField("CarriedStateTimeoutMs", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Engine, ms);

    private static int States(string xml) => Regex.Matches(xml, "Engine=\"InWorldz.Phlox\"").Count;

    /// <summary>
    /// A 100-script object whose scripts are all busy in endless loops, serialized as a take serializes it from a region
    /// thread while the scheduler runs on its own: every script's state is in the object, captured in one pass.
    /// </summary>
    [Fact]
    public void ABusyHundredScriptObjectIsCapturedInOnePass()
    {
        using var h = BusyObject();
        SetMasterRunning(h, true);
        var stop = new ManualResetEventSlim(false);
        var scheduler = new Thread(() => { while (!stop.IsSet) h.PumpOnce(); }) { IsBackground = true };
        string xml = null;
        var watch = new Stopwatch();
        try
        {
            scheduler.Start();
            xml = CarriedStateTests.OnOwnThread(() =>
            {
                watch.Start();
                string x = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
                watch.Stop();
                return x;
            });
        }
        finally
        {
            stop.Set();
            scheduler.Join();
            SetMasterRunning(h, false);
        }
        m_out.WriteLine($"captured {States(xml)} of {Scripts} busy scripts in {watch.ElapsedMilliseconds} ms");
        Assert.Equal(Scripts, States(xml));
    }

    /// <summary>
    /// The scheduler does not answer: the object waits one timeout, not one per script, and travels without state (each
    /// script starts fresh where it arrives).
    /// </summary>
    [Fact]
    public void AnUnansweredHundredScriptObjectWaitsOneTimeoutNotOnePerScript()
    {
        const int timeoutMs = 300;
        using var h = BusyObject();
        SetTimeout(h, timeoutMs);
        SetMasterRunning(h, true);
        string xml;
        var watch = Stopwatch.StartNew();
        try { xml = CarriedStateTests.OnOwnThread(() => SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup)); }
        finally { SetMasterRunning(h, false); }
        watch.Stop();
        m_out.WriteLine($"{Scripts} scripts, no answer: {watch.ElapsedMilliseconds} ms ({Scripts} x {timeoutMs} ms would be {Scripts * timeoutMs} ms)");
        Assert.Equal(0, States(xml));
        Assert.True(watch.ElapsedMilliseconds < 10 * timeoutMs, $"the object waited {watch.ElapsedMilliseconds} ms");
    }
}
