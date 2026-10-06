/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.VM;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// pcall, metamethods and library callbacks run their closure inside the calling instruction, nesting the interpreter on
/// the thread's stack. That nesting is bounded: past <see cref="Interpreter.MaxSyncCallDepth"/> the call raises the
/// script error "C stack overflow", which pcall catches and which otherwise stops only that script.
/// </summary>
// Runs in parallel: each test compiles and drives its own interpreter; nothing process-wide is touched.
public class NestedClosureCallTests
{
    /// <summary>A function that pcalls itself forever: the innermost pcall gets the error, and every level returns.</summary>
    [Fact]
    public void RecursionThroughPcallStopsAtTheLimitAndUnwinds()
    {
        var r = ExprRunner.RunSLua(@"
    local depth = 0
    local last = nil
    local function f()
        depth = depth + 1
        local ok, err = pcall(f)
        if not ok then last = err end
    end
    f()
    ll.OwnerSay(tostring(depth) .. "":"" .. tostring(last))");
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { (Interpreter.MaxSyncCallDepth + 1) + ":C stack overflow" }, r.Said);
    }

    /// <summary>An __index function that indexes its own table: no pcall, so the script stops with the error.</summary>
    [Fact]
    public void RecursionThroughAMetamethodStopsTheScript()
    {
        var r = ExprRunner.RunSLua(@"
    local t = setmetatable({}, { __index = function(tbl, k) return tbl[k] end })
    local x = t.foo
    ll.OwnerSay(""not reached"")");
        Assert.True(r.RuntimeError != null && r.RuntimeError.Contains("C stack overflow"), r.Describe());
        Assert.Empty(r.Said);
    }

    /// <summary>Nesting below the limit is untouched: pcall inside pcall, 50 deep.</summary>
    [Fact]
    public void NestingBelowTheLimitRuns()
    {
        var r = ExprRunner.RunSLua(@"
    local function f(n)
        if n == 0 then return ""bottom"" end
        local ok, v = pcall(f, n - 1)
        return v
    end
    ll.OwnerSay(f(50))");
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(new[] { "bottom" }, r.Said);
    }
}
