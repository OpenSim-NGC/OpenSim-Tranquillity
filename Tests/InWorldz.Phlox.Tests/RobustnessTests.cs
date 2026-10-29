using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InWorldz.Phlox.Glue;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script's text must not be able to take the region down or stall it:
/// deep nesting is a compile error, not a stack overflow (which no catch can stop and which ends the
/// process), and a script-supplied regular expression cannot hold the scheduler thread.
/// </summary>
[Collection("phlox-state")]
public class RobustnessTests
{
    private readonly ITestOutputHelper _out;
    public RobustnessTests(ITestOutputHelper o) => _out = o;

    private const int Depth = 100_000;
    private const int DebugChannel = 0x7FFFFFFF;

    /// <summary>Compile on a thread with a 1 MB stack - smaller than the loader's - and return what the compiler said.</summary>
    private static PhloxCompiler CompileOnSmallStack(string src, bool lua)
    {
        var listener = new PhloxCompiler();
        object result = null;
        Exception thrown = null;
        var t = new Thread(() =>
        {
            try
            {
                var fe = new CompilerFrontend(listener, templatePath: null);
                result = lua ? fe.CompileLua(src) : fe.Compile(src);
            }
            catch (Exception e) { thrown = e; }
        }, maxStackSize: 1 << 20);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "the compile did not finish");
        Assert.Null(thrown);
        Assert.Null(result);
        return listener;
    }

    [Fact]
    public void ADeeplyNestedLslExpressionIsACompileError()
    {
        var src = "default { state_entry() { integer x = " + new string('(', Depth) + "1" + new string(')', Depth) + "; } }";
        var l = CompileOnSmallStack(src, lua: false);
        _out.WriteLine(l.Report);
        Assert.Contains(l.Errors, e => e.Contains("expression nested too deeply"));
        Assert.Contains(l.Errors, e => e.StartsWith("line 1:"));
    }

    [Fact]
    public void ADeeplyNestedSluaExpressionIsACompileError()
    {
        var src = "--!slua\nlocal x = " + new string('(', Depth) + "1" + new string(')', Depth) + "\n";
        var l = CompileOnSmallStack(src, lua: true);
        _out.WriteLine(l.Report);
        Assert.Contains(l.Errors, e => e.Contains("expression nested too deeply"));
    }

    /// <summary>The loader compiles on its own 16 MB thread; nesting a real script might use still compiles there.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModestNestingStillCompilesOnTheLoadersThread(bool lua)
    {
        const int depth = 1000;
        var src = lua
            ? "--!slua\nlocal x = " + new string('(', depth) + "1" + new string(')', depth) + "\nll.Say(0, tostring(x))\n"
            : "default { state_entry() { integer x = " + new string('(', depth) + "1" + new string(')', depth) + "; llSay(0, (string)x); } }";
        var l = new PhloxCompiler();
        var compiled = global::Phlox.ScriptEngine.PhloxScriptLoader.CompileOnCompilerThread(new CompilerFrontend(l, templatePath: null), src);
        Assert.False(l.HasErrors(), l.Report);
        Assert.NotNull(compiled);
    }

    [Fact]
    public void TheLoadersThreadStillStopsAHundredThousandLevels()
    {
        var src = "default { state_entry() { integer x = " + new string('(', Depth) + "1" + new string(')', Depth) + "; } }";
        var l = new PhloxCompiler();
        var compiled = global::Phlox.ScriptEngine.PhloxScriptLoader.CompileOnCompilerThread(new CompilerFrontend(l, templatePath: null), src);
        Assert.Null(compiled);
        Assert.Contains(l.Errors, e => e.Contains("expression nested too deeply"));
    }

    private const string Evil = "(a+)+$";
    private static readonly string Victim = new string('a', 30) + "!";

    /// <summary>Pump on another thread so a stalled scheduler fails the test instead of hanging it.</summary>
    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan budget)
    {
        var task = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.Elapsed < budget) h.PumpOnce();
        });
        return task.Wait(budget + TimeSpan.FromSeconds(3)) && done();
    }

    [Fact]
    public void ACatastrophicRegexTimesOutAndAnotherScriptsTimerKeepsFiring()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, \"tick\"); } }");
        var evil = h.RezScript("default { touch_start(integer n) { llSay(0, \"start\"); " +
                               "llSay(0, \"iw=\" + (string)iwMatchString(\"" + Victim + "\", \"" + Evil + "\", IW_MATCH_REGEX)); " +
                               "llSay(0, \"os=\" + (string)llGetListLength(osMatchString(\"" + Victim + "\", \"" + Evil + "\", 0))); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.Said.Contains("tick") && h.RunStateOf(evil) == "Waiting");
        Assert.Contains("tick", h.Said);
        h.ClearSaid(evil);

        var sw = Stopwatch.StartNew();
        h.PostTouch(evil);
        bool finished = PumpUntil(h, () => h.Said.Any(s => s.StartsWith("os=")), TimeSpan.FromSeconds(5));
        var elapsed = sw.Elapsed;
        _out.WriteLine($"elapsed={elapsed.TotalMilliseconds:F0}ms said=[{string.Join(" | ", h.Said)}] debug=[{string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message))}]");
        Assert.True(finished, "the scheduler was still inside the regex after 5 s");
        Assert.Contains("iw=0", h.Said);
        Assert.Contains("os=0", h.Said);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"two regex calls took {elapsed.TotalMilliseconds:F0} ms (each must return within 1 s)");
        Assert.Contains(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("regex timed out"));

        h.ClearSaid(evil);
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.Said.Count(s => s == "tick") >= 3);
        Assert.True(h.Said.Count(s => s == "tick") >= 3, "the other script's timer stopped: [" + string.Join(" | ", h.Said) + "]");
    }

    [Fact]
    public async Task ARegexListenerThatTimesOutDoesNotStopChatForEveryoneElse()
    {
        using var h = new SchedulerHarness();
        var evil = h.RezScript("default { state_entry() { } listen(integer c, string n, key k, string m) { llSay(0, \"evil heard\"); } }");
        var listener = h.RezScript("default { state_entry() { llListen(5, \"\", NULL_KEY, \"\"); } listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + m); } }");
        h.Pump();
        h.PumpUntil(() => h.RunStateOf(evil) == "Waiting" && h.RunStateOf(listener) == "Waiting");
        h.Engine.ListenManager.Add(h.Prim.LocalId, evil, h.Prim.UUID, 5, "", UUID.Zero, Evil, 2);

        var sw = Stopwatch.StartNew();
        var deliver = Task.Run(() => h.Engine.ListenManager.DeliverChat(5, "someone", UUID.Random(), Victim));
        Assert.True(await Task.WhenAny(deliver, Task.Delay(TimeSpan.FromSeconds(5))) == deliver, "chat delivery was still inside the regex after 5 s");
        Assert.Null(deliver.Exception);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"delivery took {sw.Elapsed.TotalMilliseconds:F0} ms");
        h.Pump();
        h.PumpUntil(() => h.Said.Contains("heard " + Victim));
        Assert.Contains("heard " + Victim, h.Said);
        Assert.DoesNotContain("evil heard", h.Said);
    }
}
