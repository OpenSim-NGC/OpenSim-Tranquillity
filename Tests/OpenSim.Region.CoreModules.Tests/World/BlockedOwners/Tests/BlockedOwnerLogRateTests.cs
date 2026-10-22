/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Objects.BlockedOwners;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.BlockedOwners.Tests;

/// <summary>
/// A blocked owner can try as often as they like, so each kind of refusal writes one line in a 60 second window
/// and the rest are counted; the first refusal after the window writes the count. The refusals themselves are
/// unchanged. The gate's clock is set by hand and the logger factory swapped for a capture; both are
/// process-wide; this assembly runs its test classes one at a time (AssemblyInfo.cs).
/// </summary>
public class BlockedOwnerLogRateTests : OpenSimTestCase
{
    private static readonly UUID BlockedId = new("0c3e8d57-1a94-4b62-8f0d-e5a7c92b4d16");
    private static readonly Vector3 RezPos = new(10, 10, 25);
    private const int Attempts = 200;

    private sealed class Capture : ILoggerFactory
    {
        public readonly List<string> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            { lock (m_c.Lines) m_c.Lines.Add(logLevel + "|" + formatter(state, exception)); }
        }
    }

    private long m_ms = 3_000_000;
    private Capture m_capture = null!;
    private ILoggerFactory m_previous = null!;
    private TestScene m_scene = null!;
    private BlockedOwnerModule m_module = null!;

    public override void SetUp()
    {
        base.SetUp();
        m_capture = new Capture();
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_capture;
        RateLimitedLog.UseClockForTesting(() => m_ms);

        m_module = new BlockedOwnerModule();
        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, new IniConfigSource(), m_module);
        m_module.Block(BlockedId);
    }

    public override void Dispose()
    {
        RateLimitedLog.UseClockForTesting(null);
        LoggerProvider.LoggerFactory = m_previous;
        base.Dispose();
    }

    private List<string> Lines(string contains)
    {
        lock (m_capture.Lines) return m_capture.Lines.Where(l => l.Contains(contains)).ToList();
    }

    // Drives the refusal Attempts times inside one window, then once after it.
    private void AssertOneLineThenOneSummary(string contains, Func<bool> refusedAttempt)
    {
        for (int i = 0; i < Attempts; i++)
        {
            Assert.False(refusedAttempt());                 // still refused every time
            m_ms += 100;
        }

        var inWindow = Lines(contains);
        Assert.Single(inWindow);
        Assert.StartsWith("Information|", inWindow[0]);
        Assert.DoesNotContain("more like this", inWindow[0]);

        m_ms += 60_000;
        Assert.False(refusedAttempt());

        var all = Lines(contains);
        Assert.Equal(2, all.Count);
        Assert.StartsWith("Information|", all[1]);
        Assert.EndsWith($"({Attempts - 1} more like this in the last 80 s)", all[1]);
    }

    [Fact]
    public void RezzingInALoopWritesOneRefusalLineAMinute()
        => AssertOneLineThenOneSummary("Refused a rez of", () => m_scene.Permissions.CanRezObject(1, BlockedId, RezPos));

    [Fact]
    public void DuplicatingInALoopWritesOneRefusalLineAMinute()
    {
        SceneHelpers.AddScenePresence(m_scene, BlockedId);
        SceneObjectGroup original = SceneHelpers.CreateSceneObject(1, BlockedId, "original", 0x300);
        m_scene.AddNewSceneObject(original, false);

        AssertOneLineThenOneSummary("Refused a duplicate of", () =>
            m_scene.SceneGraph.DuplicateObject(original.LocalId, new Vector3(1, 0, 0), BlockedId, UUID.Zero,
                Quaternion.Identity, false) != null);
    }

    [Fact]
    public void EnteringInALoopWritesOneRefusalLineAMinute()
    {
        SceneObjectGroup arriving = SceneHelpers.CreateSceneObject(1, BlockedId, "arriving", 0x310);

        AssertOneLineThenOneSummary("Refused entry of", () => m_scene.Permissions.CanObjectEntry(arriving, true, RezPos));
    }

    [Fact]
    public void ThreeKindsOfRefusalAreGatedApart()
    {
        SceneHelpers.AddScenePresence(m_scene, BlockedId);
        SceneObjectGroup original = SceneHelpers.CreateSceneObject(1, BlockedId, "original", 0x320);
        m_scene.AddNewSceneObject(original, false);

        for (int i = 0; i < 20; i++)
        {
            m_scene.Permissions.CanRezObject(1, BlockedId, RezPos);
            m_scene.SceneGraph.DuplicateObject(original.LocalId, new Vector3(1, 0, 0), BlockedId, UUID.Zero,
                Quaternion.Identity, false);
            m_scene.Permissions.CanObjectEntry(original, true, RezPos);
        }

        Assert.Single(Lines("Refused a rez of"));
        Assert.Single(Lines("Refused a duplicate of"));
        Assert.Single(Lines("Refused entry of"));
    }
}
