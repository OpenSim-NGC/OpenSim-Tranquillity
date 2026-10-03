using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llReturnObjectsByOwner and llReturnObjectsByID checked only that PERMISSION_RETURN_OBJECTS was
/// set, never who granted it, and DELETED the objects. SL: "If the script is owned by an agent, PERMISSION_RETURN_OBJECTS
/// may be granted by the owner. If the script is owned by a group, this permission may be granted by an agent belonging
/// to the group's 'Owners' role." Both "Returns an integer that is the number of objects successfully returned to their
/// owners or an ERR_* flag." The objects go back to their owners' Lost and Found through the core's parcel-return call.
/// The land is three strips: west (x &lt; 86) and middle (x &lt; 172) owned by the script owner, east owned by someone else.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class ReturnObjectsTests
{
    private const int PERMISSION_RETURN_OBJECTS = 0x10000;
    private const int ERR_GENERIC = -1, ERR_PARCEL_PERMISSIONS = -2, ERR_MALFORMED_PARAMS = -3,
        ERR_RUNTIME_PERMISSIONS = -4, ERR_THROTTLED = -5;

    private static readonly Vector3 West = new(40, 128, 25), Middle = new(128, 128, 25), East = new(210, 128, 25);

    private readonly ITestOutputHelper _out;
    public ReturnObjectsTests(ITestOutputHelper o) => _out = o;

    private sealed class Rig : IDisposable
    {
        public SchedulerHarness H;
        public StripLand Land;
        public UUID ScriptOwner, Target, Other;
        public void Dispose() => H.Dispose();
    }

    private static Rig NewRig(bool inventoryAccess = true)
    {
        var h = new SchedulerHarness(cfg =>
        {
            if (inventoryAccess) cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        });
        if (inventoryAccess)
            SceneHelpers.SetupSceneModules(h.Scene, h.Config, new BasicInventoryAccessModule());
        var r = new Rig { H = h, ScriptOwner = h.Prim.OwnerID, Target = UUID.Random(), Other = UUID.Random() };
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Script", "Owner", r.ScriptOwner, "pw");
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Target", "Resident", r.Target, "pw");
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Other", "Resident", r.Other, "pw");
        r.Land = new StripLand(h.Scene, (86, r.ScriptOwner), (172, r.ScriptOwner), (256, r.Other));
        h.Scene.LandChannel = r.Land;
        h.Prim.Name = "the scripted object";
        h.Prim.ParentGroup.UpdateGroupPosition(West);
        return r;
    }

    private static SceneObjectGroup Obj(Rig r, UUID owner, Vector3 at, string name, int prims = 1)
    {
        var sog = prims == 1
            ? SceneHelpers.AddSceneObject(r.H.Scene, name, owner)
            : SceneHelpers.AddSceneObject(r.H.Scene, prims, owner, name + "-", 0x40);
        sog.RootPart.Name = name;
        sog.UpdateGroupPosition(at);
        return sog;
    }

    private static string Key(SceneObjectGroup g) => g.UUID.ToString();

    private static string Str(string s) => "\"" + s + "\"";

    /// <summary>Run <paramref name="call"/> (an LSL integer expression) without asking for any permission.</summary>
    private int Ungranted(Rig r, string call)
    {
        r.H.RezScript("default { state_entry() { llSay(0, \"rc=\" + (string)(" + call + ")); } }");
        return Rc(r);
    }

    /// <summary>Ask <paramref name="granter"/> for PERMISSION_RETURN_OBJECTS, answer yes from their viewer, then run each call.</summary>
    private int[] Granted(Rig r, UUID granter, params string[] calls)
    {
        var client = (TestClient)(r.H.Scene.GetScenePresence(granter) ?? SceneHelpers.AddScenePresence(r.H.Scene, granter)).ControllingClient;
        string body = string.Concat(calls.Select(c => "llSay(0, \"rc=\" + (string)(" + c + ")); "));
        var item = r.H.RezScript("default { state_entry() { llRequestPermissions(" + Str(granter.ToString()) +
            ", PERMISSION_RETURN_OBJECTS); } run_time_permissions(integer p) { llSay(0, \"rtp=\" + (string)p); " + body + "} }");
        Assert.True(PumpUntil(r.H, () => client.ScriptQuestions.Count > 0, TimeSpan.FromSeconds(30)), "no permission question reached the granter");
        client.FireScriptAnswer(r.H.Prim.UUID, item, PERMISSION_RETURN_OBJECTS);
        Assert.True(PumpUntil(r.H, () => r.H.Said.Count(s => s.StartsWith("rc=")) >= calls.Length, TimeSpan.FromSeconds(30)),
            "the calls never finished: " + string.Join(" | ", r.H.Said));
        Assert.Contains("rtp=" + PERMISSION_RETURN_OBJECTS, r.H.Said);
        _out.WriteLine(string.Join("\n", r.H.Said));
        return r.H.Said.Where(s => s.StartsWith("rc=")).Select(s => int.Parse(s.Substring(3))).ToArray();
    }

    private int Rc(Rig r)
    {
        Assert.True(PumpUntil(r.H, () => r.H.Said.Any(s => s.StartsWith("rc=")), TimeSpan.FromSeconds(30)),
            "the script never finished: " + string.Join(" | ", r.H.Said));
        _out.WriteLine(string.Join("\n", r.H.Said));
        return int.Parse(r.H.Said.First(s => s.StartsWith("rc=")).Substring(3));
    }

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!h.PumpOnceBusy()) Thread.Sleep(1);
        }
        return true;
    }

    private static List<string> LostAndFound(Rig r, UUID owner)
    {
        var folder = r.H.Scene.InventoryService.GetFolderForType(owner, FolderType.LostAndFound);
        Assert.NotNull(folder);
        return r.H.Scene.InventoryService.GetFolderItems(owner, folder.ID).Select(i => i.Name).ToList();
    }

    private static bool InScene(Rig r, SceneObjectGroup g)
        => !g.IsDeleted && r.H.Scene.GetSceneObjectGroup(g.UUID) != null;

    /// <summary>Returned, not deleted: gone from the scene AND an item of that name in the owner's Lost and Found.</summary>
    private static void AssertReturned(Rig r, SceneObjectGroup g, UUID owner)
    {
        Assert.True(PumpUntil(r.H, () => LostAndFound(r, owner).Contains(g.Name), TimeSpan.FromSeconds(30)),
            $"'{g.Name}' never reached its owner's Lost and Found (has: {string.Join(", ", LostAndFound(r, owner))})");
        Assert.False(InScene(r, g), $"'{g.Name}' is in Lost and Found but still in the scene");
    }

    private static void AssertUntouched(Rig r, params SceneObjectGroup[] groups)
    {
        r.H.PumpFor(TimeSpan.FromMilliseconds(300));
        foreach (var g in groups)
        {
            Assert.True(InScene(r, g), $"'{g.Name}' left the scene");
            Assert.DoesNotContain(g.Name, LostAndFound(r, g.OwnerID));
        }
    }

    // ── The granter ───────────────────────────────────────────────────────────

    [Fact]
    public void ByOwnerGrantedByTheOwnerReturnsToTheOwnersLostAndFoundAndNeverDeletes()
    {
        using var r = NewRig();
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");
        var linkset = Obj(r, r.Target, West + new Vector3(-2, 3, 0), "target's linkset", prims: 3);

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)");

        Assert.Equal(new[] { 2 }, rc);
        AssertReturned(r, a, r.Target);
        AssertReturned(r, linkset, r.Target);
        Assert.True(InScene(r, r.H.Prim.ParentGroup));
    }

    [Fact]
    public void ByIDGrantedByTheOwnerReturnsToTheOwnersLostAndFound()
    {
        using var r = NewRig();
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByID([{Str(Key(a))}])");

        Assert.Equal(new[] { 1 }, rc);
        AssertReturned(r, a, r.Target);
    }

    [Fact]
    public void AGrantFromSomeoneElseIsRefusedAndNothingMoves()
    {
        using var r = NewRig();
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");

        var rc = Granted(r, r.Other,
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)",
            $"llReturnObjectsByID([{Str(Key(a))}])");

        Assert.Equal(new[] { ERR_RUNTIME_PERMISSIONS, ERR_RUNTIME_PERMISSIONS }, rc);
        AssertUntouched(r, a);
    }

    [Fact]
    public void ATargetGrantingItOnSomeoneElsesScriptCannotReturnTheirOwnThingsEither()
    {
        // The target itself answering yes is still not the script owner.
        using var r = NewRig();
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");

        var rc = Granted(r, r.Target, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)");

        Assert.Equal(new[] { ERR_RUNTIME_PERMISSIONS }, rc);
        AssertUntouched(r, a);
    }

    [Fact]
    public void NoPermissionIsErrRuntimePermissionsForBoth()
    {
        using var r = NewRig();
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");

        Assert.Equal(ERR_RUNTIME_PERMISSIONS, Ungranted(r, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)"));
        r.H.ClearSaid(UUID.Zero);
        Assert.Equal(ERR_RUNTIME_PERMISSIONS, Ungranted(r, $"llReturnObjectsByID([{Str(Key(a))}])"));
        AssertUntouched(r, a);
    }

    [Fact]
    public void AGroupOwnedScriptTakesTheGrantFromAnOwnersRoleMemberOnly()
    {
        using var r = NewRig();
        UUID group = UUID.Random(), ownersRole = UUID.Random(), everyoneRole = UUID.Random();
        UUID groupOwner = UUID.Random(), plainMember = UUID.Random();
        r.H.Scene.RegisterModuleInterface<IGroupsModule>(StubGroups.Create(group, ownersRole,
            (ownersRole, groupOwner), (everyoneRole, groupOwner), (everyoneRole, plainMember)));
        var host = r.H.Prim.ParentGroup;
        host.SetGroup(group, null);
        host.SetOwner(group, group);
        host.RootPart.LastOwnerID = r.ScriptOwner;
        r.Land.Parcels[0].LandData.OwnerID = group;
        r.Land.Parcels[0].LandData.GroupID = group;
        r.Land.Parcels[0].LandData.IsGroupOwned = true;
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");
        var b = Obj(r, r.Target, West + new Vector3(3, 0, 0), "target's second box");

        var refused = Granted(r, plainMember, $"llReturnObjectsByID([{Str(Key(a))}])");
        Assert.Equal(new[] { ERR_RUNTIME_PERMISSIONS }, refused);
        AssertUntouched(r, a, b);

        r.H.ClearSaid(UUID.Zero);
        var rc = Granted(r, groupOwner, $"llReturnObjectsByID([{Str(Key(a))}])");
        Assert.Equal(new[] { 1 }, rc);
        AssertReturned(r, a, r.Target);
        AssertUntouched(r, b);
    }

    // ── Scope and parcels ─────────────────────────────────────────────────────

    [Fact]
    public void ParcelScopeIsTheScriptsParcelOnly()
    {
        using var r = NewRig();
        var w = Obj(r, r.Target, West + new Vector3(2, 0, 0), "west box");
        var m = Obj(r, r.Target, Middle, "middle box");
        var e = Obj(r, r.Target, East, "east box");

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)");

        Assert.Equal(new[] { 1 }, rc);
        AssertReturned(r, w, r.Target);
        AssertUntouched(r, m, e);
    }

    [Fact]
    public void ParcelOwnerScopeIsEveryParcelTheScriptOwnerOwns()
    {
        using var r = NewRig();
        var w = Obj(r, r.Target, West + new Vector3(2, 0, 0), "west box");
        var m = Obj(r, r.Target, Middle, "middle box");
        var e = Obj(r, r.Target, East, "east box");

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL_OWNER)");

        Assert.Equal(new[] { 2 }, rc);
        AssertReturned(r, w, r.Target);
        AssertReturned(r, m, r.Target);
        AssertUntouched(r, e);
    }

    [Fact]
    public void RegionScopeNeedsAnEstateOwnerOrManager()
    {
        using var r = NewRig();
        var w = Obj(r, r.Target, West + new Vector3(2, 0, 0), "west box");
        var e = Obj(r, r.Target, East, "east box");

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_REGION)");

        Assert.Equal(new[] { ERR_PARCEL_PERMISSIONS }, rc);
        AssertUntouched(r, w, e);
    }

    [Fact]
    public void RegionScopeFromAnEstateManagerReturnsOnEveryParcel()
    {
        using var r = NewRig();
        r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(r.ScriptOwner);
        var w = Obj(r, r.Target, West + new Vector3(2, 0, 0), "west box");
        var m = Obj(r, r.Target, Middle, "middle box");
        var e = Obj(r, r.Target, East, "east box");
        var others = Obj(r, r.Other, East + new Vector3(0, 3, 0), "other's box");

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_REGION)");

        Assert.Equal(new[] { 3 }, rc);
        AssertReturned(r, w, r.Target);
        AssertReturned(r, m, r.Target);
        AssertReturned(r, e, r.Target);
        AssertUntouched(r, others);
    }

    [Fact]
    public void ParcelScopeOnSomeoneElsesParcelIsErrParcelPermissions()
    {
        using var r = NewRig();
        r.H.Prim.ParentGroup.UpdateGroupPosition(East);
        var e = Obj(r, r.Target, East + new Vector3(2, 0, 0), "east box");

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)",
            $"llReturnObjectsByID([{Str(Key(e))}])");

        Assert.Equal(new[] { ERR_PARCEL_PERMISSIONS, ERR_PARCEL_PERMISSIONS }, rc);
        AssertUntouched(r, e);
    }

    [Fact]
    public void AnEstateManagersScriptReturnsOnAParcelItDoesNotOwn()
    {
        // SL llReturnObjectsByID: "If the script is owned by an estate owner or manager, this function works for objects
        // located on any parcel in the region." (Halcyon refuses: its parcel-owner test runs first.)
        using var r = NewRig();
        r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(r.ScriptOwner);
        r.H.Prim.ParentGroup.UpdateGroupPosition(East);
        var e = Obj(r, r.Target, East + new Vector3(2, 0, 0), "east box");
        var e2 = Obj(r, r.Target, East + new Vector3(3, 0, 0), "east box two");

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByID([{Str(Key(e))}])",
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)");

        Assert.Equal(new[] { 1, 1 }, rc);
        AssertReturned(r, e, r.Target);
        AssertReturned(r, e2, r.Target);
    }

    // ── Exempt owners ─────────────────────────────────────────────────────────

    [Fact]
    public void TheParcelOwnersEstateOwnersAndManagersObjectsAreNeverReturned()
    {
        using var r = NewRig();
        UUID estateOwner = UUID.Random(), manager = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(r.H.Scene, "Estate", "Owner", estateOwner, "pw");
        UserAccountHelpers.CreateUserWithInventory(r.H.Scene, "Estate", "Manager", manager, "pw");
        r.H.Scene.RegionInfo.EstateSettings.EstateOwner = estateOwner;
        r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
        var scriptOwners = Obj(r, r.ScriptOwner, West + new Vector3(2, 0, 0), "parcel owner's box");
        var eo = Obj(r, estateOwner, West + new Vector3(3, 0, 0), "estate owner's box");
        var em = Obj(r, manager, West + new Vector3(4, 0, 0), "manager's box");

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByOwner({Str(r.ScriptOwner.ToString())}, OBJECT_RETURN_PARCEL)",
            $"llReturnObjectsByOwner({Str(estateOwner.ToString())}, OBJECT_RETURN_PARCEL)",
            $"llReturnObjectsByOwner({Str(manager.ToString())}, OBJECT_RETURN_PARCEL)",
            $"llReturnObjectsByID([{Str(Key(scriptOwners))}, {Str(Key(eo))}, {Str(Key(em))}])");

        Assert.Equal(new[] { 0, 0, 0, 0 }, rc);
        AssertUntouched(r, scriptOwners, eo, em);
        Assert.True(InScene(r, r.H.Prim.ParentGroup));
    }

    [Fact]
    public void ObjectsOwnedByTheParcelsGroupAreNotReturnedByOwner()
    {
        using var r = NewRig();
        UUID group = UUID.Random();
        r.Land.Parcels[0].LandData.GroupID = group;
        var deeded = Obj(r, group, West + new Vector3(2, 0, 0), "deeded box");
        deeded.SetGroup(group, null);

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByOwner({Str(group.ToString())}, OBJECT_RETURN_PARCEL)");

        Assert.Equal(new[] { 0 }, rc);
        Assert.True(InScene(r, deeded));
    }

    [Fact]
    public void AttachmentsAreNeverReturned()
    {
        using var r = NewRig();
        var sp = SceneHelpers.AddScenePresence(r.H.Scene, r.Target);
        var worn = Obj(r, r.Target, West, "worn thing");
        worn.AttachedAvatar = sp.UUID;
        worn.IsAttachment = true;
        worn.AttachmentPoint = (uint)AttachmentPoint.Chest;
        sp.AddAttachment(worn);

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)",
            $"llReturnObjectsByID([{Str(Key(worn))}])");

        Assert.Equal(new[] { 0, 0 }, rc);
        Assert.True(InScene(r, worn));
    }

    // ── Malformed parameters ──────────────────────────────────────────────────

    [Fact]
    public void ABadOwnerKeyIsMalformedEvenWithoutThePermission()
    {
        using var r = NewRig();
        Assert.Equal(ERR_MALFORMED_PARAMS, Ungranted(r, "llReturnObjectsByOwner(\"not a key\", OBJECT_RETURN_PARCEL)"));
    }

    [Fact]
    public void NullKeyOwnerReturnsNothing()
    {
        using var r = NewRig();
        Assert.Equal(0, Ungranted(r, "llReturnObjectsByOwner(NULL_KEY, OBJECT_RETURN_PARCEL)"));
    }

    [Fact]
    public void AnUnknownScopeOrANonKeyInTheListIsMalformedAndNothingMoves()
    {
        using var r = NewRig();
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, 3)",
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, 0)",
            $"llReturnObjectsByID([{Str(Key(a))}, \"not a key\"])");

        Assert.Equal(new[] { ERR_MALFORMED_PARAMS, ERR_MALFORMED_PARAMS, ERR_MALFORMED_PARAMS }, rc);
        AssertUntouched(r, a);
    }

    // ── llReturnObjectsByID with a mix ────────────────────────────────────────

    [Fact]
    public void ByIDWithAMixReturnsOnlyTheReturnableOnes()
    {
        using var r = NewRig();
        UUID manager = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(r.H.Scene, "Estate", "Manager", manager, "pw");
        r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "west box");
        var linkset = Obj(r, r.Target, Middle, "middle linkset", prims: 3);
        var parcelOwners = Obj(r, r.ScriptOwner, West + new Vector3(3, 0, 0), "parcel owner's box");
        var em = Obj(r, manager, West + new Vector3(4, 0, 0), "manager's box");
        var foreign = Obj(r, r.Target, East, "east box");
        string child = linkset.GetLinkNumPart(2).UUID.ToString();

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByID([{Str(Key(a))}, {Str(child)}, {Str(Key(linkset))}, {Str(Key(parcelOwners))}, " +
            $"{Str(Key(em))}, {Str(Key(foreign))}, NULL_KEY, {Str(UUID.Random().ToString())}])");

        Assert.Equal(new[] { 2 }, rc);
        AssertReturned(r, a, r.Target);
        AssertReturned(r, linkset, r.Target);
        AssertUntouched(r, parcelOwners, em, foreign);
    }

    [Fact]
    public void ByIDReturnsTheCallingObjectItself()
    {
        // SL: "... can not have their objects returned by this method, except when the object returns itself."
        using var r = NewRig();
        var self = r.H.Prim.ParentGroup;
        var client = (TestClient)SceneHelpers.AddScenePresence(r.H.Scene, r.ScriptOwner).ControllingClient;
        var item = r.H.RezScript("default { state_entry() { llRequestPermissions(llGetOwner(), PERMISSION_RETURN_OBJECTS); } " +
            "run_time_permissions(integer p) { llReturnObjectsByID([llGetKey()]); } }");
        Assert.True(PumpUntil(r.H, () => client.ScriptQuestions.Count > 0, TimeSpan.FromSeconds(30)));
        client.FireScriptAnswer(r.H.Prim.UUID, item, PERMISSION_RETURN_OBJECTS);
        Assert.True(PumpUntil(r.H, () => LostAndFound(r, r.ScriptOwner).Contains(self.Name), TimeSpan.FromSeconds(15)),
            "the scripted object never reached its owner's Lost and Found");
        Assert.False(InScene(r, self));
    }

    // ── Throttle and the return path ──────────────────────────────────────────

    [Fact]
    public void ReturnsAreThrottledAtTheRegionsCapacityPerHour()
    {
        // SL: "Throttled at max parcel land impact capacity region-wide per hour."
        using var r = NewRig();
        typeof(RegionInfo).GetField("m_objectCapacity", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(r.H.Scene.RegionInfo, 2);
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "box one");
        var b = Obj(r, r.Target, West + new Vector3(3, 0, 0), "box two");
        var c = Obj(r, r.Target, West + new Vector3(4, 0, 0), "box three");

        var rc = Granted(r, r.ScriptOwner,
            $"llReturnObjectsByID([{Str(Key(a))}, {Str(Key(b))}])",
            $"llReturnObjectsByID([{Str(Key(c))}])",
            $"llReturnObjectsByOwner({Str(r.Target.ToString())}, OBJECT_RETURN_PARCEL)");

        Assert.Equal(new[] { 2, ERR_THROTTLED, ERR_THROTTLED }, rc);
        AssertReturned(r, a, r.Target);
        AssertReturned(r, b, r.Target);
        AssertUntouched(r, c);
    }

    [Fact]
    public void WithoutAnInventoryAccessModuleNothingIsTouched()
    {
        // The core's deleter removes the objects first and copies them after; with no module it would copy nothing.
        using var r = NewRig(inventoryAccess: false);
        var a = Obj(r, r.Target, West + new Vector3(2, 0, 0), "target's box");

        var rc = Granted(r, r.ScriptOwner, $"llReturnObjectsByID([{Str(Key(a))}])");

        Assert.Equal(new[] { ERR_GENERIC }, rc);
        r.H.PumpFor(TimeSpan.FromMilliseconds(300));
        Assert.True(InScene(r, a));
    }
}

/// <summary>A region in vertical strips: parcel n covers x below its bound, each with its own owner and local id.</summary>
internal sealed class StripLand : ILandChannel
{
    private readonly List<(int Bound, ILandObject Parcel)> m_strips = new();
    public float BanLineSafeHeight => 100f;
    public List<ILandObject> Parcels => m_strips.Select(s => s.Parcel).ToList();

    public StripLand(Scene scene, params (int Bound, UUID Owner)[] strips)
    {
        int id = 1;
        foreach (var (bound, owner) in strips)
        {
            var land = new LandObject(owner, false, scene);
            land.LandData.LocalID = id;
            land.LandData.Name = "strip " + id++;
            m_strips.Add((bound, land));
        }
    }

    private ILandObject At(float x) => m_strips.FirstOrDefault(s => x < s.Bound).Parcel ?? m_strips[^1].Parcel;

    public List<ILandObject> ParcelsNearPoint(Vector3 position) => new() { At(position.X) };
    public List<ILandObject> AllParcels() => Parcels;
    public void Clear(bool setupDefaultParcel) { }
    public ILandObject GetLandObject(Vector3 position) => At(position.X);
    public ILandObject GetLandObject(int x, int y) => At(x);
    public ILandObject GetLandObjectClippedXY(float x, float y) => At(x);
    public ILandObject GetLandObject(int localID) => m_strips.FirstOrDefault(s => s.Parcel.LandData.LocalID == localID).Parcel;
    public ILandObject GetLandObject(UUID ID) => m_strips.FirstOrDefault(s => s.Parcel.LandData.GlobalID == ID).Parcel;
    public ILandObject GetLandObject(float x, float y) => At(x);
    public bool IsLandPrimCountTainted() => false;
    public bool IsForcefulBansAllowed() => false;
    public void UpdateLandObject(int localID, LandData data) { }
    public void SendParcelsOverlay(IClientAPI client) { }
    public void ReturnObjectsInParcel(int localID, uint returnType, UUID[] agentIDs, UUID[] taskIDs, IClientAPI remoteClient) { }
    public void setParcelObjectMaxOverride(overrideParcelMaxPrimCountDelegate overrideDel) { }
    public void setSimulatorObjectMaxOverride(overrideSimulatorMaxPrimCountDelegate overrideDel) { }
    public void SetParcelOtherCleanTime(IClientAPI remoteClient, int localID, int otherCleanTime) { }
    public void Join(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id) { }
    public void Subdivide(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id) { }
    public void sendClientInitialLandInfo(IClientAPI remoteClient, bool overlay) { }
    public void ClearAllEnvironments() { }
}

/// <summary>An IGroupsModule that knows one group: its record (with the Owners role) and who holds which role.</summary>
internal class StubGroups : DispatchProxy
{
    private UUID m_group, m_ownersRole;
    private List<GroupRoleMembersData> m_members;

    public static IGroupsModule Create(UUID group, UUID ownersRole, params (UUID Role, UUID Member)[] members)
    {
        var proxy = Create<IGroupsModule, StubGroups>();
        var stub = (StubGroups)(object)proxy;
        stub.m_group = group;
        stub.m_ownersRole = ownersRole;
        stub.m_members = members.Select(m => new GroupRoleMembersData { RoleID = m.Role, MemberID = m.Member }).ToList();
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "GetGroupRecord" && args[0] is UUID g)
            return g == m_group ? new GroupRecord { GroupID = m_group, OwnerRoleID = m_ownersRole, GroupName = "stub" } : null;
        if (targetMethod.Name == "GroupRoleMembersRequest")
            return (UUID)args[1] == m_group ? new List<GroupRoleMembersData>(m_members) : new List<GroupRoleMembersData>();
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
