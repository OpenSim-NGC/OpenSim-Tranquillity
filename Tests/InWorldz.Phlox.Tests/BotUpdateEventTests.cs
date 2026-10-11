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
/// bot_update(string botID, integer flag, list params) as Halcyon raised it (MovementAction.cs TriggerFinishedMovement,
/// TriggerChangingNodes, TriggerFailedToMoveToNextNode, TriggerAvatarLost, TriggerBotUpdate; NodeGraph.GetNextPosition):
/// BOT_MOVE_COMPLETE [bot position]; BOT_MOVE_UPDATE [next node, bot position] each time the bot moves on to another
/// node; BOT_MOVE_FAILED [next node, bot position]; BOT_MOVE_AVATAR_LOST [followed avatar's position, distance, bot
/// position]. It goes to every script registered for the bot's navigation events, and botCreateBot registers the
/// script that created the bot (Halcyon BotManager.CreateBot).
/// </summary>
// No process-wide state: each test has its own scene and bot manager, so the class runs in parallel.
public class BotUpdateEventTests
{
    private readonly ITestOutputHelper _out;
    public BotUpdateEventTests(ITestOutputHelper o) => _out = o;

    private static readonly Vector3 A = new Vector3(100, 100, 30), B = new Vector3(150, 150, 30);

    // Each script says every bot_update it gets as "<tag> <flag> <params joined by |> <entry types joined by ,>".
    private static string Reporter(string tag, string body) => @"key bot;
        default {
            " + body + @"
            bot_update(string id, integer flag, list p) {
                list types; integer i;
                for (i = 0; i < llGetListLength(p); ++i) types += llGetListEntryType(p, i);
                llSay(0, """ + tag + @" "" + (string)flag + "" "" + llDumpList2String(p, ""|"") + "" "" + llDumpList2String(types, "",""));
            }
        }";

    private static (SchedulerHarness H, BotManager Bots, ScenePresence Owner) BotScene()
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
        return (h, bots, h.Scene.GetScenePresence(client.AgentId));
    }

    /// <summary>The creator script makes the bot with botCreateBot and says its key; returns the key.</summary>
    private static UUID CreateBot(SchedulerHarness h, string afterCreate) => CreateBot(h, afterCreate, out _);

    private static UUID CreateBot(SchedulerHarness h, string afterCreate, out UUID creatorItem)
    {
        creatorItem = h.RezScript(Reporter("c", @"state_entry() {
                bot = botCreateBot(""Test"", ""Bot"", """", <128, 128, 30>, 0);
                llSay(0, ""bot="" + (string)bot);
                " + afterCreate + @"
            }"));
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("bot=", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        UUID bot = UUID.Parse(h.Said.First(s => s.StartsWith("bot=", StringComparison.Ordinal)).Substring(4));
        Assert.NotEqual(UUID.Zero, bot);
        return bot;
    }

    /// <summary>A second script in the prim that registers for the bot's navigation events.</summary>
    private static void RezRegistered(SchedulerHarness h, UUID bot)
    {
        h.RezScript(Reporter("r", @"state_entry() {
                botRegisterForNavigationEvents(""" + bot + @""");
                llSay(0, ""registered"");
            }"));
        Assert.True(h.PumpUntil(() => h.Said.Contains("registered")), string.Join(" | ", h.Said));
    }

    private string[] Updates(SchedulerHarness h, string tag)
    {
        var all = h.Said.Where(s => s.StartsWith(tag + " ", StringComparison.Ordinal)).ToArray();
        _out.WriteLine(string.Join(Environment.NewLine, all));
        return all;
    }

    // A vector as a list entry turned into a string: six digits after the point.
    private static string V(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "<{0:F6}, {1:F6}, {2:F6}>", v.X, v.Y, v.Z);

    [Fact]
    public void ANavigationPathReportsEachNodeAndItsEndToEveryRegisteredScript()
    {
        var (h, bots, _) = BotScene();
        using (h)
        try
        {
            UUID bot = CreateBot(h, "");
            RezRegistered(h, bot);
            h.RezScript(@"default { state_entry() {
                botSetNavigationPoints(""" + bot + @""", [" + A + @", 0.5, " + B + @"],
                    [BOT_TRAVELMODE_TELEPORT, BOT_TRAVELMODE_WAIT, BOT_TRAVELMODE_TELEPORT], []);
            } }");

            Assert.True(h.PumpUntil(() => Updates(h, "c").Any(s => s.StartsWith("c 1 ", StringComparison.Ordinal))
                                       && Updates(h, "r").Any(s => s.StartsWith("r 1 ", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));

            // Teleport to A (node 0) moves on to node 1; the wait ends and moves on to node 2; the teleport to B
            // moves on to node 3, past the last; the path is done. Halcyon raised an update after every teleport,
            // the last one included (MovementAction.GetNextDestination, TravelMode.Teleport).
            string[] expected =
            {
                "2 1|" + V(A) + " 1,5",
                "2 2|" + V(A) + " 1,5",
                "2 3|" + V(B) + " 1,5",
                "1 " + V(B) + " 5",
            };
            foreach (string tag in new[] { "c", "r" })
                Assert.Equal(expected.Select(e => tag + " " + e), Updates(h, tag));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AScriptThatDeregistersStopsGettingThemAndTheOthersStillDo()
    {
        var (h, bots, _) = BotScene();
        using (h)
        try
        {
            UUID bot = CreateBot(h, "", out UUID creator);
            RezRegistered(h, bot);
            // The creator deregisters; the other registered script keeps its registration.
            bots.BotDeregisterFromPathUpdateEvents(bot, creator, h.Prim.OwnerID);

            bots.SetBotNavigationPoints(bot, new() { A }, new() { OpenSim.Region.Framework.Interfaces.TravelMode.Teleport },
                new(), h.Prim.OwnerID);

            Assert.True(h.PumpUntil(() => Updates(h, "r").Any(s => s.StartsWith("r 1 ", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            Assert.Equal(new[] { "r 2 1|" + V(A) + " 1,5", "r 1 " + V(A) + " 5" }, Updates(h, "r"));
            Assert.Empty(Updates(h, "c"));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AMoveThatTimesOutReportsTheNextNodeAndTheBotsPosition()
    {
        var (h, bots, _) = BotScene();
        using (h)
        try
        {
            UUID bot = CreateBot(h, "");
            Vector3 first = new Vector3(20, 20, 30);
            bots.SetBotNavigationPoints(bot, new() { first, B },
                new() { OpenSim.Region.Framework.Interfaces.TravelMode.Walk, OpenSim.Region.Framework.Interfaces.TravelMode.Walk },
                new(), h.Prim.OwnerID);
            // The walk times out after 60 s by default; bring the time spent on it to just under the limit.
            SetNavElapsed(bots, bot, 59_900);

            // The bot is teleported to the point it did not reach (Halcyon MovementAction.GetNextDestination), so both
            // reports carry that point as the bot's position, then it walks on to B.
            Assert.True(h.PumpUntil(() => Updates(h, "c").Length >= 2, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            Assert.Equal(new[] { "c 3 1|" + V(first) + " 1,5", "c 2 1|" + V(first) + " 1,5" }, Updates(h, "c").Take(2));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AFollowedAvatarThatLeavesIsReportedLostOnce()
    {
        var (h, bots, owner) = BotScene();
        using (h)
        try
        {
            UUID bot = CreateBot(h, "");
            ScenePresence target = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
            target.AbsolutePosition = new Vector3(130, 130, 30);
            Assert.Equal(OpenSim.Region.Framework.Interfaces.BotMovementResult.Success,
                bots.StartFollowingAvatar(bot, target.UUID, new(), h.Prim.OwnerID));

            h.Scene.CloseAgent(target.UUID, false);

            Assert.True(h.PumpUntil(() => Updates(h, "c").Any(), TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            Vector3 botPos = h.Scene.GetScenePresence(bot).AbsolutePosition;
            Assert.Equal("c 4 " + V(Vector3.Zero) + "|0.000000|" + V(botPos) + " 5,2,5", Updates(h, "c").Single());

            // Proving something does NOT happen: a second report while the avatar stays gone.
            h.PumpFor(TimeSpan.FromSeconds(1.5));
            Assert.Single(Updates(h, "c"));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AFollowedAvatarBeyondTheLostDistanceIsReportedLost()
    {
        var (h, bots, owner) = BotScene();
        using (h)
        try
        {
            UUID bot = CreateBot(h, "");
            ScenePresence target = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
            target.AbsolutePosition = new Vector3(130, 130, 30);
            const int BOT_LOST_AVATAR_DISTANCE = 8;
            Assert.Equal(OpenSim.Region.Framework.Interfaces.BotMovementResult.Success,
                bots.StartFollowingAvatar(bot, target.UUID, new() { { BOT_LOST_AVATAR_DISTANCE, 5.0f } }, h.Prim.OwnerID));

            Vector3 far = new Vector3(200, 200, 30);
            target.AbsolutePosition = far;

            Assert.True(h.PumpUntil(() => Updates(h, "c").Any(), TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));
            Vector3 botPos = h.Scene.GetScenePresence(bot).AbsolutePosition;
            Assert.Equal("c 4 " + V(far) + "|0.000000|" + V(botPos) + " 5,2,5", Updates(h, "c").Single());
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
