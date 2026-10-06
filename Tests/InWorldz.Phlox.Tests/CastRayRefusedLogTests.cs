/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Phlox.ScriptEngine;
using Xunit;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A cast the physics engine refuses returns RCERR_CAST_TIME_EXCEEDED (-3), and the log gets at most one line per script
/// each minute of the engine's clock, with the number of casts refused since that script's line before. A script casting
/// in a loop no longer writes one line per cast.
///
/// <para>The engine's clock (Clock) is driven by hand and the process's logger factory is swapped for a capture; both
/// are process-wide, hence "phlox-state".</para>
/// </summary>
[Collection("phlox-state")]
public class CastRayRefusedLogTests
{
    private const ulong Minute = LSLSystemAPI.CastRayRefusedLineIntervalMs;

    /// <summary>A physics scene that refuses every cast while <see cref="Refusing"/> is set, and hits nothing otherwise.</summary>
    private sealed class RefusingScene : OpenSim.Region.PhysicsModules.BasicPhysics.BasicScene
    {
        public bool Refusing = true;
        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int Count)
            => Refusing ? throw new InvalidOperationException("cast budget exceeded") : new List<ContactResult>();
    }

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        public readonly List<string> Lines = new();
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
            { lock (m_c.Lines) m_c.Lines.Add(formatter(state, exception)); }
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H = new();
        public readonly RefusingScene Physics = new();
        public ulong Now = 1_000_000;
        private readonly Capture m_capture = new();
        private readonly ILoggerFactory m_previousLog;
        private readonly PhysicsScene m_previousPhysics;

        public Rig()
        {
            Clock.SetSourceForTesting(() => Now);
            m_previousLog = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = m_capture;
            m_previousPhysics = H.Scene.PhysicsScene;
            H.Scene.PhysicsScene = Physics;
        }

        public LSLSystemAPI Script(UUID item) => new LSLSystemAPI(H.Engine, H.Prim, H.Prim.LocalId, item);

        public List<string> Lines()
        {
            lock (m_capture.Lines) return m_capture.Lines.Where(l => l.Contains("llCastRay")).ToList();
        }

        public void Dispose()
        {
            try
            {
                H.Scene.PhysicsScene = m_previousPhysics;
                LoggerProvider.LoggerFactory = m_previousLog;
                H.Dispose();
            }
            finally { Clock.SetSourceForTesting(null); }
        }
    }

    /// <summary>One cast, and what it returned: the status of a refused cast is RCERR_CAST_TIME_EXCEEDED alone.</summary>
    private static LSLList Cast(LSLSystemAPI api)
        => api.llCastRay(new Vector3(10, 10, 50), new Vector3(10, 10, 0), new LSLList(new List<object>()));

    private static void CastRefused(LSLSystemAPI api, int times)
    {
        for (int i = 0; i < times; i++)
        {
            LSLList result = Cast(api);
            Assert.Equal(1, result.Length);
            Assert.Equal(SlConst.RCERR_CAST_TIME_EXCEEDED, result.GetLSLIntegerItem(0));
        }
    }

    [Fact]
    public void AFloodOfRefusedCastsLogsOneLine()
    {
        using var r = new Rig();
        UUID item = UUID.Random();
        var api = r.Script(item);
        // 16,817 casts spread over 6 s of the engine's clock.
        ulong start = r.Now;
        const int casts = 16_817;
        for (int i = 0; i < casts; i++)
        {
            r.Now = start + (ulong)i * 6_000 / casts;
            CastRefused(api, 1);
        }
        var lines = r.Lines();
        Assert.Single(lines);
        Assert.Contains("llCastRay: 1 cast refused for script " + item, lines[0]);
        Assert.EndsWith(": cast budget exceeded", lines[0]);
    }

    [Fact]
    public void ALaterMinuteLogsOneMoreLineWithTheCountSinceTheLineBefore()
    {
        using var r = new Rig();
        UUID item = UUID.Random();
        var api = r.Script(item);
        CastRefused(api, 500);                       // the first writes the line; 499 more are counted
        r.Now += Minute - 1;
        CastRefused(api, 1);                         // still inside the minute: counted, not written
        Assert.Single(r.Lines());
        r.Now += 1;
        CastRefused(api, 1);                         // the minute is up: 499 + 1 + this one
        var lines = r.Lines();
        Assert.Equal(2, lines.Count);
        Assert.Contains("llCastRay: 501 casts refused for script " + item, lines[1]);
        CastRefused(api, 7);                         // a new minute starts at that line
        Assert.Equal(2, r.Lines().Count);
        r.Now += Minute;
        CastRefused(api, 1);
        Assert.Equal(3, r.Lines().Count);
        Assert.Contains("llCastRay: 8 casts refused for script " + item, r.Lines()[2]);
    }

    [Fact]
    public void TwoScriptsAreCountedApart()
    {
        using var r = new Rig();
        UUID a = UUID.Random(), b = UUID.Random();
        var apiA = r.Script(a);
        var apiB = r.Script(b);
        CastRefused(apiA, 10);
        CastRefused(apiB, 3);
        var first = r.Lines();
        Assert.Equal(2, first.Count);
        Assert.Contains("llCastRay: 1 cast refused for script " + a, first[0]);
        Assert.Contains("llCastRay: 1 cast refused for script " + b, first[1]);
        r.Now += Minute;
        CastRefused(apiA, 1);
        CastRefused(apiB, 1);
        var lines = r.Lines();
        Assert.Equal(4, lines.Count);
        Assert.Contains("llCastRay: 10 casts refused for script " + a, lines[2]);
        Assert.Contains("llCastRay: 3 casts refused for script " + b, lines[3]);
    }

    [Fact]
    public void AScriptThatStopsBeingRefusedLogsNothingMore()
    {
        using var r = new Rig();
        UUID item = UUID.Random();
        var api = r.Script(item);
        CastRefused(api, 5);
        Assert.Single(r.Lines());
        r.Physics.Refusing = false;
        for (int i = 0; i < 10; i++)
        {
            r.Now += Minute;
            LSLList result = Cast(api);
            Assert.Equal(1, result.Length);
            Assert.Equal(0, result.GetLSLIntegerItem(0));   // a cast that hits nothing: no hits, status 0
        }
        Assert.Single(r.Lines());
    }
}
