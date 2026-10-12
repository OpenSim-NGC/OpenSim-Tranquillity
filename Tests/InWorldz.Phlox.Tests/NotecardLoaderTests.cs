/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Two scripts in one prim, each loading three notecards of 100 lines a line at a time (the next line is asked for from
/// the dataserver handler, whose query id is checked), and each sending the other one link message for every line it
/// reads. Each says "loaded" when all three of its cards have reached EOF. Every variant runs on the engine's own clock
/// (moved by the test) and reports whether both loaded, the engine time taken, each script's highest queue depth, the
/// events dropped at 64 by type, the answers of another script dropped at half the limit, the link messages lost, and
/// the time each script slept, by length of sleep (each cause of a sleep here has its own length: 100 ms a notecard call
/// by default, 25 ms or 1 ms with NotecardPause = halcyon, 50 ms link-message back-pressure, 20 ms the handler's work).
/// (llMessageLinked: "in all scripts in the prim(s) described by link"; Category:LSL_Events: "If more that 64 events are
/// waiting, new events are discarded until free slots become available.") The clock and the log are process-wide,
/// hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public partial class NotecardLoaderTests
{
    private readonly ITestOutputHelper _out;
    public NotecardLoaderTests(ITestOutputHelper o) => _out = o;

    private const int Lines = 100;
    private const int Cards = 3;

    /// <summary>
    /// One loader. "go" on channel 7 starts it; "count" says its counters. Sequential: one card after the other, one
    /// request outstanding. Concurrent: the three cards at once, three requests outstanding. Each line read is sent to
    /// the prim as one link message numbered <paramref name="myNum"/>; a link message numbered <paramref name="peerNum"/> is
    /// the other loader's, and is stored (and costs <paramref name="workSeconds"/> of handler time).
    /// </summary>
    private static string Loader(string tag, int myNum, int peerNum, bool concurrent, bool links, double workSeconds, int cards = 3)
    {
        string work = workSeconds > 0 ? "llSleep(" + workSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");" : "";
        string send = links ? "sent++; llMessageLinked(LINK_THIS, " + myNum + ", d, NULL_KEY);" : "";
        string counters = @"llSay(0, """ + tag + @" st ci "" + (string)ci + "" line "" + (string)line + "" reqs "" + (string)reqs + "" own "" + (string)own
                    + "" evs "" + (string)evs + "" sent "" + (string)sent + "" got "" + (string)got + "" fin "" + (string)fin + "" eofc "" + (string)eofc
                    + "" l "" + (string)l0 + "","" + (string)l1 + "","" + (string)l2);";
        string ask, start, onAnswer;
        if (!concurrent)
        {
            ask = @"Ask() { reqs++; q0 = llGetNotecardLine(""c"" + (string)(ci + 1), line); }";
            start = "Ask();";
            onAnswer = @"
                if (id != q0) return;
                own++;
                if (d == EOF) { ci++; line = 0; eofc++; if (ci >= " + cards + @") { fin = 1; llSay(0, """ + tag + @" loaded""); return; } Ask(); return; }
                " + send + @"
                line++; Ask();";
        }
        else
        {
            ask = "";
            start = @"reqs += 3; q0 = llGetNotecardLine(""c1"", 0); q1 = llGetNotecardLine(""c2"", 0); q2 = llGetNotecardLine(""c3"", 0);";
            onAnswer = @"
                integer k = -1;
                if (id == q0) k = 0; else if (id == q1) k = 1; else if (id == q2) k = 2;
                if (k < 0) return;
                own++;
                if (d == EOF) { eofc++; if (eofc == 3) { fin = 1; llSay(0, """ + tag + @" loaded""); } return; }
                " + send + @"
                reqs++;
                if (k == 0) { l0++; q0 = llGetNotecardLine(""c1"", l0); }
                else if (k == 1) { l1++; q1 = llGetNotecardLine(""c2"", l1); }
                else { l2++; q2 = llGetNotecardLine(""c3"", l2); }";
        }
        return @"
        integer ci; integer line; integer l0; integer l1; integer l2; key q0; key q1; key q2;
        integer reqs; integer own; integer evs; integer sent; integer got; integer fin; integer eofc; list vals;
        " + ask + @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, """ + tag + @" entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""go"") { " + start + @" }
                else if (m == ""count"") { " + counters + @" }
            }
            dataserver(key id, string d) {
                evs++;
                " + onAnswer + @"
            }
            link_message(integer s, integer n, string t, key i) {
                if (n != " + peerNum + @") return;
                got++; vals += [t];
                " + work + @"
            }
        }";
    }

    private sealed class Stats
    {
        public int Ci, Line, Reqs, Own, Evs, Sent, Got, Fin, Eofc;
        public string Lines3 = "";
        public override string ToString()
            => $"cards done {Eofc}, line {Line}, l {Lines3}, requests {Reqs}, own answers {Own}, answers seen {Evs}, link sent {Sent}, link stored {Got}, loaded {Fin}";
    }

    private sealed class ScriptResult
    {
        public string Tag;
        public UUID Id;
        public bool Loaded;
        public ulong LoadedAt;
        public int MaxDepth;
        public Dictionary<string, int> DroppedAt64 = new();
        public long OtherAnswersDropped;
        public SortedDictionary<int, (int Count, long Ms)> Sleeps = new();
        public long WaitingMs;     // idle, nothing to run
        public Stats Counters;     // as the script says them at the end (null: no answer)
        public string EndState;    // scheduler's status at the end
        public int EndDepth;
        public long TotalSleepMs => Sleeps.Sum(s => s.Value.Ms);
    }

    private sealed class Result
    {
        public bool BothLoaded;
        public ulong TimeMs;
        public ScriptResult A, B;
        public int LinksLost;
        public int WarnLines;
        public string[] OtherWarnings;
    }

    private static readonly int[] KnownSleeps = { 100, 50, 25, 20, 15, 1 };

    private static string Why(int ms) => ms switch
    {
        15 => "chat (llSay), 15 ms",
        100 => "notecard call, SL's 0.1 s",
        50 => "link-message back-pressure",
        25 => "notecard fetched, Halcyon's 25 ms",
        1 => "notecard cached, Halcyon's 1 ms (every 16th line)",
        20 => "the handler's 20 ms of work",
        _ => "other"
    };

    private static object Probe(object o, string name)
        => o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o)
           ?? o?.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o);

    private static readonly Regex StatsLine = new(@"^(\w) st ci (\d+) line (\d+) reqs (\d+) own (\d+) evs (\d+) sent (\d+) got (\d+) fin (\d+) eofc (\d+) l (\d+),(\d+),(\d+)$");

    private static Stats ParseStats(string line)
    {
        Match m = StatsLine.Match(line);
        var g = m.Groups;
        return new Stats
        {
            Ci = int.Parse(g[2].Value), Line = int.Parse(g[3].Value), Reqs = int.Parse(g[4].Value), Own = int.Parse(g[5].Value),
            Evs = int.Parse(g[6].Value), Sent = int.Parse(g[7].Value), Got = int.Parse(g[8].Value), Fin = int.Parse(g[9].Value),
            Eofc = int.Parse(g[10].Value), Lines3 = g[11].Value + "," + g[12].Value + "," + g[13].Value
        };
    }

    /// <summary>Runs one variant for at most <paramref name="capMs"/> of engine time and reports it.</summary>
    private Result Run(string title, bool halcyon, bool concurrent = false, bool links = true, double workSeconds = 0,
        bool throttle = true, ulong capMs = 120_000, int cards = Cards, int lines = Lines, bool realClock = false, int msPerPump = 1)
    {
        using var r = new NotecardFloodTests.Rig(cfg =>
        {
            if (!throttle) cfg.Configs["InWorldz.Phlox"].Set("LinkMessageThrottle", "false");
        }, realClock: realClock, halcyon: halcyon);
        for (int c = 1; c <= (concurrent ? 3 : cards); c++)
            AddNotecard(r.H, r.H.Prim, "c" + c, string.Join("\n", Enumerable.Range(0, lines).Select(i => "line " + i)));
        string[] tags = { "A", "B" };
        r.Rez(r.H.Prim, "A", Loader("A", 1, 2, concurrent, links, workSeconds, cards));
        r.Rez(r.H.Prim, "B", Loader("B", 2, 1, concurrent, links, workSeconds, cards));
        return Execute(title, r, halcyon, concurrent, links, workSeconds, throttle, capMs, tags, realClock, msPerPump);
    }

    private Result Execute(string title, NotecardFloodTests.Rig r, bool halcyon, bool concurrent, bool links, double workSeconds,
        bool throttle, ulong capMs, string[] tags, bool realClock = false, int msPerPump = 1)
    {
        Assert.True(r.Started(tags), string.Join(" | ", r.H.Said));
        var exe = SavedStateRig.Exe(r.H);
        var res = new Result { A = new ScriptResult { Tag = "A" }, B = new ScriptResult { Tag = "B" } };
        var scripts = new[] { res.A, res.B };
        foreach (var s in scripts) s.Id = r.Tags.First(k => k.Value == s.Tag).Key;

        var lastWake = new Dictionary<UUID, ulong>();
        var lastSample = new Dictionary<UUID, (ulong Now, string State)>();
        ulong NowMs() => realClock ? InWorldz.Phlox.Util.Clock.Now : r.Now;
        ulong prevNow = NowMs(), nextLine = NowMs();
        r.Say("go");
        ulong start = NowMs();
        var series = new List<string>();

        bool Done()
        {
            ulong now = NowMs();
            foreach (var s in scripts)
            {
                var st = exe.GetStatus(s.Id);
                object state = r.H.StateOf(s.Id);
                string run = Probe(state, "RunState")?.ToString();
                if (run == "Sleeping")
                {
                    ulong wake = (ulong)Probe(state, "NextWakeup");
                    if (!lastWake.TryGetValue(s.Id, out ulong w) || w != wake)
                    {
                        lastWake[s.Id] = wake;
                        // The sleep was begun in the pass just run, at a clock between the two samples: its length is the
                        // one of the known lengths that fits, else the longest it could be.
                        long hi = (long)wake - (long)prevNow, lo = (long)wake - (long)now;
                        int ms = (int)hi;
                        foreach (int known in KnownSleeps)
                            if (known >= lo && known <= hi) { ms = known; break; }
                        s.Sleeps.TryGetValue(ms, out var cur);
                        s.Sleeps[ms] = (cur.Count + 1, cur.Ms + ms);
                    }
                }
                if (lastSample.TryGetValue(s.Id, out var ls) && ls.State == "Waiting") s.WaitingMs += (long)(now - ls.Now);
                lastSample[s.Id] = (now, run);
                if (!s.Loaded && r.H.Said.Contains(s.Tag + " loaded")) { s.Loaded = true; s.LoadedAt = now - start; }
                if (now >= nextLine)
                {
                    series.Add($"  t={now - start,6} ms {s.Tag}: {run,-8} queued {st.QueuedEvents,2} other answers dropped {st.OtherAnswersDropped}");
                    if (s == scripts[1]) nextLine += 5000;
                }
            }
            // msPerPump > 1: the scheduler is called less often than every millisecond.
            if (!realClock && msPerPump > 1) r.Now += (ulong)(msPerPump - 1);
            prevNow = NowMs();
            return scripts.All(s => s.Loaded);
        }

        res.BothLoaded = r.RunUntil(Done, capMs, 600);
        res.TimeMs = NowMs() - start;

        // Settle until both queues are empty and both scripts idle (the one that finished first may still be working through
        // the other's link messages), then ask each loader for its counters (a stalled one may not hear: the status below is
        // then all there is).
        r.RunUntil(() => scripts.All(s => { var st = exe.GetStatus(s.Id); return st.QueuedEvents == 0 && st.RunState == "Waiting"; }), 120_000, 300);
        // A run of queue-full drops writes its last count only after a minute with no drop: wait that minute (engine time).
        r.RunUntil(() => false, realClock ? 1_000UL : 62_000UL, 120);
        foreach (var s in scripts)
        {
            var st = exe.GetStatus(s.Id);
            s.EndState = st.RunState;
            s.EndDepth = st.QueuedEvents;
        }
        r.Say("count");
        r.RunUntil(() => tags.All(t => r.H.Said.Any(x => StatsLine.IsMatch(x) && x.StartsWith(t + " st "))), 5_000, 120);
        foreach (var s in scripts)
        {
            string line = r.H.Said.LastOrDefault(x => x.StartsWith(s.Tag + " st ") && StatsLine.IsMatch(x));
            s.Counters = line == null ? null : ParseStats(line);
            s.MaxDepth = r.MaxDepthOf(s.Tag);
            s.OtherAnswersDropped = exe.GetStatus(s.Id).OtherAnswersDropped;
            foreach (string full in r.FullLines(s.Id))
                foreach (Match m in Regex.Matches(full.Substring(full.IndexOf("dropped ") + 8), @"(\d+) ([A-Z_]+)"))
                    s.DroppedAt64[m.Groups[2].Value] = s.DroppedAt64.GetValueOrDefault(m.Groups[2].Value) + int.Parse(m.Groups[1].Value);
        }
        res.OtherWarnings = r.OtherWarnings();
        res.WarnLines = scripts.Sum(s => r.FullLines(s.Id).Length);
        if (links && res.A.Counters != null && res.B.Counters != null)
            res.LinksLost = (res.A.Counters.Sent - res.B.Counters.Got) + (res.B.Counters.Sent - res.A.Counters.Got);

        Report(title, halcyon, concurrent, links, workSeconds, throttle, res, series);
        return res;
    }

    private void Report(string title, bool halcyon, bool concurrent, bool links, double work, bool throttle, Result res, List<string> series)
    {
        _out.WriteLine($"== {title}: NotecardPause {(halcyon ? "halcyon" : "sl")}, {(concurrent ? "three cards at once" : "cards one after another")}, "
            + $"link messages {(links ? "on" : "off")}, handler work {work * 1000:F0} ms, LinkMessageThrottle {(throttle ? "on" : "off")}");
        _out.WriteLine($"   both loaded: {res.BothLoaded}; engine time to {(res.BothLoaded ? "both loaded" : "the cap")}: {res.TimeMs} ms");
        foreach (var s in new[] { res.A, res.B })
        {
            _out.WriteLine($"   {s.Tag}: loaded {s.Loaded}{(s.Loaded ? $" at {s.LoadedAt} ms" : "")}; highest queue depth {s.MaxDepth}; "
                + $"dropped at 64: {(s.DroppedAt64.Count == 0 ? "none" : string.Join(", ", s.DroppedAt64.Select(d => d.Value + " " + d.Key)))}; "
                + $"other scripts' answers dropped at 32: {s.OtherAnswersDropped}");
            _out.WriteLine($"      slept {s.TotalSleepMs} ms in {s.Sleeps.Sum(x => x.Value.Count)} sleeps: "
                + (s.Sleeps.Count == 0 ? "none" : string.Join("; ", s.Sleeps.Select(x => $"{x.Value.Count} x {x.Key} ms ({Why(x.Key)}) = {x.Value.Ms} ms"))));
            _out.WriteLine($"      idle waiting for an event {s.WaitingMs} ms; at the end: {s.EndState}, {s.EndDepth} queued; counters: {(s.Counters?.ToString() ?? "no answer")}");
            if (!s.Loaded && s.Counters != null)
                _out.WriteLine($"      stopped at card {s.Counters.Eofc + 1}, line {s.Counters.Line}; {(s.Counters.Reqs > s.Counters.Own ? $"waiting for {s.Counters.Reqs - s.Counters.Own} answer(s) it asked for and never got" : "nothing outstanding")}");
        }
        _out.WriteLine($"   link messages lost: {(links ? res.LinksLost.ToString() : "n/a (none sent)")}; queue-full lines: {res.WarnLines}; "
            + $"other WARN/ERROR lines: {(res.OtherWarnings.Length == 0 ? "none" : string.Join(" | ", res.OtherWarnings))}");
        foreach (string s in series) _out.WriteLine(s);
    }

    private static void BothLoaded(Result r)
    {
        Assert.True(r.BothLoaded, $"A loaded {r.A.Loaded}, B loaded {r.B.Loaded}: A {r.A.Counters}; B {r.B.Counters}");
    }

    private static void NothingLost(Result r)
    {
        Assert.Empty(r.A.DroppedAt64);
        Assert.Empty(r.B.DroppedAt64);
        Assert.Equal(0, r.LinksLost);
        Assert.Equal(0, r.WarnLines);
    }

    /// <summary>With SL's pause every notecard call sleeps 0.1 s: 3 x 100 lines and 3 EOF answers are 303 calls.</summary>
    private static void SlSleptTheNotecardPause(Result r)
    {
        foreach (var s in new[] { r.A, r.B })
            Assert.Equal((303, 30300L), s.Sleeps.GetValueOrDefault(100));
        Assert.InRange(r.TimeMs, 30_000UL, 40_000UL);
    }

    // b: cards read one after another, NotecardPause = sl
    [Fact]
    public void B_OneAfterAnother_Sl()
    {
        var r = Run("b", halcyon: false);
        BothLoaded(r);
        NothingLost(r);
        SlSleptTheNotecardPause(r);
        Assert.True(r.A.MaxDepth < 32 && r.B.MaxDepth < 32, $"depths {r.A.MaxDepth}, {r.B.MaxDepth}");
    }

    // d: all three at once, sl. Three requests outstanding do not shorten it: each call still sleeps 0.1 s.
    [Fact]
    public void D_AllAtOnce_Sl()
    {
        var r = Run("d", halcyon: false, concurrent: true);
        BothLoaded(r);
        NothingLost(r);
        SlSleptTheNotecardPause(r);
    }
}
