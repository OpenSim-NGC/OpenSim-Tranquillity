using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The two engines side by side on one region, the same HTTP_MIMETYPE from each. The core refuses a value that
/// is not a media type (HttpRequestMimeType.IsValid) and YEngine says why; Phlox must give the same result: the same
/// text on DEBUG_CHANNEL, the same "" return, and nothing on the wire. A valid value is sent by both.
/// In "phlox-state" (runs alone), as PhloxOutboundFilterYEngineTests: it needs YEngine's statics and the core
/// HttpRequestModule's process-wide filter at once. The endpoint is a loopback listener opened by an Except entry.
/// </summary>
[Collection("phlox-state")]
public class PhloxMimeTypeYEngineTests
{
    private const int DebugChannel = 2147483647;

    private readonly ITestOutputHelper _out;
    public PhloxMimeTypeYEngineTests(ITestOutputHelper o) => _out = o;

    private sealed record Seen(string Req, string[] Debug, int Hits, bool Responded);

    private static Seen Run(PhloxOutboundFilterTests.Rig r, Action rez, bool expectSent)
    {
        int hitsBefore = r.C.Hits;
        rez();
        r.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("req=")) && (!expectSent || r.H.Said.Any(s => s.StartsWith("status="))), 20);
        r.PumpFor(1.0);   // anything still on its way lands now
        var said = r.H.Said.ToList();
        return new Seen(
            said.FirstOrDefault(s => s.StartsWith("req=")),
            r.H.SaidOn.Where(m => m.Channel == DebugChannel).Select(m => m.Message).Distinct().ToArray(),
            r.C.Hits - hitsBefore,
            said.Any(s => s.StartsWith("status=")));
    }

    [Theory]
    [InlineData("CR", "\"text/plain\" + llUnescapeURL(\"%0D\") + \"X-SecondLife-Owner-Key: forged\"", false)]
    [InlineData("LF", "\"text/plain\" + llUnescapeURL(\"%0A\") + \"X-SecondLife-Owner-Key: forged\"", false)]
    [InlineData("header injection", "\"text/plain\" + llUnescapeURL(\"%0D%0A\") + \"X-SecondLife-Owner-Key: forged\"", false)]
    [InlineData("bare word", "\"json\"", false)]
    [InlineData("empty", "\"\"", false)]
    [InlineData("valid with a parameter", "\"text/plain;charset=utf-8\"", true)]
    [InlineData("valid with parameters", "\"application/json; charset=\\\"utf-8\\\"; v=1\"", true)]
    public void YEngineAndPhloxGiveTheSameResult(string label, string mimeTypeLsl, bool valid)
    {
        using var r = new PhloxOutboundFilterTests.Rig(except: port => "127.0.0.1:" + port, withYEngine: true);
        string src = "default { state_entry() { key k = llHTTPRequest(" + PhloxOutboundFilterTests.Lsl(r.Loopback()) +
                     ", [HTTP_METHOD, \"POST\", HTTP_MIMETYPE, " + mimeTypeLsl + "], \"hello\"); llSay(0, \"req=[\" + (string)k + \"]\"); } " +
                     "http_response(key id, integer st, list m, string b) { llSay(0, \"status=\" + (string)st); } }";

        OpenMetaverse.UUID yItem = default;
        var y = Run(r, () => yItem = SchedulerHarnessYEngine.Rez(r.H, r.H.Prim, src), valid);
        r.H.ClearSaid(yItem);
        var p = Run(r, () => r.H.RezScript(src), valid);

        _out.WriteLine($"{label}: YEngine {y.Req} debug=[{string.Join(" | ", y.Debug)}] hits={y.Hits} responded={y.Responded}");
        _out.WriteLine($"{label}: Phlox   {p.Req} debug=[{string.Join(" | ", p.Debug)}] hits={p.Hits} responded={p.Responded}");

        if (valid)
        {
            foreach (var s in new[] { y, p })
            {
                Assert.NotEqual("req=[]", s.Req);
                Assert.Equal(1, s.Hits);
                Assert.Empty(s.Debug);
            }
            // Only Phlox's response is asserted: both engines' pumps take completed requests from the core's one queue,
            // and one Phlox's pump takes is posted through Phlox only (AsyncCommand/Plugins/HttpRequest.cs), so YEngine's
            // http_response arrives or not depending on which pump runs first - a separate cross-engine defect, not the
            // MIME type. YEngine's pump posts through every engine, so Phlox's always arrives.
            Assert.True(p.Responded);
        }
        else
        {
            foreach (var s in new[] { y, p })
            {
                Assert.Equal("req=[]", s.Req);
                Assert.Equal(new[] { PhloxHttpHeaderTests.MimeTypeRefused }, s.Debug);
                Assert.Equal(0, s.Hits);
                Assert.False(s.Responded);
            }
        }
    }
}
