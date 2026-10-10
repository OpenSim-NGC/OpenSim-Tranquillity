/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;
using static InWorldz.Phlox.Tests.InventoryGivesRig;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Notecard readers that ask for the next line from their dataserver handler, alone and sharing a prim. SL (dataserver):
/// "Dataserver requests will trigger dataserver events in all scripts within the same prim where the request was
/// made", "dataserver events will not be triggered in scripts contained in other prims in the same linked object";
/// (Category:LSL_Events): "If more that 64 events are waiting, new events are discarded until free slots become
/// available." Every case reports, per script, the events it received, its highest queue depth, the events dropped
/// (summed from the queue-full log lines), the requests it made and whether it reached EOF.
/// Time is the engine's clock, moved by the test (see <see cref="Rig.RunUntil"/>); the clock and the log are
/// process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class NotecardFloodTests
{
    private readonly ITestOutputHelper _out;
    public NotecardFloodTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// A reader. "go" on channel 7 starts it at line 0 and each answer asks for the next line; "stop" ends its asking and
    /// is answered "stopped"; "count" says its counters. With <paramref name="checkId"/> an answer to anyone else's
    /// request is only counted. <paramref name="restart"/>: at EOF it starts again from line 0. A script
    /// without <paramref name="handler"/> has no dataserver event at all.
    /// </summary>
    internal static string Reader(string tag, string card = "card", bool checkId = true, bool restart = false,
        bool handler = true, bool linkCount = false, bool report = false, double sleepPerLine = 0)
    {
        string dataserver = !handler ? "" : @"
            dataserver(key id, string d) {
                evs++;
                " + (checkId ? "if (id != q) return; own++;" : "own++;") + @"
                if (!on) return;
                if (d == EOF) { eofs++; " + (restart ? "line = 0; Ask(); " : "") + @"return; }
                " + (sleepPerLine > 0 ? "llSleep(" + sleepPerLine.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");" : "") + @"
                line++; Ask();
            }";
        return @"
        integer line; key q; integer reqs; integer evs; integer own; integer eofs; integer on; integer links;
        Ask() { reqs++; q = llGetNotecardLine(""" + card + @""", line); }
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); " + (report ? "llSetTimerEvent(1.0); " : "") + @"llSay(0, """ + tag + @" entry""); }
            " + (report ? @"timer() { llSay(0, """ + tag + @" evs "" + (string)evs + "" own "" + (string)own + "" reqs "" + (string)reqs
                    + "" eofs "" + (string)eofs + "" links "" + (string)links); }" : "") + @"
            listen(integer c, string n, key k, string m) {
                if (m == ""go"") { line = 0; on = 1; " + (handler ? "Ask();" : "") + @" }
                else if (m == ""stop"") { on = 0; llSay(0, """ + tag + @" stopped""); }
                else if (m == ""count"") llSay(0, """ + tag + @" evs "" + (string)evs + "" own "" + (string)own + "" reqs "" + (string)reqs
                    + "" eofs "" + (string)eofs + "" links "" + (string)links);
            }
            " + (linkCount ? "link_message(integer s, integer n, string t, key i) { links++; }" : "") + dataserver + @"
        }";
    }

    /// <summary>Sends one link message to its prim every second.</summary>
    private const string LinkTicker = @"
        default {
            state_entry() { llSay(0, ""L entry""); llSetTimerEvent(1.0); }
            timer() { llMessageLinked(LINK_THIS, 0, ""tick"", NULL_KEY); }
        }";

    internal sealed class Counts
    {
        public int Evs, Own, Reqs, Eofs, Links, MaxDepth, Dropped;
        public override string ToString()
            => $"events {Evs}, own {Own}, requests {Reqs}, EOF {Eofs}, link {Links}, max depth {MaxDepth}, dropped {Dropped}";
    }

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> m_lines = new();
        public string[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.m_lines) m_c.m_lines.Add(formatter(state, exception)); }
        }
    }

    internal sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public ulong Now = 1_000_000;
        /// <summary>The clock moves 1 ms after every busy pump too: compute takes time.</summary>
        public bool ComputeTakesTime = true;
        private readonly Capture m_log = new();
        private readonly ILoggerFactory m_previous;
        public readonly Dictionary<UUID, string> Tags = new();
        private readonly Dictionary<UUID, int> m_maxDepth = new();

        /// <summary>The engine's own clock (real time), as on a region; <see cref="RunUntil"/> then measures wall time.</summary>
        public readonly bool RealClock;

        public Rig(Action<Nini.Config.IConfigSource> configure = null, bool realClock = false)
        {
            RealClock = realClock;
            m_previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = m_log;
            if (!realClock) Clock.SetSourceForTesting(() => Now);
            H = new SchedulerHarness(configure);
        }

        public UUID Rez(SceneObjectPart part, string tag, string source)
        {
            UUID id = H.RezScriptInto(part, source);
            Tags[id] = tag;
            return id;
        }

        public SceneObjectPart AddChild(string name)
        {
            var sog = OpenSim.Tests.Common.SceneHelpers.AddSceneObject(H.Scene, name, H.Prim.OwnerID);
            H.Prim.ParentGroup.LinkToGroup(sog);
            return H.Prim.ParentGroup.Parts.First(p => p.Name == name);
        }

        public void Say(string msg)
            => H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

        private bool WorkInFlight()
            => H.Engine.ObjectPostsInFlight > 0
               || ((Dictionary<UUID, global::Phlox.ScriptEngine.LSLSystemAPI>)SavedStateRig.Field(
                       SavedStateRig.Exe(H), "m_Apis")).Values.Any(api => api.PendingDataserverCount > 0);

        private void SampleDepth()
        {
            var exe = SavedStateRig.Exe(H);
            foreach (UUID id in Tags.Keys)
            {
                int d = exe.GetStatus(id).QueuedEvents;
                if (!m_maxDepth.TryGetValue(id, out int m) || d > m) m_maxDepth[id] = d;
            }
        }

        /// <summary>
        /// Pump, sampling every script's queue depth after each pass; the clock moves 1 ms when nothing could run (or
        /// after every busy pump, <see cref="ComputeTakesTime"/>), and stands still while work is on another thread.
        /// </summary>
        public bool RunUntil(Func<bool> done, ulong maxClockMs, int wallSeconds = 120)
        {
            if (RealClock)
            {
                var end = DateTime.UtcNow.AddMilliseconds(maxClockMs);
                while (!done())
                {
                    if (DateTime.UtcNow >= end) return false;
                    H.PumpOnceBusy();
                    SampleDepth();
                }
                return true;
            }
            ulong until = Now + maxClockMs;
            var wall = DateTime.UtcNow.AddSeconds(wallSeconds);
            while (!done())
            {
                if (Now >= until || DateTime.UtcNow > wall) return false;
                bool busy = H.PumpOnceBusy();
                SampleDepth();
                if (busy ? ComputeTakesTime : !WorkInFlight()) Now++;
                else if (!busy) System.Threading.Thread.Yield();
            }
            return true;
        }

        public bool Started(IEnumerable<string> tags)
            => H.PumpUntil(() => tags.All(t => H.Said.Contains(t + " entry")));

        /// <summary>Stop every reader (repeated until each says so: a stop can itself meet a full queue), then read their counters.</summary>
        public Dictionary<string, Counts> Finish(string[] tags)
        {
            Assert.True(RunUntil(() =>
            {
                if (Now % 50 == 0) Say("stop");
                return tags.All(t => H.Said.Contains(t + " stopped"));
            }, 600_000), "readers did not stop");
            // Everything still on its way lands, and a drop run ends (a minute with no drop) and writes its rest.
            RunUntil(() => false, 61_000);
            Say("count");
            Assert.True(RunUntil(() => tags.All(t => H.Said.Any(s => s.StartsWith(t + " evs "))), 60_000), "no counters: " + string.Join(" | ", H.Said.Where(s => s.Contains("evs"))));
            var result = new Dictionary<string, Counts>();
            foreach (string t in tags)
            {
                string[] w = H.Said.Last(s => s.StartsWith(t + " evs ")).Split(' ');
                var c = new Counts { Evs = int.Parse(w[2]), Own = int.Parse(w[4]), Reqs = int.Parse(w[6]), Eofs = int.Parse(w[8]), Links = int.Parse(w[10]) };
                UUID id = Tags.First(k => k.Value == t).Key;
                c.MaxDepth = m_maxDepth.GetValueOrDefault(id);
                c.Dropped = Dropped(id);
                result[t] = c;
            }
            return result;
        }

        /// <summary>The queue-full lines written for a script.</summary>
        public string[] FullLines(UUID item)
            => m_log.Lines.Where(l => l.Contains("Event queue full") && l.Contains(item.ToString())).ToArray();

        public int Dropped(UUID item)
            => FullLines(item).Sum(l => Regex.Matches(l.Substring(l.IndexOf("dropped ") + 8), @"(\d+) [A-Z_]+").Sum(m => int.Parse(m.Groups[1].Value)));

        public int MaxDepthOf(string tag) => m_maxDepth.GetValueOrDefault(Tags.First(k => k.Value == tag).Key);

        /// <summary>The counters a script's timer last said, with its depth and drops so far.</summary>
        public Counts Last(string tag)
        {
            string line = H.Said.LastOrDefault(s => s.StartsWith(tag + " evs "));
            UUID id = Tags.First(k => k.Value == tag).Key;
            if (line == null) return new Counts { MaxDepth = MaxDepthOf(tag), Dropped = Dropped(id) };
            string[] w = line.Split(' ');
            return new Counts { Evs = int.Parse(w[2]), Own = int.Parse(w[4]), Reqs = int.Parse(w[6]), Eofs = int.Parse(w[8]), Links = int.Parse(w[10]),
                MaxDepth = MaxDepthOf(tag), Dropped = Dropped(id) };
        }

        public string[] OtherWarnings() => m_log.Lines.Where(l => l.Contains("Event queue full") == false && (l.Contains("WARN") || l.Contains("ERROR"))).ToArray();

        public void Dispose()
        {
            try { H.Dispose(); }
            finally { Clock.SetSourceForTesting(null); LoggerProvider.LoggerFactory = m_previous; }
        }
    }

    private static string Card(int lines) => string.Join("\n", Enumerable.Range(0, lines).Select(i => "line " + i));

    private void Report(string title, Dictionary<string, Counts> c)
    {
        _out.WriteLine(title);
        foreach (var kv in c.OrderBy(k => k.Key)) _out.WriteLine($"  {kv.Key}: {kv.Value}");
    }

    private (Rig, string[]) PrimOf(int readers, int lines, bool restart = false, bool checkId = true, bool computeTakesTime = true,
        Func<int, string> source = null)
    {
        var r = new Rig { ComputeTakesTime = computeTakesTime };
        AddNotecard(r.H, r.H.Prim, "card", Card(lines));
        string[] tags = Enumerable.Range(0, readers).Select(i => ((char)('A' + i)).ToString()).ToArray();
        for (int i = 0; i < readers; i++)
            r.Rez(r.H.Prim, tags[i], source != null ? source(i) : Reader(tags[i], restart: restart, checkId: checkId));
        Assert.True(r.Started(tags), string.Join(" | ", r.H.Said));
        return (r, tags);
    }

    private static void AllReachedEof(Dictionary<string, Counts> c, int lines)
    {
        foreach (var kv in c)
        {
            Assert.True(kv.Value.Eofs >= 1, kv.Key + " never reached EOF: " + kv.Value);
            Assert.True(kv.Value.Own >= lines + 1, kv.Key + " missed some of its own answers: " + kv.Value);
        }
    }

    // 2a
    [Fact]
    public void OneReaderOfA200LineNotecard()
    {
        var (r, tags) = PrimOf(1, 200);
        using (r)
        {
            r.Say("go");
            r.RunUntil(() => false, 60_000);
            var c = r.Finish(tags);
            Report("2a one script, 200 lines", c);
            AllReachedEof(c, 200);
            Assert.Equal(201, c["A"].Evs);
            Assert.Equal(201, c["A"].Reqs);
            Assert.Equal(0, c["A"].Dropped);
        }
    }

    // 2b, single pass
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoReadersOfTheSameNotecardInOnePrim(bool computeTakesTime)
    {
        var (r, tags) = PrimOf(2, 200, computeTakesTime: computeTakesTime);
        using (r)
        {
            r.Say("go");
            r.RunUntil(() => false, 60_000);
            var c = r.Finish(tags);
            Report("2b two readers, same card, one pass, computeTakesTime=" + computeTakesTime, c);
            AllReachedEof(c, 200);
            foreach (var kv in c) Assert.Equal(0, kv.Value.Dropped);
        }
    }

    // 2b, restarting at EOF for 60 s of engine time
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoReadersRestartingAtEofFor60Seconds(bool computeTakesTime)
    {
        var (r, tags) = PrimOf(2, 200, restart: true, computeTakesTime: computeTakesTime);
        using (r)
        {
            r.Say("go");
            ulong start = r.Now;
            r.RunUntil(() => false, 60_000, 180);
            ulong spent = r.Now - start;
            var c = r.Finish(tags);
            Report($"2b two readers restarting at EOF, {spent} ms of engine time, computeTakesTime={computeTakesTime}", c);
            foreach (var kv in c) { Assert.True(kv.Value.Eofs >= 1, kv.Key + " no EOF"); Assert.Equal(0, kv.Value.Dropped); }
        }
    }

    // 2c
    [Fact]
    public void TwoReadersOfTheirOwnNotecardsInOnePrim()
    {
        var r = new Rig();
        using (r)
        {
            AddNotecard(r.H, r.H.Prim, "cardA", Card(200));
            AddNotecard(r.H, r.H.Prim, "cardB", Card(200));
            r.Rez(r.H.Prim, "A", Reader("A", "cardA"));
            r.Rez(r.H.Prim, "B", Reader("B", "cardB"));
            Assert.True(r.Started(new[] { "A", "B" }));
            r.Say("go");
            r.RunUntil(() => false, 60_000);
            var c = r.Finish(new[] { "A", "B" });
            Report("2c two readers, own cards", c);
            AllReachedEof(c, 200);
            foreach (var kv in c) Assert.Equal(0, kv.Value.Dropped);
        }
    }

    // 2d
    [Fact]
    public void EightReadersOfTheSameNotecardInOnePrim()
    {
        var (r, tags) = PrimOf(8, 200);
        using (r)
        {
            r.Say("go");
            r.RunUntil(() => false, 120_000);
            var c = r.Finish(tags);
            Report("2d eight readers, same card", c);
            AllReachedEof(c, 200);
            foreach (var kv in c) Assert.Equal(0, kv.Value.Dropped);
        }
    }

    // 2d again, on the engine's own clock: a script asleep for its read delay is lapped by the others, which read from the
    // cache without sleeping (the virtual clock above stands still while any script can run, so it never shows this).
    // Three rounds, as which readers are lapped changes from run to run. Every reader reaches EOF.
    [Fact]
    public void EightReadersOfTheSameNotecardInOnePrimOnTheRealClock()
    {
        for (int round = 1; round <= 3; round++)
        {
            var r = new Rig(realClock: true);
            using (r)
            {
                AddNotecard(r.H, r.H.Prim, "card", Card(200));
                string[] tags = Enumerable.Range(1, 8).Select(i => "r" + i).ToArray();
                foreach (string t in tags) r.Rez(r.H.Prim, t, Reader(t, report: true));
                Assert.True(r.Started(tags), string.Join(" | ", r.H.Said));
                r.Say("go");
                r.RunUntil(() => tags.All(t => r.Last(t).Eofs >= 1), 30_000);
                r.RunUntil(() => false, 2_000);
                var c = tags.ToDictionary(t => t, t => r.Last(t));
                Report("2d eight readers, same card, real clock, round " + round, c);
                foreach (string t in tags)
                    foreach (string l in r.FullLines(r.Tags.First(k => k.Value == t).Key).Take(2)) _out.WriteLine("  " + t + ": " + l);
                foreach (string t in tags)
                {
                    Assert.True(c[t].Evs <= 8 * 201, t + " got more answers than were sent: " + c[t]);
                    Assert.True(c[t].MaxDepth <= 64 + 8, t + " queue went past its limit: " + c[t]);
                    Assert.True(c[t].Eofs >= 1 && c[t].Own >= 201, "round " + round + ": " + t + " did not reach EOF: " + c[t]);
                }
            }
        }
    }
    // 2e
    [Fact]
    public void FourPrimsEachWithTwoReadersTakeOnlyTheirOwnPrimsAnswers()
    {
        var r = new Rig();
        using (r)
        {
            var prims = new List<SceneObjectPart> { r.H.Prim, r.AddChild("p2"), r.AddChild("p3"), r.AddChild("p4") };
            var tags = new List<string>();
            for (int p = 0; p < 4; p++)
            {
                AddNotecard(r.H, prims[p], "card", Card(200));
                foreach (string s in new[] { "a", "b" })
                {
                    string tag = s + p; tags.Add(tag);
                    r.Rez(prims[p], tag, Reader(tag));
                }
            }
            Assert.True(r.Started(tags));
            r.Say("go");
            r.RunUntil(() => false, 60_000);
            var c = r.Finish(tags.ToArray());
            Report("2e four prims x two readers", c);
            AllReachedEof(c, 200);
            // its own answers plus the other reader's of the same prim, nothing from the other three prims
            foreach (var kv in c) { Assert.Equal(0, kv.Value.Dropped); Assert.Equal(2 * 201, kv.Value.Evs); }
        }
    }

    // 2f
    [Fact]
    public void ScriptWithNoDataserverHandlerIsQueuedNothing()
    {
        var (r, tags) = PrimOf(2, 200);
        using (r)
        {
            r.Rez(r.H.Prim, "N", @"default { state_entry() { llSay(0, ""N entry""); } }");
            Assert.True(r.Started(new[] { "N" }));
            r.Say("go");
            r.RunUntil(() => false, 60_000);
            var c = r.Finish(tags);
            Report("2f readers A B and a script with no handler N", c);
            UUID n = r.Tags.First(k => k.Value == "N").Key;
            _out.WriteLine($"  N: highest queue depth {r.MaxDepthOf("N")}, dropped {r.Dropped(n)}");
            AllReachedEof(c, 200);
            Assert.Equal(0, r.MaxDepthOf("N"));
            Assert.Equal(0, r.Dropped(n));
        }
    }

    // 2g
    [Fact]
    public void TwoReadersWithALinkMessageEachSecond()
    {
        var (r, tags) = PrimOf(2, 200, source: i => Reader(((char)('A' + i)).ToString(), linkCount: true));
        using (r)
        {
            r.Rez(r.H.Prim, "L", LinkTicker);
            Assert.True(r.Started(new[] { "L" }));
            r.Say("go");
            r.RunUntil(() => false, 60_000);
            var c = r.Finish(tags);
            Report("2g two readers, one link message a second", c);
            AllReachedEof(c, 200);
            foreach (var kv in c) { Assert.Equal(0, kv.Value.Dropped); Assert.True(kv.Value.Links > 0, kv.Key + " got no link message"); }
        }
    }

    // 2g, restarting at EOF
    [Fact]
    public void TwoRestartingReadersWithALinkMessageEachSecond()
    {
        var (r, tags) = PrimOf(2, 200, source: i => Reader(((char)('A' + i)).ToString(), restart: true, linkCount: true));
        using (r)
        {
            r.Rez(r.H.Prim, "L", LinkTicker);
            Assert.True(r.Started(new[] { "L" }));
            r.Say("go");
            r.RunUntil(() => false, 60_000, 180);
            var c = r.Finish(tags);
            Report("2g two readers restarting at EOF, one link message a second", c);
            foreach (var kv in c) { Assert.Equal(0, kv.Value.Dropped); Assert.True(kv.Value.Links > 0, kv.Key + " got no link message"); }
        }
    }

    // 2h
    [Fact]
    public void ReadersThatDoNotCheckTheIdKeepAskingForEver()
    {
        var (r, tags) = PrimOf(2, 200, source: i => Reader(((char)('A' + i)).ToString(), checkId: false, restart: true, report: true));
        using (r)
        {
            r.Say("go");
            var series = new List<(ulong At, Counts A)>();
            ulong start = r.Now;
            for (int s = 1; s <= 6; s++)
            {
                r.RunUntil(() => false, 10_000, 180);
                series.Add((r.Now - start, r.Last("A")));
            }
            Report("2h two readers that do not check the id", new Dictionary<string, Counts> { ["A"] = r.Last("A"), ["B"] = r.Last("B") });
            foreach (var (at, a) in series) _out.WriteLine($"  at {at} ms: A requests {a.Reqs}, events {a.Evs}, dropped {a.Dropped}");
            _out.WriteLine($"  requests per engine second: A {series.Last().A.Reqs / (series.Last().At / 1000.0):F1}");
            Assert.True(series.Last().A.Reqs > 201 * 2, "requests did not outgrow a single pass");
        }
    }

    // 2j: a fast reader that never stops beside a reader that spends 50 ms per line. The slow reader's queue fills with the
    // fast reader's answers to requests that are not its own; those are dropped at half the limit, so its own answers still
    // fit and it reaches EOF.
    [Fact]
    public void ASlowReaderBesideAFastRestartingReaderStillReachesEof()
    {
        var r = new Rig();
        using (r)
        {
            AddNotecard(r.H, r.H.Prim, "card", Card(200));
            r.Rez(r.H.Prim, "F", Reader("F", restart: true, report: true));
            r.Rez(r.H.Prim, "S", Reader("S", sleepPerLine: 0.05, report: true));
            Assert.True(r.Started(new[] { "F", "S" }));
            r.Say("go");
            r.RunUntil(() => false, 60_000, 180);
            var c = new Dictionary<string, Counts> { ["F"] = r.Last("F"), ["S"] = r.Last("S") };
            Report("2j fast restarting reader F beside slow reader S (50 ms a line), 60 s", c);
            UUID s = r.Tags.First(k => k.Value == "S").Key;
            _out.WriteLine("  S: other scripts' answers dropped " + SavedStateRig.Exe(r.H).OtherAnswersDropped(s));
            Assert.True(c["S"].Eofs >= 1, "the slow reader never reached EOF: " + c["S"]);
            Assert.True(c["S"].Own >= 201, "the slow reader lost an answer of its own: " + c["S"]);
            Assert.True(c["S"].MaxDepth <= 64, "the slow reader's queue went past its limit: " + c["S"]);
            Assert.True(c["F"].Eofs > 1, "the fast reader did not keep reading: " + c["F"]);
            Assert.Equal(0, c["F"].Dropped);
        }
    }
    // 2i
    [Fact]
    public void EofAndOtherEdgesOfALineRead()
    {
        var r = new Rig();
        using (r)
        {
            AddNotecard(r.H, r.H.Prim, "three", "x\ny\nz");
            AddNotecard(r.H, r.H.Prim, "empty", "");
            AddEmbedded(r, "embedded", "embedded text one\nembedded text two");
            r.Rez(r.H.Prim, "A", @"
                key q; string name; integer line;
                default {
                    state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""A entry""); }
                    listen(integer c, string n, key k, string m) {
                        list p = llParseString2List(m, [""|""], []);
                        name = llList2String(p, 0); line = (integer)llList2String(p, 1);
                        q = llGetNotecardLine(name, line);
                    }
                    dataserver(key id, string d) {
                        if (id != q) return;
                        if (d == EOF) llSay(0, ""R "" + name + "" "" + (string)line + "" EOF"");
                        else llSay(0, ""R "" + name + "" "" + (string)line + "" ["" + d + ""]"");
                    }
                }");
            Assert.True(r.Started(new[] { "A" }));
            string[] asks = { "three|0", "three|2", "three|3", "three|100", "three|-1", "empty|0", "embedded|0", "embedded|1", "embedded|5" };
            foreach (string a in asks)
            {
                string want = "R " + a.Replace("|", " ") + " ";
                r.Say(a);
                Assert.True(r.RunUntil(() => r.H.Said.Any(s => s.StartsWith(want)), 10_000), "no answer to " + a);
                _out.WriteLine("  " + r.H.Said.First(s => s.StartsWith(want)));
            }
            Assert.Contains("R three 3 EOF", r.H.Said);
            Assert.Contains("R three 100 EOF", r.H.Said);
            Assert.Contains("R empty 0 EOF", r.H.Said);
            // SL (llGetNotecardLine): "If notecard contains embedded inventory items (such as textures and landmarks), EOF will be returned"
            Assert.Contains("R embedded 0 EOF", r.H.Said);
            Assert.Contains("R embedded 1 EOF", r.H.Said);
            Assert.Contains("R embedded 5 EOF", r.H.Said);
        }
    }

    private const string LinkCounter = @"
        integer ticks;
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSetTimerEvent(1.0); llSay(0, ""L entry""); }
            timer() { ++ticks; llMessageLinked(LINK_THIS, 0, ""tick"", NULL_KEY); }
            listen(integer c, string n, key k, string m) { if (m == ""tstop"") { llSetTimerEvent(0.0); llSay(0, ""L ticks "" + (string)ticks); } }
        }";

    // Eight readers restarting at EOF, one of them slow, and one link message a second to every script: every one arrives.
    [Fact]
    public void EveryLinkMessageArrivesBesideEightReaders()
    {
        var r = new Rig();
        using (r)
        {
            AddNotecard(r.H, r.H.Prim, "card", Card(200));
            string[] tags = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "S" };
            foreach (string t in tags)
                r.Rez(r.H.Prim, t, Reader(t, restart: true, linkCount: true, sleepPerLine: t == "S" ? 0.5 : 0));
            r.Rez(r.H.Prim, "L", LinkCounter);
            Assert.True(r.Started(tags.Append("L")), string.Join(" | ", r.H.Said));
            r.Say("go");
            r.RunUntil(() => false, 20_000, 180);
            Assert.True(r.RunUntil(() => { if (r.Now % 100 == 0) r.Say("tstop"); return r.H.Said.Any(s => s.StartsWith("L ticks ")); }, 5_000));
            int ticks = int.Parse(r.H.Said.First(s => s.StartsWith("L ticks ")).Split(' ')[2]);
            var c = r.Finish(tags);
            Report($"eight readers, one slow, {ticks} link messages sent to each", c);
            foreach (var kv in c) Assert.True(kv.Value.Links == ticks, kv.Key + " got " + kv.Value.Links + " of " + ticks + " link messages: " + kv.Value);
        }
    }

    // A script that asks for 200 lines in one handler, without waiting for an answer: its own answers keep the whole limit.
    [Fact]
    public void AScriptsOwnAnswersToTwoHundredRequestsAreStillLimitedAt64()
    {
        var r = new Rig();
        using (r)
        {
            AddNotecard(r.H, r.H.Prim, "card", Card(200));
            r.Rez(r.H.Prim, "A", @"
                integer got;
                default {
                    state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""A entry""); }
                    listen(integer c, string n, key k, string m) {
                        if (m == ""flood"") { integer i; for (i = 0; i < 200; i++) llGetNotecardLine(""card"", i); }
                        else if (m == ""count"") llSay(0, ""A evs "" + (string)got);
                    }
                    dataserver(key id, string d) { got++; }
                }");
            Assert.True(r.Started(new[] { "A" }));
            r.Say("flood");
            r.RunUntil(() => false, 5_000);
            r.RunUntil(() => false, 61_000);   // the drop run ends and writes its rest
            r.Say("count");
            Assert.True(r.RunUntil(() => r.H.Said.Any(s => s.StartsWith("A evs ")), 10_000));
            UUID a = r.Tags.First(k => k.Value == "A").Key;
            int got = int.Parse(r.H.Said.Last(s => s.StartsWith("A evs ")).Split(' ')[2]);
            long others = SavedStateRig.Exe(r.H).OtherAnswersDropped(a);
            _out.WriteLine($"  A: received {got}, highest depth {r.MaxDepthOf("A")}, dropped {r.Dropped(a)}, other scripts' answers dropped {others}");
            Assert.InRange(got, 64, 70);
            Assert.True(r.MaxDepthOf("A") <= 66, "the queue went past its limit");
            Assert.True(r.Dropped(a) >= 120, "its own answers were not limited at 64: dropped " + r.Dropped(a));
            Assert.Equal(0, others);
        }
    }

    private const string SleeperScript = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""S entry""); }
            listen(integer c, string n, key k, string m) { if (m == ""sleep"") llSleep(100000.0); }
            dataserver(key id, string d) { }
        }";

    /// <summary>A script put to sleep (the clock stands still), so what is posted to it stays queued.</summary>
    private static UUID AsleepScript(Rig r)
    {
        UUID s = r.Rez(r.H.Prim, "S", SleeperScript);
        Assert.True(r.Started(new[] { "S" }));
        r.Say("sleep");
        Assert.True(r.H.PumpUntil(() => r.H.RunStateOf(s) == "Sleeping"), "the script did not go to sleep: " + r.H.RunStateOf(s));
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(5));   // whatever was on its way to the queue has landed
        return s;
    }

    private static int Depth(Rig r, UUID s) => SavedStateRig.Exe(r.H).GetStatus(s).QueuedEvents;

    // Another script's answer is queued while the queue holds under 32 events and dropped from 32 on.
    [Fact]
    public void AnotherScriptsAnswersAreQueuedOnlyWhileTheQueueIsUnderHalfItsLimit()
    {
        var r = new Rig();
        using (r)
        {
            UUID s = AsleepScript(r);
            UUID other = UUID.Random();
            int baseline = Depth(r, s);
            for (int i = 0; i < 40; i++) r.H.Engine.PostDataserverToPrim(r.H.Prim, UUID.Zero, other, "q" + i, "x");
            var exe = SavedStateRig.Exe(r.H);
            Assert.True(r.H.PumpUntil(() => Depth(r, s) >= 32 || Depth(r, s) == baseline + 40), "depth " + Depth(r, s));
            r.H.PumpUntilIdle(TimeSpan.FromSeconds(5));
            _out.WriteLine($"  depth {Depth(r, s)}, other scripts' answers dropped {exe.OtherAnswersDropped(s)}, queue-full lines {r.FullLines(s).Length}");
            Assert.Equal(32, Depth(r, s));
            Assert.Equal(40 - (32 - baseline), exe.OtherAnswersDropped(s));
            Assert.Equal(40 - (32 - baseline), exe.GetStatus(s).OtherAnswersDropped);
            Assert.Empty(r.FullLines(s));
        }
    }

    // The asker's own answer is never marked, so it is queued past 32, up to 64.
    [Fact]
    public void TheAskersOwnAnswerIsQueuedPastHalfTheLimit()
    {
        var r = new Rig();
        using (r)
        {
            UUID s = AsleepScript(r);
            int baseline = Depth(r, s);
            for (int i = 0; i < 40; i++) r.H.Engine.PostDataserverToPrim(r.H.Prim, UUID.Zero, s, "q" + i, "x");
            Assert.True(r.H.PumpUntil(() => Depth(r, s) >= baseline + 40));
            r.H.PumpUntilIdle(TimeSpan.FromSeconds(5));
            Assert.Equal(baseline + 40, Depth(r, s));
            Assert.Equal(0, SavedStateRig.Exe(r.H).OtherAnswersDropped(s));
        }
    }

    // A dataserver event that answers no script's request (an object message, for one) is not another script's answer.
    [Fact]
    public void ADataserverEventThatAnswersNoRequestIsNotTreatedAsAnotherScriptsAnswer()
    {
        var r = new Rig();
        using (r)
        {
            UUID s = AsleepScript(r);
            int baseline = Depth(r, s);
            for (int i = 0; i < 40; i++)
                r.H.Engine.PostObjectEvent(r.H.Prim.LocalId,
                    new OpenSim.Region.ScriptEngine.Shared.EventParams("dataserver", new object[] { "m" + i, "x" },
                        new OpenSim.Region.ScriptEngine.Shared.DetectParams[0]));
            Assert.True(r.H.PumpUntil(() => Depth(r, s) >= baseline + 40));
            r.H.PumpUntilIdle(TimeSpan.FromSeconds(5));
            Assert.Equal(baseline + 40, Depth(r, s));
            Assert.Equal(0, SavedStateRig.Exe(r.H).OtherAnswersDropped(s));
        }
    }

    // At 64 the drops are reported as before; another script's answer at that depth is counted, not warned about.
    [Fact]
    public void DropsAt64AreReportedAsBeforeAndOtherScriptsAnswersAreOnlyCounted()
    {
        var r = new Rig();
        using (r)
        {
            UUID s = AsleepScript(r);
            int baseline = Depth(r, s);
            for (int i = 0; i < 64 - baseline; i++) r.H.Engine.PostDataserverToPrim(r.H.Prim, UUID.Zero, s, "q" + i, "x");
            Assert.True(r.H.PumpUntil(() => Depth(r, s) >= 64));
            for (int i = 0; i < 5; i++) r.H.Engine.PostDataserverToPrim(r.H.Prim, UUID.Zero, UUID.Random(), "o" + i, "x");
            for (int i = 0; i < 3; i++) r.H.Engine.PostDataserverToPrim(r.H.Prim, UUID.Zero, s, "p" + i, "x");
            Assert.True(r.H.PumpUntil(() => SavedStateRig.Exe(r.H).OtherAnswersDropped(s) >= 5 && r.Dropped(s) >= 3));
            Assert.Equal(64, Depth(r, s));
            Assert.Equal(5, SavedStateRig.Exe(r.H).OtherAnswersDropped(s));
            Assert.Equal(3, r.Dropped(s));
            Assert.Single(r.FullLines(s));
            Assert.Contains("dropped 3 DATASERVER events", r.FullLines(s)[0]);
        }
    }

    // The mark is not saved with a queued event.
    [Fact]
    public void TheMarkIsNotSavedWithAQueuedEvent()
    {
        var marked = new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.DATASERVER,
            Args = new object[] { "query", "data" },
            AnswersOtherScript = true,
        };
        var plain = new global::InWorldz.Phlox.VM.PostedEvent { EventType = marked.EventType, Args = new object[] { "query", "data" } };
        byte[] Bytes(global::InWorldz.Phlox.VM.PostedEvent e)
        {
            using var ms = new System.IO.MemoryStream();
            ProtoBuf.Serializer.Serialize(ms, global::InWorldz.Phlox.Serialization.SerializedPostedEvent.FromPostedEvent(e));
            return ms.ToArray();
        }
        Assert.Equal(Bytes(plain), Bytes(marked));
        // A restored event is built from the saved fields alone, so it comes back unmarked.
        Assert.Equal(typeof(global::InWorldz.Phlox.Serialization.SerializedPostedEvent).GetFields().Length,
            typeof(global::InWorldz.Phlox.Serialization.SerializedPostedEvent).GetFields().Count(f => f.Name != "AnswersOtherScript"));
    }

    /// <summary>A notecard asset in the form the viewer saves it, with one embedded item.</summary>
    private static void AddEmbedded(Rig r, string name, string text)
    {
        int bytes = Encoding.UTF8.GetByteCount(text);
        string asset = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 1\n{\next char index 0\n\titem_id\t"
            + UUID.Random() + "\n\tparent_id\t" + UUID.Zero + "\n\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t}\n\tname\tLandmark|\n\ttype\tlandmark\n}\n}\nText length "
            + bytes + "\n" + text + "}\n";
        var nc = OpenSim.Tests.Common.AssetHelpers.CreateAsset(UUID.Random(), AssetType.Notecard, "", UUID.Random());
        nc.Data = Encoding.UTF8.GetBytes(asset);
        r.H.Scene.AssetService.Store(nc);
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = nc.FullID, Name = name, Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard,
        }, false);
    }
}
