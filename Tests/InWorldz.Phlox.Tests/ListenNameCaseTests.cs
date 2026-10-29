using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An llListen name filter matches the speaker's name exactly, case included: the SL wiki's
/// llListen page says the speaker's legacy name "must match name exactly (case sensitive)", and
/// Halcyon's and YEngine's listener managers compare case-sensitively too. An empty name still hears
/// every speaker. The key and message filters are untouched, and an osListenRegex name pattern
/// stays a .NET regular expression without options: case-sensitive unless the pattern says (?i).
/// </summary>
// Not in "phlox-state": each test has its own harness and touches no process-wide state, so the class runs in parallel.
public class ListenNameCaseTests
{
    private readonly ITestOutputHelper _out;
    public ListenNameCaseTests(ITestOutputHelper o) => _out = o;

    private const int Sentinel = 9;
    private const string SpeakerName = "Test Speaker";
    private const string FilterKey = "a2e76fcd-9360-4f6d-a924-000000000005";

    // Every listen reports what it heard; channel 9 has no filter and marks the end of a batch.
    private const string Script =
        "default { state_entry() { " +
        "llListen(1, \"" + SpeakerName + "\", NULL_KEY, \"\"); " +
        "llListen(2, \"\", NULL_KEY, \"\"); " +
        "llListen(3, \"\", \"" + FilterKey + "\", \"\"); " +
        "llListen(4, \"\", NULL_KEY, \"Hello\"); " +
        "llListen(9, \"\", NULL_KEY, \"\"); " +
        "llSay(0, \"ready\"); } " +
        "listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + (string)c + \"|\" + n + \"|\" + m); } }";

    private const string RegexScript =
        "default { state_entry() { " +
        "osListenRegex(5, \"^Test\", NULL_KEY, \"\", OS_LISTEN_REGEX_NAME); " +
        "osListenRegex(6, \"^test\", NULL_KEY, \"\", OS_LISTEN_REGEX_NAME); " +
        "osListenRegex(7, \"(?i)^test\", NULL_KEY, \"\", OS_LISTEN_REGEX_NAME); " +
        "llListen(9, \"\", NULL_KEY, \"\"); " +
        "llSay(0, \"ready\"); } " +
        "listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + (string)c + \"|\" + n + \"|\" + m); } }";

    private static SchedulerHarness Start(string script, bool ossl = false)
    {
        var h = ossl
            ? new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"))
            : new SchedulerHarness();
        h.RezScript(script);
        WaitFor(h, "ready");
        return h;
    }

    private static void WaitFor(SchedulerHarness h, string line, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !h.Said.Contains(line)) h.PumpOnce();
        Assert.True(h.Said.Contains(line), $"no '{line}' line: [{string.Join(" | ", h.Said)}]");
    }

    /// <summary>
    /// Delivers the lines, then one on the sentinel channel, and waits for the sentinel: a script's events
    /// run in order, so once it is heard every earlier line has been delivered or dropped.
    /// </summary>
    private List<string> Deliver(SchedulerHarness h, params (int Channel, string Name, UUID Key, string Message)[] lines)
    {
        foreach (var l in lines)
            h.Engine.ListenManager.DeliverChat(l.Channel, l.Name, l.Key, l.Message);
        var end = "end" + Guid.NewGuid().ToString("N");
        h.Engine.ListenManager.DeliverChat(Sentinel, "End Marker", UUID.Random(), end);
        WaitFor(h, $"heard {Sentinel}|End Marker|{end}");
        var heard = h.Said.Where(s => s.StartsWith("heard ") && !s.StartsWith($"heard {Sentinel}|")).ToList();
        _out.WriteLine("heard: [" + string.Join(" | ", heard) + "]");
        return heard;
    }

    [Fact]
    public void AnExactNameMatches()
    {
        using var h = Start(Script);
        var heard = Deliver(h, (1, SpeakerName, UUID.Random(), "exact"));
        Assert.Equal(new[] { "heard 1|" + SpeakerName + "|exact" }, heard);
    }

    [Theory]
    [InlineData("test speaker")]
    [InlineData("TEST SPEAKER")]
    [InlineData("Test speaker")]
    public void ANameThatDiffersOnlyInCaseDoesNotMatch(string speaker)
    {
        using var h = Start(Script);
        var heard = Deliver(h, (1, speaker, UUID.Random(), "case"));
        Assert.Empty(heard);
    }

    [Fact]
    public void AnEmptyNameFilterHearsAnySpeaker()
    {
        using var h = Start(Script);
        var heard = Deliver(h,
            (2, SpeakerName, UUID.Random(), "a"),
            (2, "test speaker", UUID.Random(), "b"),
            (2, "Someone Else", UUID.Random(), "c"));
        Assert.Equal(new[] { "heard 2|" + SpeakerName + "|a", "heard 2|test speaker|b", "heard 2|Someone Else|c" }, heard);
    }

    [Fact]
    public void KeyAndMessageFiltersAreUnchanged()
    {
        using var h = Start(Script);
        var heard = Deliver(h,
            (3, "Any Name", new UUID(FilterKey), "key match"),
            (3, "Any Name", UUID.Random(), "key miss"),
            (4, "Any Name", UUID.Random(), "Hello"),
            (4, "Any Name", UUID.Random(), "hello"),
            (4, "Any Name", UUID.Random(), "Hello there"));
        Assert.Equal(new[] { "heard 3|Any Name|key match", "heard 4|Any Name|Hello" }, heard);
    }

    [Fact]
    public void AnOsListenRegexNamePatternIsCaseSensitiveUnlessItSaysOtherwise()
    {
        using var h = Start(RegexScript, ossl: true);
        var heard = Deliver(h,
            (5, SpeakerName, UUID.Random(), "a"),
            (6, SpeakerName, UUID.Random(), "b"),
            (7, SpeakerName, UUID.Random(), "c"));
        Assert.Equal(new[] { "heard 5|" + SpeakerName + "|a", "heard 7|" + SpeakerName + "|c" }, heard);
    }
}
