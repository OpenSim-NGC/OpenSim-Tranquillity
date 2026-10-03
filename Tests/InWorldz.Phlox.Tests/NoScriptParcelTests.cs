using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// No Scripts parcels enforced live. A script that the parcel under its object
/// does not allow is paused, never stopped or reset, and runs on when the parcel allows it again. Attachments and
/// scripts holding taken controls are exempt. Where that rule is silent, Halcyon's applies (EngineInterface.ScriptsCanRun):
/// the object's owner owns the parcel, or the parcel allows other scripts, or it allows group scripts and the object's
/// group is the parcel's group. No parcel: not allowed. No estate-manager or god exemption.
///
/// <para>The land is a real LandManagementModule with two parcels, west (x below 128, no outside or group scripts) and
/// east (everything allowed), both owned by <see cref="ParcelOwner"/>. Flags change through
/// LandChannel.UpdateLandObject, which raises EventManager.OnLandObjectAdded as the viewer's parcel dialog does.</para>
/// </summary>
[Collection("phlox-state")]
public class NoScriptParcelTests
{
    private readonly ITestOutputHelper _out;
    public NoScriptParcelTests(ITestOutputHelper o) => _out = o;

    private static readonly UUID ParcelOwner = new UUID("00000000-0000-0000-0000-430000000001");
    private static readonly UUID Resident = new UUID("00000000-0000-0000-0000-430000000002");
    private static readonly UUID GroupA = new UUID("00000000-0000-0000-8888-430000000001");
    private static readonly UUID GroupB = new UUID("00000000-0000-0000-8888-430000000002");

    private static readonly Vector3 West = new Vector3(64, 64, 25);
    private static readonly Vector3 East = new Vector3(192, 64, 25);

    private const int CONTROL_FWD = 1;

    /// <summary>A counter on a 0.1 s timer: every tick is said aloud with the object's name, so a pause shows as silence.</summary>
    private const string Ticker = @"integer n;
        default {
            state_entry() { llSay(0, llGetObjectName() + "" entry""); llSetTimerEvent(0.1); }
            timer() { n++; llSay(0, llGetObjectName() + "" tick "" + (string)n); }
        }";

    private sealed class Land : IDisposable
    {
        public SchedulerHarness H;
        public LandManagementModule Lmm;
        public ILandObject WestParcel, EastParcel;
        private int m_tail = 0x4300;

        public Land(SchedulerHarness h = null)
        {
            H = h ?? new SchedulerHarness(cfg => cfg.Configs["Startup"].Set("serverside_object_permissions", "true"));
            Lmm = new LandManagementModule();
            SceneHelpers.SetupSceneModules(H.Scene, H.Config, Lmm, new DefaultPermissionsModule());

            int half = (int)Constants.RegionSize / 2;
            var west = new LandObject(ParcelOwner, false, H.Scene);
            west.LandData.Name = "west";
            west.SetLandBitmap(west.GetSquareLandBitmap(0, 0, half, (int)Constants.RegionSize));
            WestParcel = Lmm.AddLandObject(west);
            WestParcel.LandData.Flags &= ~(uint)(ParcelFlags.AllowOtherScripts | ParcelFlags.AllowGroupScripts);

            var east = new LandObject(ParcelOwner, false, H.Scene);
            east.LandData.Name = "east";
            east.SetLandBitmap(east.GetSquareLandBitmap(half, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            EastParcel = Lmm.AddLandObject(east);
        }

        public PhloxExecutionScheduler Exe
            => (PhloxExecutionScheduler)typeof(PhloxEngine).GetField("m_ExeScheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(H.Engine)!;

        public SceneObjectGroup AddObject(string name, Vector3 pos, UUID owner, UUID group = default)
        {
            var sog = SceneHelpers.CreateSceneObject(1, owner, name + "-", m_tail);
            m_tail += 16;
            sog.Name = name;
            sog.RootPart.Name = name;
            if (!group.IsZero()) { sog.RootPart.GroupID = group; }
            sog.AbsolutePosition = pos;
            H.Scene.AddNewSceneObject(sog, false);
            return sog;
        }

        /// <summary>Rez a script as PhloxEngine.OnRezScript receives it; <paramref name="beforeRez"/> sees the item first (permissions).</summary>
        public UUID Rez(SceneObjectPart part, string source, Action<TaskInventoryItem> beforeRez = null, int stateSource = 0,
                        UUID itemId = default, UUID assetId = default)
        {
            var item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, part,
                itemId.IsZero() ? UUID.Random() : itemId, assetId.IsZero() ? UUID.Random() : assetId, "s", source);
            beforeRez?.Invoke(item);
            var rez = typeof(PhloxEngine).GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance)!;
            rez.Invoke(H.Engine, new object[] { part.LocalId, item.ItemID, source, 0, false, H.Engine.Name, stateSource });
            // a first compile can take longer than any fixed pump: wait for the instance, not for a time
            Assert.True(PumpUntil(() => H.InterpreterFor(item.ItemID) != null), "script never loaded: " + H.Diagnose(item.ItemID));
            return item.ItemID;
        }

        /// <summary>Pump until the condition holds, up to <paramref name="maxMs"/>.</summary>
        public bool PumpUntil(Func<bool> condition, int maxMs = 30000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(maxMs);
            while (!condition())
            {
                if (DateTime.UtcNow > until) return false;
                if (!H.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
            }
            return true;
        }

        /// <summary>The parcel dialog: flags through LandChannel.UpdateLandObject, which raises OnLandObjectAdded.</summary>
        public void SetFlags(ILandObject parcel, bool otherScripts, bool groupScripts, UUID group = default)
        {
            LandData d = parcel.LandData.Copy();
            if (otherScripts) d.Flags |= (uint)ParcelFlags.AllowOtherScripts; else d.Flags &= ~(uint)ParcelFlags.AllowOtherScripts;
            if (groupScripts) d.Flags |= (uint)ParcelFlags.AllowGroupScripts; else d.Flags &= ~(uint)ParcelFlags.AllowGroupScripts;
            if (!group.IsZero()) d.GroupID = group;
            H.Scene.LandChannel.UpdateLandObject(parcel.LandData.LocalID, d);
        }

        public int Ticks(string name) => H.Said.Count(s => s.StartsWith(name + " tick "));
        public int LastTick(string name)
            => H.Said.Where(s => s.StartsWith(name + " tick ")).Select(s => int.Parse(s.Substring(name.Length + 6))).DefaultIfEmpty(0).Max();
        public bool Said(string text) => H.Said.Contains(text);
        public bool Paused(UUID itemId) => H.StatusOf(itemId).Contains("LocalDisable=Parcel");
        public void Pump(int ms = 400) => H.PumpFor(TimeSpan.FromMilliseconds(ms));

        /// <summary>The scheduler's parcel-check counters, by reflection so this file builds without them.</summary>
        public (long Scanned, long Evaluated, long Paused, long Resumed) Counters()
        {
            var p = Exe.GetType().GetProperty("ParcelCounters", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(p != null, "PhloxExecutionScheduler.ParcelCounters is missing");
            object c = p!.GetValue(Exe)!;
            long F(string n) => (long)c.GetType().GetField(n)!.GetValue(c)!;
            return (F("Scanned"), F("Evaluated"), F("Paused"), F("Resumed"));
        }

        public void Dispose() => H.Dispose();
    }

    // The engine's new members are reached by reflection, so the red run (source without the parcel check) builds and fails on
    // behaviour rather than on missing names.
    private static bool Allows(LandData land, UUID owner, UUID group)
    {
        var m = typeof(PhloxEngine).GetMethod("ParcelAllowsScripts", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(m != null, "PhloxEngine.ParcelAllowsScripts is missing");
        return (bool)m!.Invoke(null, new object[] { land, owner, group })!;
    }

    private static T Static<T>(string name)
    {
        var p = typeof(PhloxEngine).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(p != null, "PhloxEngine." + name + " is missing");
        return (T)p!.GetValue(null)!;
    }

    /// <summary>The test's own look at the avatar's control registrations - not the engine's code under test.</summary>
    private static bool Holds(ScenePresence sp, UUID itemId) => sp.HasScriptControls(itemId);

    private static bool EngineSaysHolds(ScenePresence sp, UUID itemId)
    {
        var m = typeof(PhloxEngine).GetMethod("AvatarHoldsControls", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(m != null, "PhloxEngine.AvatarHoldsControls is missing");
        return (bool)m!.Invoke(null, new object[] { sp, itemId })!;
    }

    private void Log(Land l, string what) => _out.WriteLine(what + ": said=[" + string.Join(" | ", l.H.Said.TakeLast(12)) + "]");

    // ── the rule ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheParcelRuleIsHalcyons()
    {
        var land = new LandData { OwnerID = ParcelOwner, GroupID = GroupA, Flags = 0 };
        Assert.True(Allows(land, ParcelOwner, UUID.Zero));        // owner owns the parcel
        Assert.False(Allows(land, Resident, GroupA));             // group matches, flag off
        land.Flags = (uint)ParcelFlags.AllowGroupScripts;
        Assert.True(Allows(land, Resident, GroupA));
        Assert.False(Allows(land, Resident, GroupB));
        land.Flags = (uint)ParcelFlags.AllowOtherScripts;
        Assert.True(Allows(land, Resident, GroupB));
        // a parcel with no group has no group to match (core's CanRunScript rule; Halcyon compared zero to zero)
        var noGroup = new LandData { OwnerID = ParcelOwner, GroupID = UUID.Zero, Flags = (uint)ParcelFlags.AllowGroupScripts };
        Assert.False(Allows(noGroup, Resident, UUID.Zero));
        Assert.False(Allows(null, ParcelOwner, UUID.Zero));        // no parcel: not allowed
    }

    [Fact]
    public void PhloxTellsTheCoreItEnforcesTheParcelRules()
    {
        using var l = new Land();
        Assert.True(((IParcelScriptPolicyEngine)l.H.Engine).EnforcesParcelScriptRules);
        Assert.True(l.H.Scene.AnyScriptEngineEnforcesParcelRules());
    }

    // ── start ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AScriptStartedOnNoScriptsLandThroughTheCoreIsPausedNotRefused()
    {
        using var l = new Land();
        var sog = l.AddObject("start", West, Resident);
        var item = TaskInventoryHelpers.AddScript(l.H.Scene.AssetService, sog.RootPart, UUID.Random(), UUID.Random(), "s", Ticker);

        // the core's start path: CreateScriptInstance -> CanRunScript(item, part, engineEnforces) -> OnRezScript
        bool started = sog.RootPart.Inventory.CreateScriptInstance(item, 0, false, l.H.Engine.Name, 0);
        l.Pump();
        l.PumpUntil(() => l.Paused(item.ItemID), 30000);
        Log(l, "start on west");

        Assert.True(started);
        Assert.NotNull(l.H.InterpreterFor(item.ItemID));      // the engine has it
        Assert.True(l.Paused(item.ItemID));
        Assert.False(l.Said("start entry"));                    // state_entry is held, not run
        Assert.Equal(0, l.Ticks("start"));
        Assert.Contains("LocalDisable=Parcel", l.H.StatusOf(item.ItemID));

        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        l.PumpUntil(() => !l.Paused(item.ItemID) && l.Said("start entry") && l.Ticks("start") > 0, 30000);
        Log(l, "west allows");
        Assert.False(l.Paused(item.ItemID));
        Assert.True(l.Said("start entry"));                     // the held state_entry runs now
        Assert.True(l.Ticks("start") > 0);
    }

    [Fact]
    public void RegionStartRestoresAPausedScriptThatResumesWithItsGlobals()
    {
        var itemId = UUID.Random();
        var assetId = UUID.Random();
        int before;
        using (var l1 = new Land())
        {
            var sog = l1.AddObject("rs", East, Resident);
            l1.Rez(sog.RootPart, Ticker, itemId: itemId, assetId: assetId);
            l1.PumpUntil(() => l1.Ticks("rs") > 0, 30000);
            before = l1.LastTick("rs");
            Assert.True(before > 0);
            l1.H.SaveState(itemId);
        }

        using var l2 = new Land();
        var sog2 = l2.AddObject("rs", West, Resident);                       // the object now stands on west
        l2.Rez(sog2.RootPart, Ticker, itemId: itemId, assetId: assetId, stateSource: 0 /* RegionStart */);
        l2.Pump(600);
        l2.PumpUntil(() => l2.Paused(itemId), 30000);
        Log(l2, "region start on west");
        Assert.True(l2.Paused(itemId));
        Assert.Equal(0, l2.Ticks("rs"));

        l2.SetFlags(l2.WestParcel, otherScripts: true, groupScripts: false);
        l2.Pump(600);
        l2.PumpUntil(() => !l2.Paused(itemId) && l2.LastTick("rs") > before, 30000);
        Log(l2, "west allows");
        Assert.False(l2.H.Said.Contains("rs entry"));                        // restored, not restarted
        Assert.True(l2.LastTick("rs") > before);                              // n carried on from where it was
        Assert.Equal(before + 1, l2.H.Said.Where(s => s.StartsWith("rs tick ")).Select(s => int.Parse(s.Substring(8))).Min());
    }

    // ── parcel flag change ──────────────────────────────────────────────────────

    [Fact]
    public void AFlagChangePausesAndResumesWithGlobalsKept()
    {
        using var l = new Land();
        var sog = l.AddObject("flag", West, Resident);
        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Ticks("flag") > 0, 30000);
        Assert.True(l.Ticks("flag") > 0);

        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: false);
        l.Pump(100);
        l.PumpUntil(() => l.Paused(id), 30000);
        int atPause = l.LastTick("flag");
        l.Pump(500);
        l.PumpUntil(() => l.Paused(id), 30000);
        Log(l, "flag off");
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("flag"));                           // silent while paused

        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        l.Pump(500);
        l.PumpUntil(() => !l.Paused(id) && l.LastTick("flag") > atPause, 30000);
        Log(l, "flag on");
        Assert.False(l.Paused(id));
        Assert.True(l.LastTick("flag") > atPause);
        Assert.Equal(1, l.H.Said.Count(s => s == "flag entry"));             // never reset
        // globals kept: the first tick after the pause is atPause + 1
        var ticks = l.H.Said.Where(s => s.StartsWith("flag tick ")).Select(s => int.Parse(s.Substring(10))).ToList();
        Assert.Equal(Enumerable.Range(1, ticks.Count), ticks);
    }

    // ── A paused timer keeps the time it had left (Halcyon) ───────────────────────

    /// <summary>
    /// Milliseconds until the script's timer wake, or null when none is armed. Read from the scheduler's own heap by
    /// reflection (m_TimerHandles, m_SleepHeap), so the test measures what the scheduler will do, not wall-clock ticks.
    /// </summary>
    private static long? TimerDueIn(Land l, UUID itemId)
    {
        var exe = l.Exe;
        var handles = (System.Collections.IDictionary)exe.GetType().GetField("m_TimerHandles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(exe)!;
        if (!handles.Contains(itemId)) return null;
        object handle = handles[itemId]!;
        object heap = exe.GetType().GetField("m_SleepHeap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(exe)!;
        var indexer = heap.GetType().GetProperties().First(p => p.Name == "Item" && p.GetIndexParameters().Length == 1);
        object entry = indexer.GetValue(heap, new[] { handle })!;
        ulong readyOn = (ulong)entry.GetType().GetField("ReadyOn")!.GetValue(entry)!;
        return (long)readyOn - (long)global::InWorldz.Phlox.Util.Clock.Now;
    }

    private static string TimerScript(string seconds) => @"integer n;
        default {
            state_entry() { llSetTimerEvent(" + seconds + @"); llSay(0, llGetObjectName() + "" entry""); }
            timer() { n++; llSay(0, llGetObjectName() + "" tick "" + (string)n); }
        }";

    [Fact]
    public void APausedTimerResumesWithTheTimeItHadLeft()
    {
        // Halcyon ExecutionScheduler.InjectScript: readyOn = now + (TimerInterval - (StateCapturedOn - TimerLastScheduledOn)),
        // StateCapturedOn being the pause. A 10 s timer paused with about 7 s left fires about 7 s after the resume.
        using var l = new Land();
        var sog = l.AddObject("keep", West, Resident);
        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        var id = l.Rez(sog.RootPart, TimerScript("10.0"));
        Assert.True(l.PumpUntil(() => l.Said("keep entry")), "no state_entry");
        Assert.True(l.PumpUntil(() => TimerDueIn(l, id) is long d && d <= 7000, 20000), "the timer never counted down: " + TimerDueIn(l, id));

        long d0 = TimerDueIn(l, id)!.Value;
        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: false);
        Assert.True(l.PumpUntil(() => l.Paused(id)), "not paused");
        Assert.Null(TimerDueIn(l, id));                                        // no timer wake while paused
        l.Pump(3000);                                                           // a 3 s pause, not counted

        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        Assert.True(l.PumpUntil(() => !l.Paused(id)), "not resumed");
        long? d1 = TimerDueIn(l, id);
        _out.WriteLine($"due before the pause {d0} ms, after the resume {d1} ms");
        Assert.NotNull(d1);
        Assert.True(d1 <= d0 + 50, $"after the resume the timer is due in {d1} ms; it had {d0} ms left (a full interval is 10000)");
        Assert.True(d1 >= d0 - 1500, $"after the resume the timer is due in {d1} ms; it had {d0} ms left, and the 3 s pause must not count");
        Assert.Equal(0, l.Ticks("keep"));
    }

    [Fact]
    public void APauseLongerThanTheIntervalStillLeavesTheTimeThatWasLeftThenTheNormalInterval()
    {
        // Halcyon counts only the time waited before the pause, so a pause longer than the interval neither fires at once
        // nor makes up the missed events: one event after the time that was left, then the timer's own interval.
        using var l = new Land();
        var sog = l.AddObject("long", West, Resident);
        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        var id = l.Rez(sog.RootPart, TimerScript("2.0"));
        Assert.True(l.PumpUntil(() => l.Said("long entry")), "no state_entry");
        Assert.True(l.PumpUntil(() => TimerDueIn(l, id) is long d && d <= 1300, 20000), "the timer never counted down");

        long d0 = TimerDueIn(l, id)!.Value;
        int ticksAtPause = l.Ticks("long");
        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: false);
        Assert.True(l.PumpUntil(() => l.Paused(id)), "not paused");
        l.Pump(4000);                                                           // twice the interval
        Assert.Equal(ticksAtPause, l.Ticks("long"));                            // silent while paused

        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        Assert.True(l.PumpUntil(() => !l.Paused(id)), "not resumed");
        var sinceResume = Stopwatch.StartNew();
        long? d1 = TimerDueIn(l, id);
        _out.WriteLine($"due before the pause {d0} ms, after the resume {d1} ms");
        Assert.NotNull(d1);
        Assert.True(d1 > 0, $"the timer is due at once ({d1} ms): the pause was counted");
        Assert.True(d1 <= d0 + 50, $"after the resume the timer is due in {d1} ms; it had {d0} ms left (a full interval is 2000)");

        // one event, after the time that was left
        Assert.True(l.PumpUntil(() => l.Ticks("long") > ticksAtPause, 20000), "no timer event after the resume");
        long firstAfter = sinceResume.ElapsedMilliseconds;
        Assert.Equal(ticksAtPause + 1, l.Ticks("long"));
        Assert.True(firstAfter >= d1!.Value - 100, $"the first event came {firstAfter} ms after the resume; it was due in {d1}");

        // then the normal interval
        long? next = TimerDueIn(l, id);
        _out.WriteLine($"first event {firstAfter} ms after the resume; next due in {next} ms");
        Assert.NotNull(next);
        Assert.InRange(next!.Value, 1000, 2000);
    }

    [Fact]
    public void AnOwnerChangeOfTheParcelIsATrigger()
    {
        using var l = new Land();
        var sog = l.AddObject("pown", West, Resident);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));

        // the parcel is sold to the object's owner
        LandData d = l.WestParcel.LandData.Copy();
        d.OwnerID = Resident;
        l.H.Scene.LandChannel.UpdateLandObject(l.WestParcel.LandData.LocalID, d);
        l.PumpUntil(() => !l.Paused(id) && l.Ticks("pown") > 0, 30000);
        Assert.False(l.Paused(id));
        Assert.True(l.Ticks("pown") > 0);
    }

    // ── moving ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MovingOntoAndOffTheParcelPausesAndResumes()
    {
        using var l = new Land();
        var sog = l.AddObject("mover", East, Resident);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Ticks("mover") > 0, 30000);
        Assert.True(l.Ticks("mover") > 0);

        sog.AbsolutePosition = West;                                           // llSetPos, an edit, a grab
        l.Pump(100);
        l.PumpUntil(() => l.Paused(id), 30000);
        int atPause = l.LastTick("mover");
        l.Pump(400);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("mover"));

        sog.AbsolutePosition = East;
        l.PumpUntil(() => !l.Paused(id) && l.LastTick("mover") > atPause, 30000);
        Assert.False(l.Paused(id));
        Assert.True(l.LastTick("mover") > atPause);
    }

    [Fact]
    public void APhysicsMoveOntoAndOffTheParcelPausesAndResumes()
    {
        using var l = new Land();
        var sog = l.AddObject("phys", East, Resident);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Ticks("phys") > 0, 30000);
        Assert.True(l.Ticks("phys") > 0);

        var pa = sog.RootPart.PhysActor;
        Assert.NotNull(pa);
        // as the core's parcel-crossing test: the physics engine moves the body without the group setter, then asks for a terse update
        pa.OnRequestTerseUpdate += sog.RootPart.PhysicsRequestingTerseUpdate;
        pa.Position = West;
        pa.RequestPhysicsterseUpdate();
        l.Pump(100);
        l.PumpUntil(() => l.Paused(id), 30000);
        int atPause = l.LastTick("phys");
        l.Pump(400);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("phys"));

        pa.Position = East;
        pa.RequestPhysicsterseUpdate();
        l.PumpUntil(() => !l.Paused(id) && l.LastTick("phys") > atPause, 30000);
        Assert.False(l.Paused(id));
        Assert.True(l.LastTick("phys") > atPause);
    }

    // ── exemptions ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnAttachmentRunsOnNoScriptsLandAndAnObjectDetachedThereIsPaused()
    {
        using var l = new Land();
        var sp = SceneHelpers.AddScenePresence(l.H.Scene, Resident);
        var sog = l.AddObject("worn", West, Resident);
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = (uint)AttachmentPoint.Chest;
        var id = l.Rez(sog.RootPart, Ticker);
        l.Pump();
        l.PumpUntil(() => l.Ticks("worn") > 0, 30000);
        Log(l, "attachment on west");
        Assert.False(l.Paused(id));
        Assert.True(l.Ticks("worn") > 0);

        // dropped on the ground on west: the core's detach raises OnAttach with no avatar
        sog.IsAttachment = false;
        sog.AttachedAvatar = UUID.Zero;
        l.H.Scene.EventManager.TriggerOnAttach(sog.LocalId, sog.UUID, UUID.Zero);
        l.Pump(100);
        l.PumpUntil(() => l.Paused(id), 30000);
        int atPause = l.LastTick("worn");
        l.Pump(400);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("worn"));

        // worn again
        sog.IsAttachment = true;
        sog.AttachedAvatar = sp.UUID;
        l.H.Scene.EventManager.TriggerOnAttach(sog.LocalId, sog.UUID, sp.UUID);
        l.PumpUntil(() => !l.Paused(id) && l.LastTick("worn") > atPause, 30000);
        Assert.False(l.Paused(id));
        Assert.True(l.LastTick("worn") > atPause);
    }

    /// <summary>Takes controls in state_entry (permission set on the item before rez), then ticks.</summary>
    private const string Controller = @"integer n;
        default {
            state_entry() { llTakeControls(1, TRUE, FALSE); llSetTimerEvent(0.1); }
            timer() { n++; llSay(0, llGetObjectName() + "" tick "" + (string)n); }
            listen(integer c, string nm, key k, string m) { llReleaseControls(); llSay(0, llGetObjectName() + "" released""); }
            control(key id, integer l, integer e) { }
        }";

    private static Action<TaskInventoryItem> GrantControls(UUID avatar) => item =>
    {
        item.PermsGranter = avatar;
        item.PermsMask = 4;   // PERMISSION_TAKE_CONTROLS
    };

    [Fact]
    public void TheControlsCheckAsksScenePresenceHasScriptControls()
    {
        using var l = new Land();
        var sp = SceneHelpers.AddScenePresence(l.H.Scene, Resident);
        var sog = l.AddObject("ctl0", East, Resident);
        var id = l.Rez(sog.RootPart, Controller, GrantControls(sp.UUID));
        Assert.True(l.PumpUntil(() => Holds(sp, id)));   // state_entry has taken them

        Assert.True(EngineSaysHolds(sp, id));
        Assert.False(EngineSaysHolds(sp, UUID.Random()));
        Assert.False(EngineSaysHolds(null!, id));
        sp.ClearControls();
        Assert.False(EngineSaysHolds(sp, id));
    }

    [Fact]
    public void AScriptHoldingControlsKeepsRunningOnNoScriptsLandUntilTheCoreReleasesThem()
    {
        using var l = new Land();
        var sp = SceneHelpers.AddScenePresence(l.H.Scene, Resident);
        var sog = l.AddObject("ctl", East, Resident);
        var id = l.Rez(sog.RootPart, Controller, GrantControls(sp.UUID));
        Assert.True(l.PumpUntil(() => Holds(sp, id)));   // state_entry has taken them
        Assert.True(Holds(sp, id));

        sog.AbsolutePosition = West;
        l.Pump(500);
        Log(l, "controls held on west");
        Assert.False(l.Paused(id));                                             // exempt: it holds controls
        int running = l.LastTick("ctl");
        l.PumpUntil(() => l.LastTick("ctl") > running, 30000);
        Assert.True(l.LastTick("ctl") > running);

        // the viewer's "release keys": ScenePresence.HandleForceReleaseControls clears them and the scene raises
        // OnScriptControlsReleased for this item
        var client = sp.ControllingClient;
        var evt = client.GetType().GetField("OnForceReleaseControls", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((ForceReleaseControls)evt.GetValue(client)!).Invoke(client, sp.UUID);
        Assert.False(Holds(sp, id));
        l.Pump(100);
        l.PumpUntil(() => l.Paused(id), 30000);
        int atPause = l.LastTick("ctl");
        l.Pump(400);
        l.PumpUntil(() => l.Paused(id), 30000);
        Log(l, "after force release");
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("ctl"));
    }

    [Fact]
    public void ControlsClearedByTheCorePauseTheScriptAtOnce()
    {
        using var l = new Land();
        var sp = SceneHelpers.AddScenePresence(l.H.Scene, Resident);
        var sog = l.AddObject("ctl2", East, Resident);
        var id = l.Rez(sog.RootPart, Controller, GrantControls(sp.UUID));
        Assert.True(l.PumpUntil(() => Holds(sp, id)));   // state_entry has taken them
        sog.AbsolutePosition = West;
        l.Pump();
        Assert.False(l.Paused(id));

        // ClearControls (the core's release on a crossing) raises OnScriptControlsReleased for this item; nothing else
        // happens, and Phlox's own record (MiscAttr.Control) still says "taken". Before the core raised it, this waited for the next
        // trigger.
        sp.ClearControls();
        Assert.False(Holds(sp, id));
        l.Pump(100);
        l.PumpUntil(() => l.Paused(id), 30000);
        int atPause = l.LastTick("ctl2");
        l.Pump(400);
        l.PumpUntil(() => l.Paused(id), 30000);
        Log(l, "after ClearControls");
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("ctl2"));
    }

    [Fact]
    public void AScriptReleasingItsControlsOnNoScriptsLandIsPaused()
    {
        using var l = new Land();
        var sp = SceneHelpers.AddScenePresence(l.H.Scene, Resident);
        var sog = l.AddObject("ctl3", East, Resident);
        var id = l.Rez(sog.RootPart, Controller.Replace("llTakeControls(1, TRUE, FALSE);", "llTakeControls(1, TRUE, FALSE); llListen(7, \"\", NULL_KEY, \"\");"),
                       GrantControls(sp.UUID));
        Assert.True(l.PumpUntil(() => Holds(sp, id)));   // state_entry has taken them
        sog.AbsolutePosition = West;
        l.Pump();
        Assert.False(l.Paused(id));

        l.H.Scene.SimChat("go", ChatTypeEnum.Region, 7, West, "tester", UUID.Random(), false);
        l.PumpUntil(() => l.Said("ctl3 released"), 30000);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Said("ctl3 released"));
        int atPause = l.LastTick("ctl3");
        l.Pump(400);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        Assert.Equal(atPause, l.LastTick("ctl3"));
    }

    // ── group scripts, owner and group changes ─────────────────────────────────

    [Fact]
    public void GroupScriptsRunWhenTheObjectsGroupIsTheParcelsGroup()
    {
        using var l = new Land();
        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: true, group: GroupA);
        var inGroup = l.AddObject("ga", West, Resident, GroupA);
        var other = l.AddObject("gb", West, Resident, GroupB);
        var a = l.Rez(inGroup.RootPart, Ticker);
        var b = l.Rez(other.RootPart, Ticker);
        l.Pump();
        l.PumpUntil(() => l.Paused(b), 30000);
        Assert.False(l.Paused(a));
        Assert.True(l.Paused(b));

        // the group flag off: the group script pauses too
        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: false);
        l.PumpUntil(() => l.Paused(a), 30000);
        Assert.True(l.Paused(a));
    }

    [Fact]
    public void AnObjectGroupOrOwnerChangeIsATrigger()
    {
        using var l = new Land();
        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: true, group: GroupA);
        var sog = l.AddObject("chg", West, Resident, GroupB);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));

        sog.SetGroup(GroupA, null);                           // the viewer's Set Group
        l.PumpUntil(() => !l.Paused(id), 30000);
        Assert.False(l.Paused(id));

        sog.SetGroup(GroupB, null);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));

        sog.SetOwnerId(ParcelOwner);                          // given to the parcel owner
        l.PumpUntil(() => !l.Paused(id), 30000);
        Assert.False(l.Paused(id));
    }

    // ── events while paused ─────────────────────────────────────────────────────

    [Fact]
    public void EventsArrivingWhilePausedAreDroppedAndListensComeBack()
    {
        using var l = new Land();
        const string src = @"default {
            state_entry() { llListen(5, """", NULL_KEY, """"); llSay(0, ""ready""); }
            listen(integer c, string n, key k, string m) { llSay(0, ""heard "" + m); }
            touch_start(integer t) { llSay(0, ""touched""); }
        }";
        var sog = l.AddObject("ev", East, Resident);
        var id = l.Rez(sog.RootPart, src);
        l.PumpUntil(() => l.Said("ready"), 30000);
        Assert.True(l.Said("ready"));

        sog.AbsolutePosition = West;
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        l.H.Scene.SimChat("while-paused", ChatTypeEnum.Region, 5, West, "tester", UUID.Random(), false);
        PostTouch(l, id);
        l.Pump();

        sog.AbsolutePosition = East;
        l.Pump();
        Log(l, "resumed");
        Assert.False(l.Said("heard while-paused"));        // dropped (Halcyon)
        Assert.False(l.Said("touched"));
        l.PumpUntil(() => !l.Paused(id), 30000);

        l.H.Scene.SimChat("after", ChatTypeEnum.Region, 5, East, "tester", UUID.Random(), false);
        l.PumpUntil(() => l.Said("heard after"), 30000);
        Assert.True(l.Said("heard after"));                 // the listen is still there
    }

    [Fact]
    public void EventsAlreadyQueuedAreKeptAndASleepCarriesOn()
    {
        using var l = new Land();
        const string src = @"default {
            state_entry() { llSay(0, ""ready""); }
            touch_start(integer t) { llSay(0, ""sleeping""); llSleep(0.5); llSay(0, ""woke""); }
            link_message(integer s, integer n, string m, key k) { llSay(0, ""queued ran""); }
        }";
        var sog = l.AddObject("q", East, Resident);
        var id = l.Rez(sog.RootPart, src);
        l.PumpUntil(() => l.Said("ready"), 30000);
        PostTouch(l, id);
        l.PumpUntil(() => l.Said("sleeping"), 30000);
        Assert.True(l.Said("sleeping"));
        // arrives while the handler sleeps, so it waits on the script's own queue
        l.Exe.PostEvent(id, new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.LINK_MESSAGE,
            Args = new object[] { 1, 0, "m", UUID.Zero.ToString() },
        });
        l.Pump(20);

        sog.AbsolutePosition = West;                           // paused mid-sleep, one event queued
        l.Pump(900);
        l.PumpUntil(() => l.Paused(id), 30000);
        Log(l, "paused mid-sleep");
        Assert.True(l.Paused(id));
        Assert.False(l.Said("woke"));
        Assert.False(l.Said("queued ran"));

        sog.AbsolutePosition = East;
        l.PumpUntil(() => l.Said("woke") && l.Said("queued ran"), 30000);
        Log(l, "resumed");
        Assert.True(l.Said("woke"));                           // the sleep carried on
        Assert.True(l.Said("queued ran"));                     // the queued event was kept
    }

    private static void PostTouch(Land l, UUID id)
        => l.Exe.PostEvent(id, new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
            Args = new object[] { 1 },
        });

    // ── owner-stopped versus parcel-paused ─────────────────────────────────────

    [Fact]
    public void AnOwnerStoppedScriptStaysStoppedWhenTheParcelAllowsIt()
    {
        using var l = new Land();
        var sog = l.AddObject("own", West, Resident);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        Assert.True(l.H.Engine.GetScriptState(id));                           // the owner's setting: running

        l.H.Scene.EventManager.TriggerStopScript(sog.RootPart.LocalId, id);   // the viewer's Running checkbox off
        l.PumpUntil(() => !l.H.Engine.GetScriptState(id), 30000);
        Assert.False(l.H.Engine.GetScriptState(id));

        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        l.Pump();
        l.PumpUntil(() => !l.Paused(id), 30000);
        Assert.False(l.Paused(id));
        Assert.Equal(0, l.Ticks("own"));                                       // the parcel does not start it
        Assert.False(l.Said("own entry"));
        Assert.False(l.H.Engine.GetScriptState(id));

        l.H.Scene.EventManager.TriggerStartScript(sog.RootPart.LocalId, id);  // the owner does
        l.PumpUntil(() => l.Said("own entry") && l.Ticks("own") > 0, 30000);
        Assert.True(l.Said("own entry"));
        Assert.True(l.Ticks("own") > 0);
    }

    [Fact]
    public void TheOwnerStartingAScriptOnNoScriptsLandLeavesItPaused()
    {
        using var l = new Land();
        var sog = l.AddObject("own2", East, Resident);
        var id = l.Rez(sog.RootPart, Ticker);
        l.Pump();
        l.H.Scene.EventManager.TriggerStopScript(sog.RootPart.LocalId, id);
        l.Pump(100);
        l.PumpUntil(() => !l.H.Engine.GetScriptState(id), 30000);
        int stoppedAt = l.LastTick("own2");

        sog.AbsolutePosition = West;
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));

        l.H.Scene.EventManager.TriggerStartScript(sog.RootPart.LocalId, id);
        l.Pump();
        l.PumpUntil(() => l.H.Engine.GetScriptState(id), 30000);
        Assert.True(l.Paused(id));
        Assert.True(l.H.Engine.GetScriptState(id));
        Assert.Equal(stoppedAt, l.LastTick("own2"));

        sog.AbsolutePosition = East;
        l.PumpUntil(() => !l.Paused(id), 30000);
        Assert.False(l.Paused(id));
    }

    [Fact]
    public void AResetOfAPausedScriptLeavesItPausedAndItStartsFreshOnResume()
    {
        using var l = new Land();
        var sog = l.AddObject("rst", East, Resident);
        var id = l.Rez(sog.RootPart, Ticker);
        l.PumpUntil(() => l.Ticks("rst") > 0, 30000);
        Assert.True(l.Ticks("rst") > 0);

        sog.AbsolutePosition = West;
        l.PumpUntil(() => l.Paused(id), 30000);
        Assert.True(l.Paused(id));
        l.H.ClearSaid(id);

        l.H.Engine.ResetScript(id);                                            // the viewer's Reset
        l.Pump();
        Assert.True(l.Paused(id));
        Assert.False(l.Said("rst entry"));

        sog.AbsolutePosition = East;
        l.PumpUntil(() => !l.Paused(id) && l.Said("rst entry") && l.Ticks("rst") > 0, 30000);
        Assert.False(l.Paused(id));
        Assert.True(l.Said("rst entry"));
        Assert.Equal(1, l.H.Said.Where(s => s.StartsWith("rst tick ")).Select(s => int.Parse(s.Substring(9))).Min());   // globals reset
    }

    // ── cost ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AParcelChangeWith200ObjectsTouchesOnlyTheScriptsOnThatParcel()
    {
        using var l = new Land();
        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        var westIds = new List<UUID>();
        var eastIds = new List<UUID>();
        const string idle = "default { state_entry() { } }";
        for (int i = 0; i < 100; i++)
        {
            westIds.Add(l.Rez(l.AddObject("w" + i, new Vector3(10 + i, 20 + (i % 50), 25), Resident).RootPart, idle));
            eastIds.Add(l.Rez(l.AddObject("e" + i, new Vector3(140 + i, 20 + (i % 50), 25), Resident).RootPart, idle));
        }
        l.Pump(800);
        Assert.All(westIds.Concat(eastIds), id => Assert.NotNull(l.H.InterpreterFor(id)));
        Assert.All(westIds.Concat(eastIds), id => Assert.False(l.Paused(id)));

        var before = l.Counters();
        var sw = Stopwatch.StartNew();
        l.SetFlags(l.WestParcel, otherScripts: false, groupScripts: false);
        l.H.PumpOnce();
        sw.Stop();
        var after = l.Counters();
        _out.WriteLine($"flag change, 200 scripted objects: scanned {after.Scanned - before.Scanned} scripts, evaluated {after.Evaluated - before.Evaluated}, " +
                       $"paused {after.Paused - before.Paused}, resumed {after.Resumed - before.Resumed}; one DoWork pass {sw.Elapsed.TotalMilliseconds:F2} ms");

        Assert.Equal(100, after.Evaluated - before.Evaluated);                 // only west's scripts
        Assert.Equal(100, after.Paused - before.Paused);
        Assert.Equal(0, after.Resumed - before.Resumed);
        Assert.All(westIds, id => Assert.True(l.Paused(id)));
        Assert.All(eastIds, id => Assert.False(l.Paused(id)));

        // a move inside one parcel raises nothing, so it costs nothing here
        before = l.Counters();
        l.H.Scene.GetSceneObjectGroups().Where(g => g.Name.StartsWith("e")).Take(50).ToList()
            .ForEach(g => g.AbsolutePosition = g.AbsolutePosition + new Vector3(0, 1, 0));
        l.H.PumpOnce();
        after = l.Counters();
        Assert.Equal(0, after.Evaluated - before.Evaluated);
    }

    /// <summary>
    /// A script whose object arrives by crossing onto a parcel where it may not run: its arrival changed(CHANGED_REGION)
    /// is posted with the restore, the parcel check then pauses it, and a paused script drops arriving events other than
    /// state_entry and state_exit. When the parcel later allows it, it runs on with its state and the dropped event does
    /// not come back.
    /// </summary>
    [Fact]
    public void AScriptArrivingOnANoScriptParcelIsPausedAndItsArrivalEventIsDropped()
    {
        const string Src = @"
            integer n;
            default {
                state_entry() { n = 5; llSay(0, ""state_entry""); }
                changed(integer c) { llSay(0, ""changed "" + (string)c); }
                touch_start(integer t) { n++; llSay(0, ""n="" + (string)n); }
            }";
        const int PrimCrossing = 2;
        UUID asset = UUID.Random(), item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SavedStateRig.Rez(h1, h1.Prim, Src, asset, item, 0, false, 1);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("state_entry")), SavedStateRig.SaidText(h1));
            h1.SaveState(item);
        }

        using var l = new Land();
        var box = l.AddObject("box", West, Resident);
        l.Rez(box.RootPart, Src, stateSource: PrimCrossing, itemId: item, assetId: asset);
        Assert.True(l.PumpUntil(() => l.Paused(item)), "not paused: " + l.H.StatusOf(item));
        l.Pump();
        Assert.DoesNotContain(l.H.Said, s => s.StartsWith("changed", StringComparison.Ordinal));
        Assert.DoesNotContain("state_entry", l.H.Said);

        l.SetFlags(l.WestParcel, otherScripts: true, groupScripts: false);
        Assert.True(l.PumpUntil(() => !l.Paused(item)), "not resumed: " + l.H.StatusOf(item));
        l.H.PostTouch(item);
        Assert.True(l.PumpUntil(() => l.Said("n=6")), SavedStateRig.SaidText(l.H));
        Assert.DoesNotContain(l.H.Said, s => s.StartsWith("changed", StringComparison.Ordinal));
        Assert.DoesNotContain("state_entry", l.H.Said);
    }
}
