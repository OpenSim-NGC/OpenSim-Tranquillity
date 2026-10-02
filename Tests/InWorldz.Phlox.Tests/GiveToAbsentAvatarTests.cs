using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A give to an avatar who is not in this region - online elsewhere or offline - is delivered the way
/// YEngine's llGiveInventory does it (Scene.MoveTaskInventoryItem by avatar id, no client), not by passing a null
/// client into the client overload, which dereferenced it (Scene.Inventory.cs:1479). The iwDeliver* codes are
/// Halcyon's. And iwGetObjectMassMKS is 100 x llGetObjectMass for the same object, like llGetMassMKS.
/// llGiveInventoryList is the exception: like SL (the avatar must be in, or see into, the region; SVC-868) and
/// YEngine (LSL_Api.cs llGiveInventoryList, "Unable to give list, destination not found"), it gives an avatar with
/// no presence here nothing.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class GiveToAbsentAvatarTests
{
    private readonly ITestOutputHelper _out;
    public GiveToAbsentAvatarTests(ITestOutputHelper o) => _out = o;

    private const int IW_DELIVER_OK = 0, IW_DELIVER_USER = 5;

    private static TaskInventoryItem AddGift(SchedulerHarness h, string name)
        => TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, name, UUID.Random(), UUID.Random(), "a gift");

    /// <summary>Every item named <paramref name="name"/> anywhere in the user's inventory.</summary>
    private static int CountInInventory(SchedulerHarness h, UUID user, string name)
    {
        int n = 0;
        foreach (var folder in h.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>())
            n += h.Scene.InventoryService.GetFolderItems(user, folder.ID)?.Count(i => i.Name == name) ?? 0;
        return n;
    }

    private string Run(SchedulerHarness h, string body, string marker)
    {
        h.RezScript("default { state_entry() { " + body + " } }");
        var until = DateTime.UtcNow.AddSeconds(30);   // a first compile on a loaded machine can take longer than 8 s
        while (DateTime.UtcNow < until && !h.Said.Any(s => s.StartsWith(marker))) h.PumpOnce();
        var debug = h.SaidOn.Where(s => s.Channel == 0x7FFFFFFF).Select(s => s.Message);
        _out.WriteLine($"said=[{string.Join(" | ", h.Said)}] debug=[{string.Join(" | ", debug)}]");
        return h.Said.FirstOrDefault(s => s.StartsWith(marker));
    }

    [Fact]
    public void DeliverToAnAvatarWithAnAccountButNoPresenceLandsAndReturnsOk()
    {
        using var h = new SchedulerHarness();
        var absent = UserAccountHelpers.CreateUserWithInventory(h.Scene);   // an account, never in this region
        Assert.Null(h.Scene.GetScenePresence(absent.PrincipalID));
        AddGift(h, "gift");
        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventory(LINK_THIS, \"{absent.PrincipalID}\", \"gift\"));", "rc=");
        Assert.Equal("rc=" + IW_DELIVER_OK, line);
        Assert.Equal(1, CountInInventory(h, absent.PrincipalID, "gift"));
    }

    [Fact]
    public void LlGiveInventoryToAnAbsentAvatarLandsWithoutAnError()
    {
        using var h = new SchedulerHarness();
        var absent = UserAccountHelpers.CreateUserWithInventory(h.Scene);
        AddGift(h, "gift2");
        Run(h, $"llGiveInventory(\"{absent.PrincipalID}\", \"gift2\"); llSay(0, \"gave\");", "gave");
        Assert.Equal(1, CountInInventory(h, absent.PrincipalID, "gift2"));
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == 0x7FFFFFFF && (s.Message.Contains("Failed to give") || s.Message.Contains("stopped")));
    }

    [Fact]
    public void DeliverListToAnAbsentAvatarLandsAndReturnsOk()
    {
        using var h = new SchedulerHarness();
        var absent = UserAccountHelpers.CreateUserWithInventory(h.Scene);
        AddGift(h, "gift3");
        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventoryList(LINK_THIS, \"{absent.PrincipalID}\", \"box\", [\"gift3\"]));", "rc=");
        Assert.Equal("rc=" + IW_DELIVER_OK, line);
        Assert.Equal(1, CountInInventory(h, absent.PrincipalID, "gift3"));
    }

    /// <summary>The folder(s) named <paramref name="name"/> in the user's inventory.</summary>
    private static List<InventoryFolderBase> FoldersNamed(SchedulerHarness h, UUID user, string name)
        => (h.Scene.InventoryService.GetInventorySkeleton(user) ?? new List<InventoryFolderBase>()).Where(f => f.Name == name).ToList();

    [Fact]
    public void LlGiveInventoryListToAnAvatarWithNoPresenceGivesNothing()
    {
        using var h = new SchedulerHarness();
        var absent = UserAccountHelpers.CreateUserWithInventory(h.Scene);   // an account, never in this region
        Assert.Null(h.Scene.GetScenePresence(absent.PrincipalID));
        AddGift(h, "gift6");
        Run(h, $"llGiveInventoryList(\"{absent.PrincipalID}\", \"box6\", [\"gift6\"]); llSay(0, \"gave\");", "gave");
        Assert.Equal(0, CountInInventory(h, absent.PrincipalID, "gift6"));
        Assert.Empty(FoldersNamed(h, absent.PrincipalID, "box6"));
        Assert.Contains(h.SaidOn, s => s.Channel == 0x7FFFFFFF && s.Message.Contains("llGiveInventoryList: Unable to give list, destination not found"));
    }

    [Fact]
    public void LlGiveInventoryListToAPresentAvatarDeliversAFolder()
    {
        using var h = new SchedulerHarness();
        var account = UserAccountHelpers.CreateUserWithInventory(h.Scene);
        var present = SceneHelpers.AddScenePresence(h.Scene, account.PrincipalID);
        AddGift(h, "gift7");
        Run(h, $"llGiveInventoryList(\"{present.UUID}\", \"box7\", [\"gift7\"]); llSay(0, \"gave\");", "gave");
        var folder = Assert.Single(FoldersNamed(h, present.UUID, "box7"));
        Assert.Contains(h.Scene.InventoryService.GetFolderItems(present.UUID, folder.ID), i => i.Name == "gift7");
        Assert.DoesNotContain(h.SaidOn, s => s.Channel == 0x7FFFFFFF);
    }

    [Fact]
    public void DeliverToAKeyWithNoAccountReturnsUser()
    {
        using var h = new SchedulerHarness();
        AddGift(h, "gift4");
        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventory(LINK_THIS, \"{UUID.Random()}\", \"gift4\")); " +
                          $"llSay(0, \"rcl=\" + (string)iwDeliverInventoryList(LINK_THIS, \"{UUID.Random()}\", \"box\", [\"gift4\"]));", "rcl=");
        Assert.Contains("rc=" + IW_DELIVER_USER, h.Said);
        Assert.Equal("rcl=" + IW_DELIVER_USER, line);
    }

    [Fact]
    public void DeliverToAPresentAvatarIsUnchanged()
    {
        using var h = new SchedulerHarness();
        var account = UserAccountHelpers.CreateUserWithInventory(h.Scene);   // a real user: an account and an inventory
        var present = SceneHelpers.AddScenePresence(h.Scene, account.PrincipalID);
        AddGift(h, "gift5");
        var line = Run(h, $"llSay(0, \"rc=\" + (string)iwDeliverInventory(LINK_THIS, \"{present.UUID}\", \"gift5\"));", "rc=");
        Assert.Equal("rc=" + IW_DELIVER_OK, line);
        Assert.Equal(1, CountInInventory(h, present.UUID, "gift5"));
    }

    [Fact]
    public void IwGetObjectMassMksIsAHundredTimesLlGetObjectMass()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new MassiveActor();
        var line = Run(h, "key me = llGetKey(); llSay(0, \"mass=\" + (string)llGetObjectMass(me) + \" mks=\" + (string)iwGetObjectMassMKS(me));", "mass=");
        Assert.NotNull(line);
        var parts = line.Split(' ');
        float mass = float.Parse(parts[0].Substring(5), CultureInfo.InvariantCulture);
        float mks = float.Parse(parts[1].Substring(4), CultureInfo.InvariantCulture);
        Assert.True(mass > 0, line);
        Assert.InRange(mks, mass * 100 * 0.999f, mass * 100 * 1.001f);
    }
}
