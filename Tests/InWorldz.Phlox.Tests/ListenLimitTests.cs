using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script holds at most 65 listens (SL's limit; Halcyon's ListenerManager allowed 64 handles per script).
/// As in Halcyon's ListenerManager.AddListener, one over the limit returns -1 with no script error, and an
/// llListen whose channel, name, key and message are those of an active listen the script already holds
/// returns that listen's handle and takes no slot; a listen switched off with llListenControl is not reused.
/// llListenRemove frees its slot, and a reset, a state change and unloading free them all, as Halcyon's
/// UnregisterScriptFromNotifications does. llRegionSayTo on DEBUG_CHANNEL is refused, with Halcyon's error
/// text, and reaches no listener.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class ListenLimitTests
{
    private readonly ITestOutputHelper _out;
    public ListenLimitTests(ITestOutputHelper o) => _out = o;

    private const int DebugChannel = 0x7FFFFFFF;
    private const int Report = 99;
    private const string SomeKey = "a2e76fcd-9360-4f6d-a924-000000000003";
    private const string OtherKey = "a2e76fcd-9360-4f6d-a924-000000000004";

    /// <summary>LSL that opens <paramref name="count"/> listens on channels base+1.. and counts the handles that are not positive into <c>bad</c>.</summary>
    private static string Fill(int channelBase, int count)
        => $"for (i = 1; i <= {count}; ++i) {{ h = llListen({channelBase} + i, \"\", NULL_KEY, \"\"); if (h <= 0) ++bad; }} ";

    private static string WaitLine(SchedulerHarness h, string prefix, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !h.Said.Any(s => s.StartsWith(prefix))) h.PumpOnce();
        var line = h.Said.FirstOrDefault(s => s.StartsWith(prefix));
        Assert.True(line != null, $"no '{prefix}' line: [{string.Join(" | ", h.Said)}]");
        return line!;
    }

    private static Dictionary<string, int> Fields(string line)
        => line.Split(' ').Where(p => p.Contains('=')).ToDictionary(p => p.Split('=')[0], p => int.Parse(p.Split('=')[1]));

    private string[] DebugLines(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message).ToArray();

    // ── The cap ─────────────────────────────────────────────────────────────

    [Fact]
    public void SixtyFiveListensSucceedAndTheSixtySixthReturnsMinusOneWithNoError()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; " + Fill(1000, 65) +
                    "integer over = llListen(2000, \"\", NULL_KEY, \"\"); " +
                    $"llSay({Report}, \"cap bad=\" + (string)bad + \" over=\" + (string)over); }} }}");
        var f = Fields(WaitLine(h, "cap "));
        h.Pump(20);
        _out.WriteLine(string.Join("\n", h.Said));
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
        Assert.Empty(DebugLines(h));
    }

    // ── Reuse of an identical listen ───────────────────────────────────────

    [Fact]
    public void AnIdenticalListenReturnsTheSameHandleAndTakesNoSlot()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; " +
                    $"integer a = llListen(5, \"n\", \"{SomeKey}\", \"m\"); integer b = llListen(5, \"n\", \"{SomeKey}\", \"m\"); " +
                    Fill(1000, 64) +
                    "integer over = llListen(2000, \"\", NULL_KEY, \"\"); " +
                    "integer again = llListen(5, \"n\", \"" + SomeKey + "\", \"m\"); " +
                    $"llSay({Report}, \"same a=\" + (string)a + \" b=\" + (string)b + \" again=\" + (string)again + \" bad=\" + (string)bad + \" over=\" + (string)over); }} }}");
        var f = Fields(WaitLine(h, "same "));
        Assert.True(f["a"] > 0, "the first llListen failed");
        Assert.Equal(f["a"], f["b"]);
        Assert.Equal(0, f["bad"]);           // 1 + 64 = 65 slots
        Assert.Equal(-1, f["over"]);
        Assert.Equal(f["a"], f["again"]);    // at the cap an identical listen still returns its handle
    }

    [Theory]
    [InlineData("6, \"n\", \"" + SomeKey + "\", \"m\"", "channel")]
    [InlineData("5, \"o\", \"" + SomeKey + "\", \"m\"", "name")]
    [InlineData("5, \"N\", \"" + SomeKey + "\", \"m\"", "name case")]
    [InlineData("5, \"n\", \"" + OtherKey + "\", \"m\"", "key")]
    [InlineData("5, \"n\", NULL_KEY, \"m\"", "key wildcard")]
    [InlineData("5, \"n\", \"" + SomeKey + "\", \"x\"", "message")]
    [InlineData("5, \"\", \"" + SomeKey + "\", \"m\"", "name wildcard")]
    public void AListenThatDiffersInOneFilterGetsANewHandle(string args, string field)
    {
        using var h = new SchedulerHarness();
        h.RezScript($"default {{ state_entry() {{ integer a = llListen(5, \"n\", \"{SomeKey}\", \"m\"); integer b = llListen({args}); " +
                    $"llSay({Report}, \"diff a=\" + (string)a + \" b=\" + (string)b); }} }}");
        var f = Fields(WaitLine(h, "diff "));
        Assert.True(f["a"] > 0 && f["b"] > 0, $"{field}: a listen failed");
        Assert.NotEqual(f["a"], f["b"]);
    }

    [Fact]
    public void AWildcardListenIsNotReusedForANarrowerOne()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer a = llListen(5, \"\", NULL_KEY, \"\"); integer b = llListen(5, \"n\", NULL_KEY, \"\"); " +
                    $"llSay({Report}, \"wild a=\" + (string)a + \" b=\" + (string)b); }} }}");
        var f = Fields(WaitLine(h, "wild "));
        Assert.True(f["a"] > 0 && f["b"] > 0);
        Assert.NotEqual(f["a"], f["b"]);
    }

    [Fact]
    public void AnIdenticalListenToOneSwitchedOffGetsANewHandle()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript("default { state_entry() { integer a = llListen(5, \"n\", NULL_KEY, \"\"); llListenControl(a, FALSE); " +
                               "integer b = llListen(5, \"n\", NULL_KEY, \"\"); " +
                               $"llSay({Report}, \"off a=\" + (string)a + \" b=\" + (string)b); }} }}");
        var f = Fields(WaitLine(h, "off "));
        Assert.True(f["b"] > 0);
        Assert.NotEqual(f["a"], f["b"]);
        Assert.False(h.Engine.ListenManager.IsActive(item, f["a"]));
        Assert.True(h.Engine.ListenManager.IsActive(item, f["b"]));
    }

    [Fact]
    public void ASwitchedOffListenStillTakesASlot()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; integer a = llListen(5, \"\", NULL_KEY, \"\"); llListenControl(a, FALSE); " +
                    Fill(1000, 64) + "integer over = llListen(2000, \"\", NULL_KEY, \"\"); " +
                    $"llSay({Report}, \"offslot bad=\" + (string)bad + \" over=\" + (string)over); }} }}");
        var f = Fields(WaitLine(h, "offslot "));
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
    }

    // ── Freeing slots ──────────────────────────────────────────────────────

    [Fact]
    public void ListenRemoveFreesOneSlot()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; integer first = llListen(1000, \"\", NULL_KEY, \"\"); " +
                    Fill(1000, 64) + "llListenRemove(first); " +
                    "integer next = llListen(3000, \"\", NULL_KEY, \"\"); integer over = llListen(3001, \"\", NULL_KEY, \"\"); " +
                    $"llSay({Report}, \"rm bad=\" + (string)bad + \" next=\" + (string)next + \" over=\" + (string)over); }} }}");
        var f = Fields(WaitLine(h, "rm "));
        Assert.Equal(0, f["bad"]);
        Assert.True(f["next"] > 0, "the freed slot could not be used");
        Assert.Equal(-1, f["over"]);
    }

    [Fact]
    public void AResetFreesEverySlot()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; " +
                               "if (llGetObjectDesc() != \"reset\") { llSetObjectDesc(\"reset\"); integer first = llListen(900, \"\", NULL_KEY, \"\"); " +
                               Fill(1000, 64) + $"llSay({Report}, \"before first=\" + (string)first + \" bad=\" + (string)bad); llResetScript(); }} " +
                               "else { " + Fill(3000, 65) + "integer over = llListen(4000, \"\", NULL_KEY, \"\"); " +
                               $"llSay({Report}, \"after bad=\" + (string)bad + \" over=\" + (string)over); }} }} }}");
        var before = Fields(WaitLine(h, "before "));
        var after = Fields(WaitLine(h, "after "));
        Assert.Equal(0, before["bad"]);
        Assert.Equal(0, after["bad"]);
        Assert.Equal(-1, after["over"]);
        // Handles are the script's own and the lowest free comes first, so the reset's listens reuse the old numbers;
        // the listen the first one named (channel 900) is gone.
        Assert.Equal(0, h.Engine.ListenManager.ListensOnChannel(item, 900));
    }

    [Fact]
    public void AStateChangeFreesEverySlot()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; integer first = llListen(900, \"\", NULL_KEY, \"\"); " +
                               Fill(1000, 64) + $"llSay({Report}, \"before first=\" + (string)first + \" bad=\" + (string)bad); state two; }} }} " +
                               "state two { state_entry() { integer i; integer h; integer bad = 0; " + Fill(3000, 65) +
                               "integer over = llListen(4000, \"\", NULL_KEY, \"\"); " +
                               $"llSay({Report}, \"after bad=\" + (string)bad + \" over=\" + (string)over); }} }}");
        var before = Fields(WaitLine(h, "before "));
        var after = Fields(WaitLine(h, "after "));
        Assert.Equal(0, before["bad"]);
        Assert.Equal(0, after["bad"]);
        Assert.Equal(-1, after["over"]);
        // The new state's listens reuse the handle numbers; the listen the first one named (channel 900) is gone.
        Assert.Equal(0, h.Engine.ListenManager.ListensOnChannel(item, 900));
    }

    [Fact]
    public void UnloadingTheScriptFreesItsListens()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; integer first = llListen(900, \"\", NULL_KEY, \"\"); " +
                               Fill(1000, 64) + $"llSay({Report}, \"loaded first=\" + (string)first + \" last=\" + (string)h); }} }}");
        var f = Fields(WaitLine(h, "loaded "));
        Assert.True(h.Engine.ListenManager.IsActive(item, f["first"]));
        h.Scene.EventManager.TriggerRemoveScript(h.Prim.LocalId, item);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && h.Engine.ListenManager.IsActive(item, f["first"]) != null) h.PumpOnce();
        Assert.Null(h.Engine.ListenManager.IsActive(item, f["first"]));
        Assert.Null(h.Engine.ListenManager.IsActive(item, f["last"]));
    }

    [Fact]
    public void EachScriptHasItsOwnSixtyFive()
    {
        using var h = new SchedulerHarness();
        string src(string tag) => "default { state_entry() { integer i; integer h; integer bad = 0; " + Fill(1000, 65) +
                                  "integer over = llListen(2000, \"\", NULL_KEY, \"\"); " +
                                  $"llSay({Report}, \"{tag} bad=\" + (string)bad + \" over=\" + (string)over); }} }}";
        h.RezScript(src("one"));
        h.RezScript(src("two"));
        foreach (var tag in new[] { "one ", "two " })
        {
            var f = Fields(WaitLine(h, tag));
            Assert.Equal(0, f["bad"]);
            Assert.Equal(-1, f["over"]);
        }
    }

    // ── llRegionSayTo on DEBUG_CHANNEL ─────────────────────────────────────

    [Fact]
    public void RegionSayToOnDebugChannelIsRefusedAndReachesNoListener()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "target", UUID.Random());
        target.AbsolutePosition = new Vector3(240, 240, 30);
        h.RezScriptInto(target.RootPart, "default { state_entry() { llListen(DEBUG_CHANNEL, \"\", NULL_KEY, \"\"); llListen(7, \"\", NULL_KEY, \"\"); " +
                                         $"llSay({Report}, \"target ready\"); }} listen(integer c, string n, key k, string m) {{ llSay({Report}, \"target heard \" + (string)c + \":\" + m); }} }}");
        WaitLine(h, "target ready");

        var sender = SceneHelpers.AddSceneObject(h.Scene, "sender", UUID.Random());
        sender.AbsolutePosition = new Vector3(10, 10, 30);
        h.RezScriptInto(sender.RootPart, $"default {{ state_entry() {{ llRegionSayTo(\"{target.RootPart.UUID}\", DEBUG_CHANNEL, \"secret\"); " +
                                         $"llRegionSayTo(\"{target.RootPart.UUID}\", 7, \"control\"); }} }}");
        WaitLine(h, "target heard 7:control");
        h.PumpFor(TimeSpan.FromMilliseconds(400));
        h.PumpUntil(() => DebugLines(h).Contains("Script error: Cannot use llRegionSayTo() on DEBUG_CHANNEL."));
        _out.WriteLine(string.Join("\n", h.SaidOn.Select(s => s.Channel + ": " + s.Message)));

        Assert.DoesNotContain(h.Said, s => s.StartsWith("target heard " + DebugChannel));
        Assert.Contains("Script error: Cannot use llRegionSayTo() on DEBUG_CHANNEL.", DebugLines(h));
    }
}
