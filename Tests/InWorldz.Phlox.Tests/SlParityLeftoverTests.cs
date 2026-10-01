using System;
using System.Linq;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Four small SL-parity leftovers, each pinned against the SL wiki:
/// <list type="bullet">
/// <item>a script run-time error is chatted on DEBUG_CHANNEL (0x7FFFFFFF) - the channel viewers route
/// to the owner's script-error window - and not shouted on channel 0 where every avatar in range read it
/// (https://wiki.secondlife.com/wiki/DEBUG_CHANNEL);</item>
/// <item><c>quaternion</c> is a type name interchangeable with <c>rotation</c>
/// (https://wiki.secondlife.com/wiki/Quaternion);</item>
/// <item><c>&lt;&lt;=</c> and <c>&gt;&gt;=</c> are not LSL (https://wiki.secondlife.com/wiki/LSL_Operators
/// lists no shift-assign) - YEngine's acceptance was an extension Phlox had copied. The rule is
/// "Accept &lt;&lt;= and &gt;&gt;= as an extension", so they compile again for integers (Halcyon's rule) and stay an
/// error on their line for any other type (ShiftAssignTests);</item>
/// <item><c>list != list</c> is the length difference, <c>==</c> is TRUE when the lengths match
/// ("Equality test on lists does not compare contents, only the length", same page).</item>
/// </list>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SlParityLeftoverTests
{
    private const int DebugChannel = 0x7FFFFFFF;

    // ---- 1. run-time errors go to DEBUG_CHANNEL ------------------------------------------------

    [Fact]
    public void RuntimeErrorIsChattedOnDebugChannelAndNotOnZero()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer z = 0; llSay(0, (string)(1 / z)); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel));

        var said = h.SaidOn;
        var onDebug = said.Where(s => s.Channel == DebugChannel).ToList();
        var onZero = said.Where(s => s.Channel == 0).ToList();
        Assert.True(onDebug.Count == 1, $"expected one DEBUG_CHANNEL line, got {onDebug.Count}; all chat: [{string.Join(" | ", said.Select(s => $"{s.Channel}:{s.Message}"))}]");
        Assert.Contains("Script error", onDebug[0].Message);
        Assert.True(onZero.Count == 0, $"expected nothing on channel 0, got: [{string.Join(" | ", onZero.Select(s => s.Message))}]");
    }

    // ---- 2. quaternion is rotation ---------------------------------------------------------------

    [Fact]
    public void QuaternionDeclaresARotation()
    {
        var src = "quaternion g = <0, 0, 0, 1>;\n" +
                  "default { state_entry() { quaternion q = <0, 0, 0, 1>; llSetRot(q); llSetRot(g); rotation r = q; q = (quaternion)\"<0,0,0,1>\"; } }";
        var compiled = PhloxCompiler.CompileTo(src, out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        Assert.NotNull(compiled);
        // Runtime on the real path (scheduler + LSLSystemAPI): a script with a local store NREs in
        // Op_Store on the bare hand-driven interpreter (MemInfo.ReplaceStored over a never-initialised
        // local) - a harness limitation, not the engine's; noted in PhloxKnownDefects.
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { quaternion q = <0, 0, 0, 1>; llSay(0, \"q=\" + (string)(q == ZERO_ROTATION)); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("q=")));
        Assert.Contains("q=1", h.Said);
    }

    // ---- 3. <<= and >>= are not SL; an integer-only extension -------------------

    [Theory]
    [InlineData("<<=")]
    [InlineData(">>=")]
    public void ShiftAssignIsAnIntegerExtensionAndOtherwiseAnErrorOnItsLine(string op)
    {
        var src = "default\n{\n    state_entry()\n    {\n        integer x = 1;\n        x " + op + " 1;\n    }\n}\n";
        PhloxCompiler.CompileTo(src, out var listener);
        Assert.False(listener.HasErrors(), "an integer shift-assign did not compile: " + listener.Report);

        var fsrc = "default\n{\n    state_entry()\n    {\n        float x = 1.0;\n        x " + op + " 1;\n    }\n}\n";
        PhloxCompiler.CompileTo(fsrc, out var flistener);
        Assert.True(flistener.HasErrors(), "a float shift-assign compiled: " + flistener.Report);
        Assert.Contains(flistener.Errors, e => e.Contains("line 6:"));
    }

    [Fact]
    public void ExplicitShiftStillCompiles()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer x = 1; x = x << 3; x = x >> 1; llSay(0, \"x=\" + (string)x); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("x=")));
        Assert.Contains("x=4", h.Said);
    }

    // ---- 4. list != list is the length difference -------------------------------------------------

    [Theory]
    [InlineData("[1, 2, 3] != [1]", "2")]
    [InlineData("[1] != [1, 2, 3]", "-2")]
    [InlineData("[1] != [2]", "0")]
    [InlineData("[1, 2] == [3, 4]", "1")]
    [InlineData("[1] == [1, 2]", "0")]
    public void ListComparisonIsAboutLength(string expr, string expected)
    {
        var say = HandDrivenRun.FirstSay("llSay(0, (string)(" + expr + "));");
        Assert.Equal("llSay(0, " + expected + ")", say);
    }
}
