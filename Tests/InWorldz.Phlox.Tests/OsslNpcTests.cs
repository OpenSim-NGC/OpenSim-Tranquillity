using System;
using System.Linq;
using Nini.Config;
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
/// osNpc* is a second door onto BotManager's bots, not a second NPC system: an osNpcCreate'd
/// NPC is a bot (botGetBotsWithTag sees it, botIsBot says so), one BotData per NPC. The scene is the
/// one upstream's NPCModuleTests builds (AvatarFactory, UserManagement, Attachments, NPCModule,
/// BasicInventoryAccess) plus BotManager, with [NPC] Enabled and the OSSL level at High.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class OsslNpcTests
{
    private readonly ITestOutputHelper _out;
    public OsslNpcTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness NpcScene(string threat = "High")
    {
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat);
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");   // as NPCModuleTests
            cfg.AddConfig("Chat");
        });
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), new BotManager(), new ChatModule());   // ChatModule: the NPC client's chat reaches the scene through it
        Assert.NotNull(h.Scene.RequestModuleInterface<INPCModule>());
        Assert.NotNull(h.Scene.RequestModuleInterface<IBotManager>());
        return h;
    }

    private string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    [Fact]
    public void CreateIsABotAndSaysHelloFromItsOwnKey()
    {
        using var h = NpcScene();
        var client = h.AddClient();
        var owner = h.Scene.GetScenePresence(client.AgentId);
        h.Prim.OwnerID = owner.UUID;                                   // BotManager needs the owner present for the appearance

        h.RezScript(@"default { state_entry() {
            key npc = osNpcCreate(""Test"", ""Npc"", llGetPos() + <2,0,0>, """");
            llSay(0, ""npc="" + (string)npc);
            llSay(0, ""isnpc="" + (string)osIsNpc(npc) + ""|isbot="" + (string)botIsBot(npc) + ""|me="" + (string)osIsNpc(llGetOwner()));
            llSay(0, ""tagged="" + (string)llListFindList(botGetBotsWithTag(""""), [npc]));
            llSay(0, ""owner="" + (string)(osNpcGetOwner(npc) == llGetOwner()));
            osNpcSay(npc, ""hello"");
            vector p = osNpcGetPos(npc); llSay(0, ""pos="" + (string)((integer)p.x));
            llSay(0, ""done"");
        } }");
        h.PumpFor(TimeSpan.FromSeconds(3));
        h.PumpUntil(() => h.Said.Contains("done") && h.ClientChat.Any(c => c.Channel == 0 && c.Message == "hello"));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "] client=[" + string.Join(" | ", h.ClientChat.Select(c => $"{c.Sender}:{c.Channel}:{c.Message}")) + "]");

        Assert.Contains("done", h.Said);
        var npcLine = h.Said.First(s => s.StartsWith("npc="));
        var npc = UUID.Parse(npcLine.Substring(4));
        Assert.NotEqual(UUID.Zero, npc);
        Assert.Contains("isnpc=1|isbot=1|me=0", h.Said);
        Assert.Contains("tagged=0", h.Said);                           // botGetBotsWithTag("") lists it: one bot, index 0
        Assert.Contains("owner=1", h.Said);
        Assert.Contains(h.ClientChat, c => c.Sender == npc && c.Channel == 0 && c.Message == "hello");
        Assert.Equal("Test Npc", h.Scene.GetScenePresence(npc)?.Name);
        Assert.Contains("pos=2", h.Said);                              // the harness prim sits at the origin: 0 + 2
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void RemoveByANonOwnerIsDeniedAndByTheOwnerRemoves()
    {
        using var h = NpcScene();
        var client = h.AddClient();
        var owner = h.Scene.GetScenePresence(client.AgentId);
        h.Prim.OwnerID = owner.UUID;
        h.RezScript(@"default { state_entry() { llSay(0, ""npc="" + (string)osNpcCreate(""Owned"", ""Npc"", llGetPos() + <2,0,0>, """")); }
                                 touch_start(integer n) { llSay(0, ""removed""); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("npc=")));
        var npc = UUID.Parse(h.Said.First(s => s.StartsWith("npc=")).Substring(4));
        var mgr = h.Scene.RequestModuleInterface<IBotManager>();
        Assert.True(mgr.IsBot(npc));

        // another owner's prim tries to remove it
        var other = SceneHelpers.AddSceneObject(h.Scene, "other prim", UUID.Random());
        h.RezScriptInto(other.RootPart, "default { state_entry() { osNpcRemove(\"" + npc + "\"); llSay(0, \"tried\"); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.Said.Contains("tried"));
        Assert.Contains("tried", h.Said);
        Assert.True(mgr.IsBot(npc), "a non-owner removed an owned NPC");
        Assert.NotNull(h.Scene.GetScenePresence(npc));

        // the owner's own prim removes it
        h.RezScript("default { state_entry() { osNpcRemove(\"" + npc + "\"); llSay(0, \"gone\"); } }");
        h.PumpUntil(() => h.Said.Contains("gone") && !mgr.IsBot(npc));
        Assert.Contains("gone", h.Said);
        Assert.False(mgr.IsBot(npc));
        _out.WriteLine("errors=[" + Errors(h) + "]");
    }

    [Fact]
    public void NotOwnedNpcCanBeRemovedByAnyone()
    {
        using var h = NpcScene();
        var client = h.AddClient();
        h.Prim.OwnerID = h.Scene.GetScenePresence(client.AgentId).UUID;
        h.RezScript(@"default { state_entry() { key n = osNpcCreate(""Free"", ""Npc"", llGetPos() + <2,0,0>, """", OS_NPC_NOT_OWNED); llSay(0, ""npc="" + (string)n); llSay(0, ""owner="" + (string)(osNpcGetOwner(n) == n)); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("owner=")));
        var npc = UUID.Parse(h.Said.First(s => s.StartsWith("npc=")).Substring(4));
        Assert.Contains("owner=1", h.Said);                            // upstream: an unowned NPC's owner is itself
        var mgr = h.Scene.RequestModuleInterface<IBotManager>();
        Assert.Equal(UUID.Zero, mgr.GetBotOwner(npc));

        var other = SceneHelpers.AddSceneObject(h.Scene, "other prim", UUID.Random());
        h.RezScriptInto(other.RootPart, "default { state_entry() { osNpcRemove(\"" + npc + "\"); llSay(0, \"tried\"); } }");
        h.PumpUntil(() => !mgr.IsBot(npc));
        Assert.False(mgr.IsBot(npc));
    }

    [Fact]
    public void TheGateAppliesAtTheDefaultLevel()
    {
        using var h = NpcScene("VeryLow");
        var client = h.AddClient();
        h.Prim.OwnerID = h.Scene.GetScenePresence(client.AgentId).UUID;
        h.RezScript(@"default { state_entry() { osNpcCreate(""No"", ""Npc"", llGetPos(), """"); llSay(0, ""after""); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osNpcCreate permission denied")));
        Assert.DoesNotContain("after", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osNpcCreate permission denied"));
        Assert.Empty(h.Scene.RequestModuleInterface<IBotManager>().GetAllBots());
    }
}
