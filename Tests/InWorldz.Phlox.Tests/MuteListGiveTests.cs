using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A give to an avatar who has muted the object, or its owner, is not made.
/// Halcyon (InWorldz.Phlox.Engine/LSLSystemAPI.cs IsScriptMuted :4388-4403, _GiveInventory :5368-5372,
/// _GiveLinkInventoryList :8443-8447): "Not offering inventory from muted ...", IW_DELIVER_MUTED, the normal delay, and
/// the ll/iwGive forms say nothing. SL (wiki llGiveInventory): an avatar that refuses "by manual decline or muting" does
/// not get it.
/// </summary>
// Parallel: the fake mute service is registered on this test's own scene; none of the process-wide hooks
// (Clock, FailLoadForTest, compile delays, ThrowForTest, cache/state-DB files, real-time measurement) is touched.
public class MuteListGiveTests
{
    private readonly ITestOutputHelper _out;
    public MuteListGiveTests(ITestOutputHelper o) => _out = o;

    private const int IW_DELIVER_OK = 0, IW_DELIVER_MUTED = 2, IW_DELIVER_NONE = 7;
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    /// <summary>An IMuteListService that answers MuteListRequest from a fixed list, in MuteListService's line format.</summary>
    private sealed class FakeMutes : IMuteListService
    {
        public readonly Dictionary<UUID, List<UUID>> Muted = new();
        public int Requests;
        public byte[] MuteListRequest(UUID agent, uint crc)
        {
            Interlocked.Increment(ref Requests);
            if (!Muted.TryGetValue(agent, out var ids)) return Array.Empty<byte>();
            return System.Text.Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => "1 " + id + " somebody|0\n")));
        }
        public bool UpdateMute(MuteData mute) => false;
        public bool RemoveMute(UUID agentID, UUID muteID, string muteName) => false;
    }

    /// <summary>A user with an inventory and an id of its own (the one-argument helper always uses ...0099).</summary>
    private static UserAccount NewUser(SchedulerHarness h)
    {
        UUID id = UUID.Random();
        return UserAccountHelpers.CreateUserWithInventory(h.Scene, "Mute", "Tester" + id.ToString().Substring(0, 8), id, "pw");
    }

    private static void AddGift(SchedulerHarness h, string name)
        => TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, name, UUID.Random(), UUID.Random(), "a gift");

    /// <summary>Every item named <paramref name="name"/> anywhere in the user's inventory.</summary>
    private static int CountInInventory(SchedulerHarness h, UUID user, string name)
    {
        int n = 0;
        foreach (var folder in h.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>())
            n += h.Scene.InventoryService.GetFolderItems(user, folder.ID)?.Count(i => i.Name == name) ?? 0;
        return n;
    }

    private void Run(SchedulerHarness h, string body, string marker, int seconds = 30)
    {
        h.RezScript("default { state_entry() { " + body + " } }");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !h.Said.Contains(marker)) h.PumpOnce();
        var debug = h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message);
        _out.WriteLine($"said=[{string.Join(" | ", h.Said)}] debug=[{string.Join(" | ", debug)}]");
        Assert.Contains(marker, h.Said);
    }

    /// <summary>All six give functions, in turn, to <paramref name="k"/>; each gives its own item (g1..g6).</summary>
    private static string AllSix(UUID k) =>
        $"key k = \"{k}\"; " +
        "llGiveInventory(k, \"g1\"); llSay(0, \"s1\"); " +
        "llGiveInventoryList(k, \"box2\", [\"g2\"]); llSay(0, \"s2\"); " +
        "iwGiveLinkInventory(LINK_THIS, k, \"g3\"); llSay(0, \"s3\"); " +
        "iwGiveLinkInventoryList(LINK_THIS, k, \"box4\", [\"g4\"]); llSay(0, \"s4\"); " +
        "llSay(0, \"rc5=\" + (string)iwDeliverInventory(LINK_THIS, k, \"g5\")); " +
        "llSay(0, \"rc6=\" + (string)iwDeliverInventoryList(LINK_THIS, k, \"box6\", [\"g6\"])); " +
        "llSay(0, \"rc7=\" + (string)iwDeliverInventory(LINK_THIS, k, \"missing\")); " +
        "llSay(0, \"done\");";

    [Theory]
    [InlineData("owner")]      // the recipient muted the object's owner (SL/Halcyon mute by agent)
    [InlineData("object")]     // the recipient muted this object
    [InlineData("other")]      // the recipient muted somebody else
    [InlineData("noservice")]  // the region has no mute service: today's behaviour
    public void EveryGiveFunctionRespectsTheRecipientsMuteList(string muted)
    {
        using var h = new SchedulerHarness();
        var mutes = new FakeMutes();
        if (muted != "noservice") h.Scene.RegisterModuleInterface<IMuteListService>(mutes);
        var account = NewUser(h);
        var present = SceneHelpers.AddScenePresence(h.Scene, account.PrincipalID);
        mutes.Muted[present.UUID] = new List<UUID>
        {
            muted == "owner" ? h.Prim.OwnerID : muted == "object" ? h.Prim.ParentGroup.UUID : UUID.Random()
        };
        for (int i = 1; i <= 6; i++) AddGift(h, "g" + i);

        Run(h, AllSix(present.UUID), "done");

        bool refused = muted == "owner" || muted == "object";
        for (int i = 1; i <= 6; i++)
            Assert.True(CountInInventory(h, present.UUID, "g" + i) == (refused ? 0 : 1), $"g{i} ({muted})");
        Assert.Contains("rc5=" + (refused ? IW_DELIVER_MUTED : IW_DELIVER_OK), h.Said);
        Assert.Contains("rc6=" + (refused ? IW_DELIVER_MUTED : IW_DELIVER_OK), h.Said);
        // Halcyon checks the mute before looking for the item: a muted give of a missing item is MUTED.
        Assert.Contains("rc7=" + (refused ? IW_DELIVER_MUTED : IW_DELIVER_NONE), h.Said);
        if (refused)
        {
            // Nothing is said about it (Halcyon logs only; SL drops it): no DEBUG_CHANNEL line at all.
            Assert.DoesNotContain(h.SaidOn, s => s.Channel == DEBUG_CHANNEL);
            Assert.DoesNotContain(h.Scene.InventoryService.GetInventorySkeleton(present.UUID), f => f.Name.StartsWith("box"));
        }
        if (muted == "noservice") Assert.Equal(0, mutes.Requests);
        else Assert.True(mutes.Requests > 0);
    }

    /// <summary>
    /// iwGiveLinkInventoryList keeps delivering to absent avatars; the mute check is added in front of it: an absent
    /// muter gets nothing (and iwDeliverInventory says MUTED), an absent avatar who muted nobody still gets the folder.
    /// </summary>
    [Fact]
    public void AnAbsentMuterGetsNothingAndAnAbsentNonMuterStillGetsTheList()
    {
        using var h = new SchedulerHarness();
        var mutes = new FakeMutes();
        h.Scene.RegisterModuleInterface<IMuteListService>(mutes);
        var muter = NewUser(h);
        var other = NewUser(h);
        Assert.Null(h.Scene.GetScenePresence(muter.PrincipalID));
        mutes.Muted[muter.PrincipalID] = new List<UUID> { h.Prim.OwnerID };
        AddGift(h, "a1");
        AddGift(h, "a2");

        Run(h, $"iwGiveLinkInventoryList(LINK_THIS, \"{muter.PrincipalID}\", \"boxm\", [\"a1\"]); " +
               $"iwGiveLinkInventoryList(LINK_THIS, \"{other.PrincipalID}\", \"boxo\", [\"a1\"]); " +
               $"llSay(0, \"rc=\" + (string)iwDeliverInventory(LINK_THIS, \"{muter.PrincipalID}\", \"a2\")); llSay(0, \"done\");", "done");

        Assert.Equal(0, CountInInventory(h, muter.PrincipalID, "a1"));
        Assert.Equal(0, CountInInventory(h, muter.PrincipalID, "a2"));
        Assert.Contains("rc=" + IW_DELIVER_MUTED, h.Said);
        Assert.Equal(1, CountInInventory(h, other.PrincipalID, "a1"));
    }

    /// <summary>
    /// "For performance reasons, don't make this call if it's a dialog to yourself." (Halcyon IsScriptMuted): the owner as
    /// recipient is never looked up. Nor is a prim: it has no mute list (Halcyon asked and got "not muted").
    /// </summary>
    [Fact]
    public void TheOwnerAndAPrimAreNeverLookedUp()
    {
        using var h = new SchedulerHarness();
        var mutes = new FakeMutes();
        h.Scene.RegisterModuleInterface<IMuteListService>(mutes);
        mutes.Muted[h.Prim.OwnerID] = new List<UUID> { h.Prim.OwnerID, h.Prim.ParentGroup.UUID };
        var box = SceneHelpers.AddSceneObject(h.Scene, "box", h.Prim.OwnerID);
        AddGift(h, "p1");

        Run(h, $"llSay(0, \"rco=\" + (string)iwDeliverInventory(LINK_THIS, \"{h.Prim.OwnerID}\", \"p1\")); " +
               $"llSay(0, \"rcp=\" + (string)iwDeliverInventory(LINK_THIS, \"{box.UUID}\", \"p1\")); llSay(0, \"done\");", "done");

        Assert.Equal(0, mutes.Requests);
        Assert.DoesNotContain("rco=" + IW_DELIVER_MUTED, h.Said);
        Assert.Contains("rcp=" + IW_DELIVER_OK, h.Said);
        Assert.Contains(box.RootPart.Inventory.GetInventoryItems(), i => i.Name == "p1");
    }

    /// <summary>A mute service that fails is read as "not muted" (IsScriptMuted): the give goes ahead.</summary>
    [Fact]
    public void AFailingMuteServiceDoesNotStopTheGive()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IMuteListService>(new ThrowingMutes());
        var account = NewUser(h);
        AddGift(h, "t1");
        Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventory(LINK_THIS, \"{account.PrincipalID}\", \"t1\")); llSay(0, \"done\");", "done");
        Assert.Contains("rc=" + IW_DELIVER_OK, h.Said);
        Assert.Equal(1, CountInInventory(h, account.PrincipalID, "t1"));
    }

    private sealed class ThrowingMutes : IMuteListService
    {
        public byte[] MuteListRequest(UUID agent, uint crc) => throw new InvalidOperationException("mute DB down");
        public bool UpdateMute(MuteData mute) => false;
        public bool RemoveMute(UUID agentID, UUID muteID, string muteName) => false;
    }
}
