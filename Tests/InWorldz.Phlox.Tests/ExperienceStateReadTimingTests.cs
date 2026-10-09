/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.Util;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// When the engine reads the state of the Experiences scripts hold grants from: every
/// <see cref="PhloxExecutionScheduler.ExperienceStateReadIntervalMs"/> on the engine's clock, so a held grant ends within
/// one interval of its Experience being disabled or suspended.
/// </summary>
// In the "phlox-state" collection: the tests set the engine clock (Clock.SetSourceForTesting), which is process-wide.
[Collection("phlox-state")]
public class ExperienceStateReadTimingTests
{
    internal sealed class FrozenClock : IDisposable
    {
        public ulong Now = (ulong)Environment.TickCount64;
        public FrozenClock() => Clock.SetSourceForTesting(() => Now);
        public void Dispose() => Clock.SetSourceForTesting(null);
    }

    /// <summary>A harness whose scripts take no 15 ms pause after speaking: on a clock that does not move, the pause
    /// would never end.</summary>
    internal static SchedulerHarness Harness()
        => new(cfg => cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", "false"));

    [Theory]
    [InlineData(ExperienceStateReadTests.Suspended, 9)]
    [InlineData(ExperienceStateReadTests.Disabled, 8)]
    public void AHeldGrantEndsWithinOneIntervalOfItsExperienceBeingSuspendedOrDisabled(int properties, int code)
    {
        using var clock = new FrozenClock();
        using var r = new ExperienceStateReadTests.Rig(Harness());
        r.Script("s1", r.X);
        r.Grant("s1");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        // The Experience changes now; the region learns it at its next read, which is at most one interval away.
        r.XProperties = properties;
        int before = r.Service.Lookups;
        clock.Now += (ulong)PhloxExecutionScheduler.ExperienceStateReadIntervalMs - 1;
        r.H.PumpFor(TimeSpan.FromMilliseconds(500));
        Assert.Equal(before, r.Service.Lookups);
        Assert.Equal(0, r.Denials("s1"));

        clock.Now += 1;
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("s1", code))), SavedStateRig.SaidText(r.H));
        Assert.Equal(0, r.MaskOf("s1"));
        Assert.Equal(before + 1, r.Service.Lookups);
    }

    [Fact]
    public void AGrantCarriedInWithAnObjectIsReadShortlyAfterItsRestore()
    {
        using var clock = new FrozenClock();
        using var r = new ExperienceStateReadTests.Rig(Harness());
        r.XProperties = ExperienceStateReadTests.Suspended;
        // The carried grant comes back by the decision llRequestExperiencePermissions makes, which does not read the
        // state; the read shortly after the restore does.
        var (copy, item) = r.CarryIn("src");
        int before = r.Service.Lookups;

        clock.Now += (ulong)PhloxExecutionScheduler.ExperienceStateFirstReadDelayMs;
        Assert.True(r.H.PumpUntil(() => r.H.Said.Contains(r.Denied("src", 9))), SavedStateRig.SaidText(r.H));
        Assert.Equal(0, copy.RootPart.Inventory.GetInventoryItem(item).PermsMask);
        Assert.Equal(before + 1, r.Service.Lookups);
        Assert.Equal(1, r.Denials("src"));
    }

    [Fact]
    public void ReadsRepeatEveryIntervalWhileAGrantIsHeld()
    {
        using var clock = new FrozenClock();
        using var r = new ExperienceStateReadTests.Rig(Harness());
        r.Script("s1", r.X);
        r.Grant("s1");
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(2));

        int before = r.Service.Lookups;
        for (int i = 1; i <= 3; i++)
        {
            clock.Now += (ulong)PhloxExecutionScheduler.ExperienceStateReadIntervalMs;
            int expect = before + i;
            Assert.True(r.H.PumpUntil(() => r.Service.Lookups == expect && !r.Exe.ExperienceStateReadRunning), "read " + i + " never ran");
        }
        Assert.Equal(r.Perms("s1", ExperienceStateReadTests.ExperiencePerms, r.A), r.Report("s1"));
    }
}
