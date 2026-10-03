using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llHTTPRequest sends the simulator's X-SecondLife-* headers and a script cannot forge,
/// replace or remove them. Halcyon ScriptsHttpRequests.ScriptCanChangeHeader refuses "x-secondlife*" (any letter case);
/// SL llHTTPRequest: "Use HTTP_MIMETYPE to set the Content-Type header. Attempts to use HTTP_CUSTOM_HEADER to set it will
/// cause a runtime script error." What is checked is what the core's HttpRequestModule actually puts on the wire, read
/// by a loopback listener, not the script's view.
/// </summary>
// No longer in "phlox-state". In "phlox-http" with PhloxOutboundFilterTests: both build the core
// HttpRequestModule, whose outbound URL filter is process-wide.
[Collection("phlox-http")]
public class PhloxHttpHeaderTests
{
    private const string Shard = "PhloxTestShard";

    private static readonly string[] Trusted =
    {
        "X-SecondLife-Shard", "X-SecondLife-Object-Name", "X-SecondLife-Object-Key", "X-SecondLife-Region",
        "X-SecondLife-Local-Position", "X-SecondLife-Local-Rotation", "X-SecondLife-Local-Velocity",
        "X-SecondLife-Owner-Name", "X-SecondLife-Owner-Key",
    };

    /// <summary>A loopback HTTP/1.1 endpoint that records every request's raw head and answers 200.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener m_tcp = new(IPAddress.Loopback, 0);
        private readonly Thread m_thread;
        private volatile bool m_stop;
        public readonly List<string> Heads = new();
        public int Port => ((IPEndPoint)m_tcp.LocalEndpoint).Port;
        public string Url => "http://127.0.0.1:" + Port + "/phlox47";

        public Listener()
        {
            m_tcp.Start();
            m_thread = new Thread(Run) { IsBackground = true, Name = "phlox47-listener" };
            m_thread.Start();
        }

        private void Run()
        {
            while (!m_stop)
            {
                TcpClient c;
                try { c = m_tcp.AcceptTcpClient(); } catch { return; }
                using (c)
                {
                    try
                    {
                        c.ReceiveTimeout = 5000;
                        var s = c.GetStream();
                        var buf = new List<byte>();
                        var one = new byte[1];
                        while (s.Read(one, 0, 1) == 1)
                        {
                            buf.Add(one[0]);
                            int n = buf.Count;
                            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n') break;
                        }
                        string head = Encoding.ASCII.GetString(buf.ToArray());
                        var m = System.Text.RegularExpressions.Regex.Match(head, @"(?im)^content-length:\s*(\d+)");
                        if (m.Success)
                        {
                            int len = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            var body = new byte[len];
                            int got = 0;
                            while (got < len) { int r = s.Read(body, got, len - got); if (r <= 0) break; got += r; }
                        }
                        lock (Heads) Heads.Add(head);
                        byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        s.Write(resp, 0, resp.Length);
                    }
                    catch { }
                }
            }
        }

        public void Dispose()
        {
            m_stop = true;
            m_tcp.Stop();
        }
    }

    /// <summary>The header lines of a raw request head, in order, names as sent.</summary>
    private static List<(string Name, string Value)> Lines(string head)
        => head.Split("\r\n").Skip(1).Where(l => l.Length > 0)
               .Select(l => { int c = l.IndexOf(':'); return c < 0 ? (l, "") : (l[..c], l[(c + 1)..].Trim()); })
               .ToList();

    private static List<string> ValuesOf(string head, string name)
        => Lines(head).Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).Select(l => l.Value).ToList();

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly Listener L = new();
        private readonly HttpRequestModule m_http = new();

        public Rig()
        {
            // The listener is on loopback, which the outbound filter refuses by default; this Except entry
            // lets these requests through as an operator's would.
            H = new SchedulerHarness(cfg =>
            {
                var n = cfg.AddConfig("Network");
                n.Set("shard", Shard);
                n.Set("OutboundDisallowForUserScriptsExcept", "127.0.0.1:" + L.Port);
            });
            m_http.Initialise(H.Config);
            m_http.AddRegion(H.Scene);
        }

        /// <summary>
        /// Run llHTTPRequest with the given LSL option list (source text) and pump until the listener has the request, or
        /// until the script has said its result and nothing arrived for a while (a refused request).
        /// </summary>
        public string Request(string options, string url = null, bool expectSent = true)
        {
            string u = url ?? "\"" + L.Url + "\"";
            H.RezScript("default { state_entry() { key k = llHTTPRequest(" + u + ", " + options + ", \"hello\"); " +
                        "llSay(0, \"req=\" + (string)k); } http_response(key id, integer st, list m, string b) { llSay(0, \"status=\" + (string)st); } }");
            var until = DateTime.UtcNow.AddSeconds(expectSent ? 30 : 3);
            while (DateTime.UtcNow < until)
            {
                lock (L.Heads) if (L.Heads.Count > 0 && expectSent) break;
                if (!H.PumpOnceBusy()) Thread.Sleep(2);
            }
            // A refused request keeps the fixed window above (nothing arrived); the script's own "req=" line, said after
            // any refusal it reports, is then waited for.
            if (!expectSent) H.PumpUntil(() => H.Said.Any(s => s.StartsWith("req=")));
            lock (L.Heads)
            {
                if (expectSent) Assert.True(L.Heads.Count == 1, "requests received: " + L.Heads.Count + "; said=[" + string.Join(" | ", H.Said) + "]");
                return L.Heads.LastOrDefault();
            }
        }

        public void Dispose()
        {
            m_http.RemoveRegion(H.Scene);
            m_http.Close();
            L.Dispose();
            H.Dispose();
        }
    }

    private static string Lsl(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ── the simulator's values ────────────────────────────────────────────────

    [Fact]
    public void TheSimulatorsNineHeadersAreSentWithTheRealValues()
    {
        using var r = new Rig();
        var prim = r.H.Prim;
        string head = r.Request("[HTTP_METHOD, \"POST\"]");
        var ri = r.H.Scene.RegionInfo;

        Assert.Equal(new[] { Shard }, ValuesOf(head, "X-SecondLife-Shard"));
        Assert.Equal(new[] { prim.OwnerID.ToString() }, ValuesOf(head, "X-SecondLife-Owner-Key"));
        Assert.Equal(new[] { prim.UUID.ToString() }, ValuesOf(head, "X-SecondLife-Object-Key"));
        Assert.Equal(new[] { prim.Name }, ValuesOf(head, "X-SecondLife-Object-Name"));
        // SL: "the global coordinates of the region's south-west corner", e.g. "Jin Ho (264448, 233984)"
        Assert.Equal(new[] { ri.RegionName + " (" + ri.WorldLocX + ", " + ri.WorldLocY + ")" }, ValuesOf(head, "X-SecondLife-Region"));
        var p = prim.AbsolutePosition;
        Assert.Equal(new[] { string.Format(CultureInfo.InvariantCulture, "({0:0.000000}, {1:0.000000}, {2:0.000000})", p.X, p.Y, p.Z) },
                     ValuesOf(head, "X-SecondLife-Local-Position"));
        foreach (string t in Trusted) Assert.Single(ValuesOf(head, t));
    }

    // ── a script cannot set, replace or remove them ───────────────────────────

    public static IEnumerable<object[]> Forgeries()
    {
        foreach (string t in Trusted)
        {
            yield return new object[] { t };
            yield return new object[] { t.ToLowerInvariant() };
            yield return new object[] { t.ToUpperInvariant() };
        }
    }

    [Theory]
    [MemberData(nameof(Forgeries))]
    public void ATrustedHeaderSetByTheScriptInAnyCaseChangesNothing(string name)
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_CUSTOM_HEADER, " + Lsl(name) + ", \"forged-by-script\"]");
        foreach (string t in Trusted)
        {
            var values = ValuesOf(head, t);
            Assert.True(values.Count == 1, t + " sent " + values.Count + " times: " + head);
            Assert.DoesNotContain("forged-by-script", values[0]);
        }
        Assert.DoesNotContain("forged-by-script", head);
        Assert.Contains("req=", r.H.Said.Last(s => s.StartsWith("req=")));
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("Script error"));   // dropped silently, as Halcyon
    }

    [Theory]
    [InlineData("X-SecondLife-Anything")]
    [InlineData("x-secondlife")]
    [InlineData("X-SECONDLIFE-OWNER-KEY-2")]
    public void AnyXSecondLifeNameIsDropped(string name)
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_CUSTOM_HEADER, " + Lsl(name) + ", \"forged-by-script\"]");
        Assert.DoesNotContain("forged-by-script", head);
    }

    [Fact]
    public void TheOwnerKeyCannotBeForgedTwiceEither()
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_CUSTOM_HEADER, \"X-SecondLife-Owner-Key\", \"a\", HTTP_CUSTOM_HEADER, \"x-secondlife-owner-key\", \"b\"]");
        Assert.Equal(new[] { r.H.Prim.OwnerID.ToString() }, ValuesOf(head, "X-SecondLife-Owner-Key"));
    }

    // ── Content-Type ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("content-type")]
    [InlineData("CONTENT-TYPE")]
    public void ContentTypeThroughACustomHeaderIsARuntimeErrorAndNoRequest(string name)
    {
        using var r = new Rig();
        r.Request("[HTTP_CUSTOM_HEADER, " + Lsl(name) + ", \"application/forged\"]", expectSent: false);
        lock (r.L.Heads) Assert.Empty(r.L.Heads);
        Assert.Contains("req=" + UUID.Zero, r.H.Said);
        Assert.Contains("Script error: llHTTPRequest: Content-Type cannot be set with HTTP_CUSTOM_HEADER; use HTTP_MIMETYPE.", r.H.Said);
    }

    [Fact]
    public void HttpMimeTypeStillSetsContentType()
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_METHOD, \"POST\", HTTP_MIMETYPE, \"application/json\"]");
        Assert.Equal(new[] { "application/json" }, ValuesOf(head, "Content-Type"));
    }

    // ── ordinary custom headers ───────────────────────────────────────────────

    [Fact]
    public void OrdinaryCustomHeadersStillWorkAndADuplicateIsAppended()
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_CUSTOM_HEADER, \"X-My-Header\", \"one\", HTTP_CUSTOM_HEADER, \"Authorization\", \"Bearer abc\", " +
                                "HTTP_CUSTOM_HEADER, \"x-my-header\", \"two\"]");
        Assert.Equal(new[] { "one, two" }, ValuesOf(head, "X-My-Header"));
        Assert.Equal(new[] { "Bearer abc" }, ValuesOf(head, "Authorization"));
        Assert.Equal(new[] { r.H.Prim.OwnerID.ToString() }, ValuesOf(head, "X-SecondLife-Owner-Key"));
    }

    // ── other ways to shape the request ───────────────────────────────────────

    [Fact]
    public void ALineBreakInACustomHeaderCannotAddATrustedHeader()
    {
        using var r = new Rig();
        string crlf = "llUnescapeURL(\"%0D%0A\")";
        string head = r.Request("[HTTP_CUSTOM_HEADER, \"X-My-Header\", \"v\" + " + crlf + " + \"X-SecondLife-Owner-Key: forged-by-script\", " +
                                "HTTP_CUSTOM_HEADER, \"X-Ok\" + " + crlf + " + \"X-SecondLife-Owner-Key\", \"forged-by-script\", " +
                                "HTTP_CUSTOM_HEADER, \"X-Plain\", \"kept\"]");
        Assert.DoesNotContain("forged-by-script", head);
        Assert.Equal(new[] { r.H.Prim.OwnerID.ToString() }, ValuesOf(head, "X-SecondLife-Owner-Key"));
        Assert.Empty(ValuesOf(head, "X-My-Header"));
        Assert.Equal(new[] { "kept" }, ValuesOf(head, "X-Plain"));
    }

    [Fact]
    public void ALineBreakInTheMimeTypeCannotAddATrustedHeader()
    {
        using var r = new Rig();
        // The core writes the MIME type as the Content-Type line; unchecked, this sent a second header line
        // "X-SecondLife-Owner-Key: forged-by-script". Refused: an error and no request. YEngine's error and
        // result (was "Script error: ... without a line break." and NULL_KEY).
        string head = r.Request("[HTTP_METHOD, \"POST\", HTTP_MIMETYPE, \"text/plain\" + llUnescapeURL(\"%0D%0A\") + \"X-SecondLife-Owner-Key: forged-by-script\"]",
                                expectSent: false);
        Assert.Null(head);
        Assert.Contains("req=", r.H.Said);
        Assert.Contains((DebugChannel, MimeTypeRefused), r.H.SaidOn);
    }

    private const int DebugChannel = 2147483647;

    /// <summary>YEngine's text for an invalid HTTP_MIMETYPE (LSL_Api.llHTTPRequest, "command: message").</summary>
    internal const string MimeTypeRefused = "llHTTPRequest: HTTP_MIMETYPE is not a valid media type";

    /// <summary>
    /// Every value HttpRequestMimeType.IsValid refuses is refused by Phlox itself, with YEngine's result: its
    /// text on DEBUG_CHANNEL, "" to the script (not NULL_KEY), no "Script error", and nothing on the wire. Before, a
    /// value without a line break went to the core, which refused it silently: NULL_KEY and no message.
    /// </summary>
    [Theory]
    [InlineData("\"text/plain\" + llUnescapeURL(\"%0D\") + \"X-SecondLife-Owner-Key: forged-by-script\"")]   // CR
    [InlineData("\"text/plain\" + llUnescapeURL(\"%0A\") + \"X-SecondLife-Owner-Key: forged-by-script\"")]   // LF
    [InlineData("\"text/plain;charset=utf-8\" + llUnescapeURL(\"%0D%0A%0D%0A\") + \"smuggled body\"")]       // header/body injection
    [InlineData("\"json\"")]                                                                                 // a bare word
    [InlineData("\"\"")]                                                                                     // empty
    public void AMimeTypeThatIsNotAMediaTypeIsRefusedAsYEngineRefusesIt(string mimeTypeLsl)
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_METHOD, \"POST\", HTTP_MIMETYPE, " + mimeTypeLsl + "]", expectSent: false);
        Assert.Null(head);
        Assert.Contains("req=", r.H.Said);
        Assert.DoesNotContain("req=" + UUID.Zero, r.H.Said);
        Assert.Contains((DebugChannel, MimeTypeRefused), r.H.SaidOn);
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("Script error") || s.StartsWith("status="));
    }

    /// <summary>A media type with parameters is still sent, as the Content-Type line, exactly as given.</summary>
    [Theory]
    [InlineData("text/plain;charset=utf-8")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("text/xml;charset=UTF-8;version=1")]
    [InlineData("application/vnd.api+json")]
    public void AValidMimeTypeWithParametersIsSentAsGiven(string mimeType)
    {
        using var r = new Rig();
        string head = r.Request("[HTTP_METHOD, \"POST\", HTTP_MIMETYPE, " + Lsl(mimeType) + "]");
        Assert.Equal(new[] { mimeType }, ValuesOf(head, "Content-Type"));
        Assert.DoesNotContain(r.H.SaidOn, m => m.Channel == DebugChannel);
    }

    [Fact]
    public void ALineBreakInTheUrlCannotAddATrustedHeader()
    {
        using var r = new Rig();
        string head = r.Request("[]", url: Lsl(r.L.Url) + " + \"\\nX-SecondLife-Owner-Key: forged-by-script\"", expectSent: false);
        // System.Uri escapes the line break into the path ("...%0AX-SecondLife-Owner-Key:%20forged-by-script"): it is
        // part of the request line, never a header line.
        if (head != null)
        {
            Assert.DoesNotContain(Lines(head), l => l.Value.Contains("forged-by-script") || l.Name.Contains("forged-by-script"));
            Assert.Equal(new[] { r.H.Prim.OwnerID.ToString() }, ValuesOf(head, "X-SecondLife-Owner-Key"));
        }
    }
}
