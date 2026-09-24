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
            foreach (var sig in Defaults.SystemMethods.Values) map[sig.TableIndex] = sig;
            return map;
        }

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
