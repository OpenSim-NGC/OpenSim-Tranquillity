/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script decides how often it makes a call, so the log lines that call writes must not grow with it:
/// lines that only describe a script's own call are Debug, and lines an operator may want to see are written once
/// in a 60 second window with the count of the others held back.
///
/// <para>The gate clock (RateLimitedLog), the engine's clock (Clock) and the process's logger factory are
/// process-wide, hence "phlox-state".</para>
/// </summary>
[Collection("phlox-state")]
public class ScriptLogRateTests
{
    private const int PERMISSION_RETURN_OBJECTS = 0x10000;
    private static readonly Vector3 West = new(40, 128, 25);

    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public List<(LogLevel Level, string Text)> With(string text)
        {
            lock (Lines) return Lines.Where(l => l.Text.Contains(text)).ToList();
        }

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            { lock (m_c.Lines) m_c.Lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    /// <summary>A scheduler harness whose log goes to a capture and whose gate clock the test sets.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly Capture Log = new();
        public ulong Now = 2_000_000;
        public Func<long> GateClock;
        private readonly ILoggerFactory m_previousLog;

        public Rig(Action<Nini.Config.IConfigSource> configure = null, bool inventoryAccess = false)
        {
            H = new SchedulerHarness(cfg =>
            {
                if (inventoryAccess) cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
                configure?.Invoke(cfg);
            });
            if (inventoryAccess)
                SceneHelpers.SetupSceneModules(H.Scene, H.Config, new BasicInventoryAccessModule());
            m_previousLog = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = Log;
            GateClock = () => (long)Now;
            RateLimitedLog.UseClockForTesting(() => GateClock());
        }

        public void Dispose()
        {
            try
            {
                RateLimitedLog.UseClockForTesting(null);
                LoggerProvider.LoggerFactory = m_previousLog;
                H.Dispose();
            }
            finally { Clock.SetSourceForTesting(null); }
        }
    }

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!h.PumpOnceBusy()) Thread.Sleep(1);
        }
        return true;
    }

    private static string Str(string s) => "\"" + s + "\"";

    // ---- moved to Debug: they describe the script's own call ----------------------------------------------------

    private sealed class FakeMutes : IMuteListService
    {
        public readonly Dictionary<UUID, List<UUID>> Muted = new();
        public byte[] MuteListRequest(UUID agent, uint crc)
        {
            if (!Muted.TryGetValue(agent, out var ids)) return Array.Empty<byte>();
            return System.Text.Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => "1 " + id + " somebody|0\n")));
        }
        public bool UpdateMute(MuteData mute) => false;
        public bool RemoveMute(UUID agentID, UUID muteID, string muteName) => false;
    }

    [Fact]
    public void GivingToSomeoneWhoMutedTheObjectIsADebugLineNotAnInformationLine()
    {
        using var r = new Rig();
        var mutes = new FakeMutes();
        r.H.Scene.RegisterModuleInterface<IMuteListService>(mutes);
        UUID recipient = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(r.H.Scene, "Mute", "Tester", recipient, "pw");
        var present = SceneHelpers.AddScenePresence(r.H.Scene, recipient);
        mutes.Muted[present.UUID] = new List<UUID> { r.H.Prim.OwnerID };
        TaskInventoryHelpers.AddNotecard(r.H.Scene.AssetService, r.H.Prim, "gift", UUID.Random(), UUID.Random(), "a gift");

        r.H.RezScript("default { state_entry() { integer i; for (i = 0; i < 6; ++i) llGiveInventory(\"" + recipient + "\", \"gift\"); llSay(0, \"done\"); } }");
        Assert.True(PumpUntil(r.H, () => r.H.Said.Contains("done"), TimeSpan.FromSeconds(60)), string.Join(",", r.H.Said));

        var lines = r.Log.With("Not offering");
        Assert.Equal(6, lines.Count);
        Assert.All(lines, l => Assert.Equal(LogLevel.Debug, l.Level));
    }

    [Fact]
    public void DerezToInventoryNotBeingAvailableIsADebugLineNotAnInformationLine()
    {
        using var r = new Rig();
        var api = new LSLSystemAPI(r.H.Engine, r.H.Prim, r.H.Prim.LocalId, UUID.Random());
        var rezzed = SceneHelpers.AddSceneObject(r.H.Scene, "rezzed", r.H.Prim.OwnerID);
        rezzed.RezzerID = r.H.Prim.UUID;

        for (int i = 0; i < 40; i++)
            Assert.Equal(0, api.llDerezObject(rezzed.UUID.ToString(), 2));    // DEREZ_TO_INVENTORY

        var lines = r.Log.With("DEREZ_TO_INVENTORY");
        Assert.Equal(40, lines.Count);
        Assert.All(lines, l => Assert.Equal(LogLevel.Debug, l.Level));
    }

    // ---- gated: an operator may want to see them ----------------------------------------------------------------

    private sealed class ReturnRig : IDisposable
    {
        public readonly Rig R;
        public readonly UUID ScriptOwner;
        public ReturnRig()
        {
            R = new Rig(inventoryAccess: true);
            ScriptOwner = R.H.Prim.OwnerID;
            UserAccountHelpers.CreateUserWithInventory(R.H.Scene, "Script", "Owner", ScriptOwner, "pw");
            R.H.Scene.LandChannel = new StripLand(R.H.Scene, (256, ScriptOwner));
            R.H.Prim.Name = "the scripted object";
            R.H.Prim.ParentGroup.UpdateGroupPosition(West);
        }

        public UUID NewOwner(string first)
        {
            UUID id = UUID.Random();
            UserAccountHelpers.CreateUserWithInventory(R.H.Scene, first, "Resident", id, "pw");
            return id;
        }

        public SceneObjectGroup ObjectOf(UUID owner, int n)
        {
            var sog = SceneHelpers.AddSceneObject(R.H.Scene, "box" + n, owner);
            sog.UpdateGroupPosition(West + new Vector3(2 + n * 0.5f, 0, 0));
            return sog;
        }

        /// <summary>
        /// The owner grants PERMISSION_RETURN_OBJECTS, then the script makes every call. The gate's clock stays put
        /// until the script has said <paramref name="windowCalls"/> results, then moves a minute on, so the call
        /// after those is the first after the window.
        /// </summary>
        public void RunCalls(string[] calls, int windowCalls)
        {
            long start = 4_000_000;
            R.GateClock = () => start + (R.H.Said.Count(s => s.StartsWith("rc=")) >= windowCalls ? 60_000 : 0);
            var client = (TestClient)(R.H.Scene.GetScenePresence(ScriptOwner)
                ?? SceneHelpers.AddScenePresence(R.H.Scene, ScriptOwner)).ControllingClient;
            string body = string.Concat(calls.Select(c => "llSay(0, \"rc=\" + (string)(" + c + ")); "));
            var item = R.H.RezScript("default { state_entry() { llRequestPermissions(" + Str(ScriptOwner.ToString())
                + ", PERMISSION_RETURN_OBJECTS); } run_time_permissions(integer p) { " + body + "} }");
            Assert.True(PumpUntil(R.H, () => client.ScriptQuestions.Count > 0, TimeSpan.FromSeconds(30)), "no permission question");
            client.FireScriptAnswer(R.H.Prim.UUID, item, PERMISSION_RETURN_OBJECTS);
            Assert.True(PumpUntil(R.H, () => R.H.Said.Count(s => s.StartsWith("rc=")) >= calls.Length, TimeSpan.FromSeconds(60)),
                "the calls never finished: " + string.Join(" | ", R.H.Said));
        }

        public void Dispose() => R.Dispose();
    }

    [Fact]
    public void ReturningObjectsByIdInALoopWritesOneLineAMinute()
    {
        using var rr = new ReturnRig();
        UUID owner = rr.NewOwner("Target");
        const int calls = 20;
        var ids = Enumerable.Range(0, calls).Select(i => rr.ObjectOf(owner, i).UUID.ToString()).ToArray();

        rr.RunCalls(ids.Select(id => "llReturnObjectsByID([" + Str(id) + "])").ToArray(), windowCalls: calls - 1);

        var lines = rr.R.Log.With("llReturnObjectsByID returned");
        Assert.Equal(2, lines.Count);
        Assert.Equal(LogLevel.Information, lines[0].Level);
        Assert.DoesNotContain("more like this", lines[0].Text);
        Assert.Equal(LogLevel.Information, lines[1].Level);
        Assert.EndsWith($"({calls - 2} more like this in the last 60 s)", lines[1].Text);
    }

    [Fact]
    public void ReturningObjectsByOwnerInALoopWritesOneLineAMinute()
    {
        using var rr = new ReturnRig();
        const int calls = 6;
        var owners = Enumerable.Range(0, calls).Select(i => rr.NewOwner("Target" + (char)('A' + i))).ToArray();
        for (int i = 0; i < calls; i++) rr.ObjectOf(owners[i], i);

        rr.RunCalls(owners.Select(o => "llReturnObjectsByOwner(" + Str(o.ToString()) + ", OBJECT_RETURN_PARCEL)").ToArray(),
            windowCalls: calls - 1);

        var lines = rr.R.Log.With("llReturnObjectsByOwner returned");
        Assert.Equal(2, lines.Count);
        Assert.Equal(LogLevel.Information, lines[0].Level);
        Assert.DoesNotContain("more like this", lines[0].Text);
        Assert.EndsWith($"({calls - 2} more like this in the last 60 s)", lines[1].Text);
    }

    /// <summary>
    /// Three scripts each wait on a lookup held at a gate; the engine's clock passes their deadline, the scheduler
    /// answers each with its failure value (one warning, however many), and then the held lookups finish late
    /// (one line, however many). A minute later one more of each shows the counts.
    /// </summary>
    [Fact]
    public void ServiceCallsPastTheirDeadlineWriteOneWarningAndOneLateLineAMinute()
    {
        using var r = new Rig(cfg => cfg.Configs["InWorldz.Phlox"].Set("ServiceCallTimeoutMs", "5000"));
        Clock.SetSourceForTesting(() => r.Now);
        var gates = new List<ServiceGate>();
        try
        {
            var users = Enumerable.Range(0, 4).Select(_ => UUID.Random()).ToArray();
            foreach (var u in users)
                UserAccountHelpers.CreateUserWithInventory(r.H.Scene, "Slow", "User" + u.ToString().Substring(0, 6), u, "pw");
            var gateFor = users.ToDictionary(u => u, _ => { var g = new ServiceGate(); gates.Add(g); return g; });
            ServiceCallDeferralTests.InstallAccountGate(r.H, id => gateFor.TryGetValue(id, out var g) ? g : null);

            string Script(UUID u) => "default { state_entry() { string n = iwGetAgentData(\"" + u + "\", DATA_NAME); llSay(0, \"got " + u + "\"); } }";

            // First batch: three calls, all held past the deadline.
            for (int i = 0; i < 3; i++) r.H.RezScript(Script(users[i]));
            Assert.True(PumpUntil(r.H, () => gates.Take(3).All(g => g.Inside == 1), TimeSpan.FromSeconds(30)), "calls never reached the service");
            r.Now += 5_001;
            Assert.True(PumpUntil(r.H, () => users.Take(3).All(u => r.H.Said.Contains("got " + u)), TimeSpan.FromSeconds(30)),
                "scripts were not answered at the deadline: " + string.Join(",", r.H.Said));
            Assert.Single(r.Log.With("did not answer in"));
            Assert.DoesNotContain("more like this", r.Log.With("did not answer in")[0].Text);

            foreach (var g in gates.Take(3)) g.Open();
            Assert.True(PumpUntil(r.H, () => gates.Take(3).All(g => g.Inside == 0), TimeSpan.FromSeconds(30)));
            Assert.True(PumpUntil(r.H, () => r.Log.With("answered after its").Count >= 1, TimeSpan.FromSeconds(30)), "no late line");
            r.H.PumpFor(TimeSpan.FromMilliseconds(300));              // let the other two late answers be handled
            Assert.Single(r.Log.With("answered after its"));
            Assert.Equal(LogLevel.Information, r.Log.With("answered after its")[0].Level);

            // A minute later, one more call past its deadline writes both lines with the counts held back.
            r.Now += 60_000;
            r.H.RezScript(Script(users[3]));
            Assert.True(PumpUntil(r.H, () => gates[3].Inside == 1, TimeSpan.FromSeconds(30)));
            r.Now += 5_001;
            Assert.True(PumpUntil(r.H, () => r.H.Said.Contains("got " + users[3]), TimeSpan.FromSeconds(30)));
            gates[3].Open();
            Assert.True(PumpUntil(r.H, () => r.Log.With("answered after its").Count >= 2, TimeSpan.FromSeconds(30)), "no second late line");

            var warnings = r.Log.With("did not answer in");
            Assert.Equal(2, warnings.Count);
            Assert.Equal(LogLevel.Warning, warnings[1].Level);
            Assert.Matches(@"\(2 more like this in the last \d+ s\)$", warnings[1].Text);
            var late = r.Log.With("answered after its");
            Assert.Matches(@"\(\d+ more like this in the last \d+ s\)$", late[1].Text);
        }
        finally { foreach (var g in gates) g.Dispose(); }
    }
}
