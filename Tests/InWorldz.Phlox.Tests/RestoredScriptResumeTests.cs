using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script saved mid-flight does not resume where it stopped.
///
/// <para>
/// <c>PhloxExecutionScheduler.FinishedLoading</c> restores the whole saved <c>RuntimeState</c> — RunState,
/// Calls, TopFrame, RunningEvent, EventQueue, NextWakeup — and then <b>overwrites RunState with
/// Waiting</b> and re-registers only the timer and the listens. Everything the state carried about
/// what the script was <i>doing</i> is dropped on the floor.
/// </para>
///
/// <para>
/// <c>StateManager.ScriptUnloaded</c> saves at shutdown, so a script that was asleep or running when
/// the region stopped is saved in precisely the state that never resumes. It does not error; it sits.
/// Then an unrelated event arrives, <c>DoEvent</c> pushes a frame on top of the stale one
/// (<c>RuntimeState.cs:335-336</c>), and the interrupted handler finishes <i>nested inside</i> the new
/// event — after it, in the wrong order, with the wrong locals live.
/// </para>
///
/// <para>
/// <b>These are round trips, not unit tests of a struct.</b> Each runs a script in one engine, saves
/// through the real <c>ScriptUnloaded</c>, tears that engine down, stands up a fresh one and rezzes the
/// same item and asset so <c>LoadState</c> matches — which is why the harness needed a rez with both
/// ids pinned.
/// </para>
///
/// <para>
/// <b>They share one SQLite file</b> (<c>StateManager.DB_FILE</c> is a fixed relative path), so they are
/// one xUnit collection and must not run beside each other. That shared file is also worth noting:
/// it is more global state reachable from a harness test.
/// </para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class RestoredScriptResumeTests
{
    private readonly ITestOutputHelper _out;
    public RestoredScriptResumeTests(ITestOutputHelper o) => _out = o;

    /// <summary>Run a script to some point, save it, and hand back what a fresh engine sees.</summary>
    private (IReadOnlyList<string> saidBefore, string stateAtCapture) CaptureAndRestore(
        string source, Action<SchedulerHarness, UUID> runToTargetState,
        Action<SchedulerHarness, UUID> afterRestore, out IReadOnlyList<string> saidAfter)
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        string stateAtCapture;
        IReadOnlyList<string> before;
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(source, assetId, itemId);
            runToTargetState(h1, itemId);
            stateAtCapture = h1.RunStateOf(itemId);
            before = h1.Said;
            _out.WriteLine("PROBE at capture: " + h1.DumpFrame(itemId)
                           + " LastSyscallIndex=" + h1.LastSyscallIndexOf(itemId));
            _out.WriteLine("captured in RunState=" + stateAtCapture + " said=[" + string.Join(",", before) + "]");
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(source, assetId, itemId);
        _out.WriteLine("PROBE after restore, before pumping: RunState=" + h2.RunStateOf(itemId)
                       + " LastSyscallIndex=" + h2.LastSyscallIndexOf(itemId));
        afterRestore(h2, itemId);
        saidAfter = h2.Said;
        _out.WriteLine("PROBE after pumping: RunState=" + h2.RunStateOf(itemId)
                       + " LastSyscallIndex=" + h2.LastSyscallIndexOf(itemId));
        m_lastIndexAfter = h2.LastSyscallIndexOf(itemId);
        _out.WriteLine("after restore: RunState=" + h2.RunStateOf(itemId)
                       + " said=[" + string.Join(",", saidAfter) + "]");
        return (before, stateAtCapture);
    }

    /// <summary>The index as the second engine left it once the handler finished.</summary>
    private int m_lastIndexAfter = int.MinValue;

    private const string SleepScript =
        "default { state_entry() { llSay(0, \"a\"); llSleep(2); llSay(0, \"b\"); } }";

    /// <summary>
    /// 1a. Sleeping. Captured mid-<c>llSleep</c>, the script is never <c>TrackSleep</c>'d on restore, so
    /// nothing ever wakes it: "b" never arrives. And because the stale frame is still on the stack, the
    /// next unrelated event resumes it nested.
    /// </summary>
    [Fact]
    public void ASleepingScriptWakesUpAndFinishesItsHandler()
    {
        var (before, captured) = CaptureAndRestore(
            SleepScript,
            (h, id) => { h.PumpUntil(() => h.RunStateOf(id) == "Sleeping" && h.Said.Contains("a")); },   // into the llSleep, and stop there
            (h, id) => { h.PumpFor(TimeSpan.FromSeconds(3)); h.PumpUntil(() => h.Said.Contains("b")); },  // past the wake-up
            out var after);

        Assert.Equal("Sleeping", captured);
        Assert.Contains("a", before);
        Assert.DoesNotContain("b", before);

        // The whole of the defect: the sleep is never re-armed, so the handler never finishes.
        Assert.Contains("b", after);
        // ...and it must FINISH, not restart. "said" here is the second engine's scene only, so a
        // re-run of state_entry would show up as "a" appearing again; it must not.
        Assert.DoesNotContain("a", after);
        Assert.Equal(1, after.Count(s => s == "b"));
        // LastSyscallIndex means 'the syscall I am parked in'. The handler has finished,
        // so the script is parked in nothing, and a state saved now must not carry llSleep's index.
        Assert.Equal(-1, m_lastIndexAfter);
    }

    private const string CountingScript =
        "default { state_entry() { integer i; integer n; for (i = 0; i < 2000; i++) { n = n + i; } llSay(0, \"done\"); } }";

    /// <summary>
    /// 1b. Running. Captured after a timeslice, the script is never put back on the run queue, so it
    /// never finishes the loop and "done" never arrives.
    /// </summary>
    /// <summary>
    /// Un-skipped and instrumented: three probes that say WHY a restored Running
    /// script does not finish, before anything in the engine is touched.
    /// </summary>
    [Fact]
    public void ARunningScriptIsPutBackOnTheRunQueueAndFinishes()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        string dumpAtCapture;
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(CountingScript, assetId, itemId);
            // The compile runs on the loader's compile thread, so the pass that starts the script (and
            // gives it its first timeslice) is a later one, not the first.
            var started = DateTime.UtcNow.AddSeconds(30);
            do { h1.PumpOnce(); } while (h1.InterpreterFor(itemId) == null && DateTime.UtcNow < started);
            Assert.Equal("Running", h1.RunStateOf(itemId));
            dumpAtCapture = h1.DumpFrame(itemId);
            _out.WriteLine("(c) AT CAPTURE : " + dumpAtCapture);
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(CountingScript, assetId, itemId);
        _out.WriteLine("(c) AFTER RESTORE, before pumping: " + h2.DumpFrame(itemId));

        // (a) is it ticking at all? IP moving is the observable proof.
        int ipStart = h2.IpOf(itemId);
        var ips = new List<int>();
        for (int round = 0; round < 2000; round++)
        {
            h2.PumpOnce();
            if (round % 100 == 0) ips.Add(h2.IpOf(itemId));
        }
        int ipEnd = h2.IpOf(itemId);

        _out.WriteLine("(a) IP at start=" + ipStart + " at end=" + ipEnd + " moved=" + (ipStart != ipEnd));
        _out.WriteLine("(b) IP every 100 pumps: " + string.Join(",", ips));
        _out.WriteLine("(b) distinct IPs seen: " + ips.Distinct().Count());
        _out.WriteLine("(c) AFTER 2000 PUMPS: " + h2.DumpFrame(itemId));
        _out.WriteLine("    said=[" + string.Join(",", h2.Said) + "]");

        Assert.Contains("done", h2.Said);
        Assert.Equal(1, h2.Said.Count(s => s == "done"));
    }

    private const string TouchScript =
        "default { state_entry() { llSay(0, \"ready\"); } touch_start(integer n) { llSay(0, \"touched\"); } }";

    /// <summary>
    /// 1c. Waiting with a queued event. <c>ProcessEventQueue</c> reads only <c>m_PendingEvents</c>; the
    /// script's own <c>ScriptState.EventQueue</c> is drained by <c>TransitionToWait</c>, which runs only
    /// when a script already on the run queue finishes an event. A restored script is on neither, so a
    /// queued touch_start sits there for ever.
    /// </summary>
    [Fact]
    public void AQueuedEventOnARestoredScriptIsDelivered()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(TouchScript, assetId, itemId);
            h1.PumpUntil(() => h1.Said.Contains("ready") && h1.RunStateOf(itemId) == "Waiting");
            Assert.Contains("ready", h1.Said);
            h1.QueueEventOnScriptState(itemId);            // straight onto ScriptState.EventQueue
            Assert.Equal("Waiting", h1.RunStateOf(itemId));
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(TouchScript, assetId, itemId);
        h2.PumpUntil(() => h2.Said.Contains("touched"));

        // No new event is posted here on purpose: the queued one must be enough.
        _out.WriteLine("after restore said=[" + string.Join(",", h2.Said) + "]");
        Assert.Contains("touched", h2.Said);
    }
}

/// <summary>
/// One collection, so the tests that share the single script_state.db file cannot run beside
/// each other or beside anything else that builds an engine.
/// It runs alone, after every other collection. Only the classes that need that are in it:
/// - process-wide test seams every engine reads: Clock.SetSourceForTesting (ClockBasisTests, RemainingStubTests),
///   StateManager.FailLoadForTest (StateLoadFailedHoldTests), PhloxScriptLoader.CompileDelayForTest / ErrorWaitTimeout /
///   OwnerAlertGrace (CompileOffSchedulerTests, CompileErrorsToEditorTests), SyscallShim.ThrowForTest
///   (ThrownSyscallStaysDeadTests);
/// - the compile cache files on disk (CacheSchemaBumpTests) and the state database file itself (StateDbContentionTests);
/// - tests that measure real time and would read machine load as a failure: timer cadence and floor, the reset
///   throttle (ScriptCleanupTests), regex timeouts (ListenRegexTimeoutTests, RobustnessTests), the parcel timer
///   (NoScriptParcelTests), ticks counted while a lookup is held (AvatarName2KeyTests).
/// Every other class builds its own harness (its own Scene and engine) and runs in parallel:
/// - script_state.db rows are keyed by item id and every test makes its own random ids; engines sharing the file side
///   by side is the state store's design (WAL, busy timeout, one static writer lock), and 13 harness classes ran in
///   parallel before this class did;
/// - the core HttpRequestModule's statics are used by one class only (PhloxHttpHeaderTests), which runs its own tests
///   one at a time;
/// - the one process-wide store scenes share, NullPresenceData, is locked on every access and no longer replaced
///   when a scene is built (core #222), so a scene being built never disturbs another class's avatars (the
///   first parallel run threw in NullPresenceData.Get before that).
/// </summary>
[CollectionDefinition("phlox-state", DisableParallelization = true)]
public class PhloxStateCollection { }

/// <summary>
/// The classes that stand YEngine up beside Phlox. YEngine keeps compiler state in statics
/// (MMRScriptBinOpStr, MMRDelegateCommon), so two YEngines never run at once; the collection runs in parallel with
/// the Phlox-only classes, whose scenes YEngine never sees.
/// </summary>
[CollectionDefinition("phlox-yengine")]
public class PhloxYEngineCollection { }
