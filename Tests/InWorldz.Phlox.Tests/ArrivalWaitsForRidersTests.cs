/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.Util;
using InWorldz.Phlox.VM;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using ProtoBuf;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The core crosses a vehicle into the next region before its riders (SceneObjectGroup.CrossAsync), so the vehicle's
/// script is restored before them, and a rider's grant waits as a claim until the rider arrives seated. The script's
/// changed(CHANGED_REGION) waits too, until the riders it expects have arrived and their grants are decided, as Halcyon
/// posts it only once the last rider is seated (ScenePresence.ContinueSitAsRootAgent). Halcyon waits with no limit; here
/// the wait ends after PhloxExecutionScheduler.ArrivalRiderWaitMs, and the event is posted once. An object with no
/// waiting grant, a worn object and a teleport get the event at once, as before.
/// Also here: the line the region's start writes once its scripts have loaded.
/// </summary>
// In the "phlox-state" collection: the timed tests set the engine clock (Clock.SetSourceForTesting) and the log test
// swaps the process's logger factory (LoggerProvider.LoggerFactory); both are process-wide.
[Collection("phlox-state")]
public class ArrivalWaitsForRidersTests
{
    private const int TakeControls = 0x4, TriggerAnimation = 0x10, Debit = 0x2;
    private const int RegionStart = 0, NewRez = 1, PrimCrossing = 2, Teleporting = 5;
    private const string Phlox = "InWorldz.Phlox";

    private const string Vehicle = @"
        default {
            state_entry() { llSay(0, ""entry""); }
            changed(integer c) { llSay(0, ""changed "" + (string)c + "" perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            touch_start(integer t) { llSay(0, ""touched""); }
        }";

    private sealed class FrozenClock : IDisposable
    {
        public ulong Now = (ulong)Environment.TickCount64;
        public FrozenClock() => Clock.SetSourceForTesting(() => Now);
        public void Dispose() => Clock.SetSourceForTesting(null);
    }

    /// <summary>A harness whose scripts take no 15 ms pause after speaking: on a clock that does not move, the pause
    /// would never end.</summary>
    private static SchedulerHarness Harness()
        => new(cfg => cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", "false"));

    private static LSLSystemAPI Api(SchedulerHarness h, UUID item)
        => ((Dictionary<UUID, LSLSystemAPI>)SavedStateRig.Field(SavedStateRig.Exe(h), "m_Apis"))[item];

    // The arrival's own changed lines; a rider sitting down brings CHANGED_LINK (32) as well.
    private static IEnumerable<string> ArrivalLines(SchedulerHarness h)
        => h.Said.Where(s => s.StartsWith("changed 256 ", StringComparison.Ordinal) || s.StartsWith("changed 768 ", StringComparison.Ordinal));

    private static int ChangedLines(SchedulerHarness h) => ArrivalLines(h).Count();

    /// <summary>
    /// A rider arriving by the core's crossing: the agent is a child agent here until its move completes; then the core
    /// sits it back on the prim it rode (ParentUUID) and only after that raises OnMakeRootAgent
    /// (ScenePresence.MakeRootAgent).
    /// </summary>
    internal static ScenePresence ArriveSeated(Scene scene, UUID agent, SceneObjectGroup seat)
    {
        ScenePresence sp = SceneHelpers.AddChildScenePresence(scene, agent);
        sp.ParentUUID = seat.RootPart.UUID;
        sp.CompleteMovement(sp.ControllingClient, true);
        Assert.Equal(seat, sp.ParentPart?.ParentGroup);
        return sp;
    }

    /// <summary>
    /// The vehicle script's state as a crossing carries it, its grant from <paramref name="granter"/> (zero for none),
    /// handed to a new object owned by the harness prim's owner and started by the core with <paramref name="stateSource"/>.
    /// </summary>
    private static UUID ArriveWithCarried(SchedulerHarness h, UUID granter, int mask, int stateSource,
                                          out SceneObjectGroup copy, Action<SceneObjectGroup> before = null)
    {
        UUID owner = h.Prim.OwnerID;
        var asset = UUID.Random();
        var source = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, source, asset, "source", Vehicle);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(source, 0, false, Phlox, NewRez));
        Assert.True(h.PumpUntil(() => h.Said.Contains("entry")), SavedStateRig.SaidText(h));
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        var interp = (Interpreter)h.InterpreterFor(source);
        SerializedRuntimeState st = StateManager.Decode(StateManager.CaptureBlob(interp));
        if (granter.IsNotZero())
        {
            st.PermsGranter = granter.ToString();
            st.GrantedPermsMask = mask;
            st.PermsOwner = owner.ToString();
        }
        byte[] blob;
        using (var ms = new MemoryStream()) { Serializer.Serialize(ms, st); blob = ms.ToArray(); }
        SavedStateRig.PostRemove(h, h.Prim, source);
        h.Prim.Inventory.RemoveInventoryItem(source);
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));

        copy = SceneHelpers.AddSceneObject(h.Scene, "Example Vehicle", owner);
        copy.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(4, 0, 0);
        before?.Invoke(copy);
        var item = UUID.Random();
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, copy.RootPart, item, asset, "vehicle", Vehicle);
        SavedStateRig.States(h).Carry(item, asset, blob);
        h.ClearSaid(item);
        Assert.Equal(1, copy.CreateScriptInstances(0, false, Phlox, stateSource));
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null), "the arriving script did not load");
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h.Said);   // restored from the carried state, not started fresh
        return item;
    }

    private static void Touch(SchedulerHarness h, UUID item)
    {
        int before = h.Said.Count(s => s == "touched");
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Count(s => s == "touched") > before), SavedStateRig.SaidText(h));
    }

    // ── the wait ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The rider never arrives: the event comes once the wait is over, once, with no grant, and not a moment before. The
    /// script runs other events while it waits.
    /// </summary>
    [Fact]
    public void ARiderWhoNeverArrivesGetsTheEventAfterTheWaitOnceWithNoGrant()
    {
        using var clock = new FrozenClock();
        using var h = Harness();
        UUID rider = UUID.Random();
        var item = ArriveWithCarried(h, rider, TakeControls | TriggerAnimation, PrimCrossing, out _);
        Assert.True(Api(h, item).HasGrantClaim);

        Touch(h, item);   // the script is not paused
        clock.Now += PhloxExecutionScheduler.ArrivalRiderWaitMs - 1;
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(0, ChangedLines(h));

        clock.Now += 1;
        Assert.True(h.PumpUntil(() => ChangedLines(h) > 0), SavedStateRig.SaidText(h));
        Touch(h, item);
        Assert.Equal(new[] { "changed 256 perms=0 key=" + UUID.Zero }, ArrivalLines(h));
        Assert.True(Api(h, item).HasGrantClaim);   // the claim still waits; only the event stopped waiting
    }

    /// <summary>The rider arrives seated: the event comes then, and the script reads the rider's grant inside it.</summary>
    [Fact]
    public void ARiderArrivingSeatedIsInTheEventsGrant()
    {
        using var clock = new FrozenClock();
        using var h = Harness();
        UUID rider = UUID.Random();
        var item = ArriveWithCarried(h, rider, TakeControls | TriggerAnimation | Debit, PrimCrossing, out var copy);
        Assert.Equal(0, ChangedLines(h));

        ArriveSeated(h.Scene, rider, copy);

        Assert.True(h.PumpUntil(() => ChangedLines(h) > 0), SavedStateRig.SaidText(h));
        Touch(h, item);
        Assert.Equal(new[] { "changed 256 perms=" + (TakeControls | TriggerAnimation) + " key=" + rider },
            ArrivalLines(h));
    }

    /// <summary>
    /// The granter arrives in the region but not on the object: not a rider after all. The event comes then, with no
    /// grant, without waiting out the limit.
    /// </summary>
    [Fact]
    public void TheGranterArrivingElsewhereInTheRegionEndsTheWait()
    {
        using var clock = new FrozenClock();
        using var h = Harness();
        UUID granter = UUID.Random();
        var item = ArriveWithCarried(h, granter, TakeControls, PrimCrossing, out _);
        Assert.Equal(0, ChangedLines(h));

        SceneHelpers.AddScenePresence(h.Scene, granter);   // a root agent standing in the region (OnMakeRootAgent)

        Assert.True(h.PumpUntil(() => ChangedLines(h) > 0), SavedStateRig.SaidText(h));
        Touch(h, item);
        Assert.Equal(new[] { "changed 256 perms=0 key=" + UUID.Zero }, ArrivalLines(h));
    }

    // ── no wait ──────────────────────────────────────────────────────────────

    /// <summary>
    /// No rider expected (no waiting grant), a worn object whose wearer is still to come, and an object the core starts as
    /// Teleporting: the event comes at once, on a clock that does not move.
    /// </summary>
    [Theory]
    [InlineData("an object with no rider", false, false, PrimCrossing, 256)]
    [InlineData("a worn object crossing before its wearer", true, true, PrimCrossing, 256)]
    [InlineData("a worn object teleporting before its wearer", true, true, Teleporting, 768)]
    [InlineData("an object the core starts as Teleporting", true, false, Teleporting, 256)]
    public void NoRiderToWaitForMeansNoDelay(string arrival, bool withGrant, bool worn, int stateSource, int change)
    {
        using var clock = new FrozenClock();
        using var h = Harness();
        UUID granter = UUID.Random();
        var item = ArriveWithCarried(h, withGrant ? granter : UUID.Zero, TakeControls, stateSource, out _, g =>
        {
            if (!worn) return;
            g.AttachedAvatar = granter;
            g.IsAttachment = true;
            g.AttachmentPoint = (uint)AttachmentPoint.Chest;
        });
        Assert.Equal(withGrant, Api(h, item).HasGrantClaim);
        Assert.True(h.PumpUntil(() => ChangedLines(h) > 0), arrival + ": " + SavedStateRig.SaidText(h));
        Touch(h, item);
        Assert.Equal(new[] { "changed " + change + " perms=0 key=" + UUID.Zero },
            ArrivalLines(h));
    }

    // ── the region start's line ──────────────────────────────────────────────

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
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { if (level == LogLevel.Information) lock (m_c.Lines) m_c.Lines.Add(formatter(state, exception)); }
        }
    }

    /// <summary>
    /// A region starts with three scripts: two restored from their rows, one of them with its owner's grant, and one
    /// new. Once they have loaded, one line counts them, with no ids and no names.
    /// </summary>
    [Fact]
    public void TheRegionStartSaysHowManyScriptsAndGrantsWereRestored()
    {
        const string Src = "default { state_entry() { llSay(0, \"entry\"); } }";
        UUID owner = UUID.Random();
        UUID assetA = UUID.Random(), assetB = UUID.Random(), assetC = UUID.Random();
        UUID itemA = UUID.Random(), itemB = UUID.Random(), itemC = UUID.Random();

        using (var h1 = new SchedulerHarness())
        {
            foreach (SceneObjectPart p in h1.Prim.ParentGroup.Parts) p.OwnerID = owner;
            var invA = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, itemA, assetA, "a", Src);
            TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, itemB, assetB, "b", Src);
            Assert.Equal(2, h1.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
            h1.Prim.ParentGroup.ResumeScripts();
            Assert.True(h1.PumpUntil(() => h1.Said.Count(s => s == "entry") == 2), SavedStateRig.SaidText(h1));
            invA.PermsGranter = owner;
            invA.PermsMask = Debit;
            h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
            h1.SaveState(itemA);
            h1.SaveState(itemB);
            SavedStateRig.WaitForWrites(h1);
        }

        var capture = new Capture();
        ILoggerFactory previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try
        {
            using var h2 = new SchedulerHarness();
            foreach (SceneObjectPart p in h2.Prim.ParentGroup.Parts) p.OwnerID = owner;
            TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, itemA, assetA, "a", Src);
            TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, itemB, assetB, "b", Src);
            TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, itemC, assetC, "c", Src);
            Assert.Equal(3, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
            h2.Prim.ParentGroup.ResumeScripts();
            h2.Engine.StartProcessing();
            const string expected = "[PhloxExe]: Region start: 2 scripts restored, 1 with a permission grant put back";
            Assert.True(h2.PumpUntil(() => { lock (capture.Lines) return capture.Lines.Contains(expected); }),
                "no start line: " + string.Join(" | ", capture.Lines.Where(l => l.Contains("Region start"))));
            Assert.Equal(1, h2.Said.Count(s => s == "entry"));   // only the new script started fresh
            h2.PumpUntilIdle(TimeSpan.FromSeconds(2));
            lock (capture.Lines) Assert.Single(capture.Lines, l => l == expected);
        }
        finally { LoggerProvider.LoggerFactory = previous; }
    }
}
