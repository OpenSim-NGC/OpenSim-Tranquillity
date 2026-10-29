/*
 * Copyright (c) Legion Builds
 * EventQueueRoutingTests.cs - which events a script's queue keeps when it is full or the script is stopped, the
 * order a prim's events arrive in, and which script an event is for.
 */

using System.Reflection;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The event queue and event routing. Halcyon is the reference where SL says nothing: RuntimeState.QueueEvent lets
/// on_rez, state_entry, state_exit and timer past the 64-event limit; ExecutionScheduler does not queue a null-change
/// control() while one is already queued, and queues (does not run) a stopped script's state_entry. control() and the
/// target events go only to the script they are for, as YEngine does (XMREvents).
/// <para>
/// Not in "phlox-state": every test builds its own harness, touches no process-wide seam and counts events rather
/// than timing them, so the class runs in parallel.
/// </para>
/// </summary>
public class EventQueueRoutingTests
{
    private readonly ITestOutputHelper _out;
    public EventQueueRoutingTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    private static int Count(SchedulerHarness h, string line) => h.Said.Count(s => s == line);

    private static string Said(SchedulerHarness h) => "[" + string.Join(" | ", h.Said) + "]";

    private static global::Phlox.ScriptEngine.PhloxExecutionScheduler Exe(SchedulerHarness h) => TimerLifecycleTests.ExeOf(h);

    private static void Post(SchedulerHarness h, UUID id, SupportedEventList.Events type, params object[] args)
        => Exe(h).PostEvent(id, new PostedEvent { EventType = type, Args = args });

    /// <summary>A script busy in llSleep for <paramref name="sleep"/> seconds after saying "busy", so events queue behind it.</summary>
    private static UUID Busy(SchedulerHarness h, string handlers, double sleep = 1.0)
    {
        var id = h.RezScript(@"
default
{
    state_entry() { llSay(0, ""busy""); llSleep(" + sleep.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"); llSay(0, ""free""); }
" + handlers + @"
}");
        Assert.True(h.PumpUntil(() => Count(h, "busy") == 1 && h.RunStateOf(id) == "Sleeping"), Said(h));
        return id;
    }

    // ── a full queue ──────────────────────────────────────────────────────────

    [Fact]
    public void OnRezIsNotDroppedByAFullQueue()
    {
        using var h = new SchedulerHarness();
        var id = Busy(h, @"
    changed(integer c) { }
    on_rez(integer p) { llSay(0, ""rezzed "" + (string)p); }");
        for (int i = 0; i < 64; i++) Post(h, id, SupportedEventList.Events.CHANGED, 1);
        Post(h, id, SupportedEventList.Events.ON_REZ, 7);

        Assert.True(h.PumpUntil(() => Count(h, "rezzed 7") == 1, TimeSpan.FromSeconds(10)),
            "on_rez was dropped by the full queue: " + Said(h));
    }

    [Fact]
    public void OtherEventsAreStillDroppedPastSixtyFour()
    {
        using var h = new SchedulerHarness();
        var id = Busy(h, @"
    changed(integer c) { llSay(0, ""c""); }");
        for (int i = 0; i < 70; i++) Post(h, id, SupportedEventList.Events.CHANGED, 1);

        Assert.True(h.PumpUntil(() => Count(h, "c") >= 64, TimeSpan.FromSeconds(10)), Said(h));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(64, Count(h, "c"));
    }

    // ── control() spam ────────────────────────────────────────────────────────

    [Fact]
    public void NullChangeControlEventsAreCoalescedWhileOneIsQueued()
    {
        using var h = new SchedulerHarness();
        var id = Busy(h, @"
    control(key k, integer level, integer edge) { llSay(0, ""control "" + (string)level + "" "" + (string)edge); }");
        string agent = UUID.Random().ToString();
        Post(h, id, SupportedEventList.Events.CONTROL, agent, 1, 1);    // a key went down
        for (int i = 0; i < 10; i++)
            Post(h, id, SupportedEventList.Events.CONTROL, agent, 1, 0);   // held, nothing changed

        Assert.True(h.PumpUntil(() => Count(h, "free") == 1, TimeSpan.FromSeconds(10)), Said(h));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Count(h, "control 1 1"));
        Assert.Equal(0, Count(h, "control 1 0"));
    }

    [Fact]
    public void ANullChangeControlIsQueuedWhenNoControlIsWaiting()
    {
        using var h = new SchedulerHarness();
        var id = Busy(h, @"
    control(key k, integer level, integer edge) { llSay(0, ""control "" + (string)level + "" "" + (string)edge); }");
        string agent = UUID.Random().ToString();
        for (int i = 0; i < 10; i++)
            Post(h, id, SupportedEventList.Events.CONTROL, agent, 1, 0);

        Assert.True(h.PumpUntil(() => Count(h, "control 1 0") >= 1, TimeSpan.FromSeconds(10)), Said(h));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Count(h, "control 1 0"));
    }

    // ── a stopped script ──────────────────────────────────────────────────────

    [Fact]
    public void AStoppedScriptsStateEntryWaitsUntilItIsStarted()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry() { llSay(0, ""entry""); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "entry") == 1 && h.RunStateOf(id) == "Waiting"), Said(h));

        h.Engine.SetScriptState(id, false, false);
        Assert.True(h.PumpUntil(() => h.StatusOf(id).Contains("GeneralEnable=False")), h.StatusOf(id));
        Post(h, id, SupportedEventList.Events.STATE_ENTRY);
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.True(Count(h, "entry") == 1, "a stopped script ran state_entry: " + Said(h));
        Assert.False(h.IsOnRunQueue(id), "a stopped script was put on the run queue");

        h.Engine.SetScriptState(id, true, false);
        Assert.True(h.PumpUntil(() => Count(h, "entry") == 2, TimeSpan.FromSeconds(10)),
            "the held state_entry did not run on start: " + Said(h) + " " + h.StatusOf(id));
    }

    // ── order ─────────────────────────────────────────────────────────────────

    [Fact]
    public void APrimsObjectEventsArriveInTheOrderTheyWerePosted()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry() { llSay(0, ""ready""); }
    changed(integer c) { llSay(0, (string)c); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "ready") == 1 && h.RunStateOf(id) == "Waiting"), Said(h));
        h.ClearSaid(id);

        const int n = 40;
        for (int i = 1; i <= n; i++)
            h.Engine.PostObjectEvent(h.Prim.LocalId, new EventParams("changed", new object[] { i }, new DetectParams[0]));

        Assert.True(h.PumpUntil(() => h.Said.Count >= n, TimeSpan.FromSeconds(20)), Said(h));
        var expected = Enumerable.Range(1, n).Select(i => i.ToString()).ToArray();
        Assert.Equal(expected, h.Said.Take(n).ToArray());
    }

    // ── who an event is for (the script concerned only, as YEngine) ──────────

    private const string Listener = @"
default
{
    control(key k, integer level, integer edge) { llSay(0, llGetScriptName() + "" control""); }
    at_target(integer t, vector tp, vector p) { llSay(0, llGetScriptName() + "" at_target""); }
    not_at_target() { llSay(0, llGetScriptName() + "" not_at_target""); }
    at_rot_target(integer t, rotation tr, rotation r) { llSay(0, llGetScriptName() + "" at_rot_target""); }
    not_at_rot_target() { llSay(0, llGetScriptName() + "" not_at_rot_target""); }
}";

    private static (UUID a, string aName, UUID b) TwoScripts(SchedulerHarness h)
    {
        var a = h.RezScript(Listener);
        var b = h.RezScript(Listener);
        Assert.True(h.PumpUntil(() => h.RunStateOf(a) == "Waiting" && h.RunStateOf(b) == "Waiting"), h.Diagnose(a) + " / " + h.Diagnose(b));
        return (a, h.Prim.Inventory.GetInventoryItem(a).Name, b);
    }

    private static void Raise(SchedulerHarness h, string handler, params object[] args)
        => h.Engine.GetType().GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(h.Engine, args);

    [Fact]
    public void ControlGoesOnlyToTheScriptThatTookTheControls()
    {
        using var h = new SchedulerHarness();
        var (a, aName, _) = TwoScripts(h);
        Raise(h, "OnScriptControlEvent", a, UUID.Random(), 1u, 1u);

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.EndsWith(" control"))), Said(h));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { aName + " control" }, h.Said.Where(s => s.EndsWith(" control")).ToArray());
    }

    [Fact]
    public void TargetEventsGoOnlyToTheScriptThatSetTheTarget()
    {
        using var h = new SchedulerHarness();
        var (a, aName, _) = TwoScripts(h);
        Raise(h, "OnScriptAtTargetEvent", a, 1u, Vector3.Zero, Vector3.Zero);
        Raise(h, "OnScriptNotAtTargetEvent", a);
        Raise(h, "OnScriptAtRotTargetEvent", a, 1u, Quaternion.Identity, Quaternion.Identity);
        Raise(h, "OnScriptNotAtRotTargetEvent", a);

        Assert.True(h.PumpUntil(() => h.Said.Count >= 4), Said(h));
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        Assert.All(h.Said, s => Assert.StartsWith(aName + " ", s));
        Assert.Equal(4, h.Said.Count);
    }

    // ── a crashed script's message (Halcyon's wording) ────────────────────────

    [Fact]
    public void ACrashedScriptShoutsHalcyonsWording()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry() { list l; integer i = llList2Integer(l, 0) / 0; llSay(0, ""after""); }
}");
        Assert.True(h.PumpUntil(() => h.SaidOn.Any(m => m.Channel == DEBUG_CHANNEL)), Said(h));
        var shout = h.SaidOn.First(m => m.Channel == DEBUG_CHANNEL).Message;
        _out.WriteLine(shout);
        var assetId = h.Prim.Inventory.GetInventoryItem(id).AssetID;
        // ShoutError puts its own prefix in front; the message is Halcyon's.
        Assert.Contains("Script " + assetId + " encountered a problem and was stopped: ", shout);
    }

    // ── what a stop lets go of ────────────────────────────────────────────────

    private const int TAKE_CONTROLS = 0x4;

    private const string Controller = @"
default
{
    state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""start""); }
    listen(integer c, string n, key k, string m)
    {
        if (llGetSubString(m, 0, 2) == ""ask"") llRequestPermissions((key)llGetSubString(m, 4, -1), " + "0x4" + @");
        else if (m == ""take"") { llTakeControls(CONTROL_FWD, TRUE, FALSE); llSay(0, ""took""); }
    }
    run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
    control(key id, integer l, integer e) { }
}";

    private static void Say(SchedulerHarness h, string msg)
        => h.Scene.SimChat(msg, OpenSim.Framework.ChatTypeEnum.Region, 7, h.Prim.AbsolutePosition, "tester", UUID.Random(), false);

    [Fact]
    public void StoppingAScriptReleasesItsControlsAndStartingItTakesThemAgain()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(Controller);
        Assert.True(h.PumpUntil(() => Count(h, "start") == 1 && h.RunStateOf(id) == "Waiting"), Said(h));
        var client = (TestClient)SceneHelpers.AddScenePresence(h.Scene, UUID.Random()).ControllingClient;
        var sp = h.Scene.GetScenePresence(client.AgentId);

        Say(h, "ask " + client.AgentId);
        Assert.True(h.PumpUntil(() => client.ScriptQuestions.Count > 0), "no permission question was sent");
        client.FireScriptAnswer(h.Prim.UUID, id, TAKE_CONTROLS);
        Assert.True(h.PumpUntil(() => Count(h, "rtp=" + TAKE_CONTROLS) == 1), Said(h));
        Say(h, "take");
        Assert.True(h.PumpUntil(() => Count(h, "took") == 1), Said(h));
        Assert.True(sp.HasScriptControls(id));

        h.Engine.SetScriptState(id, false, false);
        Assert.True(h.PumpUntil(() => !sp.HasScriptControls(id)), "a stopped script kept the avatar's controls: " + h.StatusOf(id));
        var st = (RuntimeState)h.StateOf(id);
        Assert.True(st.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control), "the stop dropped the record a start re-takes from");

        h.Engine.SetScriptState(id, true, false);
        Assert.True(h.PumpUntil(() => sp.HasScriptControls(id)), "the started script did not take its controls again: " + h.StatusOf(id));
    }

    [Fact]
    public void StoppingAScriptStopsItsSensorRepeatAndStartingItRestoresIt()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry() { llSensorRepeat("""", NULL_KEY, AGENT, 10.0, PI, 30.0); llSay(0, ""start""); }
    no_sensor() { }
}");
        Assert.True(h.PumpUntil(() => Count(h, "start") == 1 && h.RunStateOf(id) == "Waiting"), Said(h));
        var sensors = h.Engine.AsyncCommands.SensorRepeatPlugin;
        Assert.Equal(1, sensors.RepeatersFor(id));

        h.Engine.SetScriptState(id, false, false);
        Assert.True(h.PumpUntil(() => sensors.RepeatersFor(id) == 0), "a stopped script's sensor repeat kept running");

        h.Engine.SetScriptState(id, true, false);
        Assert.True(h.PumpUntil(() => sensors.RepeatersFor(id) == 1), "the started script's sensor repeat did not come back");
    }
}
