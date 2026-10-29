/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
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
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// botFollowAvatar keeps following, as Halcyon's AvatarFollower did, and reads its options: the bot stops closer than
/// BOT_STOP_FOLLOWING_DISTANCE (2 m) to the avatar and starts again only past BOT_START_FOLLOWING_DISTANCE (3 m); it runs
/// when the avatar runs, unless BOT_ALLOW_RUNNING is 0; it flies when the avatar flies, or when the avatar is more than 3 m
/// above it, unless BOT_ALLOW_FLYING is 0; with BOT_ALLOW_JUMPING 0, or nothing to jump over, it keeps to its own height
/// for an avatar a little above it; with BOT_REQUIRES_LINE_OF_SIGHT 1 an object between them raises BOT_MOVE_AVATAR_LOST
/// [avatar position, distance, bot position] and the bot goes no further toward the avatar until it sees it again.
/// The bot is not moved by physics here; each test reads where the bot was last sent. Running is not tested: the test
/// scene's physics actor does not keep an avatar's always-run setting.
/// </summary>
// No process-wide state: each test has its own scene and bot manager, so the class runs in parallel.
public class BotFollowTests
{
    private readonly ITestOutputHelper _out;
    public BotFollowTests(ITestOutputHelper o) => _out = o;

    private const int BOT_ALLOW_FLYING = 2, BOT_ALLOW_JUMPING = 3, BOT_REQUIRES_LINE_OF_SIGHT = 5,
        BOT_START_FOLLOWING_DISTANCE = 6, BOT_STOP_FOLLOWING_DISTANCE = 7;

    private static readonly Vector3 BotStart = new Vector3(128, 128, 30);

    private sealed class Scene3 : IDisposable
    {
        public SchedulerHarness H;
        public BotManager Bots;
        public UUID Owner;
        public ScenePresence Bot, Avatar;
        public void Dispose() { Bots.Close(); H.Dispose(); }
    }

    private static Scene3 Setup(Vector3 avatarAt)
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
        UUID bot = bots.CreateBot("Test", "Bot", BotStart, "", UUID.Zero, client.AgentId, out string reason);
        Assert.True(bot != UUID.Zero, reason);
        ScenePresence avatar = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        avatar.AbsolutePosition = avatarAt;
        return new Scene3 { H = h, Bots = bots, Owner = client.AgentId, Bot = h.Scene.GetScenePresence(bot), Avatar = avatar };
    }

    private static void Follow(Scene3 s, Dictionary<int, object> options) =>
        Assert.Equal(BotMovementResult.Success, s.Bots.StartFollowingAvatar(s.Bot.UUID, s.Avatar.UUID, options, s.Owner));

    private static bool HeadingFor(ScenePresence bot, Vector3 pos) =>
        bot.MovingToTarget && Math.Abs(bot.MoveToPositionTarget.X - pos.X) < 0.01f && Math.Abs(bot.MoveToPositionTarget.Y - pos.Y) < 0.01f;

    private void Log(Scene3 s) => _out.WriteLine("bot at " + s.Bot.AbsolutePosition + ", moving " + s.Bot.MovingToTarget
        + " to " + s.Bot.MoveToPositionTarget + ", flying " + s.Bot.Flying);

    [Fact]
    public void TheBotKeepsFollowingTheAvatarAsItMoves()
    {
        Vector3 first = new Vector3(138, 128, 30), second = new Vector3(128, 140, 30), third = new Vector3(118, 120, 30);
        using var s = Setup(first);
        Follow(s, new());

        foreach (Vector3 at in new[] { first, second, third })
        {
            s.Avatar.AbsolutePosition = at;
            Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, at), TimeSpan.FromSeconds(20)), "not heading for " + at);
        }
        Log(s);
    }

    [Fact]
    public void TheBotStopsInsideTheStopDistanceAndStartsAgainOnlyPastTheStartDistance()
    {
        using var s = Setup(new Vector3(131, 128, 30));   // 3 m away
        Follow(s, new() { { BOT_STOP_FOLLOWING_DISTANCE, 4 }, { BOT_START_FOLLOWING_DISTANCE, 6.0f } });

        Assert.True(s.H.PumpUntil(() => !s.Bot.MovingToTarget, TimeSpan.FromSeconds(20)), "the bot did not stop 3 m from the avatar");

        // Proving something does NOT happen: 5 m is past the stop distance but not the start distance.
        s.Avatar.AbsolutePosition = new Vector3(133, 128, 30);
        s.H.PumpFor(TimeSpan.FromSeconds(1.5));
        Log(s);
        Assert.False(s.Bot.MovingToTarget, "the bot started again 5 m from the avatar");

        Vector3 far = new Vector3(135, 128, 30);   // 7 m away
        s.Avatar.AbsolutePosition = far;
        Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, far), TimeSpan.FromSeconds(20)), "the bot did not start again 7 m from the avatar");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBotFliesToAnAvatarHighAboveUnlessFlyingIsNotAllowed(bool allowed)
    {
        Vector3 at = new Vector3(140, 128, 60);
        using var s = Setup(at);
        Follow(s, new() { { BOT_ALLOW_FLYING, allowed ? 1 : 0 } });

        Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, at), TimeSpan.FromSeconds(20)), "not heading for the avatar");
        s.H.PumpUntil(() => s.Bot.Flying == allowed, TimeSpan.FromSeconds(5));
        Log(s);
        Assert.Equal(allowed, s.Bot.Flying);
    }

    [Fact]
    public void WithNothingToJumpOverTheBotKeepsToItsOwnHeightForAnAvatarALittleAbove()
    {
        Vector3 at = new Vector3(140, 128, 31.5f);
        using var s = Setup(at);
        Follow(s, new());

        float own = s.Bot.AbsolutePosition.Z + 0.15f;
        Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, at) && Math.Abs(s.Bot.MoveToPositionTarget.Z - own) < 0.01f,
            TimeSpan.FromSeconds(20)), "not heading for the avatar at the bot's own height");
        Log(s);
    }

    [Fact]
    public void WithJumpingAllowedAndSomethingTallInTheWayTheBotHeadsForTheAvatarsHeight()
    {
        Vector3 at = new Vector3(140, 128, 31.5f);
        using var s = Setup(at);
        Wall(s, new Vector3(134, 128, 30));
        Follow(s, new() { { BOT_ALLOW_JUMPING, 1 } });

        Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, at) && Math.Abs(s.Bot.MoveToPositionTarget.Z - at.Z) < 0.01f,
            TimeSpan.FromSeconds(20)), "not heading for the avatar's height");

        // The same, with jumping not allowed: the bot keeps to its own height.
        Follow(s, new() { { BOT_ALLOW_JUMPING, 0 } });
        float own = s.Bot.AbsolutePosition.Z + 0.15f;
        Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, at) && Math.Abs(s.Bot.MoveToPositionTarget.Z - own) < 0.01f,
            TimeSpan.FromSeconds(20)), "not heading for the avatar at the bot's own height");
        Log(s);
    }

    [Fact]
    public void WithLineOfSightRequiredAnObjectInBetweenLosesTheAvatarUntilItIsInSightAgain()
    {
        Vector3 at = new Vector3(140, 128, 30);
        using var s = Setup(at);
        SceneObjectGroup wall = Wall(s, new Vector3(134, 128, 30));
        s.H.RezScript(@"default {
            state_entry() { botRegisterForNavigationEvents(""" + s.Bot.UUID + @"""); llSay(0, ""registered""); }
            bot_update(string id, integer flag, list p) { llSay(0, ""u "" + (string)flag + "" "" + llDumpList2String(p, ""|"")); }
        }");
        Assert.True(s.H.PumpUntil(() => s.H.Said.Contains("registered")), string.Join(" | ", s.H.Said));
        Follow(s, new() { { BOT_REQUIRES_LINE_OF_SIGHT, 1 } });

        Assert.True(s.H.PumpUntil(() => Updates(s).Length > 0, TimeSpan.FromSeconds(20)), string.Join(" | ", s.H.Said));
        float distance = Vector3.Distance(at, s.Bot.AbsolutePosition);
        Assert.Equal("u 4 " + V(at) + "|" + distance.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
            + "|" + V(s.Bot.AbsolutePosition), Updates(s).Single());
        Assert.False(HeadingFor(s.Bot, at), "the bot went on toward an avatar it cannot see");

        wall.AbsolutePosition = new Vector3(134, 200, 30);
        Assert.True(s.H.PumpUntil(() => HeadingFor(s.Bot, at), TimeSpan.FromSeconds(20)), "the bot did not go on once it could see the avatar");
        Assert.Single(Updates(s));
    }

    // A tall thin wall across the line from the bot (x 128) to the avatar (x 140).
    private static SceneObjectGroup Wall(Scene3 s, Vector3 at)
    {
        SceneObjectGroup wall = SceneHelpers.AddSceneObject(s.H.Scene, "Wall", UUID.Random());
        wall.RootPart.Scale = new Vector3(0.5f, 10f, 10f);
        wall.AbsolutePosition = at;
        return wall;
    }

    private string[] Updates(Scene3 s)
    {
        var all = s.H.Said.Where(x => x.StartsWith("u ", StringComparison.Ordinal)).ToArray();
        _out.WriteLine(string.Join(Environment.NewLine, all));
        return all;
    }

    private static string V(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "<{0:F6}, {1:F6}, {2:F6}>", v.X, v.Y, v.Z);
}
