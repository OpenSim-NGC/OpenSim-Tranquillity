/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// bot_update from botWanderWithin, as Halcyon's WanderingAction raised it: each wander point is a path of the point and,
/// when BOT_WANDER_TIME_BETWEEN_NODES is not 0, a wait after it. Reaching the point moves on to the wait, so
/// BOT_MOVE_UPDATE [1, bot position] is raised; with no wait the path just ends and a new point is picked, with no event.
/// A point not reached in 60 s is teleported to, with BOT_MOVE_FAILED and BOT_MOVE_UPDATE [1, bot position].
/// </summary>
// No process-wide state: each test has its own scene and bot manager, so the class runs in parallel.
public class BotWanderUpdateTests
{
    private readonly ITestOutputHelper _out;
    public BotWanderUpdateTests(ITestOutputHelper o) => _out = o;

    private static (SchedulerHarness H, BotManager Bots) BotScene()
    {
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
        });
        var bots = new BotManager();
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), bots, new ChatModule());
        var client = h.AddClient();
        h.Prim.OwnerID = client.AgentId;
        return (h, bots);
    }

    /// <summary>The script makes the bot at &lt;128, 128, 30&gt;, says its key, then runs <paramref name="then"/>.</summary>
    private static UUID CreateBot(SchedulerHarness h, string then)
    {
        h.RezScript(@"key bot;
            default {
                state_entry() {
                    bot = botCreateBot(""Test"", ""Bot"", """", <128, 128, 30>, 0);
                    llSay(0, ""bot="" + (string)bot);
                    " + then + @"
                }
                bot_update(string id, integer flag, list p) {
                    llSay(0, ""u "" + (string)flag + "" "" + llDumpList2String(p, ""|""));
                }
            }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("bot=", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        return UUID.Parse(h.Said.First(s => s.StartsWith("bot=", StringComparison.Ordinal)).Substring(4));
    }

    private string[] Updates(SchedulerHarness h)
    {
        var all = h.Said.Where(s => s.StartsWith("u ", StringComparison.Ordinal)).ToArray();
        _out.WriteLine(string.Join(Environment.NewLine, all));
        return all;
    }

    private static string V(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "<{0:F6}, {1:F6}, {2:F6}>", v.X, v.Y, v.Z);

    [Fact]
    public void EachWanderPointWithAWaitAfterItIsReportedWhenTheBotReachesIt()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            // A wander area of no size around where the bot stands: every point is reached at once.
            UUID bot = CreateBot(h, @"botWanderWithin(bot, <128, 128, 30>, 0.0, 0.0, [BOT_WANDER_TIME_BETWEEN_NODES, 1]);");

            Assert.True(h.PumpUntil(() => Updates(h).Length >= 2, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            string expected = "u 2 1|" + V(h.Scene.GetScenePresence(bot).AbsolutePosition);
            Assert.All(Updates(h), u => Assert.Equal(expected, u));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AWanderPointWithNoWaitAfterItIsNotReported()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            CreateBot(h, @"botWanderWithin(bot, <128, 128, 30>, 0.0, 0.0, []);");

            // Proving something does NOT happen: a real window over several points reached, at the 0.5 s poll.
            h.PumpFor(TimeSpan.FromSeconds(2.5));
            Assert.Empty(Updates(h));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AWanderPointNotReachedInTimeIsReportedFailedAndTeleportedTo()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            Vector3 far = new Vector3(20, 20, 30);
            UUID bot = CreateBot(h, @"botWanderWithin(bot, " + far + @", 0.0, 0.0, [BOT_WANDER_TIME_BETWEEN_NODES, 1]); llSay(0, ""wandering"");");
            Assert.True(h.PumpUntil(() => h.Said.Contains("wandering")), string.Join(" | ", h.Said));
            // A point is teleported to after 60 s; bring the time spent walking to it to just under that.
            SetNavElapsed(bots, bot, 59_900);

            Assert.True(h.PumpUntil(() => Updates(h).Length >= 2, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            Assert.Equal(new[] { "u 3 1|" + V(far), "u 2 1|" + V(far) }, Updates(h).Take(2));
        }
        finally { bots.Close(); }
    }

    private static void SetNavElapsed(BotManager bots, UUID bot, long ms)
    {
        var map = (IDictionary)typeof(BotManager).GetField("m_bots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bots)!;
        object data;
        lock (map) data = map[bot]!;
        data.GetType().GetField("NavElapsedMs")!.SetValue(data, ms);
    }
}
