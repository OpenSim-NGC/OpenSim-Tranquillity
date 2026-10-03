using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwStringCodec against Halcyon's own code, byte for byte. Golden/iwStringCodec-vectors.jsonl
/// holds 1511 calls run through Halcyon's iwStringCodec and CodecUtil (LSLSystemAPI.cs:16053-16955, compiled unchanged)
/// on .NET Framework 4.8.1 with Halcyon's own LSLList and LibreMetaverse (Golden/README.md): every codec, both directions
/// and validate, empty and large inputs, non-ASCII text and bad input. For each call the result (or its SHA-256 when
/// long), the exception type and message, every LSL Runtime Error and the sleep must match.
///
/// <para>The calls are made on a loaded, idle script's own API object from the test thread, as the script thread makes
/// them, with the engine's clock frozen so the sleep a call sets reads exactly as NextWakeup - now (Halcyon's ScriptSleep:
/// a later sleep replaces an earlier one, so the last one counts). The clock is process-wide, hence "phlox-state".</para>
/// </summary>
[Collection("phlox-state")]
public class IwStringCodecGoldenTests
{
    private readonly ITestOutputHelper _out;
    public IwStringCodecGoldenTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    internal static string GoldenPath(string file) => Path.Combine(AppContext.BaseDirectory, "Golden", file);

    private static readonly System.Lazy<List<JsonElement>> s_vectors = new(() =>
        File.ReadAllLines(GoldenPath("iwStringCodec-vectors.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList());

    public static IEnumerable<object[]> Groups() =>
        s_vectors.Value.Select(v => v.GetProperty("group").GetString()).Distinct().Select(g => new object[] { g });

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public ulong Now;
        private bool m_frozen;
        public readonly UUID Item;

        public Rig(params string[] off)
        {
            Clock.SetSourceForTesting(() => m_frozen ? Now : (ulong)Environment.TickCount64);
            H = new SchedulerHarness(cfg => { foreach (var k in off) cfg.Configs["InWorldz.Phlox"].Set(k, "false"); });
            Item = H.RezScript("default { state_entry() { } }");
            var until = DateTime.UtcNow.AddSeconds(30);
            while (H.RunStateOf(Item) != "Waiting")
            {
                Assert.True(DateTime.UtcNow < until, "the script never loaded: " + H.RunStateOf(Item));
                if (!H.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
            }
            Now = (ulong)Environment.TickCount64;
            m_frozen = true;
        }

        public LSLSystemAPI Api
        {
            get
            {
                var exe = Field(H.Engine, "m_ExeScheduler");
                return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[Item];
            }
        }

        public RuntimeState State => ((Interpreter)H.InterpreterFor(Item)).ScriptState;

        /// <summary>One call as the script thread makes it: the result or exception, the errors it said, the sleep it set.</summary>
        public (string Ret, Exception Ex, List<string> Errors, int Sleep) Call(string str, string codec, int op, LSLList p)
        {
            RuntimeState st = State;
            st.RunState = RuntimeState.Status.Waiting;
            st.NextWakeup = 0;
            H.ClearSaid(Item);
            string ret = null;
            Exception ex = null;
            try { ret = Api.iwStringCodec(str, codec, op, p); }
            catch (Exception e) { ex = e; }
            var errors = H.SaidOn.Where(m => m.Channel == DEBUG_CHANNEL).Select(m => m.Message).ToList();
            int sleep = st.RunState == RuntimeState.Status.Sleeping ? (int)((long)st.NextWakeup - (long)Now) : 0;
            return (ret, ex, errors, sleep);
        }

        public void Dispose()
        {
            try { H.Dispose(); } finally { Clock.SetSourceForTesting(null); }
        }
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    /// <summary>A JSON string that may hold lone surrogates (System.Text.Json's GetString refuses them).</summary>
    internal static string Str(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;
        string raw = e.GetRawText();
        var sb = new StringBuilder(raw.Length);
        for (int i = 1; i < raw.Length - 1; i++)
        {
            char c = raw[i];
            if (c != '\\') { sb.Append(c); continue; }
            char n = raw[++i];
            switch (n)
            {
                case 'u': sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16)); i += 4; break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The generator's texts (Golden/generator/Harness.cs Gen), rebuilt here.</summary>
    internal static string Gen(string kind, int n)
    {
        string pat = kind switch
        {
            "ascii" => "The quick brown fox jumps over the lazy dog 0123456789. ",
            "uni" => "héllo wörld ✓ 日本語 ",
            _ => null,
        };
        if (pat != null)
        {
            var sb = new StringBuilder(n + pat.Length);
            while (sb.Length < n) sb.Append(pat);
            return sb.ToString(0, n);
        }
        uint x = 12345;
        var r = new StringBuilder(n);
        string[] words = { "the ", "quick ", "brown ", "fox ", "jumps ", "over ", "lazy ", "dog ", "été ", "日本 " };
        while (r.Length < n)
        {
            x = (x * 1103515245u + 12345u) & 0x7fffffffu;
            uint v = x >> 8;
            switch (kind)
            {
                case "rnd": r.Append((char)(0x20 + v % 0xD000)); break;
                case "ab": r.Append(v % 2 == 0 ? 'a' : 'b'); break;
                case "runs": r.Append((char)('a' + v % 3), (int)(1 + (v >> 4) % 300)); break;
                case "words": r.Append(words[v % (uint)words.Length]); break;
                default: throw new ArgumentException(kind);
            }
        }
        return r.ToString(0, n);
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(s))).ToLowerInvariant();

    private static string Show(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder();
        foreach (char c in s.Length > 60 ? s.Substring(0, 60) + "..." : s)
            sb.Append(c < 0x20 || c > 0x7e ? "\\u" + ((int)c).ToString("x4") : c.ToString());
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryGoldenCallOfTheGroupMatchesHalcyon(string group)
    {
        using var r = new Rig();
        var results = new Dictionary<int, string>();
        var failures = new List<string>();
        int count = 0;
        foreach (var v in s_vectors.Value)
        {
            int id = v.GetProperty("id").GetInt32();
            if (v.GetProperty("group").GetString() != group)
                continue;
            count++;
            var inp = v.GetProperty("input");
            string str;
            if (inp.ValueKind == JsonValueKind.String) str = Str(inp);
            else if (inp.TryGetProperty("gen", out var g)) str = Gen(g.GetString(), inp.GetProperty("n").GetInt32());
            else if (!results.TryGetValue(inp.GetProperty("fromCase").GetInt32(), out str))
            {
                failures.Add(id + ": its input is case " + inp.GetProperty("fromCase").GetInt32() + "'s result, which failed");
                continue;
            }
            var items = v.GetProperty("params").EnumerateArray().Select(e => e.ValueKind switch
            {
                JsonValueKind.String => (object)Str(e),
                JsonValueKind.Number => e.GetInt32(),
                _ => (object)e.GetProperty("float").GetSingle(),
            }).ToArray();
            string codec = Str(v.GetProperty("codec"));
            int op = v.GetProperty("op").GetInt32();

            var (ret, ex, errors, sleep) = r.Call(str, codec, op, new LSLList(items));

            var why = new List<string>();
            if (v.TryGetProperty("exception", out var eType))
            {
                string want = eType.GetString() + ": " + Str(v.GetProperty("exceptionMessage"));
                string got = ex == null ? "no exception, returned " + Show(ret) : ex.GetType().FullName + ": " + ex.Message;
                if (got != want) why.Add("exception: Halcyon " + Show(want) + " / Phlox " + Show(got));
            }
            else if (ex != null) why.Add("unexpected " + ex.GetType().FullName + ": " + ex.Message);
            else if (v.TryGetProperty("retSha256", out var sha))
            {
                if (ret == null || ret.Length != v.GetProperty("retLen").GetInt32() || Sha(ret) != sha.GetString())
                    why.Add("result: Halcyon " + v.GetProperty("retLen").GetInt32() + " chars starting " + Show(Str(v.GetProperty("retHead"))) + " / Phlox " + (ret?.Length ?? -1) + " chars starting " + Show(ret));
            }
            else
            {
                string want = Str(v.GetProperty("ret"));
                if (want != ret) why.Add("result: Halcyon " + Show(want) + " / Phlox " + Show(ret));
            }
            if (ret != null) results[id] = ret;

            var wantErrors = v.GetProperty("errors").EnumerateArray().Select(e => "Script error: " + Str(e)).ToList();
            if (!wantErrors.SequenceEqual(errors))
                why.Add("errors: Halcyon [" + string.Join(" | ", wantErrors.Select(Show)) + "] / Phlox [" + string.Join(" | ", errors.Select(Show)) + "]");
            var sleeps = v.GetProperty("sleeps").EnumerateArray().Select(e => e.GetInt32()).ToList();
            int wantSleep = sleeps.Count == 0 ? 0 : sleeps[^1];
            if (wantSleep != sleep) why.Add("sleep: Halcyon " + wantSleep + " ms / Phlox " + sleep + " ms");

            if (why.Count > 0)
                failures.Add(id + " " + Show(codec) + " op " + op + " input " + Show(str) + ": " + string.Join("; ", why));
        }
        Assert.True(count > 0, "no vectors in group " + group);
        _out.WriteLine(group + ": " + count + " calls, " + failures.Count + " differ");
        Assert.True(failures.Count == 0, failures.Count + " of " + count + " calls differ from Halcyon:\n" + string.Join("\n", failures.Take(25)));
    }

    /// <summary>ChatThrottle off (the setting for Halcyon's chat pause): an error is still said, with no 15 ms sleep.</summary>
    [Fact]
    public void WithChatThrottleOffAnErrorDoesNotSleep()
    {
        using var r = new Rig("ChatThrottle");
        var (ret, ex, errors, sleep) = r.Call("Hello", "rot13", 1, new LSLList());
        Assert.Null(ex);
        Assert.Equal("", ret);
        Assert.Equal(new[] { "Script error: LSL Runtime Error: Error: \"rot13\" is not a valid codec for iwStringCodec!" }, errors);
        Assert.Equal(0, sleep);
        Assert.Equal(50, r.Call(new string('a', 1000), "base64", 2, new LSLList()).Sleep);   // Halcyon's size sleep stays: (1000 / 100) * 5
    }
}
