using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.SLua;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Compiles a script, runs its <c>state_entry</c> on the real <see cref="Interpreter"/> and
/// records what it said. The shim pops each syscall's arguments straight off the VM operand
/// stack, so a test sees the raw runtime values (an <c>int</c> is an <c>int</c>, not the
/// text a proxy would print).
/// </summary>
public static class ExprRunner
{
    /// <summary>Upper bound on VM ticks per run; a loop that never ends fails instead of hanging.</summary>
    public const int MaxTicks = 2_000_000;

    public sealed class Result
    {
        public string CompileError;
        public string RuntimeError;
        public bool TimedOut;
        /// <summary>The text of every llOwnerSay / llSay / ll.Say, in order.</summary>
        public readonly List<string> Said = new();
        /// <summary>Every syscall with its raw arguments, in order.</summary>
        public readonly List<(string Name, object[] Args)> Calls = new();

        public bool Ok => CompileError == null && RuntimeError == null && !TimedOut;

        /// <summary>A one-line description for assertion messages.</summary>
        public string Describe()
        {
            if (CompileError != null) return "COMPILE ERROR: " + CompileError;
            var said = "[" + string.Join(" | ", Said) + "]";
            if (RuntimeError != null) return "RUNTIME ERROR: " + RuntimeError + " after " + said;
            if (TimedOut) return "TIMED OUT (loop never ended) after " + said;
            return said;
        }
    }

    /// <summary>Wraps a body in <c>default { state_entry() { ... } }</c> after the globals.</summary>
    public static Result RunInDefault(string body, string globals = "")
        => RunLsl(globals + "\ndefault\n{\n    state_entry()\n    {\n" + body + "\n    }\n}\n");

    public static Result RunLsl(string source)
    {
        var compiled = PhloxCompiler.CompileTo(source, out var listener);
        if (compiled == null || listener.HasErrors())
            return new Result { CompileError = listener.HasErrors() ? listener.Report : "no script produced" };
        return Run(compiled);
    }

    /// <summary>Runs an SLua body inside <c>function run() ... end run()</c>.</summary>
    public static Result RunSLua(string body)
    {
        string src = "--!slua\nfunction run()\n" + body + "\nend\nrun()\n";
        var listener = new PhloxCompiler();
        string asm;
        try { asm = SLuaCompiler.CompileToAssembly(src, listener); }
        catch (Exception ex) { return new Result { CompileError = ex.Message }; }
        if (asm == null || listener.HasErrors())
            return new Result { CompileError = listener.HasErrors() ? listener.Report : "no assembly produced" };
        CompiledScript compiled;
        try { compiled = new CompilerFrontend(new PhloxCompiler(), ".").AssembleText(asm); }
        catch (Exception ex) { return new Result { CompileError = "assemble: " + ex.Message }; }
        return Run(compiled);
    }

    private static Result Run(CompiledScript compiled)
    {
        var result = new Result();
        var shim = new RecordingShim(result);
        var interp = new Interpreter(compiled, shim);
        shim.Interp = interp;
        try
        {
            // Global initialisers run first, up to the halt.
            RunToWait(interp, result);
            var info = compiled.FindEvent(0, (int)SupportedEventList.Events.STATE_ENTRY);
            if (info != null && !result.TimedOut)
            {
                interp.ScriptState.RunState = RuntimeState.Status.Running;
                interp.ScriptState.DoEvent(info,
                    new PostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() },
                    Array.Empty<object>());
                RunToWait(interp, result);
            }
        }
        catch (Exception ex)
        {
            result.RuntimeError ??= ex.GetType().Name + ": " + FirstLine(ex.Message);
        }
        return result;
    }

    /// <summary>Compiles LSL for a test that drives the interpreter itself; throws on a compile error.</summary>
    public static CompiledScript CompileLsl(string source)
    {
        var compiled = PhloxCompiler.CompileTo(source, out var listener);
        if (compiled == null || listener.HasErrors())
            throw new InvalidOperationException("compile failed: " + listener.Report);
        return compiled;
    }

    /// <summary>
    /// One interpreter a test can stop and continue: it runs until the script is no longer
    /// Running (finished, or parked in llSleep as the region's API parks it), and it can be built
    /// on a restored <see cref="RuntimeState"/>.
    /// </summary>
    public sealed class Session
    {
        public readonly Result Result = new();
        public readonly Interpreter Interp;
        public CompiledScript Script => Interp.Script;
        public RuntimeState State => Interp.ScriptState;

        private Session(CompiledScript script, RuntimeState restored)
        {
            var shim = new RecordingShim(Result);
            Interp = restored == null ? new Interpreter(script, shim) : new Interpreter(script, restored, shim);
            shim.Interp = Interp;
        }

        /// <summary>A fresh script: globals, then state_entry, up to the first stop.</summary>
        public static Session Start(CompiledScript script)
        {
            var s = new Session(script, null);
            s.Guard(() =>
            {
                RunToWait(s.Interp, s.Result);
                var info = script.FindEvent(0, (int)SupportedEventList.Events.STATE_ENTRY);
                s.State.RunState = RuntimeState.Status.Running;
                s.State.DoEvent(info,
                    new PostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() },
                    Array.Empty<object>());
                RunToWait(s.Interp, s.Result);
            });
            return s;
        }

        /// <summary>A script built on a restored state; nothing runs until the test says so.</summary>
        public static Session Attach(CompiledScript script, RuntimeState restored) => new(script, restored);

        /// <summary>Starts a handler for a waiting script, as the scheduler does, and runs it.</summary>
        public void Deliver(PostedEvent evt)
        {
            if (State.RunState != RuntimeState.Status.Waiting)
            {
                Result.RuntimeError ??= "not waiting: " + State.RunState;
                return;
            }
            var info = Script.FindEvent(State.LSLState, (int)evt.EventType);
            State.RunState = RuntimeState.Status.Running;
            Guard(() =>
            {
                State.DoEvent(info, evt, evt.Args);
                RunToWait(Interp, Result);
            });
        }

        /// <summary>Wakes a sleeping script, as the scheduler does when NextWakeup passes, and runs on.</summary>
        public void Wake()
        {
            if (State.RunState != RuntimeState.Status.Sleeping) return;
            State.RunState = RuntimeState.Status.Running;
            Guard(() => RunToWait(Interp, Result));
        }

        private void Guard(Action run)
        {
            try { run(); }
            catch (Exception ex) { Result.RuntimeError ??= ex.GetType().Name + ": " + FirstLine(ex.Message); }
        }
    }

    private static void RunToWait(Interpreter interp, Result result)
    {
        int ticks = 0;
        while (interp.ScriptState.RunState == RuntimeState.Status.Running && result.RuntimeError == null)
        {
            if (++ticks > MaxTicks) { result.TimedOut = true; return; }
            interp.Tick();
        }
    }

    private static string FirstLine(string s)
    {
        if (s == null) return string.Empty;
        int nl = s.IndexOf('\n');
        return nl < 0 ? s : s.Substring(0, nl);
    }

    private sealed class RecordingShim : ISyscallShim
    {
        private static readonly Dictionary<int, FunctionSig> ByIndex = BuildIndex();
        private readonly Result _result;
        public Interpreter Interp;

        public RecordingShim(Result result) { _result = result; }

        private static Dictionary<int, FunctionSig> BuildIndex()
        {
            var map = new Dictionary<int, FunctionSig>();
            foreach (object entry in Defaults.SystemMethods.Values)
                foreach (var sig in Signatures(entry)) map[sig.TableIndex] = sig;
            return map;
        }

        // A name maps to one signature, or to a list of them where a builtin has overloads.
        private static IEnumerable<FunctionSig> Signatures(object entry)
            => entry is FunctionSig sig ? new[] { sig } : (IEnumerable<FunctionSig>)entry;

        public void Call(int funcid)
        {
            var sig = ByIndex[funcid];
            int n = sig.ParamTypes.Length;
            var args = new object[n];
            for (int k = n - 1; k >= 0; k--) args[k] = Interp.ScriptState.Operands.Pop();
            _result.Calls.Add((sig.FunctionName, args));
            switch (sig.FunctionName)
            {
                case "llOwnerSay":
                    _result.Said.Add(Format(args[0]));
                    break;
                case "llSay": case "llShout": case "llWhisper": case "llRegionSay":
                    _result.Said.Add(Format(args[1]));
                    break;
                case "llSleep":
                    // As the region's API does it: park the script until NextWakeup.
                    Interp.ScriptState.NextWakeup = InWorldz.Phlox.Util.Clock.GetLongTickCount()
                        + (ulong)(Convert.ToSingle(args[0]) * 1000f);
                    Interp.ScriptState.RunState = RuntimeState.Status.Sleeping;
                    break;
            }
            if (sig.ReturnType != VarType.Void) Interp.SafeOperandsPush(DefaultOf(sig.ReturnType));
        }

        private static string Format(object o)
        {
            if (o == null || o is LuaNil) return "nil";
            if (o is bool b) return b ? "true" : "false";
            return o.ToString();
        }

        private static object DefaultOf(VarType t) => t switch
        {
            VarType.Integer => 0,
            VarType.Float => 0f,
            VarType.Vector => OpenMetaverse.Vector3.Zero,
            VarType.Rotation => OpenMetaverse.Quaternion.Identity,
            VarType.List => new LSLList(new List<object>()),
            _ => string.Empty,
        };

        public void SetScriptEventFlags() { }
        public void ShoutError(string errorText) { _result.RuntimeError ??= FirstLine(errorText); }
        public void OnScriptReset() { }
        public void OnStateChange() { }
        public void OnScriptUnloaded(ScriptUnloadReason reason, RuntimeState.LocalDisableFlag flags) { }
        public void AddExecutionTime(double ms) { }
        public float GetAverageScriptTime() => 0f;
        public void OnScriptInjected(bool fromCrossing) { }
        public void OnGroupCrossedAvatarReady(OpenMetaverse.UUID avatarId) { }
    }
}
