/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The region's "disable scripts" switch (the Region/Estate debug tab and the "debug scene set scripting" console
/// command) stops scripts without removing them: each engine keeps the script and its state, and the script goes on
/// from its state when scripts are allowed again. Each engine runs a script that counts with a timer and says the count.
/// No process-wide state: classes using it run in parallel.
/// </summary>
public class DisableScriptsPauseTests
{
    private const string Counter =
        "integer n; default { state_entry() { llSetTimerEvent(0.1); } timer() { n++; llSay(0, \"n=\" + (string)n); } }";

    private static int[] Counts(SchedulerHarness h)
        => h.Said.Where(s => s.StartsWith("n=")).Select(s => int.Parse(s[2..])).ToArray();

    private static void Disable(SchedulerHarness h)
    {
        // as the viewer's debug request does: the setting first, then the switch
        h.Scene.RegionInfo.RegionSettings.DisableScripts = true;
        h.Scene.ScriptsEnabled = false;
    }

    private static void Enable(SchedulerHarness h)
    {
        h.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        h.Scene.ScriptsEnabled = true;
    }

    private static void StopsKeepsAndGoesOn(SchedulerHarness h, UUID item, Func<bool> pump)
    {
        Assert.True(pump(), "the script never counted to 3");
        h.Scene.ScriptsEnabled = true;

        Disable(h);
        // the engine still has the script: it was paused, not removed
        h.PumpFor(TimeSpan.FromMilliseconds(200));
        Assert.True(HasScript(h, item), "the script was removed");
        int[] before = Counts(h);
        int last = before.Last();

        // a window in which nothing may happen
        h.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(last, Counts(h).Last());
        Assert.True(HasScript(h, item), "the script was removed while disabled");

        Enable(h);
        Assert.True(h.PumpUntil(() => Counts(h).Last() > last, TimeSpan.FromSeconds(30)), "the script never counted again");
        int[] after = Counts(h);
        // it carried on from its count: the next count follows the last one, it did not start again from 1
        Assert.Equal(last + 1, after[before.Length]);
    }

    private static bool HasScript(SchedulerHarness h, UUID item)
    {
        foreach (var engine in h.Scene.RequestModuleInterfaces<OpenSim.Region.Framework.Interfaces.IScriptModule>())
            if (engine.HasScript(item, out _))
                return true;
        return false;
    }

    [Fact]
    public void PhloxStopsScriptsKeepsTheirCountAndGoesOnFromIt()
    {
        using var h = new SchedulerHarness();
        UUID item = h.RezScript(Counter);
        StopsKeepsAndGoesOn(h, item, () => h.PumpUntil(() => Counts(h).Length >= 3, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void YEngineStopsScriptsKeepsTheirCountAndGoesOnFromIt()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        UUID item = SchedulerHarnessYEngine.Rez(h, h.Prim, Counter);
        StopsKeepsAndGoesOn(h, item, () => h.PumpUntil(() => Counts(h).Length >= 3, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void TheViewersDebugRequestAndTheConsoleSwitchDoTheSame()
    {
        // The console command changes only the switch, not the saved setting; the scripts are paused just the same.
        using var h = new SchedulerHarness();
        UUID item = h.RezScript(Counter);
        Assert.True(h.PumpUntil(() => Counts(h).Length >= 3, TimeSpan.FromSeconds(30)));

        h.Scene.ScriptsEnabled = false;
        h.PumpFor(TimeSpan.FromMilliseconds(200));
        int last = Counts(h).Last();
        h.PumpFor(TimeSpan.FromSeconds(1));
        Assert.Equal(last, Counts(h).Last());
        Assert.True(HasScript(h, item));

        h.Scene.ScriptsEnabled = true;
        Assert.True(h.PumpUntil(() => Counts(h).Last() > last, TimeSpan.FromSeconds(30)));
        Assert.Equal(last + 1, Counts(h).First(c => c > last));
    }

    [Fact]
    public void ASwitchSetToTheValueItHasChangesNothing()
    {
        using var h = new SchedulerHarness();
        UUID item = h.RezScript(Counter);
        Assert.True(h.PumpUntil(() => Counts(h).Length >= 3, TimeSpan.FromSeconds(30)));

        h.Scene.ScriptsEnabled = true;
        Assert.True(h.PumpUntil(() => Counts(h).Length >= 5, TimeSpan.FromSeconds(30)));
        Assert.True(HasScript(h, item));
        // never started again from 1
        Assert.Equal(Counts(h).OrderBy(c => c).ToArray(), Counts(h));
    }
}
