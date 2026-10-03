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
/// A row saved by the engine before listens and the timer's phase were restored still loads. The row below was written
/// by that engine (the build before this change) for <see cref="Src"/>, rezzed with start parameter 42: g = 7, a 30 s
/// timer, a listen on channel 5 (which that build did not record), idle in state default.
/// </summary>
// Runs in parallel: each run restores the row under a new item id.
public class SavedStateCompatTests
{
    private readonly ITestOutputHelper _out;
    public SavedStateCompatTests(ITestOutputHelper o) => _out = o;

    internal const string Src =
        "integer g = 0;\n" +
        "default {\n" +
        "  state_entry() { g = 7; llListen(5, \"\", NULL_KEY, \"\"); llSetTimerEvent(30.0); llSay(0, \"up\"); }\n" +
        "  listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + m + \" g=\" + (string)g + \" p=\" + (string)llGetStartParameter()); }\n" +
        "  touch_start(integer n) { llSay(0, \"g=\" + (string)g + \" p=\" + (string)llGetStartParameter()); }\n" +
        "  timer() { llSay(0, \"tick\"); }\n" +
        "}\n";

    /// <summary>The asset the row was saved for: a row is restored only for the asset it names.</summary>
    internal static readonly UUID AssetId = new UUID("2b9e4c71-8d3a-4f6e-b1c5-7a0d9e2f3c84");

    internal const string RowWrittenByEarlierBuild =
        "CEgQABoCCAcqHAoPCgtzdGF0ZV9lbnRyeSALKP///////////wEyHAoPCgtzdGF0ZV9lbnRyeSALKP///////////wE6Awj9AkgCUAFa" +
        "Cwiav7qK1f/PPxAFYgsImr+6itX/zz8QBWoLCODllIrV/88/EAVwsOoBeg0IHSD///////////8BmAEqrQG28309sAH///////////8B" +
        "2gFAMTc2QkMzNURFRDk0RUJFNDE4NzdDNUVGQTI2RjY2N0M0QUVDRTQyOUIxQzkwOTZDM0E4ODM2MTkzRDExRjhDRA==";

    [Fact]
    public void ARowSavedByTheEarlierBuildRestores()
    {
        var item = UUID.Random();
        SavedStateRig.PutRow(item, AssetId, Convert.FromBase64String(RowWrittenByEarlierBuild), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var h = new SchedulerHarness();
        h.RezScript(Src, AssetId, item);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null, TimeSpan.FromSeconds(15)), "not loaded: " + h.StatusOf(item));
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("up", h.Said);   // restored, not started fresh

        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("g="))), SavedStateRig.SaidText(h));
        // The row carries the start parameter (42), but a region start does not restore it: SL's llGetStartParameter
        // "does not survive region restarts (SVC-2251)".
        using (var ms = new System.IO.MemoryStream(Convert.FromBase64String(RowWrittenByEarlierBuild)))
            Assert.Equal(42, ProtoBuf.Serializer.Deserialize<InWorldz.Phlox.Serialization.SerializedRuntimeState>(ms).StartParameter);
        Assert.Contains("g=7 p=0", h.Said);

        // The timer is armed again: that row recorded when it was scheduled, so it keeps (about) its whole 30 s.
        ulong? readyOn = (ulong?)SavedStateRig.Member(SavedStateRig.Exe(h), "TimerReadyOn", item);
        Assert.NotNull(readyOn);
        long inMs = (long)readyOn!.Value - (long)InWorldz.Phlox.Util.Clock.Now;
        _out.WriteLine($"timer in {inMs} ms");
        Assert.InRange(inMs, 20_000, 30_500);

        // That build never recorded listens, so a row it wrote has none to bring back: the script hears nothing until it
        // listens again (its next reset or state_entry). Rows written from now on carry them.
        h.Engine.ListenManager.DeliverChat(5, "Test User", UUID.Random(), "hello");
        h.PumpFor(TimeSpan.FromMilliseconds(500));
        Assert.DoesNotContain(h.Said, s => s.StartsWith("heard"));
    }
}
