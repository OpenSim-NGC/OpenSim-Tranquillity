using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL pure-helper family, each ported from OSSL_Api.cs at its
/// own threat level. One script calls every one through the harness and says a value the test asserts
/// exactly; the Low-gated one is checked both ways.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class OsslPureHelpersTests
{
    private readonly ITestOutputHelper _out;
    public OsslPureHelpersTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private const string Script = @"default { state_entry() {
        string k = ""sekrit"";
        llSay(0, ""aes="" + (string)(osAESDecrypt(k, osAESEncrypt(k, ""hello"")) == ""hello"") + ""/"" + (string)(osAESDecryptFrom(k, osAESEncryptTo(k, ""hello"", ""iv-1234""), ""iv-1234"") == ""hello""));
        llSay(0, ""angle="" + (string)osAngleBetween(<1,0,0>, <0,1,0>));
        llSay(0, ""approx="" + (string)osApproxEquals(1.0, 1.0000001) + (string)osApproxEquals(1.0, 1.5, 0.6) + (string)osApproxEquals(1.0, 2.0));
        llSay(0, ""ode="" + (string)osCheckODE());
        llSay(0, ""fmt="" + osFormatString(""{0}-{1}"", [""a"", 2]));
        llSay(0, ""nan="" + (string)osIsNotValidNumber(1.0) + ""|uuid="" + (string)osIsUUID(NULL_KEY) + (string)osIsUUID(""x""));
        vector lv = osListAsVector([<1,2,3>], 0); rotation lr = osListAsRotation([<0,0,0,1>], 0);
        llSay(0, ""las="" + (string)osListAsFloat([1.5, ""x""], 0) + ""|"" + (string)osListAsInteger([7], 0) + ""|"" + osListAsString([""s""], 0) + ""|"" + (string)((integer)lv.x) + ""|"" + (string)((integer)lr.s) + ""|"" + (string)osListAsInteger([7], 5));
        llSay(0, ""match="" + llList2CSV(osMatchString(""hello world"", ""o"", 0)));
        llSay(0, ""minmax="" + (string)osMin(1.0, 2.0) + ""|"" + (string)osMax(1.0, 2.0));
        llSay(0, ""round="" + (string)osRound(2.5, 0) + ""|"" + (string)osRound(2.345, 2));
        llSay(0, ""sha="" + osSHA256(""abc""));
        rotation s = osSlerp(ZERO_ROTATION, ZERO_ROTATION, 0.5); llSay(0, ""slerp="" + (string)((integer)s.s));
        llSay(0, ""str="" + (string)osStringStartsWith(""Hello"", ""he"", 1) + (string)osStringEndsWith(""Hello"", ""LO"", 0)
            + ""|"" + (string)osStringIndexOf(""hello"", ""l"", 0) + ""|"" + (string)osStringIndexOf(""hello"", ""l"", 3, 2, 0)
            + ""|"" + (string)osStringLastIndexOf(""hello"", ""l"", 0) + ""|"" + (string)osStringLastIndexOf(""hello"", ""l"", 4, 5, 0)
            + ""|"" + osStringRemove(""hello"", 1, 3) + ""|"" + osStringReplace(""hello"", ""l"", ""L"") + ""|"" + osStringSubString(""hello"", 2) + ""|"" + osStringSubString(""hello"", 1, 3));
        llSay(0, ""ts="" + osUnixTimeToTimestamp(0));
        llSay(0, ""vec="" + (string)osVecDistSquare(<0,0,0>, <3,4,0>) + ""|"" + (string)osVecMagSquare(<3,4,0>));
        llSay(0, ""done"");
    } }";

    [Fact]
    public void EveryHelperDispatchesAndReturnsUpstreamsValue()
    {
        using var h = new SchedulerHarness();
        h.RezScript(Script);
        h.PumpUntil(() => h.Said.Contains("done"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message)) + "]");

        Assert.Contains("done", h.Said);
        Assert.Contains("aes=1/1", h.Said);
        Assert.Contains("angle=1.570796", h.Said);
        Assert.Contains("approx=110", h.Said);
        Assert.Contains("ode=0", h.Said);
        Assert.Contains("fmt=a-2", h.Said);
        Assert.Contains("nan=0|uuid=10", h.Said);
        Assert.Contains("las=1.500000|7|s|1|1|0", h.Said);
        Assert.Contains("match=o, 4, o, 7", h.Said);
        Assert.Contains("minmax=1.000000|2.000000", h.Said);
        Assert.Contains("round=3.000000|2.350000", h.Said);
        Assert.Contains("sha=ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h.Said);
        Assert.Contains("slerp=1", h.Said);
        Assert.Contains("str=10|2|3|3|-1|ho|heLLo|llo|ell", h.Said);
        Assert.Contains("ts=1970-01-01T00:00:00.0000000Z", h.Said);
        Assert.Contains("vec=25.000000|25.000000", h.Said);
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == DebugChannel);
    }

    [Fact]
    public void TheLowGatedRegexMatchIsDeniedByDefaultAndAllowedAtLow()
    {
        using (var h = new SchedulerHarness())
        {
            h.RezScript(@"default { state_entry() { llSay(0, ""rx="" + (string)osRegexIsMatch(""abc"", ""^a"")); } }");
            h.PumpFor(TimeSpan.FromSeconds(1));
            h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osRegexIsMatch permission denied")));
            Assert.DoesNotContain(h.Said, s => s.StartsWith("rx="));
            Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osRegexIsMatch permission denied"));
        }
        using (var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Low")))
        {
            h.RezScript(@"default { state_entry() { llSay(0, ""rx="" + (string)osRegexIsMatch(""abc"", ""^a"") + (string)osRegexIsMatch(""abc"", ""^b"")); } }");
            h.PumpUntil(() => h.Said.Any(s => s.StartsWith("rx=")));
            Assert.Contains("rx=10", h.Said);
        }
    }
}
