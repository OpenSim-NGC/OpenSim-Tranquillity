using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A saved script state carries an execution position (IP, call frames, operand stack, running
/// event) that is only meaningful in the bytecode it was captured on. When the script has been
/// recompiled since (a compiler change, a cache purge), a state saved mid-event must come back
/// idle: globals, the LSL state, queued events and timers kept, the in-progress event dropped.
/// Unchanged bytecode resumes where it stopped.
/// </summary>
public class StateRecompileGuardTests
{
    private static readonly UUID Item = new UUID("00000000-0000-0000-0000-0000000000a1");

    private const string Script =
        "integer g;\n" +
        "default\n{\n" +
        "    state_entry()\n    {\n        g = 41;\n        llSleep(30.0);\n        g = g + 1;\n" +
        "        llOwnerSay(\"after \" + (string)g);\n    }\n" +
        "    touch_start(integer n)\n    {\n        llOwnerSay(\"touched \" + (string)g);\n    }\n}\n";

    // The same script with one more statement ahead of the sleep: same globals, different code
    // addresses - what a recompile by a different compiler looks like to a saved state.
    private static readonly string Recompiled = Script.Replace("g = 41;", "llOwnerSay(\"begin\");\n        g = 41;");

    private static PostedEvent Touch() =>
        new PostedEvent { EventType = SupportedEventList.Events.TOUCH_START, Args = new object[] { 1 } };

    /// <summary>Saves the state as the state store does (protobuf) and reads it back.</summary>
    private static SerializedRuntimeState Capture(RuntimeState state)
    {
        var ser = SerializedRuntimeState.FromRuntimeState(state);
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, ser);
        ms.Position = 0;
        return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
    }

    /// <summary>The restore under test: today's path, which records no bytecode identity.</summary>
    private static RuntimeState Restore(SerializedRuntimeState ser, CompiledScript script, out string note)
    {
        note = null;
        return ser.ToRuntimeState();
    }

    /// <summary>A row written before states recorded their bytecode: every row, today.</summary>
    private static void MakeLegacy(SerializedRuntimeState ser) { }

    private static string Describe(RuntimeState s)
    {
        int queued;
        lock (s.EventQueueLock) queued = s.EventQueue.Count;
        return $"run={s.RunState} state={s.LSLState} frames={s.Calls.Count} operands={s.Operands.Count} " +
               $"running={(s.RunningEvent == null ? "none" : s.RunningEvent.EventType.ToString())} " +
               $"g={s.Globals[0]} queued={queued} timer={s.TimerInterval}";
    }

    private static string RecompiledLine =>
        $"[PhloxState]: {Item} recompiled since its state was saved; resumed idle in state 0, in-progress event dropped";

    /// <summary>A script asleep in llSleep with a touch queued behind it and a timer set.</summary>
    private static ExprRunner.Session SleepingWithQueue()
    {
        var s = ExprRunner.Session.Start(ExprRunner.CompileLsl(Script));
        Assert.True(s.State.RunState == RuntimeState.Status.Sleeping, "did not park in llSleep: " + s.Result.Describe());
        lock (s.State.EventQueueLock) s.State.EventQueue.Add(Touch());
        s.State.TimerInterval = 5000;
        return s;
    }

    [Fact]
    public void ChangedBytecode_ScriptSavedInLlSleep_ComesBackIdleWithGlobalsQueueAndTimer()
    {
        var ser = Capture(SleepingWithQueue().State);
        var recompiled = ExprRunner.CompileLsl(Recompiled);

        var restored = Restore(ser, recompiled, out string note);
        string state = Describe(restored);

        // Idle: the scheduler hands it its queued touch; nothing of the old handler runs.
        var s = ExprRunner.Session.Attach(recompiled, restored);
        s.Wake();
        PostedEvent next;
        lock (restored.EventQueueLock) next = restored.EventQueue.Count > 0 ? restored.EventQueue.RemoveFirst() : null;
        if (next != null) s.Deliver(next);

        const string expectState = "run=Waiting state=0 frames=0 operands=0 running=none g=41 queued=1 timer=5000";
        Assert.True(state == expectState && s.Result.Ok && s.Result.Said.SequenceEqual(new[] { "touched 41" }) && note == RecompiledLine,
            $"restored {state} (expect {expectState}); then {s.Result.Describe()} (expect [touched 41]); " +
            $"log \"{note ?? "(none)"}\" (expect \"{RecompiledLine}\")");
    }

    [Fact]
    public void UnchangedBytecode_ScriptSavedInLlSleep_ResumesAndFinishesTheHandler()
    {
        var script = ExprRunner.CompileLsl(Script);
        var ser = Capture(SleepingWithQueue().State);

        var restored = Restore(ser, script, out string note);
        var s = ExprRunner.Session.Attach(script, restored);
        s.Wake();

        Assert.True(note == null && s.Result.Ok && s.Result.Said.SequenceEqual(new[] { "after 42" }),
            $"log \"{note ?? "(none)"}\"; then {s.Result.Describe()} (expect no log, [after 42])");
        Assert.Equal(42, s.State.Globals[0]);
    }

    [Fact]
    public void LegacyStateWithoutIdentity_MidEvent_IsDroppedToIdle()
    {
        var script = ExprRunner.CompileLsl(Script);
        var ser = Capture(SleepingWithQueue().State);
        MakeLegacy(ser);

        var restored = Restore(ser, script, out string note);
        string state = Describe(restored);

        const string expectState = "run=Waiting state=0 frames=0 operands=0 running=none g=41 queued=1 timer=5000";
        Assert.True(state == expectState && note == RecompiledLine,
            $"restored {state} (expect {expectState}); log \"{note ?? "(none)"}\" (expect \"{RecompiledLine}\")");
    }

    [Fact]
    public void LegacyStateWithoutIdentity_Idle_IsRestoredAsToday()
    {
        var script = ExprRunner.CompileLsl(Script);
        var first = ExprRunner.Session.Start(script);
        first.Wake();   // finish state_entry: idle, g = 42
        Assert.True(first.State.RunState == RuntimeState.Status.Waiting, first.Result.Describe());
        var ser = Capture(first.State);
        MakeLegacy(ser);

        var restored = Restore(ser, script, out string note);
        string state = Describe(restored);
        var s = ExprRunner.Session.Attach(script, restored);
        s.Deliver(Touch());

        // Exactly what today's restore gives (the finished handler's event is still recorded).
        string expectState = Describe(ser.ToRuntimeState());
        Assert.True(state == expectState && note == null && s.Result.Ok && s.Result.Said.SequenceEqual(new[] { "touched 42" }),
            $"restored {state} (expect {expectState}); log \"{note ?? "(none)"}\"; then {s.Result.Describe()} (expect [touched 42])");
    }
}
