/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
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
/// A listen comes back from a region stop and start as it was: switched off with llListenControl it stays off, an
/// osListenRegex listen keeps its regex filters, and a botListen listen still hears from its bot. YEngine's
/// WorldCommModule saves the on/off state and the regex bitfield with each listen. A row saved before these were kept
/// loads as it always did. Each test stops the first region as the simulator does (Scene.Close, the final save) and
/// starts the same item and asset in a second.
/// </summary>
// Runs in parallel: each test has its own items in the shared state file and nothing process-wide is changed.
public class ListenSaveTests
{
    private readonly ITestOutputHelper _out;
    public ListenSaveTests(ITestOutputHelper o) => _out = o;

    /// <summary>Channel 5 on; channel 6 switched off; channel 7 a message regex. Also the source of the earlier build's row.</summary>
    internal const string Src =
        "default {\n" +
        "  state_entry() {\n" +
        "    llListen(5, \"\", NULL_KEY, \"\");\n" +
        "    llListenControl(llListen(6, \"\", NULL_KEY, \"\"), FALSE);\n" +
        "    osListenRegex(7, \"\", NULL_KEY, \"^ab+c$\", OS_LISTEN_REGEX_MESSAGE);\n" +
        "    llSay(0, \"up\");\n" +
        "  }\n" +
        "  listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + (string)c + \" \" + m); }\n" +
        "}\n";

    private static SchedulerHarness Ossl() => new(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));

    private static void StartAndStop(string src, UUID asset, UUID item, Action<SchedulerHarness> before = null)
    {
        using var h1 = Ossl();
        h1.RezScript(src, asset, item);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
        before?.Invoke(h1);
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
        h1.StopRegionAsTheSimulatorDoes();
    }

    /// <summary>The same item and asset started in a new region, restored from its row (not started fresh).</summary>
    private static SchedulerHarness Restarted(string src, UUID asset, UUID item, SchedulerHarness h = null)
    {
        h ??= Ossl();
        h.RezScript(src, asset, item);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null, TimeSpan.FromSeconds(15)), "not loaded: " + h.StatusOf(item));
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("up", h.Said);
        return h;
    }

    /// <summary>Region chat on each (channel, message), then a line on <paramref name="endChannel"/> that is heard last.</summary>
    private static void Chat(SchedulerHarness h, int endChannel, params (int Channel, string Message)[] lines)
    {
        foreach (var (channel, message) in lines)
            h.Engine.ListenManager.DeliverChat(channel, "Test User", UUID.Random(), message);
        string end = "end" + Guid.NewGuid().ToString("N").Substring(0, 6);
        h.Engine.ListenManager.DeliverChat(endChannel, "Test User", UUID.Random(), end);
        Assert.True(h.PumpUntil(() => h.Said.Contains("heard " + endChannel + " " + end)), SavedStateRig.SaidText(h));
    }

    [Fact]
    public void AListenSwitchedOffStaysOffAndARegexListenKeepsItsFilterAfterARegionStopAndStart()
    {
        UUID asset = UUID.Random(), item = UUID.Random();
        StartAndStop(Src, asset, item);

        using var h = Restarted(Src, asset, item);
        Chat(h, 5, (5, "one"), (6, "two"), (7, "abbbc"), (7, "xyz"));
        _out.WriteLine(SavedStateRig.SaidText(h));
        Assert.Contains("heard 5 one", h.Said);
        Assert.DoesNotContain("heard 6 two", h.Said);     // still off
        Assert.Contains("heard 7 abbbc", h.Said);         // the regex listen is back
        Assert.DoesNotContain("heard 7 xyz", h.Said);     // with its filter
    }

    private const string ToggleSrc =
        "default {\n" +
        "  state_entry() { llListenControl(llListen(6, \"\", NULL_KEY, \"\"), FALSE); llListen(9, \"\", NULL_KEY, \"\"); llSay(0, \"up\"); }\n" +
        "  touch_start(integer n) { llListenControl(1, TRUE); llSay(0, \"on\"); }\n" +
        "  listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + (string)c + \" \" + m); }\n" +
        "}\n";

    [Fact]
    public void AListenSwitchedBackOnAfterARestartIsSavedOn()
    {
        UUID asset = UUID.Random(), item = UUID.Random();
        StartAndStop(ToggleSrc, asset, item);

        using (var h2 = Restarted(ToggleSrc, asset, item))
        {
            Chat(h2, 9, (6, "first"));
            Assert.DoesNotContain("heard 6 first", h2.Said);
            h2.PostTouch(item);
            Assert.True(h2.PumpUntil(() => h2.Said.Contains("on")), SavedStateRig.SaidText(h2));
            Chat(h2, 9, (6, "second"));
            Assert.Contains("heard 6 second", h2.Said);
            h2.PumpUntilIdle(TimeSpan.FromSeconds(2));
            h2.StopRegionAsTheSimulatorDoes();
        }

        using var h3 = Restarted(ToggleSrc, asset, item);
        Chat(h3, 9, (6, "third"));
        Assert.Contains("heard 6 third", h3.Said);
    }

    /// <summary>
    /// A row the build before this change wrote for <see cref="Src"/> (asset <see cref="EarlierAsset"/>): it records the
    /// listens on channels 5 and 6 with no on/off state, and no osListenRegex listen, which that build did not record.
    /// It loads as that build loaded it: both llListen listens on, no regex listen.
    /// </summary>
    internal static readonly UUID EarlierAsset = new UUID("6c0f2a94-3e1b-4d78-9a52-b8e7d1c4f306");

    internal const string RowWrittenByEarlierBuild =
        "CAAQADoDCI4CSAFQAVoLCNyG+eb99dI/EAViCwjchvnm/fXSPxAFagsIqJb55v310j8QBYIBAJIBMggBEi4IARAFGgAiJDAwMDAwMDAwLTAw" +
        "MDAtMDAwMC0wMDAwLTAwMDAwMDAwMDAwMCoAkgEyCAISLggCEAYaACIkMDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAwKgCt" +
        "AQAAID+wAf///////////wHaAUA0NUUwRDdGQ0Q0NkQyRUM2QkZBMDBBMTdENzNBNkZCREFGNzkyODU5NTYxMzU4N0U4MUNCODA3MEJDRTVB" +
        "N0Yw";

    [Fact]
    public void ARowSavedBeforeTheOnOffStateAndRegexWereKeptLoadsAsBefore()
    {
        var item = UUID.Random();
        SavedStateRig.PutRow(item, EarlierAsset, Convert.FromBase64String(RowWrittenByEarlierBuild), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var h = Restarted(Src, EarlierAsset, item);
        Chat(h, 5, (5, "one"), (6, "two"), (7, "abbbc"));
        _out.WriteLine(SavedStateRig.SaidText(h));
        Assert.Contains("heard 5 one", h.Said);
        Assert.Contains("heard 6 two", h.Said);          // no on/off state in the row: on, as before
        Assert.DoesNotContain("heard 7 abbbc", h.Said);   // not in the row: not restored, as before
    }

    // ---- botListen ----

    private static SchedulerHarness BotScene(SchedulerHarness h = null)
    {
        h ??= new SchedulerHarness(cfg =>
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

    private const string BotSrc =
        "default {\n" +
        "  state_entry() {\n" +
        "    key bot = osNpcCreate(\"Test\", \"Bot\", llGetPos() + <2,0,0>, \"\");\n" +
        "    llSay(0, \"bot=\" + (string)bot + \" h=\" + (string)botListen(bot, 8, \"\", NULL_KEY, \"\"));\n" +
        "  }\n" +
        "  listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + (string)c + \" \" + m + \" by \" + iwDetectedBot()); }\n" +
        "}\n";

    /// <summary>
    /// A botListen listen comes back and still hears from its bot's position: said chat beside the bot, out of say range
    /// of the script's prim, is heard, and names the bot. The bot is in the second region under the same key.
    /// </summary>
    [Fact]
    public void ABotListenStillHearsFromItsBotAfterARegionStopAndStart()
    {
        UUID asset = UUID.Random(), item = UUID.Random(), bot;
        using (var h1 = BotScene())
        {
            var owner = h1.AddClient();
            h1.Prim.OwnerID = owner.AgentId;   // BotManager needs the owner present
            h1.RezScript(BotSrc, asset, item);
            Assert.True(h1.PumpUntil(() => h1.Said.Any(s => s.StartsWith("bot=", StringComparison.Ordinal))), SavedStateRig.SaidText(h1));
            string line = h1.Said.First(s => s.StartsWith("bot=", StringComparison.Ordinal));
            _out.WriteLine(line);
            bot = UUID.Parse(line.Substring(4, 36));
            Assert.EndsWith(" h=1", line);
            h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
            h1.StopRegionAsTheSimulatorDoes();
        }

        using var h = Restarted(BotSrc, asset, item, new SchedulerHarness());
        Vector3 prim = h.Prim.AbsolutePosition;
        Vector3 there = prim + new Vector3(prim.X < 128 ? 60 : -60, 0, 0);
        SceneHelpers.AddScenePresence(h.Scene, bot).AbsolutePosition = there;
        var speaker = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        speaker.AbsolutePosition = there + new Vector3(0, 1, 0);

        h.Engine.ListenManager.DeliverChat(ChatTypeEnum.Say, 8, "Test User", speaker.UUID, "ping", Vector3.Zero, UUID.Zero);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("heard 8 ", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)),
            SavedStateRig.SaidText(h));
        Assert.Contains("heard 8 ping by " + bot, h.Said);
    }
}

/// <summary>
/// An osListenRegex listen its pattern switched off (a match that timed out, as llListenControl(handle, FALSE) would)
/// comes back off after a region stop and start, and llListenControl turns it on again.
/// </summary>
// Serial ("phlox-state"): the regex timeout is real time, and a parallel load could stretch it.
[Collection("phlox-state")]
public class ListenRegexTimeoutSaveTests
{
    private const int DebugChannel = 0x7FFFFFFF;
    private const string Evil = "(a+)+$";
    private static readonly string Victim = new string('a', 30) + "!";

    private const string Src =
        "default {\n" +
        "  state_entry() { osListenRegex(5, \"\", NULL_KEY, \"" + Evil + "\", OS_LISTEN_REGEX_MESSAGE); llListen(9, \"\", NULL_KEY, \"\"); llSay(0, \"up\"); }\n" +
        "  touch_start(integer n) { llListenControl(1, TRUE); llSay(0, \"on\"); }\n" +
        "  listen(integer c, string n, key k, string m) { llSay(0, \"heard \" + (string)c + \" \" + m); }\n" +
        "}\n";

    private static SchedulerHarness Ossl() => new(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));

    [Fact]
    public void AListenItsPatternSwitchedOffComesBackOffAndCanBeTurnedOn()
    {
        UUID asset = UUID.Random(), item = UUID.Random();
        using (var h1 = Ossl())
        {
            h1.RezScript(Src, asset, item);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")), SavedStateRig.SaidText(h1));
            h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
            h1.Engine.ListenManager.DeliverChat(5, "Test User", UUID.Random(), Victim);
            Assert.True(h1.PumpUntil(() => h1.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("timed out"))),
                SavedStateRig.SaidText(h1));
            Assert.False(h1.Engine.ListenManager.IsActive(item, 1));
            h1.StopRegionAsTheSimulatorDoes();
        }

        using var h = Ossl();
        h.RezScript(Src, asset, item);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null, TimeSpan.FromSeconds(15)), "not loaded: " + h.StatusOf(item));
        h.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("up", h.Said);
        Assert.False(h.Engine.ListenManager.IsActive(item, 1));   // registered, and off

        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("on")), SavedStateRig.SaidText(h));
        h.Engine.ListenManager.DeliverChat(5, "Test User", UUID.Random(), "aaa");
        Assert.True(h.PumpUntil(() => h.Said.Contains("heard 5 aaa")), SavedStateRig.SaidText(h));
    }
}
