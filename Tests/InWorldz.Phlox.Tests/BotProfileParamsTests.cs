/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// botGetProfileParams reads back what botSetProfileParams stored, all four fields, as Halcyon's botGetProfileParams
/// read the bot's profile (Halcyon LSLSystemAPI.cs botGetProfileParams: BOT_ABOUT_TEXT, BOT_EMAIL, BOT_IMAGE_UUID,
/// BOT_PROFILE_URL from the stored profile). The profile is the bot's, wherever the bot stands, so a script in another
/// region of the same simulator reads the same values.
/// </summary>
// Runs in parallel: each test has its own scenes and bot manager. The one process-wide state it touches is the grid store
// the test regions share, and its second region stands where no other test region is.
public class BotProfileParamsTests
{
    private static readonly UUID Image = new UUID("6b1c3f0e-2d4a-4e8b-9c7d-5a1f2e3b4c5d");

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
        return (h, bots);
    }

    private static string ReadBack(SchedulerHarness h, UUID bot, string before)
    {
        h.RezScript(@"default { state_entry() {
            " + before + @"
            llSay(0, ""profile="" + llDumpList2String(botGetProfileParams(""" + bot + @""",
                [BOT_ABOUT_TEXT, BOT_EMAIL, BOT_IMAGE_UUID, BOT_PROFILE_URL]), ""|""));
        } }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("profile=", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        return h.Said.First(s => s.StartsWith("profile=", StringComparison.Ordinal)).Substring("profile=".Length);
    }

    [Fact]
    public void TheEmailAndProfileUrlASetStoredReadBack()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            var client = h.AddClient();
            h.Prim.OwnerID = client.AgentId;
            UUID bot = bots.CreateBot("Test", "Bot", new Vector3(130, 130, 25), "", UUID.Zero, client.AgentId, out string reason);
            Assert.True(bot != UUID.Zero, reason);

            string got = ReadBack(h, bot, @"botSetProfileParams(""" + bot + @""", [BOT_ABOUT_TEXT, ""About me"",
                BOT_EMAIL, ""bot@example.org"", BOT_IMAGE_UUID, """ + Image + @""", BOT_PROFILE_URL, ""http://example.org/bot""]);");

            Assert.Equal("About me|bot@example.org|" + Image + "|http://example.org/bot", got);
        }
        finally { bots.Close(); }
    }

    [Fact]
    public void AScriptInAnotherRegionReadsTheSameProfile()
    {
        var (h, bots) = BotScene();
        using (h)
        try
        {
            // A second region on the same simulator, sharing the bot manager. The bot's owner stands only there,
            // so the bot is created there; the script that reads the profile runs in the harness's region.
            // Test regions share one grid store in the process: put this one where it neighbours no other test region
            // (a neighbour of the default region at 1000,1000 would change llEdgeOfWorld in tests running beside it).
            TestScene other = new SceneHelpers().SetupScene("Example Region B", UUID.Random(), 7360, 7360);
            SceneHelpers.SetupSceneModules(other, h.Config,
                new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
                new BasicInventoryAccessModule(), bots);
            ScenePresence owner = SceneHelpers.AddScenePresence(other, UUID.Random());
            h.Prim.OwnerID = owner.UUID;
            UUID bot = bots.CreateBot("Test", "Bot", new Vector3(130, 130, 25), "", UUID.Zero, owner.UUID, out string reason);
            Assert.True(bot != UUID.Zero, reason);
            Assert.NotNull(other.GetScenePresence(bot));
            Assert.Null(h.Scene.GetScenePresence(bot));

            bots.SetBotProfile(bot, "About me", "bot@example.org", Image, "http://example.org/bot", owner.UUID);

            Assert.Equal("About me|bot@example.org|" + Image + "|http://example.org/bot", ReadBack(h, bot, ""));
        }
        finally { bots.Close(); }
    }
}
