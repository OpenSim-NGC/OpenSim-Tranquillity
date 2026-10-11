/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Diagnostics;
using System.Linq;
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
/// botSetNavigationPoints options as Halcyon read them (NavigationPathAction.NavigationPathDescription, NodeGraph.GetNextPosition,
/// MovementAction.GetNextDestination). A bot that has not reached a point after BOT_MOVEMENT_TELEPORT_AFTER seconds (60 by
/// default) is teleported to it, BOT_MOVE_FAILED and BOT_MOVE_UPDATE report the node after it, and the path goes on.
/// BOT_MOVEMENT_TYPE BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY starts the path again at its first point after the last, reported
/// as a move on to node (number of points), and the path never completes.
/// </summary>
// No process-wide state: each test has its own scene and bot manager, so the class runs in parallel.
public class BotNavigationTimeoutTests
{
    private readonly ITestOutputHelper _out;
    public BotNavigationTimeoutTests(ITestOutputHelper o) => _out = o;

    // Far is too far to walk to in the seconds these tests wait; A and B are reached by teleport.
    private static readonly Vector3 Far = new Vector3(20, 20, 30), A = new Vector3(100, 100, 30), B = new Vector3(150, 150, 30);

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

    /// <summary>The script makes the bot, says its key, then runs <paramref name="then"/> with the key in "bot".</summary>
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

    private static bool Near(Vector3 a, Vector3 b) => Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f;

    [Fact]
    public void ABotThatHasNotReachedAPointInTimeIsTeleportedThereAndGoesOn()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            var clock = Stopwatch.StartNew();
            CreateBot(h, @"botSetNavigationPoints(bot, [" + Far + ", " + B + @"],
                [BOT_TRAVELMODE_WALK, BOT_TRAVELMODE_TELEPORT], [BOT_MOVEMENT_TELEPORT_AFTER, 1]);");

            Assert.True(h.PumpUntil(() => Updates(h).Any(s => s.StartsWith("u 1 ", StringComparison.Ordinal)), TimeSpan.FromSeconds(20)),
                string.Join(" | ", h.Said));
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), "teleported after " + clock.Elapsed);
            Assert.Equal(new[]
            {
                "u 3 1|" + V(Far),
                "u 2 1|" + V(Far),
                "u 2 2|" + V(B),
                "u 1 " + V(B),
            }, Updates(h));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void FollowIndefinitelyStartsThePathAgainAndNeverCompletes()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            CreateBot(h, @"botSetNavigationPoints(bot, [" + A + ", " + B + @"],
                [BOT_TRAVELMODE_TELEPORT, BOT_TRAVELMODE_TELEPORT], [BOT_MOVEMENT_TYPE, BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY]);");

            Assert.True(h.PumpUntil(() => Updates(h).Length >= 6, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            // Each teleport moves on to the next node; after the last, the path starts again, reported as node 2.
            Assert.Equal(new[]
            {
                "u 2 1|" + V(A),
                "u 2 2|" + V(B),
                "u 2 2|" + V(B),
                "u 2 1|" + V(A),
                "u 2 2|" + V(B),
                "u 2 2|" + V(B),
            }, Updates(h).Take(6));
            Assert.DoesNotContain(Updates(h), s => s.StartsWith("u 1 ", StringComparison.Ordinal));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void TimeWhilePausedDoesNotCountTowardTheTeleport()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            UUID bot = CreateBot(h, @"botSetNavigationPoints(bot, [" + Far + @"], [BOT_TRAVELMODE_WALK], [BOT_MOVEMENT_TELEPORT_AFTER, 1]);
                botPauseMovement(bot);");
            ScenePresence sp = h.Scene.GetScenePresence(bot);

            // Proving something does NOT happen: a real window past the 1 s limit and the 0.5 s poll.
            h.PumpFor(TimeSpan.FromSeconds(2.5));
            Assert.Empty(Updates(h));
            Assert.False(Near(sp.AbsolutePosition, Far), "the paused bot was teleported");

            bots.ResumeBotMovement(bot, h.Prim.OwnerID);
            Assert.True(h.PumpUntil(() => Updates(h).Any(s => s.StartsWith("u 1 ", StringComparison.Ordinal)), TimeSpan.FromSeconds(20)),
                string.Join(" | ", h.Said));
            Assert.Equal(new[] { "u 3 1|" + V(Far), "u 2 1|" + V(Far), "u 1 " + V(Far) }, Updates(h));
        }
        finally { bots.Close(); }
    }
}
