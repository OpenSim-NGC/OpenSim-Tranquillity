using Xunit;
using Xunit.Abstractions;
using OpenMetaverse;
using OpenSim.Tests.Common;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The bot functions post their own SysReturn (with the result and delay) from a finally
/// inside the body. Deferral moved their shims from the raw async delegate to RunAsync, whose completion also
/// posts a return; without the per-call SyscallContext that would be TWO returns, the second a null
/// that could land in the script's NEXT syscall. The context keeps it to one: the body's result wins,
/// and the completion posts it once, sequenced.
/// </summary>
public class SyscallSingleReturnTests
{
    private readonly ITestOutputHelper _out;
    public SyscallSingleReturnTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void ABotCallReturnsOnceAndNothingLandsInTheNextCall()
    {
        using var h = new SchedulerHarness();
        var slow = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Next", "Call", slow, "pw");
        // The next call is parked for 800 ms: long enough for any stray second return to arrive in it.
        ServiceCallDeferralTests.InstallAccountDelay(h, id => id == slow ? 800 : 0);

        h.RezScript(@"
default
{
    state_entry()
    {
        key bot = botCreateBot(""Bot"", ""One"", """", <128, 128, 25>, 0);
        llSay(0, ""bot ["" + (string)bot + ""]"");
        string n = iwGetAgentData(""" + slow + @""", DATA_NAME);
        llSay(0, ""next ["" + n + ""]"");
    }
}");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !h.Said.Any(m => m.StartsWith("next")))
            h.PumpFor(TimeSpan.FromMilliseconds(50));
        h.PumpFor(TimeSpan.FromMilliseconds(300));

        var said = h.Said.Where(m => m.StartsWith("bot") || m.StartsWith("next")).ToList();
        _out.WriteLine("said: " + string.Join(" | ", said));
        // No bot manager in the test scene: the body's own SysReturn carries NULL_KEY.
        Assert.Equal(new[] { "bot [00000000-0000-0000-0000-000000000000]", "next [Next Call]" }, said);
    }
}
