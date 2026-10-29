using System.Diagnostics;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;

// Compiles generated scripts on a thread with the loader's 16 MB stack, in its own process, so a test
// can see a real stack overflow (which no catch stops) as a dead child instead of a dead test host - and so "cold"
// means what it says: a fresh process whose compiler has never run.
//
//   PhloxCompileProbe cold|warm lang:kind:depth [lang:kind:depth ...]
//
// warm compiles 100 ordinary scripts first and waits for tiered JIT to promote the compiler, as a running region
// has. One line per case:  CASE lsl:paren:1000 COMPILED 12ms  |  CASE ... ERROR 9ms line 5:3017 expression ...
namespace PhloxCompileProbe;

public static class Program
{
    public const int CompileStackSize = 16 * 1024 * 1024;

    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: cold|warm lang:kind:depth ..."); return 2; }
        if (args[0] == "warm") Warm();
        foreach (var spec in args.Skip(1))
        {
            var p = spec.Split(':');
            var (ok, ms, errors) = Compile(Scripts.Make(p[0], p[1], int.Parse(p[2])));
            Console.WriteLine($"CASE {spec} {(ok ? "COMPILED" : "ERROR")} {ms}ms {string.Join(" / ", errors.Take(2))}");
            Console.Out.Flush();
        }
        return 0;
    }

    public static void Warm()
    {
        for (int i = 0; i < 100; i++)
        {
            Compile(Scripts.Ordinary);
            Compile(Scripts.OrdinaryLua);
            if (i % 20 == 19) Thread.Sleep(300);
        }
        Thread.Sleep(1500);
    }

    public static (bool ok, long ms, List<string> errors) Compile(string src)
    {
        var listener = new Listener();
        object result = null;
        Exception fail = null;
        var sw = Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            try
            {
                var fe = new CompilerFrontend(listener, ".");
                result = InWorldz.Phlox.SLua.SLuaCompiler.IsLuaScript(src) ? fe.CompileLua(src) : fe.Compile(src);
            }
            catch (Exception e) { fail = e; }
        }, CompileStackSize);
        t.Start();
        t.Join();
        sw.Stop();
        if (fail != null) listener.Errors.Add("threw " + fail.GetType().Name + ": " + fail.Message);
        return (result != null && listener.Errors.Count == 0, sw.ElapsedMilliseconds, listener.Errors);
    }

    private sealed class Listener : ILSLListener
    {
        public readonly List<string> Errors = new();
        public void Info(string m) { }
        public void Error(string m) { Errors.Add(m); }
        public bool HasErrors() => Errors.Count > 0;
        public void CompilationFinished() { }
    }
}

/// <summary>The generated scripts: one construct nested <c>n</c> deep (or an else-if chain <c>n</c> long).</summary>
public static class Scripts
{
    public const string Ordinary =
        "integer g = 3;\nlist items = [\"a\", \"b\"];\nfloat half(float v) { return v / 2.0; }\n" +
        "default\n{\n    state_entry()\n    {\n        integer i;\n        for (i = 0; i < g; ++i) { llSay(0, llList2String(items, i % 2) + (string)half((float)i)); }\n" +
        "        if (g > 2) llSetTimerEvent(1.0); else llSetTimerEvent(0.0);\n    }\n    timer() { llSay(0, (string)llGetUnixTime()); }\n}\n";

    public const string OrdinaryLua =
        "--!slua\nlocal t = {1, 2, 3}\nlocal function sum(v) local s = 0 for i = 1, #v do s = s + v[i] end return s end\n" +
        "if sum(t) > 5 then ll.Say(0, tostring(sum(t))) else ll.Say(0, \"small\") end\n";

    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static string Lsl(string body) => "default\n{\n    state_entry()\n    {\n" + body + "\n    }\n}\n";

    public static string Make(string lang, string kind, int n) => lang switch
    {
        "lsl" => kind switch
        {
            "paren"  => Lsl("        integer x = " + Rep("(", n) + "1" + Rep(")", n) + ";\n        llSay(0, (string)x);"),
            "neg"    => Lsl("        integer x = " + Rep("- ", n) + "1;\n        llSay(0, (string)x);"),
            "not"    => Lsl("        integer x = " + Rep("! ", n) + "1;\n        llSay(0, (string)x);"),
            "bitnot" => Lsl("        integer x = " + Rep("~ ", n) + "1;\n        llSay(0, (string)x);"),
            "cast"   => Lsl("        integer x = " + Rep("(integer)", n) + "1;\n        llSay(0, (string)x);"),
            "call"   => Lsl("        integer x = " + Rep("llAbs(", n) + "1" + Rep(")", n) + ";\n        llSay(0, (string)x);"),
            "list"   => Lsl("        list x = " + Rep("[", n) + "1" + Rep("]", n) + ";\n        llSay(0, (string)llGetListLength(x));"),
            "assign" => Lsl("        integer a;\n        a = " + Rep("a = ", n) + "1;\n        llSay(0, (string)a);"),
            "block"  => Lsl(Rep("        if (llGetUnixTime() > 0) {\n", n) + "        llSay(0, \"deep\");\n" + Rep("        }\n", n)),
            "bare"   => Lsl(Rep("        {\n", n) + "        llSay(0, \"deep\");\n" + Rep("        }\n", n)),
            "elseif" => Lsl("        integer c = (integer)llFrand(10.0);\n        if (c == 0) { llSay(0, \"0\"); }\n" +
                            string.Concat(Enumerable.Range(1, n - 1).Select(i => $"        else if (c == {i}) {{ llSay(0, \"{i}\"); }}\n")) +
                            "        else { llSay(0, \"other\"); }"),
            // The same with a syntax error at the end: the parse falls back from SLL to full LL (the worst case).
            "elseifbad" => Make("lsl", "elseif", n).Replace("        else { llSay(0, \"other\"); }", "        else { llSay(0, \"other\") }"),
            "blockbad"  => Make("lsl", "block", n).Replace("        llSay(0, \"deep\");", "        llSay(0, \"deep\")"),
            "assignbad" => Make("lsl", "assign", n).Replace("        llSay(0, (string)a);", "        llSay(0, (string)a)"),
            "parenbad"  => Make("lsl", "paren", n).Replace("        llSay(0, (string)x);", "        llSay(0, (string)x)"),
            "callbad"   => Make("lsl", "call", n).Replace("        llSay(0, (string)x);", "        llSay(0, (string)x)"),
            _ => throw new ArgumentException(kind),
        },
        "lua" => kind switch
        {
            "paren"  => "--!slua\nlocal x = " + Rep("(", n) + "1" + Rep(")", n) + "\nll.Say(0, tostring(x))\n",
            "neg"    => "--!slua\nlocal x = " + Rep("- ", n) + "1\nll.Say(0, tostring(x))\n",
            "not"    => "--!slua\nlocal x = " + Rep("not ", n) + "true\nll.Say(0, tostring(x))\n",
            "table"  => "--!slua\nlocal t = " + Rep("{", n) + "1" + Rep("}", n) + "\nll.Say(0, tostring(#t))\n",
            "concat" => "--!slua\nlocal s = " + Rep("\"a\" .. ", n) + "\"a\"\nll.Say(0, s)\n",
            "call"   => "--!slua\nlocal function f(v) return v end\nlocal x = " + Rep("f(", n) + "1" + Rep(")", n) + "\nll.Say(0, tostring(x))\n",
            "func"   => "--!slua\nlocal f = " + Rep("function() return ", n) + "1" + Rep(" end", n) + "\nll.Say(0, tostring(f))\n",
            "block"  => "--!slua\n" + Rep("if true then\n", n) + "ll.Say(0, \"deep\")\n" + Rep("end\n", n),
            "elseif" => "--!slua\nlocal c = math.floor(ll.Frand(10))\nif c == 0 then ll.Say(0, \"0\")\n" +
                        string.Concat(Enumerable.Range(1, n - 1).Select(i => $"elseif c == {i} then ll.Say(0, \"{i}\")\n")) +
                        "else ll.Say(0, \"other\") end\n",
            _ => throw new ArgumentException(kind),
        },
        _ => throw new ArgumentException(lang),
    };
}
