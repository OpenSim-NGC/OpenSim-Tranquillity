using System.Reflection;
using Nini.Config;
using OpenSim.Region.CoreModules.Scripting.WorldComm;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Phlox's listen caps come from [LL-Functions] max_listens_per_script and max_listens_per_region, read
/// the way the core WorldCommModule reads them for YEngine (WorldCommModule.Initialise: one GetInt each with defaults
/// 65 and 1000; a value below 1 means no limit; a region cap below the script cap is raised to it), so one config value
/// means the same thing to both engines. With no key the script cap stays 65, as before. A value that is not an
/// integer keeps the default, as WorldComm does, and Phlox logs one warning.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class ListenCapConfigTests
{
    private readonly ITestOutputHelper _out;
    public ListenCapConfigTests(ITestOutputHelper o) => _out = o;

    private const int Report = 99;

    private static Action<IConfigSource> LlFunctions(string perScript = null, string perRegion = null) => cfg =>
    {
        var ll = cfg.AddConfig("LL-Functions");
        if (perScript is not null) ll.Set("max_listens_per_script", perScript);
        if (perRegion is not null) ll.Set("max_listens_per_region", perRegion);
    };

    private static string Fill(int channelBase, int count)
        => $"for (i = 1; i <= {count}; ++i) {{ h = llListen({channelBase} + i, \"\", NULL_KEY, \"\"); if (h <= 0) ++bad; }} ";

    /// <summary>A script that opens <paramref name="count"/> listens, then one more, and reports "tag bad=.. over=..".</summary>
    private static string FillAndOneMore(string tag, int count)
        => "default { state_entry() { integer i; integer h; integer bad = 0; " + Fill(1000, count) +
           "integer over = llListen(99000, \"\", NULL_KEY, \"\"); " +
           $"llSay({Report}, \"{tag} bad=\" + (string)bad + \" over=\" + (string)over); }} }}";

    private static string WaitLine(SchedulerHarness h, string prefix, double seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !h.Said.Any(s => s.StartsWith(prefix))) h.PumpOnce();
        var line = h.Said.FirstOrDefault(s => s.StartsWith(prefix));
        Assert.True(line != null, $"no '{prefix}' line: [{string.Join(" | ", h.Said)}]");
        return line!;
    }

    // By reflection, so this file builds against an engine without these settings and the red run shows what is missing.
    private static int Prop(SchedulerHarness h, string name)
    {
        object mgr = h.Engine.ListenManager;
        var p = mgr.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p != null, $"PhloxListenManager has no property {name}");
        return (int)p!.GetValue(mgr)!;
    }

    private static (int Script, int Region, string Warning) ReadCaps(IConfigSource config)
    {
        var m = typeof(PhloxListenManager).GetMethod("ReadListenCaps", BindingFlags.Public | BindingFlags.Static);
        Assert.True(m != null, "PhloxListenManager has no ReadListenCaps");
        var args = new object[] { config, null };
        var (script, region) = ((int, int))m!.Invoke(null, args)!;
        return (script, region, (string)args[1]);
    }

    private static Dictionary<string, int> Fields(string line)
        => line.Split(' ').Where(p => p.Contains('=')).ToDictionary(p => p.Split('=')[0], p => int.Parse(p.Split('=')[1]));

    // ── Through llListen ─────────────────────────────────────────────────────

    [Fact]
    public void WithNoKeyAScriptHolds65ListensAndThe66thReturnsMinusOne()
    {
        using var h = new SchedulerHarness(LlFunctions());
        h.RezScript(FillAndOneMore("cap", 65));
        var f = Fields(WaitLine(h, "cap "));
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
        Assert.Equal(65, Prop(h, "MaxListensPerScript"));
    }

    [Fact]
    public void AConfigured64GivesAScript64ListensAndThe65thReturnsMinusOne()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "64"));
        h.RezScript(FillAndOneMore("cap", 64));
        var f = Fields(WaitLine(h, "cap "));
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
    }

    [Fact]
    public void AConfiguredZeroMeansNoLimitAsWorldCommReadsIt()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "0"));
        h.RezScript(FillAndOneMore("cap", 200));
        var f = Fields(WaitLine(h, "cap "));
        Assert.Equal(0, f["bad"]);
        Assert.True(f["over"] > 0, "the 201st listen was refused with no limit configured");
        Assert.Equal(int.MaxValue, Prop(h, "MaxListensPerScript"));
    }

    [Fact]
    public void AValueThatIsNotAnIntegerKeeps65()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "sixty"));
        h.RezScript(FillAndOneMore("cap", 65));
        var f = Fields(WaitLine(h, "cap "));
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
    }

    [Fact]
    public void AnIdenticalListenStillReturnsItsHandleAtAConfiguredCap()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "64"));
        h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; " +
                    "integer a = llListen(5, \"n\", NULL_KEY, \"m\"); " + Fill(1000, 63) +
                    "integer again = llListen(5, \"n\", NULL_KEY, \"m\"); " +
                    "integer over = llListen(99000, \"\", NULL_KEY, \"\"); " +
                    $"llSay({Report}, \"reuse a=\" + (string)a + \" again=\" + (string)again + \" bad=\" + (string)bad + \" over=\" + (string)over); }} }}");
        var f = Fields(WaitLine(h, "reuse "));
        Assert.True(f["a"] > 0);
        Assert.Equal(f["a"], f["again"]);
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
    }

    [Fact]
    public void ThePerRegionCapCountsEveryScriptsListens()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "5", perRegion: "8"));
        h.RezScript(FillAndOneMore("first", 5));
        var a = Fields(WaitLine(h, "first "));
        Assert.Equal(0, a["bad"]);
        Assert.Equal(-1, a["over"]);     // its own cap of 5

        h.RezScript(FillAndOneMore("second", 3));
        var b = Fields(WaitLine(h, "second "));
        Assert.Equal(0, b["bad"]);       // 5 + 3 = the region's 8
        Assert.Equal(-1, b["over"]);     // the region is full though this script holds only 3
        Assert.Equal(8, Prop(h, "ListenCount"));
    }

    [Fact]
    public void AListenRemovedFreesARegionSlot()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "3", perRegion: "3"));
        h.RezScript("default { state_entry() { integer i; integer h; integer bad = 0; " + Fill(1000, 3) +
                    "llListenRemove(h); integer again = llListen(98000, \"\", NULL_KEY, \"\"); " +
                    $"llSay({Report}, \"freed bad=\" + (string)bad + \" again=\" + (string)again); }} }}");
        var f = Fields(WaitLine(h, "freed "));
        Assert.Equal(0, f["bad"]);
        Assert.True(f["again"] > 0, "a removed listen did not free its region slot");
    }

    [Fact]
    public void ARegionCapBelowTheScriptCapIsRaisedToIt()
    {
        using var h = new SchedulerHarness(LlFunctions(perScript: "6", perRegion: "2"));
        h.RezScript(FillAndOneMore("raised", 6));
        var f = Fields(WaitLine(h, "raised "));
        Assert.Equal(0, f["bad"]);
        Assert.Equal(-1, f["over"]);
        Assert.Equal(6, Prop(h, "MaxListensPerRegion"));
    }

    // ── The read itself, against WorldCommModule on the same config ─────────

    public static IEnumerable<object[]> Configs() => new[]
    {
        new object[] { null, null },
        new object[] { "64", null },
        new object[] { "0", null },
        new object[] { "-3", null },
        new object[] { null, "0" },
        new object[] { "20", "10" },
        new object[] { "7", "4000" },
        new object[] { "sixty", null },
        new object[] { null, "lots" },
        new object[] { "64", "lots" },
    };

    [Theory]
    [MemberData(nameof(Configs))]
    public void PhloxReadsTheCapsAsWorldCommModuleDoes(string perScript, string perRegion)
    {
        var config = new IniConfigSource();
        config.AddConfig("Chat");   // WorldComm reads [Chat] first in the same try
        LlFunctions(perScript, perRegion)(config);

        var wc = new WorldCommModule();
        wc.Initialise(config);
        int wcScript = (int)typeof(WorldCommModule).GetField("m_maxhandles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wc)!;
        int wcRegion = (int)typeof(WorldCommModule).GetField("m_maxlisteners", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wc)!;

        var (script, region, warning) = ReadCaps(config);
        _out.WriteLine($"script={perScript ?? "(none)"} region={perRegion ?? "(none)"} -> phlox {script}/{region}, worldcomm {wcScript}/{wcRegion}, warning: {warning ?? "(none)"}");
        Assert.Equal(wcScript, script);
        Assert.Equal(wcRegion, region);

        bool bad = (perScript is not null && !int.TryParse(perScript, out _)) || (perRegion is not null && !int.TryParse(perRegion, out _));
        if (bad) Assert.Contains("max_listens_per_", warning);
        else Assert.Null(warning);
    }

    [Fact]
    public void WithNoLlFunctionsSectionTheCapsAre65And1000WithNoWarning()
    {
        var (script, region, warning) = ReadCaps(new IniConfigSource());
        Assert.Equal(65, script);
        Assert.Equal(1000, region);
        Assert.Null(warning);
    }
}
