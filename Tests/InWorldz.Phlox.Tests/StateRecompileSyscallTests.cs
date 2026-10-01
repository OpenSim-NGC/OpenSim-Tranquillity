using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A state saved parked in a syscall, restored onto recompiled bytecode, must come back
/// idle with no syscall to resume. LastSyscallIndex is what the scheduler's Syscall arm
/// resumes from; left set, it names a call the dropped event will never return from.
/// </summary>
public class StateRecompileSyscallTests
{
    private const string Script =
        "integer g;\n" +
        "default\n{\n" +
        "    state_entry()\n    {\n        g = 41;\n        llSleep(30.0);\n        g = g + 1;\n    }\n}\n";

    [Fact]
    public void ChangedBytecode_ScriptParkedInASyscall_ComesBackIdleWithNoSyscallToResume()
    {
        var s = ExprRunner.Session.Start(ExprRunner.CompileLsl(Script));
        s.State.RunState = RuntimeState.Status.Syscall;   // as the shim leaves an off-thread call
        s.State.LastSyscallIndex = 5;
        var ser = SerializedRuntimeState.FromRuntimeState(s.State);
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, ser);
        ms.Position = 0;
        ser = ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);

        var recompiled = ExprRunner.CompileLsl(Script.Replace("g = 41;", "g = 41; g = g;"));
        var restored = ser.ToRuntimeStateFor(recompiled, UUID.Random(), out string note);

        Assert.True(restored.RunState == RuntimeState.Status.Waiting && restored.LastSyscallIndex == -1 && note != null,
            $"restored run={restored.RunState} lastSyscall={restored.LastSyscallIndex} note={(note == null ? "none" : "logged")} " +
            "(expect run=Waiting lastSyscall=-1 note=logged)");
        Assert.Equal(41, restored.Globals[0]);
    }
}
