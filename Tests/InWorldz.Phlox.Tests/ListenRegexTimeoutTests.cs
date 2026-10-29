using System.Diagnostics;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A timed-out osListenRegex filter was made "no match", but the listener stayed
/// active, so every later line on its channel paid the 250 ms timeout again - on the chat thread,
/// for every listener behind it. A listener whose pattern times out is now switched off exactly as
/// llListenControl(handle, FALSE) would, and its owner is told once on DEBUG_CHANNEL.
/// </summary>
[Collection("phlox-state")]
public class ListenRegexTimeoutTests
{
    private readonly ITestOutputHelper _out;
    public ListenRegexTimeoutTests(ITestOutputHelper o) => _out = o;

    private const int DebugChannel = 0x7FFFFFFF;
    private const string Evil = "(a+)+$";
    private static readonly string Victim = new string('a', 30) + "!";
    private const string Notice = "osListenRegex: pattern timed out; listener disabled";

    [Fact]
    public async Task ATimedOutListenRegexDisablesItsListenerAndCostsOneTimeout()
    {
        using var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));
        var s1 = h.RezScript("default { state_entry() { llSay(0, \"h=\" + (string)osListenRegex(5, \"\", NULL_KEY, \"" + Evil + "\", OS_LISTEN_REGEX_MESSAGE)); } " +
                             "listen(integer c, string n, key k, string m) { llSay(0, \"s1 heard\"); } }");
        h.RezScript("default { state_entry() { llListen(5, \"someone\", NULL_KEY, \"\"); llSay(0, \"s2 ready\"); } " +
                    "listen(integer c, string n, key k, string m) { llSay(0, \"s2 heard \" + m); } }");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !(h.Said.Any(s => s.StartsWith("h=")) && h.Said.Contains("s2 ready"))) h.PumpOnce();
        var handleLine = h.Said.FirstOrDefault(s => s.StartsWith("h="));
        Assert.True(handleLine != null && handleLine != "h=-1" && handleLine != "h=0", "osListenRegex did not register: [" + string.Join(" | ", h.Said) + "]");
        int handle = int.Parse(handleLine!.Substring(2));

        var speaker = UUID.Random();
        var perLine = new double[20];
        var total = Stopwatch.StartNew();
        var deliver = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                h.Engine.ListenManager.DeliverChat(5, "someone", speaker, Victim + i);
                perLine[i] = sw.Elapsed.TotalMilliseconds;
            }
        });
        Assert.True(await Task.WhenAny(deliver, Task.Delay(TimeSpan.FromSeconds(30))) == deliver, "delivery of 20 lines did not finish in 30 s");
        await deliver;   // surfaces a delivery exception, as Wait did
        total.Stop();
        _out.WriteLine($"total={total.Elapsed.TotalMilliseconds:F0} ms per line=[{string.Join(", ", perLine.Select(t => t.ToString("F0")))}]");

        until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && h.Said.Count(s => s.StartsWith("s2 heard ")) < 20) h.PumpOnce();
        h.Pump(20);
        var debug = h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message).ToList();
        _out.WriteLine($"s2 heard={h.Said.Count(s => s.StartsWith("s2 heard "))} debug=[{string.Join(" | ", debug)}]");

        var timeout = global::Phlox.ScriptEngine.ScriptRegex.MatchTimeout.TotalMilliseconds;
        Assert.True(perLine[0] <= timeout + 500, $"the first line cost {perLine[0]:F0} ms (one timeout is {timeout} ms)");
        Assert.True(total.Elapsed.TotalMilliseconds < timeout + 500, $"20 lines took {total.Elapsed.TotalMilliseconds:F0} ms; a timed-out listener is paying its timeout on every line");
        Assert.Equal(20, h.Said.Count(s => s.StartsWith("s2 heard ")));
        Assert.DoesNotContain("s1 heard", h.Said);
        Assert.True(h.Engine.ListenManager.IsActive(s1, handle) == false, "script 1's listener is still active");
        Assert.Single(debug, d => d.Contains(Notice));
    }
}
