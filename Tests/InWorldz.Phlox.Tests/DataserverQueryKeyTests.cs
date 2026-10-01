using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The key a dataserver request returns is the key its dataserver event
/// carries - that is how a script matches the answer to the question
/// (https://wiki.secondlife.com/wiki/Dataserver). Each request here is used as an expression and
/// compared with the event's key.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class DataserverQueryKeyTests
{
    private readonly ITestOutputHelper _out;
    public DataserverQueryKeyTests(ITestOutputHelper o) => _out = o;

    private string Run(Func<OpenSim.Tests.Common.TestClient, string> request)
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        h.RezScript("key q; default { state_entry() { q = " + request(client) + "; llSay(0, \"asked \" + (string)q); } " +
                    "dataserver(key id, string d) { llSay(0, \"answer match=\" + (string)(id == q) + \" data=\" + d); } }");
        // Wait for the answer (up to 30 s), not a fixed 3 s; under a full parallel run the script had not yet
        // spoken once when the window ended.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!h.Said.Any(s => s.StartsWith("answer ")) && DateTime.UtcNow < until)
            h.PumpFor(TimeSpan.FromMilliseconds(50));
        var said = string.Join(" | ", h.Said);
        _out.WriteLine(said + " || debug: " + string.Join(" | ", h.SaidOn.Where(s => s.Channel == 0x7FFFFFFF).Select(s => s.Message)));
        Assert.True(h.Said.Any(s => s.StartsWith("asked ") && s != "asked " + OpenMetaverse.UUID.Zero), "no query key came back: " + said);
        Assert.True(h.Said.Any(s => s.StartsWith("answer match=1")), "the dataserver key is not the returned key: " + said);
        return h.Said.First(s => s.StartsWith("answer match=1"));
    }

    [Fact]
    public void RequestAgentDataNameReturnsTheEventsKey()
    {
        var answer = Run(c => $"llRequestAgentData(\"{c.AgentId}\", DATA_NAME)");
        Assert.Contains("data=", answer);
    }

    [Fact]
    public void RequestAgentDataPayinfoReturnsTheEventsKeyAndAPayinfoValue()
    {
        var answer = Run(c => $"llRequestAgentData(\"{c.AgentId}\", {SlConstantsTests.Fixture.Value["DATA_PAYINFO"].Value})");
        Assert.Matches("data=[0-3]$", answer);
    }

    [Fact]
    public void RequestDisplayNameReturnsTheEventsKey() => Run(c => $"llRequestDisplayName(\"{c.AgentId}\")");

    [Fact]
    public void RequestUsernameReturnsTheEventsKey() => Run(c => $"llRequestUsername(\"{c.AgentId}\")");

    // iwAvatarName2Key is not a dataserver request: as in Halcyon it returns the avatar's key
    // directly (AvatarName2KeyTests).
}
