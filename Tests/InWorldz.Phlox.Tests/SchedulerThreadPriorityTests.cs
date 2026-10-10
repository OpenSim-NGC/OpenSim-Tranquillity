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
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// [InWorldz.Phlox] SchedulerThreadPriority: the priority of the one thread that runs every script in the region
/// (PhloxMasterScheduler). It takes the names of System.Threading.ThreadPriority, case ignored. Unset, it is Lowest,
/// the priority that thread always had, so a region that does not set it runs as before. Any other value logs one
/// warning at start and uses Lowest.
///
/// <para>The class swaps the process's logger factory (LoggerProvider.LoggerFactory) to read the start-up lines, and
/// that is process-wide, hence "phlox-state".</para>
/// </summary>
[Collection("phlox-state")]
public class SchedulerThreadPriorityTests
{
    private const string Key = "SchedulerThreadPriority";

    // By reflection, so this file builds against an engine without the setting and the red run shows what is missing.
    private static (ThreadPriority Priority, string Warning) Read(IConfig phlox)
    {
        var m = typeof(PhloxEngine).GetMethod("ReadSchedulerThreadPriority", BindingFlags.Public | BindingFlags.Static);
        Assert.True(m != null, "PhloxEngine has no ReadSchedulerThreadPriority");
        var args = new object[] { phlox, null };
        var p = (ThreadPriority)m!.Invoke(null, args)!;
        return (p, (string)args[1]);
    }

    private static IConfig Section(string value = null)
    {
        var config = new IniConfigSource();
        var phlox = config.AddConfig("InWorldz.Phlox");
        if (value is not null) phlox.Set(Key, value);
        return phlox;
    }

    private static ThreadPriority EngineSetting(PhloxEngine engine)
    {
        var p = typeof(PhloxEngine).GetProperty(Key, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p != null, "PhloxEngine has no property " + Key);
        return (ThreadPriority)p!.GetValue(engine)!;
    }

    // ── Reading the setting ──────────────────────────────────────────────────

    [Fact]
    public void WithNoKeyThePriorityIsLowestAndNothingIsWarned()
    {
        var (p, warning) = Read(Section());
        Assert.Equal(ThreadPriority.Lowest, p);
        Assert.Null(warning);
    }

    [Fact]
    public void WithNoSectionThePriorityIsLowestAndNothingIsWarned()
    {
        var (p, warning) = Read(null);
        Assert.Equal(ThreadPriority.Lowest, p);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("Lowest", ThreadPriority.Lowest)]
    [InlineData("BelowNormal", ThreadPriority.BelowNormal)]
    [InlineData("Normal", ThreadPriority.Normal)]
    [InlineData("AboveNormal", ThreadPriority.AboveNormal)]
    [InlineData("Highest", ThreadPriority.Highest)]
    [InlineData("abovenormal", ThreadPriority.AboveNormal)]
    [InlineData(" Normal ", ThreadPriority.Normal)]
    public void EachThreadPriorityNameIsAccepted(string value, ThreadPriority expected)
    {
        var (p, warning) = Read(Section(value));
        Assert.Equal(expected, p);
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("High")]
    [InlineData("Realtime")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("Normal,Highest")]
    [InlineData("")]
    public void AnyOtherValueIsLowestWithAWarningNamingTheKeyAndTheValue(string value)
    {
        var (p, warning) = Read(Section(value));
        Assert.Equal(ThreadPriority.Lowest, p);
        Assert.NotNull(warning);
        Assert.Contains(Key, warning);
        Assert.Contains("'" + value + "'", warning);
    }

    // ── The engine at start ──────────────────────────────────────────────────

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
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
            { lock (m_c.Lines) m_c.Lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    private static (PhloxEngine Engine, List<(LogLevel Level, string Text)> Lines) Start(string value)
    {
        var capture = new Capture();
        ILoggerFactory before = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        var engine = new PhloxEngine();
        try
        {
            var config = new IniConfigSource();
            var phlox = config.AddConfig("InWorldz.Phlox");
            phlox.Set("Enabled", "true");
            if (value is not null) phlox.Set(Key, value);
            engine.Initialise(config);
        }
        finally { LoggerProvider.LoggerFactory = before; }
        lock (capture.Lines) return (engine, capture.Lines.ToList());
    }

    private static List<string> Mentions(List<(LogLevel Level, string Text)> lines, LogLevel level)
        => lines.Where(l => l.Level == level && l.Text.Contains(Key)).Select(l => l.Text).ToList();

    [Fact]
    public void UnsetTheEngineUsesLowestAndItsStartupSaysSo()
    {
        var (engine, lines) = Start(null);
        Assert.Equal(ThreadPriority.Lowest, EngineSetting(engine));
        Assert.Equal(new[] { "[PhloxEngine]: SchedulerThreadPriority = Lowest" }, Mentions(lines, LogLevel.Information));
        Assert.Empty(Mentions(lines, LogLevel.Warning));
    }

    [Fact]
    public void SetTheEngineUsesTheValueAndItsStartupSaysSo()
    {
        var (engine, lines) = Start("AboveNormal");
        Assert.Equal(ThreadPriority.AboveNormal, EngineSetting(engine));
        Assert.Equal(new[] { "[PhloxEngine]: SchedulerThreadPriority = AboveNormal" }, Mentions(lines, LogLevel.Information));
        Assert.Empty(Mentions(lines, LogLevel.Warning));
    }

    [Fact]
    public void ABadValueLogsOneWarningAndTheEngineUsesLowest()
    {
        var (engine, lines) = Start("Fastest");
        Assert.Equal(ThreadPriority.Lowest, EngineSetting(engine));
        var warnings = Mentions(lines, LogLevel.Warning);
        Assert.Single(warnings);
        Assert.Contains("'Fastest'", warnings[0]);
        Assert.Equal(new[] { "[PhloxEngine]: SchedulerThreadPriority = Lowest" }, Mentions(lines, LogLevel.Information));
    }

    // ── The scheduler thread itself ─────────────────────────────────────────

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern int GetThreadPriority(IntPtr thread);

    // The Win32 levels .NET sets for each ThreadPriority (THREAD_PRIORITY_LOWEST .. THREAD_PRIORITY_HIGHEST).
    private static int Win32Level(ThreadPriority p) => p switch
    {
        ThreadPriority.Lowest => -2,
        ThreadPriority.BelowNormal => -1,
        ThreadPriority.Normal => 0,
        ThreadPriority.AboveNormal => 1,
        ThreadPriority.Highest => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>
    /// A region with the setting, with its own scheduler thread left running. A script says one line from
    /// state_entry; the chat event is raised on the thread that runs the script, and the handler reads that thread's
    /// name and priority there: the managed value, and on Windows the operating system's own.
    /// </summary>
    [Theory]
    [InlineData(null, ThreadPriority.Lowest)]
    [InlineData("Lowest", ThreadPriority.Lowest)]
    [InlineData("BelowNormal", ThreadPriority.BelowNormal)]
    [InlineData("Normal", ThreadPriority.Normal)]
    [InlineData("AboveNormal", ThreadPriority.AboveNormal)]
    [InlineData("Highest", ThreadPriority.Highest)]
    [InlineData("Fastest", ThreadPriority.Lowest)]
    public void TheSchedulerThreadRunsScriptsAtTheChosenPriority(string value, ThreadPriority expected)
    {
        using var h = new SchedulerHarness(cfg => { if (value is not null) cfg.Configs["InWorldz.Phlox"].Set(Key, value); },
            keepMasterThread: true);

        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Name, ThreadPriority Managed, int Os)>();
        h.Scene.EventManager.OnChatFromWorld += (sender, chat) =>
        {
            if (chat.Message != "priority probe") return;
            var t = Thread.CurrentThread;
            seen.Enqueue((t.Name, t.Priority, OperatingSystem.IsWindows() ? GetThreadPriority(GetCurrentThread()) : int.MinValue));
        };

        h.RezScript("default { state_entry() { llSay(0, \"priority probe\"); } }");

        // Wait for the result: the region's own thread runs the script, so nothing here pumps.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (seen.IsEmpty && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.True(seen.TryPeek(out var s), "the script never ran on the scheduler thread");

        Assert.Equal("PhloxMasterScheduler", s.Name);
        Assert.Equal(expected, s.Managed);
        if (OperatingSystem.IsWindows()) Assert.Equal(Win32Level(expected), s.Os);
    }
}
