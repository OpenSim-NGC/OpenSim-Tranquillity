/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
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
/// A persistent bot brought back after a region restart keeps the key it had, so a script that stored the key still
/// reaches it. If something else in the region already holds that key, the bot comes back under a new key, as it did
/// before, and its saved record moves to the new key.
/// </summary>
// No process-wide state: each test has its own scene, bot manager and database file, so the class runs in parallel.
public class BotRespawnKeyTests
{
    private static (SchedulerHarness H, BotManager Bots, UUID Owner) BotScene()
    {
        // Each test's database goes in its own folder under the test output folder.
        string dir = Path.Combine(AppContext.BaseDirectory, "bot-respawn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
            var p = cfg.AddConfig("BotPersistence");
            p.Set("Enabled", "true");
            p.Set("DatabaseFile", Path.Combine(dir, "bots.db"));
            p.Set("RespawnRate", "10");
        });
        var bots = new BotManager();
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), bots, new ChatModule());
        var client = h.AddClient();
        h.Prim.OwnerID = client.AgentId;
        return (h, bots, client.AgentId);
    }

    private static UUID PersistentBot(SchedulerHarness h, BotManager bots, UUID owner)
    {
        UUID bot = bots.CreateBot("Test", "Bot", new Vector3(128, 128, 30), "", UUID.Zero, owner, out string reason);
        Assert.True(bot != UUID.Zero, reason);
        Assert.Equal(BotPersistError.OK, bots.PersistenceManager.SetPersistent(bot, owner, UUID.Zero, h.Prim.UUID, 0));
        return bot;
    }

    /// <summary>As the simulator does on a restart: the old bot manager leaves the region, a new one loads into it.</summary>
    private static BotManager Restart(SchedulerHarness h, BotManager old, Action beforeLoad = null)
    {
        old.RemoveRegion(h.Scene);
        old.Close();
        beforeLoad?.Invoke();
        var bots = new BotManager();
        bots.Initialise(h.Config);
        bots.AddRegion(h.Scene);
        bots.RegionLoaded(h.Scene);
        return bots;
    }

    // The bot is in the region and its saved record names its key (the record moves after the bot is made).
    private static bool BackAndRecorded(BotManager bots)
    {
        var all = bots.GetAllBots();
        return all.Count == 1 && bots.PersistenceManager.IsPersistent(all[0]);
    }

    [Fact]
    public void APersistentBotComesBackUnderTheKeyItHad()
    {
        var (h, bots, owner) = BotScene();
        using (h)
        try
        {
            UUID bot = PersistentBot(h, bots, owner);
            bots = Restart(h, bots);

            Assert.True(h.PumpUntil(() => BackAndRecorded(bots), TimeSpan.FromSeconds(20)), "the bot never came back");
            Assert.Equal(bot, bots.GetAllBots().Single());
            Assert.True(bots.IsBot(bot));
            Assert.NotNull(h.Scene.GetScenePresence(bot));
            Assert.True(bots.PersistenceManager.IsPersistent(bot));
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void ABotWhoseKeyIsInUseComesBackUnderANewKey()
    {
        var (h, bots, owner) = BotScene();
        using (h)
        try
        {
            UUID bot = PersistentBot(h, bots, owner);
            // Before the bots load, an avatar holding the bot's key is in the region.
            bots = Restart(h, bots, () => SceneHelpers.AddScenePresence(h.Scene, bot));

            Assert.True(h.PumpUntil(() => BackAndRecorded(bots), TimeSpan.FromSeconds(20)), "the bot never came back");
            UUID back = bots.GetAllBots().Single();
            Assert.NotEqual(bot, back);
            Assert.NotNull(h.Scene.GetScenePresence(back));
            Assert.False(bots.IsBot(bot));
            Assert.False(h.Scene.GetScenePresence(bot).IsNPC);
            Assert.True(bots.PersistenceManager.IsPersistent(back));
            Assert.False(bots.PersistenceManager.IsPersistent(bot));
        }
        finally { bots.Close(); }
    }
}
