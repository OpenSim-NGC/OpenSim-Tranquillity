/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenSim.Tests.Common;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// YEngine's OSSL implementation (OSSL_Api) reads the [OSSL] threat level and the Allow_ and Creators_ lines once per process,
/// when the first YEngine script in the process is made, and keeps them in statics. A harness that stands YEngine up with its own
/// [OSSL] section must still get that section, whichever harness stood YEngine up before it, and two harnesses with YEngine must
/// not be up at once because they would change each other's settings.
/// </summary>
[Collection("phlox-yengine")]
public class YEngineOsslSettingsTests
{
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene(string threat)
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat), withYEngine: true);

    /// <summary>Has a YEngine script call osMakeNotecard (threat level High) and gives the notecards made and the errors it was told.</summary>
    private static (int Notecards, string Errors) MakeNotecardInYEngine(SchedulerHarness h)
    {
        var part = SceneHelpers.AddSceneObject(h.Scene, "yengine prim", h.Prim.OwnerID).RootPart;
        SchedulerHarnessYEngine.Rez(h, part, "default { state_entry() { osMakeNotecard(\"made\", \"text\"); llSay(0, \"done\"); } }");
        string Errors() => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));
        Assert.True(h.PumpUntil(() => h.Said.Contains("done") || Errors().Length > 0, TimeSpan.FromSeconds(30)), "said: " + string.Join(" | ", h.Said));
        int cards = part.Inventory.GetInventoryItems().Count(i => i.Type == (int)OpenMetaverse.AssetType.Notecard);
        return (cards, Errors());
    }

    [Fact]
    public void ASceneStoodUpAfterAStricterOneGetsItsOwnLooserThreatLevel()
    {
        using (var strict = Scene("VeryLow"))
        {
            var (cards, errors) = MakeNotecardInYEngine(strict);
            Assert.Equal(0, cards);
            Assert.Contains("Allowed threat level is VeryLow", errors);
        }
        using (var loose = Scene("High"))
        {
            var (cards, errors) = MakeNotecardInYEngine(loose);
            Assert.Empty(errors);
            Assert.Equal(1, cards);
        }
    }

    [Fact]
    public void ASceneStoodUpAfterALooserOneGetsItsOwnStricterThreatLevel()
    {
        using (var loose = Scene("High"))
        {
            var (cards, errors) = MakeNotecardInYEngine(loose);
            Assert.Empty(errors);
            Assert.Equal(1, cards);
        }
        using (var strict = Scene("VeryLow"))
        {
            var (cards, errors) = MakeNotecardInYEngine(strict);
            Assert.Equal(0, cards);
            Assert.Contains("Allowed threat level is VeryLow", errors);
        }
    }

    [Fact]
    public void TwoHarnessesWithYEngineAreNeverUpAtOnce()
    {
        var order = new ConcurrentQueue<string>();
        using var first = new SchedulerHarness(withYEngine: true);
        Task second = Task.Run(() =>
        {
            using var h = new SchedulerHarness(withYEngine: true);
            order.Enqueue("second up");
        });

        // Not happening is what is shown here, so this is a real wait; standing a harness up with YEngine takes about a second.
        Assert.False(second.Wait(TimeSpan.FromSeconds(4)), "the second harness with YEngine came up while the first was up");
        order.Enqueue("first down");
        first.Dispose();   // a second Dispose at the end of the test does nothing
        Assert.True(second.Wait(TimeSpan.FromSeconds(60)), "the second harness did not come up after the first was disposed");
        Assert.Equal(new[] { "first down", "second up" }, order.ToArray());
    }
}
