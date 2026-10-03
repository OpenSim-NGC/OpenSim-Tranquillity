using System;
using System.Collections.Generic;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Compile a one-event script, run its state_entry on a bare interpreter with the recording
/// ISystemAPI, and hand back every API call that reached it. No scene, no scheduler, no clock -
/// the value a script computes is observed as the argument it passes to llSay. Same shape as the
/// private helper in BuiltinOverloadTests and ConstantsAuditTests, shared.
/// </summary>
internal static class HandDrivenRun
{
    public static List<string> StateEntry(string body)
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

    /// <summary>The first llSay that reached the API, as "llSay(channel, text)".</summary>
    public static string FirstSay(string body)
    {
        var calls = StateEntry(body);
        var say = calls.Find(c => c.StartsWith("llSay("));
        Assert.True(say != null, $"no llSay reached the API; all syscalls: [{string.Join(" | ", calls)}]");
        return say!;
    }
}
