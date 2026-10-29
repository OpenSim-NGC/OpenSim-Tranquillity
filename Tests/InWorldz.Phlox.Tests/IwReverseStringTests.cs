using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwReverseString is Halcyon's: <c>src.Length &lt;= 1</c> returns the input, otherwise
/// <c>new string(src.Reverse().ToArray())</c> - the string's UTF-16 code units in reverse order.
/// So a character above U+FFFF (a surrogate pair) comes back as its two halves swapped, and a
/// combining mark ends up in front of the character it followed. Those are pinned here too, so a
/// later "fix" to grapheme order is a deliberate change, not an accident.
/// </summary>
// Touches no process-wide state, so it runs in parallel.
public class IwReverseStringTests
{
    private readonly ITestOutputHelper _out;
    public IwReverseStringTests(ITestOutputHelper o) => _out = o;

    private List<string> Run(string body, string lastPrefix)
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { " + body + " } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith(lastPrefix)));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        return h.Said.ToList();
    }

    [Fact]
    public void AsciiStringIsReversed()
    {
        var said = Run("llSay(0, \"r=\" + iwReverseString(\"Hello, world!\"));", "r=");
        Assert.Contains("r=!dlrow ,olleH", said);
    }

    [Fact]
    public void EmptyAndOneCharacterStringsComeBackAsGiven()
    {
        var said = Run("llSay(0, \"e=[\" + iwReverseString(\"\") + \"]\"); llSay(0, \"o=[\" + iwReverseString(\"x\") + \"]\");", "o=");
        Assert.Contains("e=[]", said);
        Assert.Contains("o=[x]", said);
    }

    [Fact]
    public void NonAsciiStringIsReversed()
    {
        // Precomposed letters (U+00E9, U+00FC, U+00DF): one code unit each, so they reverse whole.
        var said = Run("llSay(0, \"r=\" + iwReverseString(\"héllo über ß\"));", "r=");
        Assert.Contains("r=ß rebü olléh", said);
    }

    [Fact]
    public void SurrogatePairsAndCombiningMarksReverseByCodeUnit()
    {
        // "a" + U+1F600 (D83D DE00): reversed by code unit it is DE00 D83D 'a'.
        // "e" + U+0301 + "x": reversed it is 'x' U+0301 'e' - the accent now precedes the 'e'.
        var said = Run(
            "string s = iwReverseString(\"a\U0001F600\"); " +
            "llSay(0, \"s=\" + (string)llStringLength(s) + \",\" + (string)iwChar2Int(s, 0) + \",\" + (string)iwChar2Int(s, 1) + \",\" + (string)iwChar2Int(s, 2)); " +
            "string c = iwReverseString(\"éx\"); " +
            "llSay(0, \"c=\" + (string)iwChar2Int(c, 0) + \",\" + (string)iwChar2Int(c, 1) + \",\" + (string)iwChar2Int(c, 2));",
            "c=");
        Assert.Contains($"s=3,{0xDE00},{0xD83D},{(int)'a'}", said);
        Assert.Contains($"c={(int)'x'},{0x0301},{(int)'e'}", said);
    }
}
