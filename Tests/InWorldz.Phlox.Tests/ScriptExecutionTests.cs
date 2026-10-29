using System.Reflection;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Xunit;
namespace InWorldz.Phlox.Tests;
/// <summary>
/// Everything before this ran the compiler; nothing ran a script. In world the manhole
/// compiled and then did nothing — no <c>state_entry</c>, no error — and the suite could not say
/// whether compiled bytecode still executes.
///
/// <para>
/// This drives a compiled script through the real <see cref="Interpreter"/> against a recording
/// <see cref="ISystemAPI"/>. It is what tells dispatch apart from the loader/start path: if
/// <c>llSay</c> arrives here then the bytecode and the syscall dispatch are sound and the fault is
/// upstream, in how a script is started.
/// </para>
/// </summary>
public class ScriptExecutionTests
{
    [Fact]
    public void AFreshlyCompiledStateEntryReachesTheSystemApi()
    {
        var compiled = PhloxCompiler.CompileTo(@"
default
{
    state_entry()
    {
        llSay(0, ""x"");
    }
}
", out var listener);
        Assert.NotNull(compiled);
        Assert.False(listener.HasErrors(), listener.Report);
        var api = RecordingSystemApi.Create(out var calls);
        var shim = new SyscallShim(call => call());
        shim.SystemAPI = api;
        var interp = new Interpreter(compiled, shim);
        shim.Interpreter = interp;
        // Exactly what PhloxExecutionScheduler.StartEvent does: find the handler for the entry
        // state, hand it to the runtime state, then run.
        var info = compiled.FindEvent(interp.ScriptState.LSLState, (int)SupportedEventList.Events.STATE_ENTRY);
        Assert.NotNull(info);
        interp.ScriptState.DoEvent(info,
            new PostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() },
            Array.Empty<object>());
        // Tick the event through. Driving the interpreter directly, without the scheduler that
        // normally owns the run queue, means it can be ticked past the end of the event and throw;
        // that is this harness's business, not the script's, so it is caught. What matters is
        // whether the syscall reached the API.
        try
        {
            for (var i = 0; i < 100_000 && interp.ScriptState.RunningEvent != null; i++)
                interp.Tick();
        }
        catch (InvalidOperationException) { /* ticked past the end of the event */ }
        Assert.True(calls.Exists(c => c.StartsWith("llSay")),
            $"llSay never reached the API; the syscalls seen were [{string.Join(" | ", calls)}]");
    }
}
/// <summary>
/// A recording <see cref="ISystemAPI"/>. The interface has hundreds of members, so it is generated
/// rather than written: every call is recorded and returns default.
/// </summary>
public class RecordingSystemApi : DispatchProxy
{
    private List<string> _calls;
    public static ISystemAPI Create(out List<string> calls)
    {
        calls = new List<string>();
        var proxy = Create<ISystemAPI, RecordingSystemApi>();
        ((RecordingSystemApi)(object)proxy)._calls = calls;
        return proxy;
    }
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        _calls.Add(targetMethod.Name + "(" + string.Join(", ", args ?? Array.Empty<object>()) + ")");
        var rt = targetMethod.ReturnType;
        // A null string or list pushed onto the operand stack throws in SafeOperandsPush,
        // and the aborted syscall is then re-dispatched - which recorded every non-void call twice
        // and made llGetKey() unusable in a recorded script. Empty values are what a script would
        // get from a quiet API, and they keep the VM on its rails.
        if (rt == typeof(string)) return string.Empty;
        if (rt == typeof(LSLList)) return new LSLList(new List<object>());
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
/// <summary>
/// The start path, pinned at source level because constructing the scheduler needs a
/// Scene and a prim. The defect was not in dispatch — <see cref="ScriptExecutionTests"/> shows a
/// compiled script's state_entry reaching the API — it was that a freshly started script was set
/// Running before its state_entry was posted, and ProcessEventQueue only STARTS an event for a
/// script that is Waiting.
/// </summary>
public class FreshStartRunStateTests
{
    private static string Scheduler()
    {
        var here = Path.GetDirectoryName(typeof(FreshStartRunStateTests).Assembly.Location)!;
        var path = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "..",
            "Source", "Phlox.ScriptEngine", "PhloxExecutionScheduler.cs"));
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path);
    }
    [Fact]
    public void AFreshScriptIsLeftWaitingSoItsStateEntryIsActuallyStarted()
    {
        var src = Scheduler();
        // The freshStart branch, taken from its marker comment to the assignment that follows.
        var marker = "PHLOX-2d: this MUST be Waiting";
        var at = src.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(at > 0, "the freshStart branch has lost its PHLOX-2d marker comment");
        var branch = src.Substring(at, 900);

        Assert.Contains("RunState = RuntimeState.Status.Waiting", branch);
    }
    [Fact]
    public void OnlyAWaitingScriptHasItsEventStarted()
    {
        // The rule the branch above has to satisfy. If this gate ever changes, the fresh-start
        // state has to change with it.
        Assert.Contains("if (script.ScriptState.RunState == RuntimeState.Status.Waiting)", Scheduler());
    }
}