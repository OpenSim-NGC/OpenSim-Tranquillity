/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Linq;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A running script's memory counts its globals and its call frames' locals. Values waiting on its operand stack and the
/// arguments of events still in its queue are not counted, so a carried state could hold any amount there. Those are held
/// to the script's memory limit on their own: a carried state holding more is refused and the script starts fresh, and a
/// state at the limit is still taken.
/// </summary>
// Runs in parallel: each test has its own harness, items and assets; nothing process-wide is changed.
public class CarriedValueSizeTests
{
    private const string Holder = @"
        string s;
        integer n;
        default {
            state_entry() { n = 5; llSay(0, ""entry""); }
            touch_start(integer t) { n++; llSay(0, ""n="" + (string)n); }
        }";

    private static (SerializedRuntimeState State, UUID Asset) Captured()
    {
        using var h = new SchedulerHarness();
        var asset = UUID.Random();
        var item = UUID.Random();
        h.RezScript(Holder, asset, item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        Assert.True(h.PumpUntil(() => h.RunStateOf(item) == "Waiting"), "state_entry did not finish");
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(h.Engine.GetXMLState(item));
        using var ms = new MemoryStream(Convert.FromBase64String(doc.DocumentElement!["ScriptState"]!.InnerText));
        return (ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms), asset);
    }

    private static UUID Arrive(SchedulerHarness h, UUID asset, SerializedRuntimeState s)
    {
        var item = UUID.Random();
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, s);
        string envelope = $"<State Engine=\"InWorldz.Phlox\" UUID=\"{item}\" Asset=\"{asset}\" Version=\"1\"><ScriptState>{Convert.ToBase64String(ms.ToArray())}</ScriptState></State>";
        Assert.True(h.Engine.SetXMLState(item, envelope), "the envelope was not taken");
        h.RezScript(Holder, asset, item);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        return item;
    }

    private static SerializedLSLPrimitive Value(object v) => SerializedLSLPrimitive.FromPrimitive(v);

    /// <summary>Memory in use of the honest state as restored: the script's base memory, its two globals.</summary>
    private static int HonestMemory(SerializedRuntimeState state, UUID asset)
    {
        using var h = new SchedulerHarness();
        return ((Interpreter)h.InterpreterFor(Arrive(h, asset, state))).ScriptState.MemInfo.MemoryUsed;
    }

    private static void AssertFreshStart(SchedulerHarness h, UUID item, string what)
    {
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), what + ": no fresh start " + SavedStateRig.SaidText(h));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=6")), what + ": " + SavedStateRig.SaidText(h));
    }

    private static void AssertTaken(SchedulerHarness h, UUID item, string what)
    {
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("n=6")), what + ": " + SavedStateRig.SaidText(h));
        Assert.DoesNotContain("entry", h.Said);
    }

    [Fact]
    public void AMillionCharacterStringOnTheStackIsRefused()
    {
        var (state, asset) = Captured();
        state.Operands = new[] { Value(new string('x', 1_000_000)) };
        using var h = new SchedulerHarness();
        AssertFreshStart(h, Arrive(h, asset, state), "a 1,000,000-character operand");
    }

    [Fact]
    public void AQueuedEventCarryingAMillionCharacterStringIsRefused()
    {
        var (state, asset) = Captured();
        state.EventQueue = new[]
        {
            new SerializedPostedEvent
            {
                EventType = SupportedEventList.Events.TOUCH_START,
                Args = new[] { Value(new string('x', 1_000_000)) },
                TransitionToState = PostedEvent.NO_TRANSITION
            }
        };
        using var h = new SchedulerHarness();
        AssertFreshStart(h, Arrive(h, asset, state), "a queued event with a 1,000,000-character argument");
    }

    /// <summary>
    /// The honest state with its string global grown until memory in use is exactly the limit, and an operand of ordinary
    /// size: restored as it is (no state_entry, memory in use at the limit). It has no memory left to run a handler in, as
    /// it had none where it was captured.
    /// </summary>
    [Fact]
    public void AStateAtTheMemoryLimitIsStillTaken()
    {
        var (state, asset) = Captured();
        int honest = HonestMemory(state, asset);
        int spare = MemoryInfo.MAX_MEMORY - honest;
        Assert.True(spare > 0 && spare % 2 == 0, $"memory in use {honest}");
        Assert.Equal("", state.Globals[0].Value);
        state.Globals[0] = Value(new string('x', spare / 2));       // 2 bytes a character on top of what "" took
        state.Operands = new[] { Value(new string('y', 1000)) };
        using var h = new SchedulerHarness();
        var item = Arrive(h, asset, state);
        var restored = ((Interpreter)h.InterpreterFor(item)).ScriptState;
        Assert.Equal(MemoryInfo.MAX_MEMORY, restored.MemInfo.MemoryUsed);
        Assert.Equal(spare / 2, ((string)restored.Globals[0]).Length);
        Assert.DoesNotContain("entry", h.Said);
    }

    /// <summary>An operand that alone takes the script's whole memory limit is still taken; a byte more is not.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void TheStackIsHeldToTheMemoryLimitOnItsOwn(int charactersOver, bool taken)
    {
        var (state, asset) = Captured();
        int chars = (MemoryInfo.MAX_MEMORY - 4) / 2 + charactersOver;     // a string takes 4 + 2 per character
        state.Operands = new[] { Value(new string('x', chars)) };
        using var h = new SchedulerHarness();
        var item = Arrive(h, asset, state);
        if (taken) AssertTaken(h, item, $"an operand of {chars} characters");
        else AssertFreshStart(h, item, $"an operand of {chars} characters");
    }
}
