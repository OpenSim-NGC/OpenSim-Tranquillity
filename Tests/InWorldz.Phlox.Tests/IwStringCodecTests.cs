using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The parts of iwStringCodec that lean on .NET Framework behaviour .NET 10 changed, each
/// against Framework 4.8.1's own answers (Golden/README.md), and what a script sees. No clock, no process-wide state:
/// runs in parallel.
/// </summary>
public class IwStringCodecTests
{
    private readonly ITestOutputHelper _out;
    public IwStringCodecTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    // ── what a script sees ──────────────────────────────────────────────────────────────────

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!h.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
        }
        return true;
    }

    [Fact]
    public void AScriptGetsHalcyonsResults()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default { state_entry() {
            llSay(0, iwStringCodec(""Hello, World!"", ""md5"", 1, []));
            llSay(0, iwStringCodec(""Hello, World!"", ""base64"", 1, []));
            llSay(0, iwStringCodec(""Hello, World!"", ""gzip"", 1, [""output codec"", ""base64""]));
            string k = iwStringCodec(""password"", ""aes-key"", 1, [""salt"", ""saltsalt""]);
            llSay(0, k);
            string c = iwStringCodec(""Hello, World!"", ""aes"", 1, [""key"", k, ""vector"", ""000102030405060708090a0b0c0d0e0f"", ""output codec"", ""base64""]);
            llSay(0, c);
            llSay(0, iwStringCodec(c, ""aes"", 0, [""key"", k, ""vector"", ""000102030405060708090a0b0c0d0e0f"", ""output codec"", ""base64""]));
            llSay(0, iwStringCodec(iwStringCodec(""héllo 日本"", ""base4096"", 1, []), ""base4096"", 0, []));
            llSay(0, ""done"");
        } }");
        Assert.True(PumpUntil(h, () => h.Said.Contains("done")), "said: " + string.Join(" | ", h.Said));
        Assert.Equal(new[]
        {
            "65a8e27d8879283831b664bd8b7f0ad4",
            "SABlAGwAbABvACwAIABXAG8AcgBsAGQAIQA=",
            "H4sIAAAAAAAEAPNgSGXIAcJ8Bh0GBYZwIF0E5KUwKDIAALi+bfgaAAAA",
            "465a78ac43f54af013fbb9a7f6bd150a402367233e65d4df971ffbf93a4bd125",
            "Q493pJjKRfuQ9ngQNOvJwa5qLSHYF+ET9Qb4qWtJhS8=",
            "Hello, World!",
            "héllo 日本",
            "done",
        }, h.Said.ToArray());
    }

    /// <summary>Halcyon's LSLError: the error on DEBUG_CHANNEL, "" returned, and the script goes on.</summary>
    [Fact]
    public void AnErrorIsReportedAndTheScriptGoesOn()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default { state_entry() {
            llSay(0, ""["" + iwStringCodec(""x"", ""rot13"", 1, []) + ""]"");
            llSay(0, ""["" + iwStringCodec(""x"", ""aes"", 1, [""key"", ""00""]) + ""]"");
            llSay(0, ""after"");
        } }");
        Assert.True(PumpUntil(h, () => h.Said.Contains("after")), "said: " + string.Join(" | ", h.Said));
        Assert.Equal(new[] { "[]", "[]", "after" }, h.Said.Where(s => !s.StartsWith("Script error")).ToArray());
        var debug = h.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToArray();
        Assert.Equal(new[]
        {
            "Script error: LSL Runtime Error: Error: \"rot13\" is not a valid codec for iwStringCodec!",
            "Script error: LSL Runtime Error: Error: some parameters for AES encryption are blank or missing!",
        }, debug);
    }

    /// <summary>
    /// Bad input that Halcyon's code did not catch (here base16 text that is not hex) stops the script, as an exception
    /// out of any call does, and the owner reads .NET Framework's message, as under Halcyon.
    /// </summary>
    [Fact]
    public void UncaughtBadInputStopsTheScriptWithFrameworksMessage()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"default { state_entry() {
            llSay(0, ""before"");
            llSay(0, iwStringCodec(""zz"", ""base16"", 0, []));
            llSay(0, ""after"");
        } }");
        Assert.True(PumpUntil(h, () => h.SaidOn.Any(m => m.Channel == DEBUG_CHANNEL && m.Message.Contains("stopped"))),
            "said: " + string.Join(" | ", h.SaidOn.Select(m => m.Channel + ":" + m.Message)));
        h.Pump();
        Assert.Contains("before", h.Said);
        Assert.DoesNotContain("after", h.Said);
        Assert.Contains(h.SaidOn, m => m.Channel == DEBUG_CHANNEL && m.Message.EndsWith("stopped: Could not find any recognizable digits."));
    }

    /// <summary>
    /// A hash with an unknown output codec: Halcyon reported the error, then its Hash returned null, which the VM refuses
    /// to push ("Attempt to push null operand"), so the script stopped. Phlox's VM is Halcyon's and does the same.
    /// </summary>
    [Fact]
    public void AHashWithABadOutputCodecStopsTheScriptAsUnderHalcyon()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default { state_entry() {
            llSay(0, iwStringCodec(""abc"", ""sha256"", 1, [""output codec"", ""rot13""]));
            llSay(0, ""after"");
        } }");
        Assert.True(PumpUntil(h, () => h.SaidOn.Any(m => m.Channel == DEBUG_CHANNEL && m.Message.Contains("stopped"))),
            "said: " + string.Join(" | ", h.SaidOn.Select(m => m.Channel + ":" + m.Message)));
        h.Pump();
        Assert.DoesNotContain("after", h.Said);
        var debug = h.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();
        Assert.Equal("Script error: LSL Runtime Error: Error: invalid codec for sha256 hash: rot13", debug[0]);
        Assert.Contains(debug, m => m.Contains("stopped: Attempt to push null operand."));
    }
}
