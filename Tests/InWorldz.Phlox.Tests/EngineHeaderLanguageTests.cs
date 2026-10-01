using System.Collections;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The language part of Phlox's own header ("//InWorldz.Phlox:&lt;language&gt;"), read as YEngine reads its own
/// (XMREngine.OnRezScript: the text after the colon, trimmed, lowercased, ignored unless two or more characters).
/// Empty or "lsl" is LSL; "slua" sends the script to Phlox and compiles the rest as SLua, so SLua runs on a region where
/// YEngine is the default; anything else is a compile error naming the header, in the editor, with no instance.
/// SLua source starts with "--", never "//", so without the header the rule gives it to the default engine.
/// Rezzes go through the region's own path. In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class EngineHeaderLanguageTests
{
    private readonly ITestOutputHelper _out;
    public EngineHeaderLanguageTests(ITestOutputHelper o) => _out = o;

    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    private static TaskInventoryItem Add(SchedulerHarness h, string source)
        => TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(),
            "lang" + Guid.NewGuid().ToString("N")[..6], source);

    /// <summary>
    /// The editor's first Save of a script, as the region does it (CreateScriptInstanceEr with the region's default
    /// engine on the caps thread, answered with every engine's GetScriptErrors), then resumed.
    /// </summary>
    private static (UUID item, ArrayList errors) Save(SchedulerHarness h, string source, string defaultEngine)
    {
        var item = Add(h, source);
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item.ItemID, 0, false, defaultEngine, 1));
        Assert.True(h.PumpUntil(() => save.IsCompleted, TimeSpan.FromSeconds(30)), "the Save did not return");
        h.Prim.ParentGroup.ResumeScripts();
        return (item.ItemID, save.Result);
    }

    private static int Count(SchedulerHarness h, string marker) => h.Said.Count(s => s == marker);

    private void SaidExactlyOnce(SchedulerHarness h, string marker)
    {
        Assert.True(h.PumpUntil(() => Count(h, marker) >= 1, Cap), $"nobody said '{marker}': [{string.Join(" | ", h.Said)}]");
        h.PumpFor(TimeSpan.FromSeconds(1));   // a second instance would say it again
        Assert.Equal(1, Count(h, marker));
    }

    private static void InPhloxOnly(SchedulerHarness h, UUID item)
    {
        Assert.True(h.Engine.HasScript(item, out _));
        Assert.False(h.YEngine.HasScript(item, out _));
    }

    private static string Lua(string marker) => $"ll.Say(0, \"{marker}\")\n";

    [Theory]
    [InlineData(YEngine)]
    [InlineData(Phlox)]
    public void TheSluaHeaderRunsAsSluaInPhloxOnly(string defaultEngine)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (item, errors) = Save(h, "//InWorldz.Phlox:slua\n" + Lua("slua on " + defaultEngine), defaultEngine);
        _out.WriteLine($"[{string.Join(" | ", errors.Cast<object>())}]");
        Assert.Empty(errors);
        SaidExactlyOnce(h, "slua on " + defaultEngine);
        InPhloxOnly(h, item);
    }

    /// <summary>The language part as YEngine reads it: trimmed, any case, a trailing \r and blanks dropped.</summary>
    [Theory]
    [InlineData("//InWorldz.Phlox: SLua \r\n")]
    [InlineData("//InWorldz.Phlox:SLUA\n")]
    [InlineData("// InWorldz.Phlox : slua\n")]
    public void TheSluaHeaderIsReadAsYEngineReadsItsOwn(string header)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (item, errors) = Save(h, header + "--!slua\n" + Lua("variant"), YEngine);
        Assert.Empty(errors);
        SaidExactlyOnce(h, "variant");
        InPhloxOnly(h, item);
    }

    [Theory]
    [InlineData("//InWorldz.Phlox:\n")]
    [InlineData("//InWorldz.Phlox:lsl\n")]
    [InlineData("//InWorldz.Phlox: LSL\n")]
    [InlineData("//InWorldz.Phlox:x\n")]   // one character: YEngine ignores a language part this short
    public void TheEmptyAndLslHeadersStayLsl(string header)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (item, errors) = Save(h, header + "default { state_entry() { llSay(0, \"lsl\"); } }", YEngine);
        Assert.Empty(errors);
        SaidExactlyOnce(h, "lsl");
        InPhloxOnly(h, item);
    }

    [Theory]
    [InlineData(YEngine)]
    [InlineData(Phlox)]
    public void AnUnknownLanguageIsAnEditorErrorAndRunsNowhere(string defaultEngine)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (item, errors) = Save(h, "//InWorldz.Phlox:python\ndefault { state_entry() { llSay(0, \"ran\"); } }", defaultEngine);
        _out.WriteLine($"[{string.Join(" | ", errors.Cast<object>())}]");
        string only = Assert.Single(errors.Cast<string>());
        Assert.StartsWith("(1,0) Error: ", only);
        Assert.Contains("//InWorldz.Phlox:python", only);
        h.PumpFor(TimeSpan.FromSeconds(1));   // nothing may start it
        Assert.Equal(0, Count(h, "ran"));
        Assert.False(h.Engine.HasScript(item, out _));
        Assert.Null(h.InterpreterFor(item));
        Assert.False(h.YEngine.HasScript(item, out _));
    }

    [Fact]
    public void AnSluaErrorLineCountsTheHeaderLine()
    {
        const string body = "--!slua\nlocal x = (1\nll.Say(0, tostring(x))\n";
        using var h = new SchedulerHarness(withYEngine: true);
        var (_, bare) = Save(h, body, Phlox);   // no header: today's detection
        var (item, headed) = Save(h, "//InWorldz.Phlox:slua\n" + body, YEngine);
        _out.WriteLine($"bare [{string.Join(" | ", bare.Cast<object>())}] headed [{string.Join(" | ", headed.Cast<object>())}]");
        int Line(ArrayList e) => int.Parse(System.Text.RegularExpressions.Regex.Match((string)e[0]!, @"^\((\d+),0\) Error: ").Groups[1].Value);
        Assert.NotEmpty(bare);
        Assert.NotEmpty(headed);
        Assert.Equal(Line(bare) + 1, Line(headed));
        Assert.Null(h.InterpreterFor(item));
    }

    [Fact]
    public void SluaWithNoHeaderOnAPhloxDefaultRegionIsUnchanged()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (item, errors) = Save(h, "--!slua\n" + Lua("no header"), Phlox);
        Assert.Empty(errors);
        SaidExactlyOnce(h, "no header");
        InPhloxOnly(h, item);
    }
}
