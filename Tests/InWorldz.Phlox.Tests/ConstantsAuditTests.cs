using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Phlox's constant table against upstream <c>LSL_Constants.cs</c> and the SL wiki, by name and
/// by value (<c>Docs/audit/phlox-constants-audit.py</c>). Two things are pinned here:
/// <list type="bullet">
/// <item>every name upstream declares that Phlox lacked now compiles — <c>Fixtures/phlox8-upstream-names.lsl</c>
/// references each one once; before the batch none of them did;</item>
/// <item>the four same-name-different-value hits, each settled against its wiki page: TOUCH_INVALID_FACE
/// was wrong here (0x7FFFFFFF; the wiki says 0xFFFFFFFF, which is -1 as a 32-bit integer), EOF and
/// TOUCH_INVALID_VECTOR only looked wrong (the assembler unescapes the table's <c>\n</c>; upstream aliases
/// ZERO_VECTOR), and JSON_APPEND is right here and stored as a string upstream (the wiki says integer).</item>
/// </list>
/// The value probes run the compiled script on a hand-driven interpreter with a recording API, so the
/// value asserted is the one a script sees at runtime, not the table text.
/// </summary>
public class ConstantsAuditTests
{
    private static string FixturePath(string name) => Path.Combine(
        Path.GetDirectoryName(typeof(ConstantsAuditTests).Assembly.Location)!, "Fixtures", name);

    [Fact]
    public void EveryUpstreamNamePhloxLackedNowCompiles()
    {
        var src = File.ReadAllText(FixturePath("phlox8-upstream-names.lsl"));
        var compiled = PhloxCompiler.CompileTo(src, out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        Assert.NotNull(compiled);
    }

    // https://wiki.secondlife.com/wiki/TOUCH_INVALID_FACE   integer TOUCH_INVALID_FACE = 0xFFFFFFFF  (= -1)
    // https://wiki.secondlife.com/wiki/JSON_APPEND          integer JSON_APPEND = -1
    // https://wiki.secondlife.com/wiki/TOUCH_INVALID_VECTOR vector TOUCH_INVALID_VECTOR = <0.0, 0.0, 0.0>
    // https://wiki.secondlife.com/wiki/EOF                  string EOF = "\n\n\n"  (three 0x0a)
    // NAK is one of the added names and the only one with a control character in it: upstream "\n\n".
    [Theory]
    [InlineData("(string)TOUCH_INVALID_FACE", "-1")]
    [InlineData("(string)JSON_APPEND", "-1")]
    [InlineData("(string)(TOUCH_INVALID_VECTOR == ZERO_VECTOR)", "1")]
    [InlineData("EOF", "\n\n\n")]
    [InlineData("NAK", "\n\n")]
    public void ConstantValueAtRuntimeMatchesTheWiki(string expr, string expected)
    {
        var calls = Run("llSay(0, " + expr + ");");
        var say = calls.FirstOrDefault(c => c.StartsWith("llSay("));
        Assert.True(say != null, $"no llSay reached the API; all syscalls: [{string.Join(" | ", calls)}]");
        Assert.Equal("llSay(0, " + expected + ")", say);
    }

    /// <summary>Same shape as BuiltinOverloadTests.Run: compile, run state_entry by hand, return the API calls.</summary>
    private static List<string> Run(string body)
    {
        var compiled = PhloxCompiler.CompileTo(
            "default { state_entry() { " + body + " } }", out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        Assert.NotNull(compiled);
        var api = RecordingSystemApi.Create(out var calls);
        var shim = new InWorldz.Phlox.Glue.SyscallShim(call => call());
        shim.SystemAPI = api;
        var interp = new InWorldz.Phlox.VM.Interpreter(compiled, shim);
        shim.Interpreter = interp;
        var info = compiled.FindEvent(interp.ScriptState.LSLState,
            (int)InWorldz.Phlox.Types.SupportedEventList.Events.STATE_ENTRY);
        Assert.NotNull(info);
        interp.ScriptState.DoEvent(info,
            new InWorldz.Phlox.VM.PostedEvent { EventType = InWorldz.Phlox.Types.SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() },
            Array.Empty<object>());
        try { for (var i = 0; i < 100_000 && interp.ScriptState.RunningEvent != null; i++) interp.Tick(); }
        catch (InvalidOperationException) { /* ticked past the end of the event */ }
        return calls;
    }
}
