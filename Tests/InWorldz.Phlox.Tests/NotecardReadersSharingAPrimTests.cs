/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;
using static InWorldz.Phlox.Tests.InventoryGivesRig;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Several scripts in one prim each read the same notecard one line per dataserver event, and one of them sleeps for
/// part of its read: every reader finishes, with none of its own lines lost. SL (dataserver): "Dataserver requests
/// will trigger dataserver events in all scripts within the same prim where the request was made", so every reader also
/// gets every other reader's answers; SL (Category:LSL_Events): "If more that 64 events are waiting, new events are
/// discarded until free slots become available." While the sleeper sleeps, the others' answers fill its queue and the
/// rest of them are dropped; its own answer arrives straight after its own request and is kept.
/// This guards the notecard read delays: with a 0.1 s pause after each read the others are still reading when the
/// sleeper wakes, its own answer arrives while its queue is full of theirs, and its read stops.
/// Time is the engine's clock, moved by the test when nothing can run (and, in the second model, after every busy pump
/// too), so a sleep takes exactly its length of that clock. Work on another thread is not "nothing can run": a notecard
/// fetched on the thread pool, or an object event still with the pool, holds the clock until it lands, so a loaded
/// machine (a slow pool) does not use up the clock budget while the scripts wait for it.
/// The clock is process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class NotecardReadersSharingAPrimTests
{
    private readonly ITestOutputHelper _out;
    public NotecardReadersSharingAPrimTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] Tags = { "A", "B", "C", "D" };

    /// <summary>
    /// A reader. "go" on channel 7 starts its read at line 0; each answer to its own request asks for the next line;
    /// answers to anyone else's are counted. The sleeper sleeps 3 s after its line 10.
    /// </summary>
    private static string Reader(string tag, bool sleeper) => @"
        integer line; key q; integer own; integer others;
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, """ + tag + @" entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""go"") { line = 0; own = 0; others = 0; q = llGetNotecardLine(""card"", line); }
                else if (m == ""count"") llSay(0, """ + tag + @" own "" + (string)own + "" others "" + (string)others);
            }
            dataserver(key id, string d) {
                if (id != q) { others++; return; }
                if (d == EOF) { llSay(0, """ + tag + @" done "" + (string)line); return; }
                own++; line++;
                " + (sleeper ? "if (line == 10) llSleep(3.0);" : "") + @"
                q = llGetNotecardLine(""card"", line);
            }
        }";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public ulong Now = 1_000_000;
        /// <summary>Also move the clock 1 ms after every busy pump: compute takes time too.</summary>
        public bool ComputeTakesTime;
        public Rig()
        {
            Clock.SetSourceForTesting(() => Now);
            H = new SchedulerHarness();
        }

        /// <summary>
        /// Pump; move the clock 1 ms when nothing could run (or after every busy pump, see ComputeTakesTime). While work
        /// is on another thread (<see cref="WorkInFlight"/>) and nothing could run, the clock stands still.
        /// </summary>
        public bool RunUntil(Func<bool> done, ulong maxClockMs)
        {
            ulong until = Now + maxClockMs;
            var wall = DateTime.UtcNow.AddSeconds(120);
            while (!done())
            {
                if (Now >= until || DateTime.UtcNow > wall) return false;
                bool busy = H.PumpOnceBusy();
                if (busy ? ComputeTakesTime : !WorkInFlight()) Now++;
                else if (!busy) System.Threading.Thread.Yield();
            }
            return true;
        }

        /// <summary>
        /// A dataserver request still owed its answer (a notecard fetched on the thread pool) or an object event still
        /// with the pool (PhloxEngine.ObjectPostsInFlight). Read on this thread, which is the scheduler's here.
        /// </summary>
        private bool WorkInFlight()
            => H.Engine.ObjectPostsInFlight > 0
               || ((System.Collections.Generic.Dictionary<UUID, global::Phlox.ScriptEngine.LSLSystemAPI>)SavedStateRig.Field(
                       SavedStateRig.Exe(H), "m_Apis")).Values.Any(api => api.PendingDataserverCount > 0);

        public void Say(string msg)
            => H.Scene.SimChat(msg, OpenSim.Framework.ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

        public void Dispose() { try { H.Dispose(); } finally { Clock.SetSourceForTesting(null); } }
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    [InlineData(true, 2000)]
    public void EveryReaderFinishesWithNoneOfItsOwnLinesLost(bool computeTakesTime, int lines)
    {
        using var r = new Rig { ComputeTakesTime = computeTakesTime };
        AddNotecard(r.H, r.H.Prim, "card", string.Join("\n", Enumerable.Range(0, lines).Select(i => "line " + i)));
        foreach (string t in Tags) r.H.RezScriptInto(r.H.Prim, Reader(t, t == "D"));
        // Loading and compiling run on their own threads: the clock stands still until every reader is in.
        Assert.True(r.H.PumpUntil(() => Tags.All(t => r.H.Said.Contains(t + " entry"))), string.Join(" | ", r.H.Said));

        r.Say("go");
        Assert.True(r.RunUntil(() => Tags.All(t => r.H.Said.Any(s => s.StartsWith(t + " done "))), (ulong)lines * 1_000),
            "not every reader finished: " + string.Join(" | ", r.H.Said.Where(s => s.Contains(" done "))));
        r.Say("count");
        Assert.True(r.RunUntil(() => Tags.All(t => r.H.Said.Any(s => s.StartsWith(t + " own "))), 60_000), string.Join(" | ", r.H.Said));

        foreach (string t in Tags)
        {
            string[] w = r.H.Said.Last(s => s.StartsWith(t + " own ")).Split(' ');
            int own = int.Parse(w[2]), others = int.Parse(w[4]);
            _out.WriteLine($"{t}: own {own}/{lines}, others' answers {others}/{3 * (lines + 1)}" + (t == "D" ? " (sleeper)" : ""));
            Assert.Contains(t + " done " + lines, r.H.Said);
            Assert.Equal(lines, own);
            // The readers that never sleep drop nothing at all; the sleeper drops only answers to the others' requests.
            if (t != "D") Assert.Equal(3 * (lines + 1), others);
        }
    }
}
