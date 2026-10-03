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
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwDetectedBot names the bot a botSensor scanned for or a botListen heard with, as Halcyon's iwDetectedBot reads
/// DetectVariables.BotID (Halcyon SensorRepeat bot branches, ExecutionScheduler botListen detect data). A bot does not
/// sense itself, and no_sensor still names the bot. In other detect events it is NULL_KEY, as Halcyon copied an empty
/// bot key into every entry.
/// </summary>
// No process-wide state: each test has its own scene and bot manager, so the class runs in parallel.
public class BotDetectTests
{
    private readonly ITestOutputHelper _out;
    public BotDetectTests(ITestOutputHelper o) => _out = o;

    private static SchedulerHarness BotScene()
    {
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "High");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
        });
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), new BotManager(), new ChatModule());
        Assert.NotNull(h.Scene.RequestModuleInterface<IBotManager>());
        return h;
    }

    private static UUID MakeBot(SchedulerHarness h, string body)
    {
        var client = h.AddClient();
        h.Prim.OwnerID = client.AgentId;   // BotManager needs the owner present
        h.Scene.GetScenePresence(client.AgentId).AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(1, 1, 0);   // in sensor range
        h.RezScript(@"key bot;
        default {
            state_entry() {
                bot = osNpcCreate(""Test"", ""Bot"", llGetPos() + <2,0,0>, """");
                llSay(0, ""bot="" + (string)bot);
                " + body + @"
            }
            sensor(integer n) {
                integer i;
                for (i = 0; i < n; ++i) llSay(0, ""sensed "" + (string)llDetectedKey(i) + "" by "" + iwDetectedBot());
                llSay(0, ""sensor done"");
            }
            no_sensor() { llSay(0, ""none by "" + iwDetectedBot()); }
            listen(integer ch, string name, key id, string msg) { llSay(0, ""heard "" + msg + "" by "" + iwDetectedBot()); }
            touch_start(integer n) { llSay(0, ""touch by ["" + iwDetectedBot() + ""]""); }
        }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("bot=", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        var bot = UUID.Parse(h.Said.First(s => s.StartsWith("bot=", StringComparison.Ordinal)).Substring(4));
        Assert.NotEqual(UUID.Zero, bot);
        return bot;
    }

    [Fact]
    public void ABotSensorNamesTheBotAndDoesNotSenseIt()
    {
        using var h = BotScene();
        UUID bot = MakeBot(h, @"botSensor(bot, """", NULL_KEY, AGENT, 20.0, PI);");

        Assert.True(h.PumpUntil(() => h.Said.Contains("sensor done")), string.Join(" | ", h.Said));
        _out.WriteLine(string.Join(" | ", h.Said));
        var sensed = h.Said.Where(s => s.StartsWith("sensed ", StringComparison.Ordinal)).ToArray();
        Assert.Contains(sensed, s => s.StartsWith("sensed " + h.Prim.OwnerID, StringComparison.Ordinal));
        Assert.DoesNotContain(sensed, s => s.StartsWith("sensed " + bot, StringComparison.Ordinal));
        Assert.All(sensed, s => Assert.EndsWith(" by " + bot, s));
    }

    [Fact]
    public void ABotSensorFindingNothingStillNamesTheBot()
    {
        using var h = BotScene();
        UUID bot = MakeBot(h, @"botSensor(bot, ""Nobody Here"", NULL_KEY, AGENT, 20.0, PI);");

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("none by", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        Assert.Contains("none by " + bot, h.Said);
    }

    [Fact]
    public void ABotListenNamesTheBotThatHeard()
    {
        using var h = BotScene();
        UUID bot = MakeBot(h, @"botListen(bot, 7, """", NULL_KEY, """");");
        var speaker = SceneHelpers.AddSceneObject(h.Scene, "speaker", h.Prim.OwnerID);
        h.RezScriptInto(speaker.RootPart, @"default { touch_start(integer n) { } state_entry() { llSleep(0.5); llSay(7, ""ping""); } }");

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("heard ", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        Assert.Contains("heard ping by " + bot, h.Said);
    }

    [Fact]
    public void InATouchIwDetectedBotIsNullKey()
    {
        using var h = BotScene();
        MakeBot(h, "");
        var client = h.AddClient();
        Assert.True(h.PumpUntil(() => (h.Prim.ScriptEvents & OpenSim.Region.Framework.Scenes.scriptEvents.touch_start) != 0));

        h.Scene.ProcessObjectGrab(h.Prim.LocalId, Vector3.Zero, client, null);

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("touch by", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        Assert.Contains("touch by [" + UUID.Zero + "]", h.Said);
    }
}
