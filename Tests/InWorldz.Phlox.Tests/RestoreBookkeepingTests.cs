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
/// What a script gets back when it is restored from its saved state at a region start: its start parameter, its timer's
/// phase, its listens, its stopped state, and the start-up events in SL's order. Each test saves in one engine and
/// restores the same item and asset in a second, as a region restart does.
/// </summary>
// Runs in parallel: each test has its own items in the shared state file; nothing process-wide is changed, except in
// WithTheEngineClockDriven, which is serial.
public class RestoreBookkeepingTests
{
    private readonly ITestOutputHelper _out;
    public RestoreBookkeepingTests(ITestOutputHelper o) => _out = o;

    private const int RegionStart = 0, NewRez = 1, PrimCrossing = 2, AttachedRez = 4;

    /// <summary>
    /// llGetStartParameter after a region start is 0, whatever the object was rezzed with. SL's llGetStartParameter:
    /// "The start parameter does not survive region restarts (SVC-2251) or region change (SVC-3258, crossing or
    /// teleport)." The rest of the saved state still comes back.
    /// </summary>
    [Fact]
    public void TheStartParameterDoesNotSurviveARegionStart()
    {
        const string src = "default { state_entry() { llSay(0, \"up\"); } touch_start(integer n) { llSay(0, \"p=\" + (string)llGetStartParameter()); } }";
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SavedStateRig.Rez(h1, h1.Prim, src, asset, item, 42, true, NewRez);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
            h1.SaveState(item);
        }
        using var h2 = new SchedulerHarness();
        h2.RezScript(src, asset, item);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h2.PostTouch(item);
        Assert.True(h2.PumpUntil(() => h2.Said.Any(s => s.StartsWith("p="))), SavedStateRig.SaidText(h2));
        Assert.Contains("p=0", h2.Said);
        Assert.DoesNotContain("up", h2.Said);   // restored, not started fresh
    }

    /// <summary>
    /// A crossing restores the script without its start parameter, as a region start does (SL's llGetStartParameter:
    /// it does not survive "region change (SVC-3258, crossing or teleport)"); a rez that restores a saved state gives the
    /// script the rez's parameter, as on_rez reports it.
    /// </summary>
    [Theory]
    [InlineData(PrimCrossing, 0, false, "p=0")]
    [InlineData(NewRez, 17, true, "p=17")]
    public void ARestoreKeepsOnlyTheStartParameterItsLoadCarries(int stateSource, int startParam, bool postOnRez, string expected)
    {
        const string src = "default { state_entry() { llSay(0, \"up\"); } touch_start(integer n) { llSay(0, \"p=\" + (string)llGetStartParameter()); } }";
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SavedStateRig.Rez(h1, h1.Prim, src, asset, item, 42, true, NewRez);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
            h1.SaveState(item);
        }
        using var h2 = new SchedulerHarness();
        SavedStateRig.Rez(h2, h2.Prim, src, asset, item, startParam, postOnRez, stateSource);
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null, TimeSpan.FromSeconds(15)), "not loaded: " + h2.StatusOf(item));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h2.PostTouch(item);
        Assert.True(h2.PumpUntil(() => h2.Said.Any(s => s.StartsWith("p="))), SavedStateRig.SaidText(h2));
        Assert.Contains(expected, h2.Said);
        Assert.DoesNotContain("up", h2.Said);   // restored, not started fresh
    }

    /// <summary>
    /// The restored timer's wake is on the engine's clock (Clock), so this test stops that clock and moves it by hand:
    /// whether timer() has come then depends only on how far the clock was moved, never on how fast the machine is. The
    /// clock is process-wide, hence "phlox-state"; the rest of the class runs in parallel.
    /// </summary>
    [Collection("phlox-state")]
    public class WithTheEngineClockDriven : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private bool m_frozen;
        private ulong m_now;

        public WithTheEngineClockDriven(ITestOutputHelper o)
        {
            _out = o;
            InWorldz.Phlox.Util.Clock.SetSourceForTesting(() => m_frozen ? m_now : (ulong)Environment.TickCount64);
        }

        public void Dispose() => InWorldz.Phlox.Util.Clock.SetSourceForTesting(null);

        private void Freeze() { m_now = (ulong)Environment.TickCount64; m_frozen = true; }

        /// <summary>
        /// A timer keeps its phase across a restart: 7 s of a 10 s timer had passed when the state was saved, so the first
        /// timer() comes 3 s after the restore (Halcyon InjectScript), not a full interval later and not at once.
        /// The saved state carries its times as wall-clock dates (SerializedRuntimeState), taken a few statements apart
        /// in real time, which can only add to the 3 s; the test allows a second for that and no more.
        /// </summary>
        [Fact]
        public void ARestoredTimerFiresAfterTheTimeItHadLeft()
        {
            const string src = "default { state_entry() { llSetTimerEvent(10.0); llSay(0, \"up\"); } timer() { llSay(0, \"tick\"); } }";
            var asset = UUID.Random(); var item = UUID.Random();
            using (var h1 = new SchedulerHarness())
            {
                h1.RezScript(src, asset, item);
                Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
                Freeze();   // the 7 s below are exactly 7 s when the state is captured
                var interp = (InWorldz.Phlox.VM.Interpreter)h1.InterpreterFor(item);
                interp.ScriptState.TimerLastScheduledOn = InWorldz.Phlox.Util.Clock.Now - 7000;   // scheduled 7 s ago
                h1.SaveState(item);
            }
            using var h2 = new SchedulerHarness();
            Freeze();
            ulong restoredAt = m_now;
            h2.RezScript(src, asset, item);
            Assert.True(h2.PumpUntil(() => h2.InterpreterFor(item) != null, TimeSpan.FromSeconds(15)), "not loaded: " + h2.StatusOf(item));
            Assert.True(h2.PumpUntilIdle(TimeSpan.FromSeconds(10)));

            // 1 ms short of the 3 s it had left: no timer() yet. Firing here is a restore that lost the phase forwards.
            m_now = restoredAt + 2999;
            Assert.True(h2.PumpUntilIdle(TimeSpan.FromSeconds(10)));
            _out.WriteLine($"at +2999 ms: said {SavedStateRig.SaidText(h2)}");
            Assert.DoesNotContain("tick", h2.Said);

            // At +4 s, well short of a full 10 s interval, exactly one timer().
            m_now = restoredAt + 4000;
            Assert.True(h2.PumpUntil(() => h2.Said.Contains("tick"), TimeSpan.FromSeconds(10)),
                "no timer() 4 s after the restore; a full 10 s interval was waited again. said " + SavedStateRig.SaidText(h2));
            Assert.True(h2.PumpUntilIdle(TimeSpan.FromSeconds(10)));
            _out.WriteLine($"at +4000 ms: said {SavedStateRig.SaidText(h2)}");
            Assert.Equal(1, h2.Said.Count(s => s == "tick"));
            Assert.DoesNotContain("up", h2.Said);   // restored, not started fresh
        }
    }

    private const string LoopSrc =
        "integer n; default { state_entry() { llSay(0, \"up\"); } " +
        "touch_start(integer t) { llSay(0, \"begin\"); integer i; for (i = 0; i < 4000; i++) n++; llSay(0, \"end \" + (string)n); } }";

    /// <summary>
    /// A script stopped in the middle of an event (the Running box unticked) is saved stopped and comes back stopped: it
    /// does not finish the event after a restart (Halcyon InjectScript runs only an enabled script). Ticking Running
    /// carries on from where it was.
    /// </summary>
    [Fact]
    public void AScriptStoppedMidEventStaysStoppedAfterARestoreUntilStarted()
    {
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(LoopSrc, asset, item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
            h1.PostTouch(item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("begin")), SavedStateRig.SaidText(h1));
            var exe1 = SavedStateRig.Exe(h1);
            exe1.ChangeEnabledStatus(item, false);
            h1.PumpOnce();
            Assert.Equal("Running", h1.RunStateOf(item));
            Assert.DoesNotContain(h1.Said, s => s.StartsWith("end"));
            h1.SaveState(item);
        }
        using var h2 = new SchedulerHarness();
        h2.RezScript(LoopSrc, asset, item);
        h2.PumpFor(TimeSpan.FromSeconds(1.5));   // a window that proves nothing runs
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("end"));
        Assert.False(SavedStateRig.Exe(h2).IsOnRunQueue(item));

        SavedStateRig.Exe(h2).ChangeEnabledStatus(item, true);
        Assert.True(h2.PumpUntil(() => h2.Said.Any(s => s.StartsWith("end"))), SavedStateRig.SaidText(h2));
        Assert.Contains("end 4000", h2.Said);
    }

    /// <summary>
    /// llListen's listens are saved with the script and come back with their handles (Halcyon llListen records an
    /// ActiveListen; OnScriptInjected listens again with the saved handle), so the restored script hears its channel
    /// and llListenRemove of the handle it kept still works.
    /// </summary>
    [Fact]
    public void ListensComeBackWithTheirHandlesAfterARestore()
    {
        const string src =
            "default { state_entry() { llSay(0, \"h=\" + (string)llListen(5, \"\", \"\", \"\")); } " +
            "listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + m); } " +
            "touch_start(integer t) { llListenRemove(1); llSay(0, \"removed\"); } }";
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(src, asset, item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("h=1")), SavedStateRig.SaidText(h1));
            h1.SaveState(item);
        }
        using var h2 = new SchedulerHarness();
        h2.RezScript(src, asset, item);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h2.Engine.ListenManager.DeliverChat(5, "Test User", UUID.Random(), "hello");
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("heard hello"), TimeSpan.FromSeconds(5)), SavedStateRig.SaidText(h2));

        h2.PostTouch(item);
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("removed")), SavedStateRig.SaidText(h2));
        h2.Engine.ListenManager.DeliverChat(5, "Test User", UUID.Random(), "again");
        h2.PumpFor(TimeSpan.FromSeconds(1));   // proves the removed listen hears nothing
        Assert.DoesNotContain("heard again", h2.Said);
    }

    /// <summary>SL state: "All listens are released". A listen of the state the script left is not brought back by a restore.</summary>
    [Fact]
    public void AStateChangeLeavesNoSavedListenBehind()
    {
        const string src =
            "default { state_entry() { llListen(5, \"\", \"\", \"\"); state two; } } " +
            "state two { state_entry() { llSay(0, \"in two\"); } listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + m); } }";
        var asset = UUID.Random(); var item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(src, asset, item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("in two")), SavedStateRig.SaidText(h1));
            var interp = (InWorldz.Phlox.VM.Interpreter)h1.InterpreterFor(item);
            Assert.Empty(interp.ScriptState.ActiveListens);
            h1.SaveState(item);
        }
        using var h2 = new SchedulerHarness();
        h2.RezScript(src, asset, item);
        h2.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h2.Engine.ListenManager.DeliverChat(5, "Test User", UUID.Random(), "hello");
        h2.PumpFor(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("heard hello", h2.Said);
    }

    private const string OrderSrc =
        "default { state_entry() { llSay(0, \"state_entry\"); } on_rez(integer p) { llSay(0, \"on_rez\"); } " +
        "attach(key id) { llSay(0, \"attach\"); } changed(integer c) { if (c & CHANGED_REGION_START) llSay(0, \"region_start\"); } }";

    /// <summary>
    /// An attachment worn from inventory: state_entry, then on_rez, then attach - SL: "on_rez will be triggered prior to
    /// attach when attaching from inventory or during login" - and attach once.
    /// </summary>
    [Fact]
    public void AnAttachmentFromInventoryGetsOnRezBeforeAttachAndAttachOnce()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.AttachedAvatar = UUID.Random();
        var item = SavedStateRig.Rez(h, h.Prim, OrderSrc, UUID.Random(), UUID.Random(), 0, true, AttachedRez);
        Assert.True(h.PumpUntil(() => h.Said.Contains("attach") && h.Said.Contains("on_rez")), SavedStateRig.SaidText(h));
        h.PumpFor(TimeSpan.FromMilliseconds(500));   // proves no second attach arrives
        var said = h.Said.Where(s => s is "state_entry" or "on_rez" or "attach").ToList();
        Assert.Equal(new[] { "state_entry", "on_rez", "attach" }, said);
    }

    /// <summary>A script started fresh by the region's start gets changed(CHANGED_REGION_START), as a restored one does (YEngine, Halcyon).</summary>
    [Fact]
    public void AFreshScriptAtARegionStartGetsChangedRegionStart()
    {
        using var h = new SchedulerHarness();
        h.RezScript(OrderSrc, UUID.Random(), UUID.Random());
        Assert.True(h.PumpUntil(() => h.Said.Contains("region_start"), TimeSpan.FromSeconds(5)), SavedStateRig.SaidText(h));
        Assert.Equal(new[] { "state_entry", "region_start" }, h.Said.Where(s => s is "state_entry" or "region_start").ToArray());
    }

    /// <summary>
    /// A row that is there but cannot be decoded is bad data, not a busy database: it is moved to script_state_rejected
    /// and the script starts fresh (Halcyon: "Could not load state ... script will be reset"; YEngine deletes the bad state
    /// file and resets), instead of being held stopped on every restart.
    /// </summary>
    [Fact]
    public void ACorruptRowIsMovedAsideAndTheScriptStartsFresh()
    {
        const string src = "default { state_entry() { llSay(0, \"up\"); } }";
        var asset = UUID.Random(); var item = UUID.Random();
        SavedStateRig.PutRow(item, asset, new byte[] { 0x07, 0x07, 0x07, 0x07 }, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var h = new SchedulerHarness();
        h.RezScript(src, asset, item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("up"), TimeSpan.FromSeconds(10)), "the script did not start fresh: " + h.StatusOf(item));
        SavedStateRig.WaitForWrites(h);
        Assert.Equal(1, SavedStateRig.RejectedRows(item));
    }
}
