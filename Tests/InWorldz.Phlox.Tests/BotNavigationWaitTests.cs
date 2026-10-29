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

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A BOT_TRAVELMODE_WAIT point in botSetNavigationPoints holds the bot where it is for the point's number of seconds
/// before it goes on to the next point, as Halcyon's NodeGraph.GetNextPosition waited position.X seconds on a Wait node.
/// The points here are teleports, so the test needs no walking physics.
/// </summary>
// No process-wide state: each test has its own scene and bot manager, so the class runs in parallel.
public class BotNavigationWaitTests
{
    private static readonly Vector3 A = new Vector3(100, 100, 30), B = new Vector3(150, 150, 30);

    private static (SchedulerHarness H, BotManager Bots, UUID Bot) BotScene()
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
        UUID bot = bots.CreateBot("Test", "Bot", new Vector3(128, 128, 30), "", UUID.Zero, client.AgentId, out string reason);
        Assert.True(bot != UUID.Zero, reason);
        return (h, bots, bot);
    }

    private static bool Near(Vector3 a, Vector3 b) => Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f;

    private static void Navigate(SchedulerHarness h, UUID bot, string then = "")
    {
        h.RezScript(@"default { state_entry() {
            botSetNavigationPoints(""" + bot + @""", [" + A + @", 1.0, " + B + @"],
                [BOT_TRAVELMODE_TELEPORT, BOT_TRAVELMODE_WAIT, BOT_TRAVELMODE_TELEPORT], []);
            llSay(0, ""set"");
            " + then + @"
        } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("set")), string.Join(" | ", h.Said));
    }

    [Fact]
    public void TheBotWaitsAtTheWaitPointForItsSecondsThenGoesOn()
    {
        var (h, bots, bot) = BotScene();
        using (h)
        try
        {
            ScenePresence sp = h.Scene.GetScenePresence(bot);
            var clock = Stopwatch.StartNew();
            Navigate(h, bot);

            Assert.True(Near(sp.AbsolutePosition, A), "after the call the bot should wait at the first point, is at " + sp.AbsolutePosition);
            Assert.True(h.PumpUntil(() => Near(sp.AbsolutePosition, B), TimeSpan.FromSeconds(20)), "never went on, at " + sp.AbsolutePosition);
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), "went on after " + clock.Elapsed);
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void StoppingTheBotDuringTheWaitEndsThePath()
    {
        var (h, bots, bot) = BotScene();
        using (h)
        try
        {
            ScenePresence sp = h.Scene.GetScenePresence(bot);
            Navigate(h, bot, @"botStopMovement(""" + bot + @"""); llSay(0, ""stopped"");");
            Assert.True(h.PumpUntil(() => h.Said.Contains("stopped")), string.Join(" | ", h.Said));

            // Proving something does NOT happen: a real window past the 1 s wait and the 0.5 s poll.
            h.PumpFor(TimeSpan.FromSeconds(2.5));
            Assert.True(Near(sp.AbsolutePosition, A), "the stopped bot went on to " + sp.AbsolutePosition);
        }
        finally { bots.Close(); }
    }
}
