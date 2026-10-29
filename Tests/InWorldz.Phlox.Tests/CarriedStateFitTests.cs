/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// State carried inside an object is input from outside the simulator: inventory from another grid, a visitor's
/// attachments, an object another resident made. A state that decodes but does not fit the compiled script it is loaded
/// for is refused and the script starts fresh; what cannot be checked without the script's declarations is contained, so
/// the worst outcome is that one script stopping with its usual error. Memory in use is recomputed from the restored
/// values, the queue and the timer are held to a running script's limits, and the envelope's length is bounded before
/// anything is decoded.
/// </summary>
// Runs in parallel: each test has its own harness, items and assets; nothing process-wide is changed.
public class CarriedStateFitTests
{
    private const int DebugChannel = 2147483647;

    private const string Counter = @"
        integer n;
        default {
            state_entry() { n = 5; llSay(0, ""entry""); }
            touch_start(integer t) { n++; llSay(0, ""n="" + (string)n); }
        }";

    private const string Other = @"
        default {
            touch_start(integer t) { llSay(0, ""other here""); }
        }";

    /// <summary>A real state of the counter, captured after one touch (n = 6), and the asset it runs.</summary>
    private static (SerializedRuntimeState State, UUID Asset) Captured()
    {
        using var h = new SchedulerHarness();
        var asset = UUID.Random();
        var item = UUID.Random();
        h.RezScript(Counter, asset, item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=6")), SavedStateRig.SaidText(h));
        Assert.True(h.PumpUntil(() => h.RunStateOf(item) == "Waiting"), "the touch did not finish");   // captured idle
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(h.Engine.GetXMLState(item));
        byte[] blob = Convert.FromBase64String(doc.DocumentElement!["ScriptState"]!.InnerText);
        return (Decode(blob), asset);
    }

    private static SerializedRuntimeState Decode(byte[] blob)
    {
        using var ms = new MemoryStream(blob);
        return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
    }

    private static byte[] Encode(SerializedRuntimeState s)
    {
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, s);
        return ms.ToArray();
    }

    private static string Envelope(UUID item, UUID asset, byte[] blob)
        => $"<State Engine=\"InWorldz.Phlox\" UUID=\"{item}\" Asset=\"{asset}\" Version=\"1\"><ScriptState>{Convert.ToBase64String(blob)}</ScriptState></State>";

    /// <summary>The counter arrives in a new region carrying <paramref name="s"/>; returns its item once loaded.</summary>
    private static UUID Arrive(SchedulerHarness h, UUID asset, SerializedRuntimeState s)
    {
        var item = UUID.Random();
        Assert.True(h.Engine.SetXMLState(item, Envelope(item, asset, Encode(s))), "the envelope was not taken");
        h.RezScript(Counter, asset, item);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        return item;
    }

    private static SerializedStackFrame Frame(int address = 0, int args = 0, int locals = 0, int returnAddress = 0)
        => new SerializedStackFrame
        {
            FunctionInfo = new FunctionInfo { Name = "f", Address = address, NumberOfArguments = args, NumberOfLocals = locals },
            Locals = new SerializedLSLPrimitive[args + locals],
            ReturnAddress = returnAddress
        };

    private static SerializedLSLPrimitive Value(object v) => SerializedLSLPrimitive.FromPrimitive(v);

    private static LSLTable Nested(int depth)
    {
        var t = new LSLTable();
        for (int i = 0; i < depth; i++)
        {
            var outer = new LSLTable();
            outer.Set(1, t);
            t = outer;
        }
        return t;
    }

    public static IEnumerable<object[]> Misfits()
    {
        yield return Case("a state index the script does not have", s => s.LSLState = 7);
        yield return Case("one global too many", s => s.Globals = s.Globals.Append(Value(1)).ToArray());
        yield return Case("no globals", s => s.Globals = null);
        yield return Case("an unknown run state", s => s.RunState = (RuntimeState.Status)77);
        yield return Case("running with no call frame", s => { s.RunState = RuntimeState.Status.Running; s.Calls = null; });
        yield return Case("a waiting script with a call frame", s => { s.RunState = RuntimeState.Status.Waiting; s.Calls = new[] { Frame() }; });
        yield return Case("the execution position outside the code", s =>
        {
            s.RunState = RuntimeState.Status.Running; s.Calls = new[] { Frame() }; s.IP = 1_000_000;
        });
        yield return Case("a frame claiming two billion locals", s =>
        {
            s.RunState = RuntimeState.Status.Running;
            var f = Frame(); f.FunctionInfo.NumberOfLocals = int.MaxValue - 1; s.Calls = new[] { f };
        });
        yield return Case("a frame with fewer locals than its function", s =>
        {
            s.RunState = RuntimeState.Status.Running;
            var f = Frame(locals: 3); f.Locals = new[] { Value(0) }; s.Calls = new[] { f };
        });
        yield return Case("a return address outside the code", s =>
        {
            s.RunState = RuntimeState.Status.Running; s.Calls = new[] { Frame(returnAddress: 1_000_000) };
        });
        yield return Case("a function address outside the code", s =>
        {
            s.RunState = RuntimeState.Status.Running; s.Calls = new[] { Frame(address: -5) };
        });
        yield return Case("a queued event of an unknown type", s =>
            s.EventQueue = new[] { new SerializedPostedEvent { EventType = (SupportedEventList.Events)9999, Args = Array.Empty<SerializedLSLPrimitive>() } });
        yield return Case("a queued touch_start with no argument", s =>
            s.EventQueue = new[] { new SerializedPostedEvent { EventType = SupportedEventList.Events.TOUCH_START, Args = Array.Empty<SerializedLSLPrimitive>() } });
        yield return Case("a queued change to a state the script does not have", s =>
            s.EventQueue = new[] { new SerializedPostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<SerializedLSLPrimitive>(), TransitionToState = 4 } });
        yield return Case("a record of taken controls that is not three integers", s =>
            s.MiscAttributes = new Dictionary<int, SerializedLSLPrimitive[]> { [(int)RuntimeState.MiscAttr.Control] = new[] { Value("x") } });
        yield return Case("a sensor record of the wrong shape", s =>
            s.MiscAttributes = new Dictionary<int, SerializedLSLPrimitive[]> { [(int)RuntimeState.MiscAttr.SensorRepeat] = new[] { Value(1) } });
        yield return Case("a record no script writes", s =>
            s.MiscAttributes = new Dictionary<int, SerializedLSLPrimitive[]> { [99] = new[] { Value(1) } });
        yield return Case("a function value outside the code", s =>
            s.Globals[0] = Value(new LuaClosure(new FunctionInfo { Name = "g", Address = -1 }, Array.Empty<UpvalCell>())));
        yield return Case("values nested deeper than any script nests them", s => s.Globals[0] = Value(Nested(80)));
        yield return Case("memory above the script's limit", s => s.Globals[0] = Value(new string('x', 70_000)));
    }

    private static object[] Case(string what, Action<SerializedRuntimeState> spoil) => new object[] { what, spoil };

    /// <summary>
    /// A carried state that decodes but does not fit the script is refused: the script starts fresh (state_entry runs)
    /// and nothing escapes into the loader or the scheduler.
    /// </summary>
    [Theory]
    [MemberData(nameof(Misfits))]
    public void AStateThatDoesNotFitTheScriptGivesAFreshStart(string what, Action<SerializedRuntimeState> spoil)
    {
        var (state, asset) = Captured();
        spoil(state);
        using var h = new SchedulerHarness();
        var item = Arrive(h, asset, state);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), what + ": no fresh start " + SavedStateRig.SaidText(h));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=6")), what + ": " + SavedStateRig.SaidText(h));
        Assert.Equal(0, SavedStateRig.RejectedRows(item));
    }

    /// <summary>The same state, untouched, is taken: the counter carries on at n = 6 with no state_entry.</summary>
    [Fact]
    public void AStateThatFitsIsTakenAsItIs()
    {
        var (state, asset) = Captured();
        using var h = new SchedulerHarness();
        var item = Arrive(h, asset, state);
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=7")), SavedStateRig.SaidText(h));
        Assert.DoesNotContain("entry", h.Said);
    }

    /// <summary>
    /// A global of another type than the script declared cannot be told from the state alone: it is restored, and the
    /// first instruction that uses it stops that one script with its usual error. Other scripts carry on.
    /// </summary>
    [Fact]
    public void AGlobalOfTheWrongTypeStopsOnlyThatScript()
    {
        var (state, asset) = Captured();
        state.Globals[0] = Value("six");
        using var h = new SchedulerHarness();
        var other = UUID.Random();
        h.RezScript(Other, UUID.Random(), other);
        var item = Arrive(h, asset, state);
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("encountered a problem"))),
            SavedStateRig.SaidText(h));
        Assert.Equal("Killed", h.RunStateOf(item));
        h.PostTouch(other);
        Assert.True(h.PumpUntil(() => h.Said.Contains("other here")), SavedStateRig.SaidText(h));
    }

    /// <summary>
    /// Memory in use is recomputed from the restored values: a state claiming any figure, or none, restores with the same
    /// memory in use as the honest state.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MemoryInUseIsRecomputedNotTakenFromTheState(bool absent)
    {
        var (state, asset) = Captured();
        int honest;
        using (var h1 = new SchedulerHarness())
            honest = ((Interpreter)h1.InterpreterFor(Arrive(h1, asset, state))).ScriptState.MemInfo.MemoryUsed;
        Assert.InRange(honest, 1, MemoryInfo.MAX_MEMORY);

        state.MemInfo = absent ? null : new MemoryInfo { MemoryUsed = 1_000_000 };
        using var h = new SchedulerHarness();
        var item = Arrive(h, asset, state);
        Assert.Equal(honest, ((Interpreter)h.InterpreterFor(item)).ScriptState.MemInfo.MemoryUsed);
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=7")), SavedStateRig.SaidText(h));
    }

    /// <summary>The queue keeps what a running script's queue would have let in: 64 events, the rest dropped.</summary>
    [Fact]
    public void TheQueueIsHeldToARunningScriptsLimit()
    {
        var (state, asset) = Captured();
        state.EventQueue = Enumerable.Range(0, 200).Select(_ => new SerializedPostedEvent
        {
            EventType = SupportedEventList.Events.TOUCH_START,
            Args = new[] { Value(1) },
            TransitionToState = PostedEvent.NO_TRANSITION
        }).ToArray();
        using var h = new SchedulerHarness();
        Arrive(h, asset, state);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=" + (6 + RuntimeState.MAX_EVENT_QUEUE_SIZE))), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(RuntimeState.MAX_EVENT_QUEUE_SIZE, h.Said.Count(s => s.StartsWith("n=")));
    }

    /// <summary>The timer is held as llSetTimerEvent holds it: never below 0, never below the region's floor.</summary>
    [Theory]
    [InlineData(-5000, 0)]
    [InlineData(1, 100)]
    [InlineData(250, 250)]
    public void TheTimerIsHeldToTheRegionsFloor(int saved, int restored)
    {
        var (state, asset) = Captured();
        state.TimerInterval = saved;
        using var h = new SchedulerHarness();
        var item = Arrive(h, asset, state);
        Assert.Equal(restored, ((Interpreter)h.InterpreterFor(item)).ScriptState.TimerInterval);
    }

    /// <summary>An envelope longer than the largest state an object carries is refused before anything is decoded.</summary>
    [Fact]
    public void AnEnvelopeAboveTheLimitIsRefusedBeforeDecoding()
    {
        var (state, asset) = Captured();
        state.Globals[0] = Value(new string('x', 9_000_000));
        using var h = new SchedulerHarness();
        var item = UUID.Random();
        Assert.False(h.Engine.SetXMLState(item, Envelope(item, asset, Encode(state))));
    }

    /// <summary>
    /// Values nested past the decoder's depth limit (protobuf-net's TypeModel.MaxDepth, 512) are refused at the envelope,
    /// and nothing overflows. The writer will not produce such a state, so the bytes are put together by hand: one more
    /// global (field 3) holding a table (field 10) whose metatable (field 3) holds a metatable, 2000 deep.
    /// </summary>
    [Fact]
    public void ValuesNestedPastTheDecodersLimitAreRefused()
    {
        var (state, asset) = Captured();
        byte[] table = Array.Empty<byte>();
        for (int i = 0; i < 2000; i++) table = Field(3, table);
        byte[] blob = Encode(state).Concat(Field(3, Field(10, table))).ToArray();
        using var h = new SchedulerHarness();
        var item = UUID.Random();
        Assert.False(h.Engine.SetXMLState(item, Envelope(item, asset, blob)));
    }

    /// <summary>A length-delimited protobuf field.</summary>
    private static byte[] Field(int number, byte[] body)
    {
        var bytes = new List<byte> { (byte)((number << 3) | 2) };
        uint n = (uint)body.Length;
        while (n >= 0x80) { bytes.Add((byte)(n | 0x80)); n >>= 7; }
        bytes.Add((byte)n);
        bytes.AddRange(body);
        return bytes.ToArray();
    }
}
