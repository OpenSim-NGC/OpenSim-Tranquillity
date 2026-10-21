/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework.Tests;

/// <summary>
/// The gate behind lines that a remote party or a script can trigger without limit: the first occurrence in a
/// 60 second window is written in full, later ones are counted, and the next occurrence after the window is written
/// with the number held back. Each gate keeps one counter and one timestamp, whatever the callers send.
/// The gates under test take their own clock, so no test waits.
/// </summary>
public class RateLimitedLogTests
{
    private sealed class Clock
    {
        public long Ms = 5_000_000;
        public long Now() => Interlocked.Read(ref Ms);
        public void AdvanceSeconds(int seconds) => Interlocked.Add(ref Ms, seconds * 1000L);
        public void AdvanceMs(int ms) => Interlocked.Add(ref Ms, ms);
    }

    private sealed class Recorder : ILogger
    {
        public readonly List<(LogLevel Level, string Text, Exception? Exception)> Entries = new();
        public LogLevel Minimum = LogLevel.Trace;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }

        public List<string> Texts() { lock (Entries) return Entries.Select(e => e.Text).ToList(); }
    }

    [Fact]
    public void TheFirstOccurrenceIsWrittenInFullAtItsOwnLevel()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        gate.Log(log, LogLevel.Warning, "refused {0} from {1}", "link_region", "192.0.2.7");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("refused link_region from 192.0.2.7", entry.Text);
    }

    [Fact]
    public void RepeatsInsideTheWindowAreCountedNotWritten()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        for (int i = 0; i < 1000; i++)
        {
            gate.Log(log, LogLevel.Information, "refused {0}", i);
            clock.AdvanceMs(50);   // 1000 calls over 50 s
        }

        Assert.Equal(new[] { "refused 0" }, log.Texts());
    }

    [Fact]
    public void TheNextOccurrenceAfterTheWindowCarriesTheCount()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        for (int i = 0; i < 500; i++) gate.Log(log, LogLevel.Information, "refused {0}", i);
        clock.AdvanceSeconds(RateLimitedLog.WindowSeconds - 1);
        gate.Log(log, LogLevel.Information, "refused {0}", "late");
        Assert.Single(log.Entries);                  // 59 s: still inside the window

        clock.AdvanceSeconds(1);
        gate.Log(log, LogLevel.Information, "refused {0}", "after");

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("refused after (500 more like this in the last 60 s)", log.Entries[1].Text);
        Assert.Equal(LogLevel.Information, log.Entries[1].Level);
    }

    [Fact]
    public void AnOccurrenceAfterAQuietWindowIsWrittenWithoutASummary()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        gate.Log(log, LogLevel.Information, "refused");
        clock.AdvanceSeconds(600);
        gate.Log(log, LogLevel.Information, "refused");

        Assert.Equal(new[] { "refused", "refused" }, log.Texts());
    }

    [Fact]
    public void ASummaryNamesTheSecondsSinceTheLastWrittenLine()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        gate.Log(log, LogLevel.Information, "refused");
        gate.Log(log, LogLevel.Information, "refused");
        clock.AdvanceSeconds(95);
        gate.Log(log, LogLevel.Information, "refused");

        Assert.Equal("refused (1 more like this in the last 95 s)", log.Entries[1].Text);
    }

    [Fact]
    public void ANewWindowStartsAtTheLineThatWasWritten()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        gate.Log(log, LogLevel.Information, "refused");
        clock.AdvanceSeconds(60);
        gate.Log(log, LogLevel.Information, "refused");     // written, opens a window
        clock.AdvanceSeconds(30);
        gate.Log(log, LogLevel.Information, "refused");     // inside it
        clock.AdvanceSeconds(29);
        gate.Log(log, LogLevel.Information, "refused");     // still inside (59 s)
        Assert.Equal(2, log.Entries.Count);

        clock.AdvanceSeconds(1);
        gate.Log(log, LogLevel.Information, "refused");
        Assert.Equal(3, log.Entries.Count);
        Assert.EndsWith("(2 more like this in the last 60 s)", log.Entries[2].Text);
    }

    [Fact]
    public void TwoGatesAreIndependent()
    {
        var clock = new Clock();
        var a = new RateLimitedLog(clock.Now);
        var b = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        for (int i = 0; i < 20; i++) a.Log(log, LogLevel.Information, "a");
        for (int i = 0; i < 5; i++) b.Log(log, LogLevel.Information, "b");
        Assert.Equal(new[] { "a", "b" }, log.Texts());

        clock.AdvanceSeconds(60);
        a.Log(log, LogLevel.Information, "a");
        b.Log(log, LogLevel.Information, "b");
        Assert.Equal(new[] { "a", "b", "a (19 more like this in the last 60 s)", "b (4 more like this in the last 60 s)" },
            log.Texts());
    }

    [Fact]
    public void TheExceptionOverloadKeepsTheExceptionOnTheWrittenLine()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();
        var first = new InvalidOperationException("first");

        gate.Log(log, LogLevel.Warning, first, "failed {0}", 1);
        gate.Log(log, LogLevel.Warning, new InvalidOperationException("second"), "failed {0}", 2);
        clock.AdvanceSeconds(60);
        var third = new InvalidOperationException("third");
        gate.Log(log, LogLevel.Warning, third, "failed {0}", 3);

        Assert.Equal(2, log.Entries.Count);
        Assert.Same(first, log.Entries[0].Exception);
        Assert.Same(third, log.Entries[1].Exception);
        Assert.Equal("failed 3 (1 more like this in the last 60 s)", log.Entries[1].Text);
    }

    [Fact]
    public void ALevelThatIsOffDoesNotUseUpTheWindow()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder { Minimum = LogLevel.Warning };

        gate.Log(log, LogLevel.Information, "quiet");
        log.Minimum = LogLevel.Information;
        gate.Log(log, LogLevel.Information, "loud");

        Assert.Equal(new[] { "loud" }, log.Texts());
    }

    [Fact]
    public void ManyCallersAtOnceWriteOneLineAndCountTheRest()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();
        const int threads = 16, perThread = 5_000;

        var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++) gate.Log(log, LogLevel.Information, "refused");
        })).ToArray();
        Task.WaitAll(workers);
        Assert.Single(log.Entries);

        clock.AdvanceSeconds(60);
        gate.Log(log, LogLevel.Information, "refused");
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal($"refused ({threads * perThread - 1} more like this in the last 60 s)", log.Entries[1].Text);
    }

    [Fact]
    public void ManyCallersRacingAtTheEndOfAWindowWriteExactlyOneSummary()
    {
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();
        for (int i = 0; i < 100; i++) gate.Log(log, LogLevel.Information, "refused");
        clock.AdvanceSeconds(60);

        var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            gate.Log(log, LogLevel.Information, "refused");
        })).ToArray();
        start.Set();
        Task.WaitAll(workers);

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("refused (99 more like this in the last 60 s)", log.Entries[1].Text);
    }

    [Fact]
    public void AGateKeepsNoStatePerValueTheCallerSends()
    {
        // The gate has no key: a hundred thousand different messages leave it one counter and one timestamp, so
        // what a remote party chooses to send cannot grow it.
        var clock = new Clock();
        var gate = new RateLimitedLog(clock.Now);
        var log = new Recorder();

        for (int i = 0; i < 100_000; i++)
            gate.Log(log, LogLevel.Information, "from {0}", "host-" + i + ".example.org");

        Assert.Single(log.Entries);
        var collections = typeof(RateLimitedLog)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType));
        Assert.Empty(collections);
    }
}
