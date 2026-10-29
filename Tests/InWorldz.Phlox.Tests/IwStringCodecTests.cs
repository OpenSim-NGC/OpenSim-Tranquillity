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

    private static LSLSystemAPI StandaloneApi(SchedulerHarness h) => new LSLSystemAPI(null, h.Prim, h.Prim.LocalId, UUID.Random());

    /// <summary>
    /// Halcyon's gzip decode (GZipStream.CopyTo on .NET Framework 4.x) on 6431 streams: every truncation and every
    /// single-bit flip of four small members (static, dynamic and stored blocks), truncations of a 16947-byte member,
    /// and 3000 random blocks. Framework's answer is the text Halcyon made of the bytes (Encoding.Unicode) or the
    /// exception, type and message. The call goes through iwStringCodec(hex, "gzip", 0, ["output codec", "base16"]).
    /// </summary>
    [Fact]
    public void GzipDecodeMatchesFrameworkOnTheFuzzCorpus()
    {
        using var h = new SchedulerHarness();
        var api = StandaloneApi(h);
        var bases = new Dictionary<int, byte[]>();
        var failures = new List<string>();
        int n = 0;
        foreach (string line in File.ReadAllLines(IwStringCodecGoldenTests.GoldenPath("gzip-decoder-vectors.jsonl")))
        {
            var v = JsonDocument.Parse(line).RootElement;
            if (v.TryGetProperty("base", out var b))
            {
                bases[b.GetInt32()] = Convert.FromHexString(v.GetProperty("hex").GetString());
                continue;
            }
            byte[] stream;
            string label;
            if (v.TryGetProperty("hex", out var hx)) { stream = Convert.FromHexString(hx.GetString()); label = "random " + hx.GetString(); }
            else
            {
                byte[] s = bases[v.GetProperty("of").GetInt32()];
                if (v.TryGetProperty("trunc", out var t)) { stream = s.Take(t.GetInt32()).ToArray(); label = "base " + v.GetProperty("of").GetInt32() + " cut to " + t.GetInt32(); }
                else
                {
                    stream = (byte[])s.Clone();
                    var f = v.GetProperty("flip");
                    stream[f[0].GetInt32()] ^= (byte)(1 << f[1].GetInt32());
                    label = "base " + v.GetProperty("of").GetInt32() + " bit " + f[1].GetInt32() + " of byte " + f[0].GetInt32() + " flipped";
                }
            }
            n++;
            string want;
            if (v.TryGetProperty("ex", out var ex)) want = "ex " + ex.GetString();
            else if (v.TryGetProperty("ok", out var ok)) want = "ok " + Encoding.Unicode.GetString(Convert.FromHexString(ok.GetString()));
            else want = "okLen " + v.GetProperty("okLen").GetInt32() + " sha " + v.GetProperty("okSha256").GetString();

            string got;
            try
            {
                string ret = api.iwStringCodec(Convert.ToHexString(stream).ToLowerInvariant(), "gzip", 0, new LSLList(new object[] { "output codec", "base16" }));
                if (want.StartsWith("okLen"))
                {
                    // the bytes are not stored; their text is: compare the text Halcyon would have made of them
                    got = ret;
                    want = null;
                }
                else got = "ok " + ret;
            }
            catch (Exception e) { got = "ex " + e.GetType().FullName + ": " + e.Message; }

            if (want == null)
            {
                string wantText = TextOfBytesWithHash(v, got);
                if (wantText != null) failures.Add(label + ": " + wantText);
            }
            else if (got != want) failures.Add(label + ": Framework " + Short(want) + " / Phlox " + Short(got));
        }
        _out.WriteLine(n + " streams, " + failures.Count + " differ");
        Assert.Equal(6431, n);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    // A long result is stored as the length and SHA-256 of Framework's output bytes. Phlox's text is Halcyon's
    // Encoding.Unicode of its bytes; both runs of the same bytes give the same text, so the bytes are checked by
    // turning the text back only when that is lossless (an even length, no replacement characters), else by length.
    private static string TextOfBytesWithHash(JsonElement v, string got)
    {
        if (got == null || got.StartsWith("ex ")) return "Framework decoded " + v.GetProperty("okLen").GetInt32() + " bytes / Phlox " + Short(got);
        int len = v.GetProperty("okLen").GetInt32();
        if (got.Length != len / 2 + len % 2) return "Framework " + len + " bytes / Phlox " + got.Length + " chars";
        if (len % 2 == 0 && got.IndexOf('�') < 0)
        {
            string sha = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(got))).ToLowerInvariant();
            if (sha != v.GetProperty("okSha256").GetString()) return "bytes differ (SHA-256)";
        }
        return null;
    }

    private static string Short(string s) => s == null ? "null" : s.Length > 120 ? s.Substring(0, 120) + "..." : s;

    private static object CallInternal(string type, string method, params object[] args)
    {
        var t = typeof(LSLSystemAPI).Assembly.GetType("Phlox.ScriptEngine.Codecs." + type, throwOnError: true);
        var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type, method);
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }

    /// <summary>
    /// Framework's Uri.EscapeDataString, which Halcyon's base4096 decode calls, on 3441 strings of ASCII, non-ASCII,
    /// high and low surrogates and runs of 30-50 non-ASCII characters; and its 65520-character limit. .NET 10 writes
    /// U+FFFD for a lone high surrogate where Framework threw, and has no limit.
    /// </summary>
    [Fact]
    public void UriEscapeMatchesFramework()
    {
        var failures = new List<string>();
        int n = 0;
        foreach (string line in File.ReadAllLines(IwStringCodecGoldenTests.GoldenPath("uri-escape-vectors.jsonl")))
        {
            var v = JsonDocument.Parse(line).RootElement;
            string s = IwStringCodecGoldenTests.Str(v.GetProperty("s"));
            string want = v.TryGetProperty("r", out var r) ? IwStringCodecGoldenTests.Str(r) : "EX " + IwStringCodecGoldenTests.Str(v.GetProperty("ex"));
            string got;
            try { got = (string)CallInternal("FrameworkText", "EscapeDataString", s); }
            catch (Exception e) { got = "EX " + e.GetType().FullName + ": " + e.Message; }
            n++;
            if (got != want) failures.Add(line + " / Phlox " + got);
        }
        Assert.Equal(3441, n);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        Assert.Equal(65519 * 3, ((string)CallInternal("FrameworkText", "EscapeDataString", new string('!', 65519))).Length);
        var tooLong = Assert.Throws<UriFormatException>(() => CallInternal("FrameworkText", "EscapeDataString", new string('a', 65520)));
        Assert.Equal("Invalid URI: The Uri string is too long.", tooLong.Message);
    }

    /// <summary>
    /// Char.IsLetterOrDigit and Char.IsDigit as Framework 4.8.1 answers them for every UTF-16 code unit (Halcyon's base64
    /// and base16 validation use them); .NET 10's newer Unicode tables differ on 259 characters.
    /// </summary>
    [Fact]
    public void CharClassesMatchFramework()
    {
        string table = File.ReadAllText(IwStringCodecGoldenTests.GoldenPath("framework-char-classes.txt"));
        Assert.Equal(65536, table.Length);
        int differ = 0, net10Differs = 0;
        for (int c = 0; c < 65536; c++)
        {
            int want = table[c] - '0';
            int got = ((bool)CallInternal("FrameworkText", "IsLetterOrDigit", (char)c) ? 1 : 0) + ((bool)CallInternal("FrameworkText", "IsDigit", (char)c) ? 2 : 0);
            if (got != want) differ++;
            if ((char.IsLetterOrDigit((char)c) ? 1 : 0) + (char.IsDigit((char)c) ? 2 : 0) != want) net10Differs++;
        }
        _out.WriteLine(".NET 10's own answers differ on " + net10Differs + " characters");
        Assert.Equal(0, differ);
    }

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
