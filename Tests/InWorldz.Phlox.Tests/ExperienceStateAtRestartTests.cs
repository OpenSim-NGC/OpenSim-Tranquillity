/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Threading;
using InWorldz.Phlox.Serialization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A grant from an Experience saved by a real region stop (Scene.Close) and given back from the state database at the
/// next start comes back whole: the start asks the Experience service nothing. The region reads the Experience's state
/// shortly after the restore, off the scheduler thread (<see cref="PhloxExecutionScheduler.ExperienceStateFirstReadDelayMs"/>),
/// and when that read finds it disabled or suspended the grant ends there: the grant and the controls it took go, the
/// next save holds no grant, and the script is told once with experience_permissions_denied and the Experience's state,
/// XP_ERROR_EXPERIENCE_DISABLED (8) or XP_ERROR_EXPERIENCE_SUSPENDED (9), 8 when both are set (SL wiki
/// llGetExperienceErrorMessage). A read the service does not answer changes nothing; a later one that it answers does.
/// </summary>
// In the "phlox-state" collection: the tests set the engine clock (Clock.SetSourceForTesting), which is process-wide.
[Collection("phlox-state")]
public class ExperienceStateAtRestartTests
{
    /// <summary>SL's list: TAKE_CONTROLS | TRIGGER_ANIMATION | ATTACH | TRACK_CAMERA | CONTROL_CAMERA | TELEPORT.</summary>
    private const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const int RegionStart = 0;
    private const string Phlox = "InWorldz.Phlox";
    private const int Disabled = (int)ExperienceFlags.Disabled;
    private const int Suspended = (int)ExperienceFlags.Suspended;

    /// <summary>Driven over channel 7 as "xp KEY"; a touch reports the grant.</summary>
    private const string Game = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                if (llList2String(w, 0) == ""xp"") llRequestExperiencePermissions(llList2Key(w, 1), """");
            }
            experience_permissions(key a) { llSay(0, ""xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""xpdenied="" + (string)a + "" "" + (string)r); }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
        }";

    private readonly UUID m_owner = UUID.Random(), m_visitor = UUID.Random(), m_experience = UUID.Random(),
                          m_asset = UUID.Random(), m_item = UUID.Random();

    /// <summary>
    /// The core's Experience module on <paramref name="h"/>'s scene, over a service that knows the Experience with
    /// <paramref name="properties"/>; the estate allows it.
    /// </summary>
    private ExperienceStateTests.StateService Region(SchedulerHarness h, int properties)
    {
        foreach (SceneObjectPart p in h.Prim.ParentGroup.Parts) p.OwnerID = m_owner;
        var service = ExperienceStateTests.StateService.Create(new ExperienceInfo
        {
            public_id = m_experience, owner_id = UUID.Random(), group_id = UUID.Random(), name = "Example Experience", properties = properties
        });
        h.Scene.RegisterModuleInterface<IExperienceService>((IExperienceService)(object)service);
        h.Scene.RegionInfo.EstateSettings.AllowedExperiences = new[] { m_experience };
        var config = new IniConfigSource();
        config.AddConfig("Experience").Set("Enabled", "true");
        var module = new ExperienceModule();
        SceneHelpers.SetupSceneModules(h.Scene, config, module);
        return service;
    }

    /// <summary>The visitor grants the script the Experience's list, and the region stops as the simulator stops it.</summary>
    private void GrantAndStopRegion()
    {
        using var h1 = ExperienceStateReadTimingTests.Harness();
        Region(h1, 0);
        SceneHelpers.AddScenePresence(h1.Scene, m_visitor);
        Assert.True(h1.Scene.RequestModuleInterface<OpenSim.Region.Framework.Interfaces.IExperienceModule>()
                      .SetExperiencePermissions(m_visitor, m_experience, true));
        var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, m_item, m_asset, "game", Game);
        inv.ExperienceID = m_experience;
        Assert.True(h1.Prim.Inventory.CreateScriptInstance(m_item, 0, false, Phlox, RegionStart));
        h1.Prim.ParentGroup.ResumeScripts();
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        h1.Scene.SimChat("xp " + m_visitor, ChatTypeEnum.Region, 7, h1.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("xp=" + m_visitor)), SavedStateRig.SaidText(h1));
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
        h1.StopRegionAsTheSimulatorDoes();
        Assert.Equal(m_experience.ToString(), RowState().PermsExperience);
    }

    /// <summary>
    /// The next start: a new region holding the same item, its Experience now with <paramref name="properties"/>.
    /// <paramref name="prepare"/> sets the service up before the script starts.
    /// </summary>
    private SchedulerHarness Restart(int properties, Action<ExperienceStateTests.StateService> prepare, out ExperienceStateTests.StateService service)
    {
        var h2 = ExperienceStateReadTimingTests.Harness();
        service = Region(h2, properties);
        prepare?.Invoke(service);
        var inv = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, m_item, m_asset, "game", Game);
        inv.ExperienceID = m_experience;
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(m_item) != null), "the script did not load");
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh
        return h2;
    }

    private SerializedRuntimeState RowState() => StateManager.Decode(SavedStateRig.Row(m_item)!.Value.Blob);

    private string Report(SchedulerHarness h)
    {
        static bool Line(string s) => s.StartsWith("perms=", StringComparison.Ordinal);
        int before = h.Said.Count(Line);
        h.PostTouch(m_item);
        Assert.True(h.PumpUntil(() => h.Said.Count(Line) > before), SavedStateRig.SaidText(h));
        return h.Said.Last(Line);
    }

    private static string Perms(int mask, UUID key) => "perms=" + mask + " key=" + key;

    private static int Denials(SchedulerHarness h) => h.Said.Count(s => s.StartsWith("xpdenied=", StringComparison.Ordinal));

    /// <summary>Move the engine clock on by <paramref name="ms"/> and pump until the read that brings has finished.</summary>
    private static void ReadAfter(SchedulerHarness h, ExperienceStateReadTimingTests.FrozenClock clock, ExperienceStateTests.StateService service, int ms)
    {
        int before = service.Lookups;
        clock.Now += (ulong)ms;
        Assert.True(h.PumpUntil(() => service.Lookups > before && !SavedStateRig.Exe(h).ExperienceStateReadRunning),
            "no read finished; lookups " + before + " -> " + service.Lookups);
        h.PumpUntilIdle(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(Suspended, 9)]
    [InlineData(Disabled, 8)]
    [InlineData(Disabled | Suspended, 8)]
    public void AGrantRestoredWhileItsExperienceCannotRunEndsAtTheFirstReadAndIsToldOnce(int properties, int code)
    {
        using var clock = new ExperienceStateReadTimingTests.FrozenClock();
        GrantAndStopRegion();

        using var h2 = Restart(properties, null, out var service);
        // The start asked the service nothing: the grant is back as it was saved.
        Assert.Equal(0, service.Lookups);
        Assert.Equal(ExperiencePerms, h2.Prim.Inventory.GetInventoryItem(m_item).PermsMask);
        Assert.Equal(0, Denials(h2));

        ReadAfter(h2, clock, service, PhloxExecutionScheduler.ExperienceStateFirstReadDelayMs);
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("xpdenied=" + m_visitor + " " + code)), SavedStateRig.SaidText(h2));
        Assert.Equal(0, h2.Prim.Inventory.GetInventoryItem(m_item).PermsMask);
        Assert.Equal(Perms(0, UUID.Zero), Report(h2));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Denials(h2));
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("xp=", StringComparison.Ordinal) || s.StartsWith("rtp=", StringComparison.Ordinal));

        // The ended grant is not in the next row either.
        h2.SaveState(m_item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Null(RowState().PermsGranter);
        Assert.Null(RowState().PermsExperience);
    }

    [Fact]
    public void AGrantRestoredWhileItsExperienceIsEnabledComesBackWholeAndStaysAfterTheFirstRead()
    {
        using var clock = new ExperienceStateReadTimingTests.FrozenClock();
        GrantAndStopRegion();

        using var h2 = Restart(0, null, out var service);
        Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));
        ReadAfter(h2, clock, service, PhloxExecutionScheduler.ExperienceStateFirstReadDelayMs);
        Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));
        Assert.Equal(0, Denials(h2));
        h2.SaveState(m_item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Equal(m_experience.ToString(), RowState().PermsExperience);
    }

    [Fact]
    public void AGrantRestoredWhileTheServiceCannotBeReachedIsKeptAndEndsAtALaterReadOnceTheServiceAnswers()
    {
        using var clock = new ExperienceStateReadTimingTests.FrozenClock();
        GrantAndStopRegion();

        using var h2 = Restart(Suspended, s => s.LookupFails = true, out var service);
        ReadAfter(h2, clock, service, PhloxExecutionScheduler.ExperienceStateFirstReadDelayMs);
        Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));
        Assert.Equal(0, Denials(h2));

        // The service answers again, and the Experience is still suspended: the next read ends the grant.
        service.LookupFails = false;
        ReadAfter(h2, clock, service, PhloxExecutionScheduler.ExperienceStateReadIntervalMs);
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("xpdenied=" + m_visitor + " 9")), SavedStateRig.SaidText(h2));
        Assert.Equal(Perms(0, UUID.Zero), Report(h2));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Denials(h2));
    }

    [Fact]
    public void ARegionStartDoesNotWaitForAServiceThatDoesNotAnswer()
    {
        using var clock = new ExperienceStateReadTimingTests.FrozenClock();
        GrantAndStopRegion();

        // The service takes every lookup and answers none until the gate opens (or 5 s pass, so a start that does wait
        // ends this test instead of holding it).
        var gate = new ManualResetEventSlim(false);
        using var h2 = Restart(0, s => { s.Gate = gate; s.GateTimeout = TimeSpan.FromSeconds(5); }, out var service);
        try
        {
            Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));

            // The read starts and waits on the service; the scripts still run.
            int before = service.Lookups;
            clock.Now += (ulong)PhloxExecutionScheduler.ExperienceStateFirstReadDelayMs;
            Assert.True(h2.PumpUntil(() => service.Lookups > before), "the read never asked");
            Assert.True(SavedStateRig.Exe(h2).ExperienceStateReadRunning);
            Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));

            // Not one lookup was made on the scheduler's thread (here the test's own, which pumps it).
            string pumping = Thread.CurrentThread.Name ?? "(unnamed " + Thread.CurrentThread.ManagedThreadId + ")";
            Assert.All(service.LookupThreads, t => Assert.StartsWith("Phlox experience state ", t));
            Assert.DoesNotContain(pumping, service.LookupThreads);
        }
        finally { gate.Set(); }
        Assert.True(h2.PumpUntil(() => !SavedStateRig.Exe(h2).ExperienceStateReadRunning), "the read never finished");
        Assert.Equal(0, Denials(h2));
    }
}
