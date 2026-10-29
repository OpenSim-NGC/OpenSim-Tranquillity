using System.Reflection;
using OpenMetaverse;
using ProtoBuf;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script saved mid-syscall.
///
/// <para>
/// This is the state with no way home. A sleeping script has a wake-up time and a running one has a
/// frame to continue from, but a syscall in flight when the region stopped has <b>no completion
/// coming</b> — whatever was going to call <c>SysReturn</c> died with the old process. The only way
/// back is to supply the return value ourselves, and that means knowing which function it was, which
/// is what <c>LastSyscallIndex</c> is now persisted for (tag 22).
/// </para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SyscallResumeTests
{
    private readonly ITestOutputHelper _out;
    public SyscallResumeTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// A state row exactly as the code before tag 22 wrote one: the three required members and nothing
    /// else. Deserialising it as the current type is what every existing row on a running grid will do
    /// on the next start, so it had better not come back claiming syscall index 0 — which is a real
    /// function.
    /// </summary>
    [ProtoContract]
    private class PreTag22RuntimeState
    {
        [ProtoMember(1, IsRequired = true)] public int IP;
        [ProtoMember(2, IsRequired = true)] public int LSLState;
        [ProtoMember(9, IsRequired = true)] public InWorldz.Phlox.VM.RuntimeState.Status RunState;
    }

    [Fact]
    public void ARowWrittenBeforeTagTwentyTwoLoadsWithMinusOne()
    {
        var old = new PreTag22RuntimeState
        {
            IP = 42,
            LSLState = 0,
            RunState = InWorldz.Phlox.VM.RuntimeState.Status.Waiting,
        };

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, old);
        ms.Position = 0;
        var loaded = Serializer.Deserialize<InWorldz.Phlox.Serialization.SerializedRuntimeState>(ms);

        _out.WriteLine($"IP={loaded.IP} RunState={loaded.RunState} LastSyscallIndex={loaded.LastSyscallIndex}");
        Assert.Equal(42, loaded.IP);
        // The whole point of -1 rather than 0: 0 is a valid TableIndex, so a default of 0 would make
        // every pre-existing row claim it was interrupted inside whichever function that is.
        Assert.Equal(-1, loaded.LastSyscallIndex);
    }

    [Fact]
    public void TheIndexSurvivesAFullRoundTripThroughTheRuntimeState()
    {
        var state = new InWorldz.Phlox.VM.RuntimeState(4);
        state.RunState = InWorldz.Phlox.VM.RuntimeState.Status.Syscall;
        state.LastSyscallIndex = 137;

        var ser = InWorldz.Phlox.Serialization.SerializedRuntimeState.FromRuntimeState(state);
        Assert.Equal(137, ser.LastSyscallIndex);

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, ser);
        ms.Position = 0;
        var back = Serializer.Deserialize<InWorldz.Phlox.Serialization.SerializedRuntimeState>(ms)
                             .ToRuntimeState();

        Assert.Equal(137, back.LastSyscallIndex);
        Assert.Equal(InWorldz.Phlox.VM.RuntimeState.Status.Syscall, back.RunState);
    }

    private const string TouchScript =
        "default { state_entry() { llSay(0, \"ready\"); } touch_start(integer n) { llSay(0, \"touched\"); } }";

    /// <summary>
    /// 1d. Captured in Syscall, the script used to be restored Waiting with a value missing from its
    /// stack and no note of it. Now it is resumed with that call's default return value, reaches
    /// Waiting, and answers the next event.
    ///
    /// <para>
    /// The state is put into Syscall directly rather than by blocking a real shim: a gate would have to
    /// block the same thread the harness pumps on. What matters for this arm is the state that reaches
    /// <c>FinishedLoading</c>, and that is identical either way.
    /// </para>
    /// </summary>
    [Fact]
    public void AScriptSavedMidSyscallResumesAndAnswersTheNextEvent()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(TouchScript, assetId, itemId);
            h1.PumpUntil(() => h1.Said.Contains("ready") && h1.RunStateOf(itemId) == "Waiting");
            Assert.Contains("ready", h1.Said);

            var st = h1.StateOf(itemId)!;
            var t = st.GetType();
            // llSay's own table index, so the resume reports a real function name.
            int sayIndex = InWorldz.Phlox.Types.Defaults.AllMethods
                .First(m => m.FunctionName == "llSay").TableIndex;
            t.GetField("LastSyscallIndex")!.SetValue(st, sayIndex);
            t.GetField("RunState")!.SetValue(st, InWorldz.Phlox.VM.RuntimeState.Status.Syscall);
            _out.WriteLine($"captured RunState=Syscall LastSyscallIndex={sayIndex} (llSay)");
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(TouchScript, assetId, itemId);
        h2.PumpUntil(() => h2.RunStateOf(itemId) == "Waiting");

        _out.WriteLine("after restore: RunState=" + h2.RunStateOf(itemId));
        Assert.Equal("Waiting", h2.RunStateOf(itemId));

        // Not stuck: it takes a new event.
        h2.PostTouch(itemId);
        h2.PumpUntil(() => h2.Said.Contains("touched"));
        _out.WriteLine("said=[" + string.Join(",", h2.Said) + "]");
        Assert.Contains("touched", h2.Said);
    }
}
