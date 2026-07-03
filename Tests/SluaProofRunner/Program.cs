using System;
using System.Collections.Generic;
using System.IO;

using InWorldz.Phlox.Glue;
using InWorldz.Phlox.SLua;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Serialization;

using ProtoBuf;

namespace Phlox.ScriptEngine
{
    internal sealed class RecordingShim : ISyscallShim
    {
        public Interpreter Interp;
        public readonly List<string> Records = new List<string>();
        private readonly Dictionary<int, FunctionSig> _byIndex = new Dictionary<int, FunctionSig>();
        public RecordingShim() { foreach (var sig in Defaults.SystemMethods.Values) _byIndex[sig.TableIndex] = sig; }
        public void Call(int funcid)
        {
            var sig = _byIndex[funcid];
            int n = sig.ParamTypes.Length;
            var args = new object[n];
            for (int k = n - 1; k >= 0; k--) args[k] = Interp.ScriptState.Operands.Pop();
            switch (sig.FunctionName)
            {
                case "llSay": case "llShout": case "llWhisper": case "llOwnerSay":
                    Records.Add(Fmt(args.Length > 1 ? args[1] : (args.Length > 0 ? args[0] : null))); return;
            }
            if (sig.ReturnType != VarType.Void) Interp.SafeOperandsPush(0);
        }
        private static string Fmt(object o)
        {
            if (o == null || o is LuaNil) return "nil";
            if (o is bool b) return b ? "true" : "false";
            return o.ToString();
        }
        public void SetScriptEventFlags() { } public void ShoutError(string e) { }
        public void OnScriptReset() { } public void OnStateChange() { }
        public void OnScriptUnloaded(ScriptUnloadReason r, RuntimeState.LocalDisableFlag f) { }
        public void AddExecutionTime(double ms) { } public float GetAverageScriptTime() { return 0f; }
        public void OnScriptInjected(bool fromCrossing) { } public void OnGroupCrossedAvatarReady(OpenMetaverse.UUID a) { }
    }
    internal sealed class Quiet : ILSLListener
    {
        public string Err;
        public void Info(string m) { } public void Error(string m) { Err = m; }
        public bool HasErrors() { return Err != null; } public void CompilationFinished() { }
    }

    internal static class Program
    {
        private static readonly CompilerFrontend Fe = new CompilerFrontend(new NullListener(), ".");
        private static int _pass = 0, _divergence = 0, _gap = 0;

        // Run a snippet whose body goes inside `function run() ... end` then `run()` at top level.
        // Returns the ll.Say outputs joined by '|', or "COMPILE:<msg>" / "RUNTIME:<msg>".
        private static string RunBody(string body)
        {
            string src = "--!slua\nfunction run()\n" + body + "\nend\nrun()\n";
            var lis = new Quiet();
            string asm = SLuaCompiler.CompileToAssembly(src, lis);
            if (asm == null) return "COMPILE:" + (lis.Err ?? "?");
            CompiledScript cs;
            try { cs = Fe.AssembleText(asm); } catch (Exception e) { return "ASSEMBLE:" + e.Message; }
            if (cs == null) return "ASSEMBLE:null";
            var sh = new RecordingShim(); var ip = new Interpreter(cs, sh); sh.Interp = ip;
            try
            {
                RunToWait(ip);
                var ei = cs.FindEvent(0, (int)SupportedEventList.Events.STATE_ENTRY);
                if (ei != null) { ip.ScriptState.RunState = RuntimeState.Status.Running; ip.ScriptState.DoEvent(ei, new PostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() }, Array.Empty<object>()); RunToWait(ip); }
            }
            catch (Exception e) { return "RUNTIME:" + e.GetType().Name; }
            return string.Join("|", sh.Records);
        }

        // bucket: "div" = real divergence (we support it, wrong result), "gap" = unsupported feature.
        private static void T(string name, string body, string expect, string bucketIfFail)
        {
            string got = RunBody(body);
            bool ok = got == expect;
            if (ok) { _pass++; Console.WriteLine("PASS  " + name); return; }
            if (bucketIfFail == "gap") { _gap++; Console.WriteLine("GAP   " + name + "  (" + Trim(got) + ")"); }
            else { _divergence++; Console.WriteLine("DIVERGENCE  " + name + "  got=[" + Trim(got) + "] want=[" + expect + "]"); }
        }
        private static string Trim(string s) => s.Length > 70 ? s.Substring(0, 70) : s;

        private static void Main()
        {
            Console.WriteLine("=== MATH (from SL's actual math.luau assertions) ===");
            // supported subset -- should PASS (we round to clean values to avoid float-format noise)
            T("math.sin(pi/2)=1",   "ll.Say(0, tostring(math.round(math.sin(math.pi/2))))", "1", "div");
            T("math.cos(pi/2)=0",   "ll.Say(0, tostring(math.round(math.cos(math.pi/2))))", "0", "div");
            T("math.tan(pi/4)=1",   "ll.Say(0, tostring(math.round(math.tan(math.pi/4))))", "1", "div");
            T("math.atan(1)=pi/4",  "ll.Say(0, tostring(math.round(math.atan(1)*1000)))", "785", "div");
            T("math.atan2(1,0)",    "ll.Say(0, tostring(math.round(math.atan2(1,0)*1000)))", "1571", "div");
            T("math.acos(0)=pi/2",  "ll.Say(0, tostring(math.round(math.acos(0)*1000)))", "1571", "div");
            T("math.ceil(4.5)=5",   "ll.Say(0, tostring(math.ceil(4.5)))", "5", "div");
            T("math.floor(4.5)=4",  "ll.Say(0, tostring(math.floor(4.5)))", "4", "div");
            T("math.abs(-10)=10",   "ll.Say(0, tostring(math.abs(-10)))", "10", "div");
            T("math.sqrt(10)^2=10", "ll.Say(0, tostring(math.round(math.sqrt(10)*math.sqrt(10))))", "10", "div");
            T("math.log(2,2)=1",    "ll.Say(0, tostring(math.round(math.log(2,2))))", "1", "div");
            T("math.exp(0)=1",      "ll.Say(0, tostring(math.exp(0)))", "1", "div");
            T("math.sign(42)=1",    "ll.Say(0, tostring(math.sign(42)))", "1", "div");
            T("math.clamp(-1,0,1)=0","ll.Say(0, tostring(math.clamp(-1,0,1)))", "0", "div");
            T("math.round(0.5)=1",  "ll.Say(0, tostring(math.round(0.5)))", "1", "div");
            // SL's math.luau also requires these -- expected GAPS (Luau math we don't ship yet)
            T("math.log10",  "ll.Say(0, tostring(math.log10(100)))", "2", "gap");
            T("math.sinh",   "ll.Say(0, tostring(math.sinh(0)))", "0", "gap");
            T("math.cosh",   "ll.Say(0, tostring(math.cosh(0)))", "1", "gap");
            T("math.tanh",   "ll.Say(0, tostring(math.tanh(0)))", "0", "gap");
            T("math.noise",  "ll.Say(0, tostring(math.noise(0.5)))", "0", "gap");
            T("math.noise 2d", "ll.Say(0, tostring(math.noise(0.5, 0.5)))", "-0.25", "div");
            T("math.noise 3d", "ll.Say(0, tostring(math.noise(0.5, 0.5, -0.5)))", "0.125", "div");
            T("math.noise bigfloat (==)", "if math.noise(455.7204209769105, 340.80410508750134, 121.80087666537628) == 0.5010709762573242 then ll.Say(0,\"eq\") else ll.Say(0,\"ne\") end", "eq", "div");
            T("math.map",    "ll.Say(0, tostring(math.map(0,-1,1,0,2)))", "1", "gap");
            T("math.lerp",   "ll.Say(0, tostring(math.lerp(1,5,0.5)))", "3", "gap");
            T("math.isnan",  "ll.Say(0, tostring(math.isnan(0/0)))", "true", "gap");
            T("math.isinf",  "ll.Say(0, tostring(math.isinf(math.huge)))", "true", "gap");
            T("math.isfinite","ll.Say(0, tostring(math.isfinite(1.0)))", "true", "gap");

            Console.WriteLine("\n=== LANGUAGE CORE (closure/calls/basic/locals/literals/comparison/iter) ===");
            T("closure upvalue", "local function mk() local n=0 return function() n=n+1 return n end end\n local c=mk()\n ll.Say(0, tostring(c())..tostring(c()))", "12", "div");
            T("multiple return", "local function two() return 1,2 end\n local a,b=two()\n ll.Say(0, tostring(a)..tostring(b))", "12", "div");
            T("varargs ...",     "local function sum(...) local t={...} local s=0 for i=1,#t do s=s+t[i] end return s end\n ll.Say(0, tostring(sum(1,2,3)))", "6", "gap");
            T("and/or short-circuit", "local x = false and error(\"no\") or \"ok\"\n ll.Say(0, x)", "ok", "div");
            T("truthiness 0 is true", "if 0 then ll.Say(0,\"t\") else ll.Say(0,\"f\") end", "t", "div");
            T("not nil",          "ll.Say(0, tostring(not nil))", "true", "div");
            T("string.len(#)",    "ll.Say(0, tostring(#\"abc\"))", "3", "div");
            T("numeric for",      "local s=0 for i=1,5 do s=s+i end ll.Say(0, tostring(s))", "15", "div");
            T("numeric for step", "local s=\"\" for i=10,1,-3 do s=s..i..\",\" end ll.Say(0, s)", "10,7,4,1,", "div");
            T("generic for pairs","local t={a=1,b=2} local s=0 for k,v in pairs(t) do s=s+v end ll.Say(0, tostring(s))", "3", "div");
            T("ipairs",          "local t={10,20,30} local s=0 for i,v in ipairs(t) do s=s+v end ll.Say(0, tostring(s))", "60", "div");
            T("ternary if-expr", "local x = if true then 1 else 2\n ll.Say(0, tostring(x))", "1", "gap");
            T("string escape",   "ll.Say(0, \"a\\nb\")", "a\nb", "div");
            T("hex literal",     "ll.Say(0, tostring(0xFF))", "255", "gap");
            T("multiple assign", "local a,b = 1,2\n a,b = b,a\n ll.Say(0, tostring(a)..tostring(b))", "21", "div");

            Console.WriteLine("\n=== STDLIB string/table ===");
            T("string.format",   "ll.Say(0, string.format(\"%d-%s\", 5, \"x\"))", "5-x", "div");
            T("string.sub",      "ll.Say(0, string.sub(\"hello\",2,4))", "ell", "div");
            T("string.gsub",     "ll.Say(0, (string.gsub(\"aaa\",\"a\",\"b\")))", "bbb", "div");
            T("string.rep",      "ll.Say(0, string.rep(\"ab\",3))", "ababab", "div");
            T("table.sort+concat","local t={3,1,2} table.sort(t) ll.Say(0, table.concat(t,\",\"))", "1,2,3", "div");
            T("table.remove",    "local t={1,2,3} ll.Say(0, tostring(table.remove(t))..table.concat(t,\",\"))", "31,2", "div");
            T("table.unpack",    "local a,b,c=table.unpack({7,8,9}) ll.Say(0, tostring(a)..tostring(b)..tostring(c))", "789", "div");
            T("select('#')",     "local function n(...) return select(\"#\", ...) end ll.Say(0, tostring(n(1,2,3)))", "3", "gap");
            T("string.split",    "ll.Say(0, table.concat(string.split(\"a,b\",\",\"),\"|\"))", "a|b", "div");

            Console.WriteLine("\n=== ERRORS (assert/error/pcall/exceptions) ===");
            T("pcall success",   "local ok,r=pcall(function() return 5 end) ll.Say(0, tostring(ok)..tostring(r))", "true5", "div");
            T("pcall error",     "local ok,e=pcall(function() error(\"boom\") end) ll.Say(0, tostring(ok)..\" \"..tostring(e))", "false boom", "div");
            T("assert pass",     "ll.Say(0, tostring(assert(7)))", "7", "div");
            T("assert fail msg", "local ok,e=pcall(function() assert(false,\"X\") end) ll.Say(0, tostring(e))", "X", "div");

            Console.WriteLine("\n=== METATABLES / OOP ===");
            T("__index method",  "local C={} C.__index=C function C.new() return setmetatable({},C) end function C:hi() return \"hi\" end local o=C.new() ll.Say(0, o:hi())", "hi", "div");
            T("__add",           "local V={} V.__add=function(a,b) return a.n+b.n end local x=setmetatable({n=2},V) local y=setmetatable({n=3},V) ll.Say(0, tostring(x+y))", "5", "div");
            T("__tostring",      "local V={} V.__tostring=function() return \"VV\" end ll.Say(0, tostring(setmetatable({},V)))", "VV", "div");

            Console.WriteLine("\n=== TYPES / VECTORS ===");
            T("type(vector)",    "ll.Say(0, type(vector(1,2,3)))", "vector", "div");
            T("vector dot",      "ll.Say(0, tostring(vector(1,2,3)*vector(4,5,6)))", "32", "div");
            T("vector .x",       "ll.Say(0, tostring(vector(7,8,9).x))", "7", "div");
            T("tonumber",        "ll.Say(0, tostring(tonumber(\"42\")+1))", "43", "div");

            Console.WriteLine();
            int total = _pass + _divergence + _gap;
            Console.WriteLine("TOTALS: " + _pass + " PASS, " + _divergence + " DIVERGENCE, " + _gap + " GAP  (of " + total + ")");
        }

        private static void RunToWait(Interpreter ip) { int g = 0; while (ip.ScriptState.RunState == RuntimeState.Status.Running && g++ < 5000000) ip.Tick(); }
    }
}
