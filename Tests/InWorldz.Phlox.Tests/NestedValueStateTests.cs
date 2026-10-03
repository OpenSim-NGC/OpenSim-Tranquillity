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
/// Saving a script's state walks its tables. A table reached again while it is being written, as a key, a value or a
/// metatable, ends the walk there, and nesting is bounded both ways, so no script's values can make the walk overflow the
/// thread's stack. Each script here parks in ll.Sleep with the tables in its locals, is captured as the state manager
/// captures it, restored, and woken.
/// </summary>
// Runs in parallel: each test compiles and drives its own interpreter; nothing process-wide is touched.
public class NestedValueStateTests
{
    private static CompiledScript CompileSLua(string body)
    {
        string src = "--!slua\nfunction run()\n" + body + "\nend\nrun()\n";
        var listener = new PhloxCompiler();
        string asm = SLuaCompiler.CompileToAssembly(src, listener);
        Assert.True(asm != null && !listener.HasErrors(), listener.Report);
        return new CompilerFrontend(new PhloxCompiler(), ".").AssembleText(asm);
    }

    /// <summary>Runs <paramref name="body"/> up to its ll.Sleep.</summary>
    private static ExprRunner.Session Parked(string body)
    {
        var s = ExprRunner.Session.Start(CompileSLua(body));
        Assert.True(s.Result.RuntimeError == null, s.Result.Describe());
        Assert.Equal(RuntimeState.Status.Sleeping, s.State.RunState);
        return s;
    }

    /// <summary>What <c>StateManager</c> writes for the script.</summary>
    private static byte[] Capture(RuntimeState st)
    {
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, SerializedRuntimeState.FromRuntimeState(st));
        return ms.ToArray();
    }

    private static RuntimeState Restore(byte[] blob)
    {
        using var ms = new MemoryStream(blob);
        return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms).ToRuntimeState();
    }

    /// <summary>Captures the parked script, restores the capture, wakes it and returns what it said after the sleep.</summary>
    private static ExprRunner.Result RoundTrip(ExprRunner.Session parked)
    {
        var s = ExprRunner.Session.Attach(parked.Script, Restore(Capture(parked.State)));
        s.Wake();
        return s.Result;
    }

    [Fact]
    public void ATableThatIsItsOwnKeyRoundTrips()
    {
        var r = RoundTrip(Parked(@"
    local t = {}
    t[t] = ""self""
    t.x = 1
    ll.Sleep(1)
    ll.OwnerSay(tostring(t[t]) .. "","" .. tostring(t.x))"));
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "self,1" }, r.Said);
    }

    /// <summary>
    /// Two tables that are each other's keys: each is written as its own tree, and the key that refers back to the table
    /// being written drops its entry, as such a value drops to nil. Both restore with their other entries.
    /// </summary>
    [Fact]
    public void TwoTablesThatAreEachOthersKeysRoundTrip()
    {
        var r = RoundTrip(Parked(@"
    local a, b = {}, {}
    a[b] = 1
    b[a] = 2
    a.name = ""a""
    b.name = ""b""
    ll.Sleep(1)
    local na, nb = 0, 0
    for k, v in pairs(a) do na = na + 1 end
    for k, v in pairs(b) do nb = nb + 1 end
    ll.OwnerSay(a.name .. b.name .. "","" .. tostring(na) .. "","" .. tostring(nb))"));
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "ab,2,2" }, r.Said);
    }

    /// <summary>A table whose metatable's key is the table: the walk ends there too.</summary>
    [Fact]
    public void AMetatableKeyedByItsTableRoundTrips()
    {
        var r = RoundTrip(Parked(@"
    local t = {}
    local mt = {}
    mt[t] = true
    mt.__index = mt
    mt.greet = ""hi""
    t = setmetatable(t, mt)
    ll.Sleep(1)
    ll.OwnerSay(t.greet)"));
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "hi" }, r.Said);
    }

    private static string Chain(int depth) => $@"
    local t = {{}}
    local cur = t
    for i = 2, {depth} do local n = {{}} cur[1] = n cur = n end
    ll.Sleep(1)
    local d = 0
    local c = t
    while c ~= nil do d = d + 1 c = c[1] end
    ll.OwnerSay(tostring(d))";

    [Fact]
    public void TablesNestedToTheBoundRoundTrip()
    {
        var r = RoundTrip(Parked(Chain(SerializedLSLTable.MaxNesting)));
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { SerializedLSLTable.MaxNesting.ToString() }, r.Said);
    }

    /// <summary>
    /// One level past the bound the capture fails with a serialization error, which every capture path catches and logs:
    /// the state is not saved and the script runs on, unaffected.
    /// </summary>
    [Fact]
    public void TablesNestedPastTheBoundAreNotCapturedAndTheScriptRunsOn()
    {
        var parked = Parked(Chain(SerializedLSLTable.MaxNesting + 1));
        var e = Assert.Throws<SerializationException>(() => Capture(parked.State));
        Assert.Contains(SerializedLSLTable.MaxNesting.ToString(), e.Message);
        parked.Wake();
        Assert.True(parked.Result.Ok, parked.Result.Describe());
        Assert.Equal(new[] { (SerializedLSLTable.MaxNesting + 1).ToString() }, parked.Result.Said);
    }

    /// <summary>A table nested past the bound, put together by hand, decodes but does not restore.</summary>
    [Fact]
    public void ASavedStateNestedPastTheBoundDoesNotRestore()
    {
        var parked = Parked(Chain(2));
        var state = SerializedRuntimeState.FromRuntimeState(parked.State);
        var table = new SerializedLSLTable { Keys = new(), Values = new() };
        for (int i = 1; i < SerializedLSLTable.MaxNesting + 1; i++)
            table = new SerializedLSLTable
            {
                Keys = new List<SerializedLSLPrimitive> { SerializedLSLPrimitive.FromPrimitive(1) },
                Values = new List<SerializedLSLPrimitive> { new SerializedLSLPrimitive { Value = table } }
            };
        state.Globals = new[] { new SerializedLSLPrimitive { Value = table } };
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, state);
        Assert.Throws<SerializationException>(() => Restore(ms.ToArray()));
    }

    /// <summary>The same table one level shallower restores.</summary>
    [Fact]
    public void ASavedStateNestedToTheBoundRestores()
    {
        var parked = Parked(Chain(2));
        var state = SerializedRuntimeState.FromRuntimeState(parked.State);
        var table = new SerializedLSLTable { Keys = new(), Values = new() };
        for (int i = 1; i < SerializedLSLTable.MaxNesting; i++)
            table = new SerializedLSLTable
            {
                Keys = new List<SerializedLSLPrimitive> { SerializedLSLPrimitive.FromPrimitive(1) },
                Values = new List<SerializedLSLPrimitive> { new SerializedLSLPrimitive { Value = table } }
            };
        state.Globals = new[] { new SerializedLSLPrimitive { Value = table } };
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, state);
        var restored = Restore(ms.ToArray());
        int depth = 0;
        for (object c = restored.Globals[0]; c is LSLTable t; c = t.Get(1)) depth++;
        Assert.Equal(SerializedLSLTable.MaxNesting, depth);
    }
}
