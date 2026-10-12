/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script reads a 500-line notecard, asking for the next line from its dataserver handler and checking the query
/// id, while another thread adds items to the same prim's inventory and removes them again as fast as it can. The
/// core's inventory writers (SceneObjectPartInventory.AddInventoryItem, RemoveInventoryItem) change the dictionary
/// under its reader-writer lock (TaskInventoryDictionary.LockItemsForWrite); a script call that walks the dictionary
/// without that lock can be broken by such a change ("Collection was modified", or a walk that misses the notecard),
/// and then no answer reaches the script. Each case reports whether the read reached the end of the notecard with
/// every answer, the script's run state and queued events at the end, the churn thread's count of changes, and every
/// line the engine logged. Run with the notecard in the region's cache (the answer is posted on the script's own
/// thread) and without it (each answer is fetched and posted from the thread pool).
/// The log is process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class NotecardReadUnderInventoryChurnTests
{
    private const int Lines = 500;
    private const int Fillers = 64;

    private readonly ITestOutputHelper _out;
    public NotecardReadUnderInventoryChurnTests(ITestOutputHelper o) => _out = o;

    public static IEnumerable<object[]> Runs()
    {
        foreach (bool cached in new[] { true, false })
            for (int run = 1; run <= 20; run++)
                yield return new object[] { cached, run };
    }

    /// <summary>
    /// The reader: "go" on channel 7 starts it at line 0; each answer to its own request asks for the next line, one
    /// from another request is ignored; at the end of the notecard it says how many answers it had and the last line asked.
    /// "count" says how far it has got.
    /// </summary>
    private const string Reader = @"
        integer line; key q; integer own;
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""A entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""go"") { line = 0; own = 0; q = llGetNotecardLine(""card"", line); }
                else if (m == ""count"") llSay(0, ""A evs 0 own "" + (string)own + "" line "" + (string)line);
            }
            dataserver(key id, string d) {
                if (id != q) return;
                own++;
                if (d == EOF) { llSay(0, ""A done "" + (string)own + "" "" + (string)line); return; }
                line++;
                q = llGetNotecardLine(""card"", line);
            }
        }";
    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> m_lines = new();
        public string[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this, category);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            private readonly string m_category;
            public L(Capture c, string category) { m_c = c; m_category = category; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                // Chatty debug lines are not what this reports on.
                if (logLevel < LogLevel.Information) return;
                lock (m_c.m_lines) m_c.m_lines.Add($"{logLevel} {formatter(state, exception)}");
            }
        }
    }

    private static TaskInventoryItem NewItem(SceneObjectPart prim, string name) => new TaskInventoryItem
    {
        ItemID = UUID.Random(),
        AssetID = UUID.Random(),
        Name = name,
        Type = (int)AssetType.Notecard,
        InvType = (int)InventoryType.Notecard,
        OwnerID = prim.OwnerID,
        CreatorID = prim.OwnerID,
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public void AReaderOfAFiveHundredLineNotecardLosesNoAnswerWhileTheInventoryChanges(bool cached, int run)
    {
        var log = new Capture();
        ILoggerFactory previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = log;
        var stop = new ManualResetEventSlim(false);
        Thread churn = null;
        try
        {
            using var h = new SchedulerHarness(cfg =>
            {
                var p = cfg.Configs["InWorldz.Phlox"];
                p.Set("NotecardThrottle", "false");           // no pause per read: as many reads as the thread can make
                p.Set("NotecardCache", cached ? "true" : "false");
            });
            SceneObjectPart prim = h.Prim;

            // The notecard comes after the fillers, so a walk of the dictionary passes them before it finds it.
            for (int i = 0; i < Fillers; i++) prim.Inventory.AddInventoryItem(NewItem(prim, "filler " + i), false);
            AddNotecard(h, prim, "card", string.Join("\n", Enumerable.Range(0, Lines).Select(i => "line " + i)));
            UUID script = h.RezScriptInto(prim, Reader);
            Assert.True(h.PumpUntil(() => h.Said.Contains("A entry")), string.Join(" | ", h.Said));

            long changes = 0;
            churn = new Thread(() =>
            {
                var live = new Queue<UUID>();
                int n = 0;
                while (!stop.IsSet)
                {
                    TaskInventoryItem item = NewItem(prim, "churn " + (n++ % 10_000));
                    prim.Inventory.AddInventoryItem(item, false);
                    live.Enqueue(item.ItemID);
                    changes++;
                    if (live.Count > 32)
                    {
                        prim.Inventory.RemoveInventoryItem(live.Dequeue());
                        changes++;
                    }
                }
            }) { IsBackground = true, Name = "inventory churn" };
            churn.Start();

            h.Scene.SimChat("go", ChatTypeEnum.Region, 7, prim.AbsolutePosition, "tester", UUID.Random(), false);
            bool reachedEnd = h.PumpUntil(() => h.Said.Any(s => s.StartsWith("A done ")) || h.StatusOf(script).Contains("terminated=") && !h.StatusOf(script).EndsWith("terminated=-"),
                TimeSpan.FromSeconds(30));
            stop.Set();
            churn.Join();

            string status = h.StatusOf(script);
            string done = h.Said.FirstOrDefault(s => s.StartsWith("A done "));
            bool allAnswers = false;
            if (done != null)
            {
                string[] w = done.Split(' ');   // "A done <answers received> <last line asked for>"
                allAnswers = int.Parse(w[2]) == Lines + 1 && int.Parse(w[3]) == Lines;   // every line's answer, and the EOF
            }            else if (!status.Contains("Killed"))
            {
                // Still reading: how far it got.
                h.Scene.SimChat("count", ChatTypeEnum.Region, 7, prim.AbsolutePosition, "tester", UUID.Random(), false);
                h.PumpUntil(() => h.Said.Any(s => s.StartsWith("A evs ")), TimeSpan.FromSeconds(5));
                string progress = h.Said.LastOrDefault(s => s.StartsWith("A evs "));
                _out.WriteLine("  progress: " + (progress ?? "no answer to count"));
            }

            _out.WriteLine($"cached={cached} run={run}: reachedEnd={reachedEnd} done='{done}' allAnswers={allAnswers}");
            _out.WriteLine($"  status: {status}; runState={h.RunStateOf(script)}; inventory changes made meanwhile: {Interlocked.Read(ref changes)}");
            foreach (string s in h.Said.Where(s => !s.StartsWith("A entry") && !s.StartsWith("A evs "))) _out.WriteLine("  said: " + s);
            // Every warning and error the engine wrote, with the top of any stack trace; the start-up information lines are
            // counted, not listed.
            string[] lines = log.Lines;
            foreach (string l in lines.Where(l => !l.StartsWith("Information ")).Take(12))
                _out.WriteLine("  log: " + string.Join(" / ", l.Split('\n').Take(6).Select(x => x.Trim())));
            _out.WriteLine($"  log: {lines.Count(l => l.StartsWith("Information "))} information lines not listed");

            Assert.True(reachedEnd, "the read did not reach the end of the notecard: " + status);
            Assert.True(allAnswers, "an answer was lost: " + status);
            Assert.EndsWith("terminated=-", status);
        }
        finally
        {
            stop.Set();
            churn?.Join();
            LoggerProvider.LoggerFactory = previous;
        }
    }
}
