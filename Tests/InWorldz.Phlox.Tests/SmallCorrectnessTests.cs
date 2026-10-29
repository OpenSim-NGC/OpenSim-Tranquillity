using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>Small correctness fixes, one test each.</summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SmallCorrectnessTests
{
    private readonly ITestOutputHelper _out;
    public SmallCorrectnessTests(ITestOutputHelper o) => _out = o;

    private static string Said(SchedulerHarness h) => string.Join(" | ", h.Said);

    /// <summary>Pump until <paramref name="done"/> holds or <paramref name="limit"/> passes; false on a timeout.</summary>
    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!h.PumpOnceBusy()) System.Threading.Thread.Sleep(1);
        }
        return true;
    }

    // ---- llGetMassMKS is 100 x llGetMass (wiki: mass in kilograms; YEngine LSL_Api.llGetMassMKS) ----

    [Fact]
    public void GetMassMksIsAHundredTimesGetMass()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new MassiveActor();   // the test scene's physics gives every prim mass 0
        h.RezScript("default { state_entry() { llSay(0, \"mass=\" + (string)llGetMass() + \" mks=\" + (string)llGetMassMKS()); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("mass=")));
        var line = h.Said.FirstOrDefault(s => s.StartsWith("mass="));
        Assert.True(line != null, Said(h));
        var parts = line.Split(' ');
        float mass = float.Parse(parts[0].Substring(5), CultureInfo.InvariantCulture);
        float mks = float.Parse(parts[1].Substring(4), CultureInfo.InvariantCulture);
        _out.WriteLine(line);
        Assert.True(mass > 0, "the test prim has no mass: " + line);
        Assert.InRange(mks, mass * 100 * 0.999f, mass * 100 * 1.001f);
    }

    // ---- llClearLinkMedia(link, face) clears that face of that link ----

    [Fact]
    public void ClearLinkMediaClearsTheNamedFaceOfTheNamedLink()
    {
        using var h = new SchedulerHarness();
        var moap = RecordingMoap.Create(out var rec);
        h.Scene.RegisterModuleInterface<IMoapModule>(moap);
        h.RezScript("default { state_entry() { llSay(0, \"status=\" + (string)llClearLinkMedia(LINK_THIS, 2)); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("status=")) && rec.Cleared.Count > 0);
        Assert.True(h.Said.Contains("status=0"), Said(h));
        Assert.Equal(new[] { (h.Prim.LocalId, 2) }, rec.Cleared);
    }

    // ---- DATA_ONLINE answers "1" for an avatar that is online ----

    [Fact]
    public void DataOnlineIsOneForAPresentAvatar()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        h.RezScript($"default {{ state_entry() {{ llRequestAgentData(\"{client.AgentId}\", DATA_ONLINE); }} dataserver(key q, string d) {{ llSay(0, \"online=\" + d); }} }}");
        // Wait for the answer, not a fixed 2 s (the "nowhere" case below missed that window once in a full run).
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("online=")), TimeSpan.FromSeconds(30));
        Assert.True(h.Said.Contains("online=1"), Said(h));
    }

    [Fact]
    public void DataOnlineIsZeroForAnAvatarThatIsNowhere()
    {
        using var h = new SchedulerHarness();
        h.RezScript($"default {{ state_entry() {{ llRequestAgentData(\"{UUID.Random()}\", DATA_ONLINE); }} dataserver(key q, string d) {{ llSay(0, \"online=\" + d); }} }}");
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("online=")), TimeSpan.FromSeconds(30));   // As above
        Assert.True(h.Said.Contains("online=0"), Said(h));
    }

    // ---- llSetInventoryPermMask is a god function (YEngine: AllowGodFunctions and an administrator owner) ----

    private static TaskInventoryItem AddTexture(SchedulerHarness h)
    {
        uint full = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move);
        var item = new TaskInventoryItem
        {
            Name = "tex", AssetID = UUID.Random(), ItemID = UUID.Random(),
            Type = (int)AssetType.Texture, InvType = (int)InventoryType.Texture,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, OwnerID = h.Prim.OwnerID,
        };
        h.Prim.Inventory.AddInventoryItem(item, true);
        return h.Prim.Inventory.GetInventoryItem(item.ItemID);
    }

    private const string SetNextToCopyOnly = "default { state_entry() { llSetInventoryPermMask(\"tex\", MASK_NEXT, PERM_COPY); llSay(0, \"done\"); } }";

    [Fact]
    public void SetInventoryPermMaskWorksForAGodWhenGodFunctionsAreAllowed()
    {
        using var h = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("AllowGodFunctions", "true"));
        var owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => id == owner;
        var item = AddTexture(h);
        h.RezScript(SetNextToCopyOnly);
        h.PumpUntil(() => h.Said.Contains("done"));
        Assert.Contains("done", h.Said);
        Assert.Equal((uint)PermissionMask.Copy, item.NextPermissions & (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer));
    }

    // The MASK_* number alone picks the category (SL: "Sets the given permission category to the new value
    // on the inventory item"; Halcyon's and Phlox's llSetObjectPermMask). YEngine's limit ANDed the MASK_* number with
    // the item's PERM_* bits, so with a base mask that is not full every category but MASK_BASE collapsed to 0, the base.

    private const uint C = (uint)PermissionMask.Copy, M = (uint)PermissionMask.Modify, T = (uint)PermissionMask.Transfer, Mv = (uint)PermissionMask.Move;
    private const uint Four = C | M | T | Mv;

    [Theory]
    [InlineData(0, "base")]
    [InlineData(1, "owner")]
    [InlineData(2, "group")]
    [InlineData(3, "everyone")]
    [InlineData(4, "next")]
    public void SetInventoryPermMaskWritesTheCategoryItIsGiven(int mask, string field)
    {
        using var h = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("AllowGodFunctions", "true"));
        var owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => id == owner;
        var item = AddTexture(h);
        // a base that is not full (no move), as most resold items have
        item.BasePermissions = C | M | T;
        item.CurrentPermissions = C | M | T;
        item.GroupPermissions = 0;
        item.EveryonePermissions = 0;
        item.NextPermissions = C | M | T;
        var before = new Dictionary<string, uint>
        {
            ["base"] = item.BasePermissions, ["owner"] = item.CurrentPermissions, ["group"] = item.GroupPermissions,
            ["everyone"] = item.EveryonePermissions, ["next"] = item.NextPermissions,
        };

        h.RezScript("default { state_entry() { llSetInventoryPermMask(\"tex\", " + mask + ", PERM_COPY); llSay(0, \"done\"); } }");
        Assert.True(PumpUntil(h, () => h.Said.Contains("done"), TimeSpan.FromSeconds(30)), Said(h));

        var after = new Dictionary<string, uint>
        {
            ["base"] = item.BasePermissions, ["owner"] = item.CurrentPermissions, ["group"] = item.GroupPermissions,
            ["everyone"] = item.EveryonePermissions, ["next"] = item.NextPermissions,
        };
        _out.WriteLine("mask " + mask + ": " + string.Join(" ", after.Select(kv => kv.Key + "=0x" + kv.Value.ToString("x"))));
        Assert.Equal(C, after[field] & Four);
        foreach (var other in before.Keys.Where(k => k != field))
            Assert.True(before[other] == after[other], $"{other} changed from 0x{before[other]:x} to 0x{after[other]:x} when {field} was set");
    }

    [Fact]
    public void SetInventoryPermMaskDoesNothingForAnOrdinaryOwner()
    {
        using var h = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("AllowGodFunctions", "true"));
        // The test scene has no permissions module, and with no handler IsAdministrator says yes to
        // everyone; a region's PermissionsModule answers no for an ordinary owner.
        h.Scene.Permissions.OnIsAdministrator += id => false;
        var item = AddTexture(h);
        uint before = item.NextPermissions;
        h.RezScript(SetNextToCopyOnly);
        h.Pump();
        h.PumpUntil(() => h.Said.Contains("done"));
        Assert.Contains("done", h.Said);
        Assert.Equal(before, item.NextPermissions);
    }

    // ---- llManageEstateAccess returns an integer, and leaves the operand stack clean ----

    [Fact]
    public void ManageEstateAccessReturnsAnIntegerAndLeavesTheStackClean()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegionInfo.EstateSettings.EstateOwner = h.Prim.OwnerID;
        // A region always has an estate data service; the test scene has none (Scene.EstateDataService throws).
        h.Scene.RegisterModuleInterface<OpenSim.Services.Interfaces.IEstateDataService>(RecordingEstates.Create(out var estates));
        var client = h.AddClient();
        var item = h.RezScript($"default {{ state_entry() {{ integer r = llManageEstateAccess(ESTATE_ACCESS_ALLOWED_AGENT_ADD, \"{client.AgentId}\"); " +
                               "integer bad = llManageEstateAccess(ESTATE_ACCESS_ALLOWED_AGENT_ADD, \"not a key\"); " +
                               "llSay(0, \"r=\" + (string)r + \" bad=\" + (string)bad); } }");
        // Wait for the result, not the clock. Each call takes 200 ms of syscall time, and a fixed 2 s window was
        // once too short with classes running in parallel (the script was still parked in the syscall).
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!h.Said.Any(s => s.StartsWith("r=")) && DateTime.UtcNow < until) h.Pump(20);
        _out.WriteLine(Said(h) + " || " + h.DumpFrame(item));
        Assert.True(h.Said.Contains("r=1 bad=0"), Said(h));
        Assert.Contains(client.AgentId, h.Scene.RegionInfo.EstateSettings.EstateAccess);
        Assert.Equal(1, estates.Stores);
        var ops = h.StateOf(item)?.GetType().GetProperty("Operands")?.GetValue(h.StateOf(item)) as System.Collections.ICollection
                  ?? h.StateOf(item)?.GetType().GetField("Operands")?.GetValue(h.StateOf(item)) as System.Collections.ICollection;
        Assert.NotNull(ops);
        Assert.Empty(ops);
    }

    [Fact]
    public void ManageEstateAccessIsFalseForAnOwnerWhoIsNotAManager()
    {
        using var h = new SchedulerHarness();
        // Gods may manage (Halcyon CanIssueEstateCommand); without the hook everybody is a god.
        h.Scene.Permissions.OnIsAdministrator += id => false;
        var client = h.AddClient();
        h.RezScript($"default {{ state_entry() {{ llSay(0, \"r=\" + (string)llManageEstateAccess(ESTATE_ACCESS_ALLOWED_AGENT_ADD, \"{client.AgentId}\")); }} }}");
        // Under full-suite load the said line can come after a fixed 2 s, so pump until it is said.
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("r=")), TimeSpan.FromSeconds(30)),
            "nothing said within 30 s: [" + Said(h) + "]");
        Assert.True(h.Said.Contains("r=0"), Said(h));
    }
}

/// <summary>A physics actor with a mass, so llGetMass has something to report.</summary>
public class MassiveActor : OpenSim.Region.PhysicsModules.SharedBase.NullPhysicsActor
{
    public override float Mass => 1.25f;
}

/// <summary>An IEstateDataService that counts StoreEstateSettings and does nothing else.</summary>
public class RecordingEstates : DispatchProxy
{
    public int Stores;

    public static OpenSim.Services.Interfaces.IEstateDataService Create(out RecordingEstates rec)
    {
        var p = Create<OpenSim.Services.Interfaces.IEstateDataService, RecordingEstates>();
        rec = (RecordingEstates)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "StoreEstateSettings") System.Threading.Interlocked.Increment(ref Stores);
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>An IMoapModule that records every ClearMediaEntry as (part local id, face).</summary>
public class RecordingMoap : DispatchProxy
{
    public List<(uint, int)> Cleared { get; } = new();

    public static IMoapModule Create(out RecordingMoap rec)
    {
        var p = Create<IMoapModule, RecordingMoap>();
        rec = (RecordingMoap)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "ClearMediaEntry") lock (Cleared) Cleared.Add((((SceneObjectPart)a[0]).LocalId, (int)a[1]));
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
