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
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.SLua;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The counts a restored call frame carries for a value call (how many results the caller wants, and how deep the
/// operand stack was when the call began) drive how many values its return pushes or pops. A carried state's frames
/// must hold what the compiled code gives them, and the interpreter holds every script's operand stack to a limit, so a
/// count that slips through stops that one script with its out-of-memory error instead of pushing billions of values.
/// </summary>
// Runs in parallel: each test compiles and drives its own interpreter; nothing process-wide is touched.
public class RestoredFrameFitTests
{
    /// <summary>A closure called for two results that sleeps inside, so a capture holds a value-call frame.</summary>
    private const string ValueCall = @"--!slua
function run()
    local g = function() ll.Sleep(1) return 1 end
    local a, b = g()
    ll.OwnerSay(tostring(a) .. "","" .. tostring(b))
end
run()
";

    private const int OperandLimit = MemoryInfo.MAX_MEMORY / 4;

    private static CompiledScript CompileSLua(string src)
    {
        var listener = new PhloxCompiler();
        string asm = SLuaCompiler.CompileToAssembly(src, listener);
        Assert.True(asm != null && !listener.HasErrors(), listener.Report);
        return new CompilerFrontend(new PhloxCompiler(), ".").AssembleText(asm);
    }

    private static SerializedRuntimeState Serialize(RuntimeState st)
    {
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, SerializedRuntimeState.FromRuntimeState(st));
        ms.Position = 0;
        return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
    }

    /// <summary>The script parked in the sleep inside the value call, and its state as a carried copy would decode.</summary>
    private static (CompiledScript Script, SerializedRuntimeState State) Parked()
    {
        var script = CompileSLua(ValueCall);
        var s = ExprRunner.Session.Start(script);
        Assert.True(s.Result.RuntimeError == null, s.Result.Describe());
        Assert.Equal(RuntimeState.Status.Sleeping, s.State.RunState);
        Assert.Contains(s.State.Calls, f => f.Wanted == 2);
        return (script, Serialize(s.State));
    }

    private static SerializedStackFrame ValueFrame(SerializedRuntimeState s) => s.Calls.Single(f => f.Wanted >= 0);
    private static SerializedStackFrame NamedFrame(SerializedRuntimeState s) => s.Calls.First(f => f.Wanted < 0);

    private static string Fit(SerializedRuntimeState s, CompiledScript script)
        => RestoredStateFit.CheckSerialized(s) ?? RestoredStateFit.Check(s.ToRuntimeState(), script);

    [Fact]
    public void AnHonestValueCallFrameFits()
    {
        var (script, state) = Parked();
        Assert.Null(Fit(state, script));
    }

    public static IEnumerable<object[]> Misfits()
    {
        yield return Case("a value call wanting two billion results", s => ValueFrame(s).Wanted = int.MaxValue);
        yield return Case("a value call wanting other than its call site asks for", s => ValueFrame(s).Wanted = 3);
        yield return Case("a wanted count below -1", s => ValueFrame(s).Wanted = -7);
        yield return Case("a named call wanting results", s => NamedFrame(s).Wanted = 5);
        yield return Case("an operand base far above the stack", s => ValueFrame(s).OperandBase = int.MaxValue);
        yield return Case("an operand base below zero", s => ValueFrame(s).OperandBase = -1);
        yield return Case("an operand base on a named call", s => NamedFrame(s).OperandBase = 3);
        yield return Case("a frame's closure whose function lies outside the code", s =>
            ValueFrame(s).Closure.Fn = new FunctionInfo { Name = "g", Address = 1_000_000 });
        yield return Case("a string.gmatch position far outside its string", s =>
            s.Operands = Operands(s).Append(SerializedLSLPrimitive.FromPrimitive(new LuaGmatch("abc", "%a") { Pos = int.MinValue })).ToArray());
        yield return Case("more values on the stack than any script holds", s =>
            s.Operands = Operands(s).Concat(Enumerable.Range(0, OperandLimit + 1).Select(i => SerializedLSLPrimitive.FromPrimitive(i))).ToArray());
    }

    private static SerializedLSLPrimitive[] Operands(SerializedRuntimeState s) => s.Operands ?? Array.Empty<SerializedLSLPrimitive>();

    private static object[] Case(string what, Action<SerializedRuntimeState> spoil) => new object[] { what, spoil };

    [Theory]
    [MemberData(nameof(Misfits))]
    public void AFrameCountTheCodeCannotProduceIsRefused(string what, Action<SerializedRuntimeState> spoil)
    {
        var (script, state) = Parked();
        spoil(state);
        Assert.True(Fit(state, script) != null, what + " was taken");
    }

    /// <summary>
    /// The interpreter's own limit, whatever the fit check lets through: a return whose frame wants a million results
    /// stops the script with its out-of-memory error once the operand stack is full, instead of pushing them all.
    /// </summary>
    [Fact]
    public void AReturnWantingAMillionResultsStopsAtTheStackLimit()
    {
        var (script, state) = Parked();
        ValueFrame(state).Wanted = 1_000_000;
        var s = ExprRunner.Session.Attach(script, state.ToRuntimeState());
        s.Wake();
        Assert.True(s.Result.RuntimeError != null && s.Result.RuntimeError.Contains("Out of memory"),
            $"{s.Result.Describe()} with {s.State.Operands.Count} values left on the stack");
        Assert.InRange(s.State.Operands.Count, 0, OperandLimit);
    }

    /// <summary>The same parked script, untouched, carries on and prints the two results its call site asked for.</summary>
    [Fact]
    public void AnHonestValueCallResumesWithTheResultsItWanted()
    {
        var (script, state) = Parked();
        var s = ExprRunner.Session.Attach(script, state.ToRuntimeState());
        s.Wake();
        Assert.True(s.Result.Ok, s.Result.Describe());
        Assert.Equal(new[] { "1,nil" }, s.Result.Said);
    }

    /// <summary>
    /// A script may still push many values at once below the limit: table.unpack of a 10000-entry table pushes all
    /// 10000 before the assignment keeps the last two.
    /// </summary>
    [Fact]
    public void ManyValuesBelowTheLimitStillPush()
    {
        var r = ExprRunner.RunSLua(@"
    local t = {}
    for i = 1, 10000 do t[i] = i end
    local a, b = table.unpack(t)
    ll.OwnerSay(tostring(a) .. "","" .. tostring(b))");
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "1,2" }, r.Said);
    }
}
