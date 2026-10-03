using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

// Who a script may teleport or eject.
// - llTeleportAgent / llTeleportAgentGlobalCoords follow SL: PERMISSION_TELEPORT from the avatar teleported, the owner
//   only (or an experience the avatar granted), a landmark by inventory name or "" for this region, SL's throttle.
// - iwTeleportAgent, llTeleportAgentHome, llEjectFromLand follow Halcyon's IsTeleportAuthorized with its holes closed:
//   never a god (unless the god owns the object), land rights on group land for an object deeded to the group or
//   an owner holding the group's Eject and Freeze power.
// - osTeleportAgent follows YEngine's OSSL (Severe on the region and grid forms, checkAllowAgentTPbyLandOwner, no god
//   rule - parity).
// No test reaches a network service: every teleport ends in a recording IEntityTransferModule, and every destination is
// this region, a landmark asset in the harness's memory asset service, or a grid handle the recorder never resolves.
// Test grouping: none of these classes touches process-wide state (no Clock seam, no shared files), so they run in
// parallel, one harness per test.

/// <summary>Records every teleport the scene asks the transfer module for; moves nobody.</summary>
internal class RecordingTransfer : DispatchProxy
{
    public readonly ConcurrentQueue<(string Kind, UUID Agent, ulong Handle, Vector3 Pos)> Calls = new();

    public static RecordingTransfer InstallIn(Scene scene)
    {
        var proxy = Create<IEntityTransferModule, RecordingTransfer>();
        typeof(Scene).GetProperty(nameof(Scene.EntityTransferModule))!.SetValue(scene, proxy);
        return (RecordingTransfer)(object)proxy;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        switch (m.Name)
        {
            case nameof(IEntityTransferModule.Teleport):
                Calls.Enqueue(("tp", ((ScenePresence)a[0]).UUID, (ulong)a[1], (Vector3)a[2]));
                return null;
            case nameof(IEntityTransferModule.TeleportHome):
                Calls.Enqueue(("home", (UUID)a[0], 0, Vector3.Zero));
                return true;
            case nameof(IEntityTransferModule.RequestTeleportLandmark):
                var lm = (AssetLandmark)a[1];
                Calls.Enqueue(("landmark", ((IClientAPI)a[0]).AgentId, lm.RegionHandle, lm.Position));
                return null;
        }
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>An experience module that knows one experience, allowed in the estate and granted by one avatar.</summary>
internal class OneExperience : DispatchProxy
{
    private UUID m_experience, m_grantedBy;

    public static IExperienceModule Create(UUID experience, UUID grantedBy)
    {
        var proxy = Create<IExperienceModule, OneExperience>();
        var stub = (OneExperience)(object)proxy;
        stub.m_experience = experience;
        stub.m_grantedBy = grantedBy;
        return proxy;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == nameof(IExperienceModule.GetExperiencePermission))
            return (UUID)a[0] == m_grantedBy && (UUID)a[1] == m_experience ? ExperiencePermission.Allowed : ExperiencePermission.None;
        if (m.Name == nameof(IExperienceModule.GetEstateAllowedExperiences)) return new[] { m_experience };
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>
/// The region is three strips: West (x &lt; 86) owned by and deeded to group G, Middle (x &lt; 172) owned by the person P,
/// East owned by a stranger S and tagged (not deeded) to G. The object carries G's group tag. Nobody is a god in the
/// permissions module (without the hook Scene.Permissions.IsGod answers yes for everybody).
/// </summary>
internal sealed class TeleportRig : IDisposable
{
    public static readonly Vector3 West = new(40, 128, 25), Middle = new(128, 128, 25), East = new(210, 128, 25);
    public static readonly UUID DeededToGroup = new("52525252-0000-4000-8000-00000000000f");
    public const int DebugChannel = 0x7FFFFFFF, TELEPORT = 0x1000;

    public SchedulerHarness H;
    public RecordingTransfer Tp;
    public UUID Group = UUID.Random(), Person = UUID.Random(), Stranger = UUID.Random(), Owner;
    private readonly ITestOutputHelper m_out;

    /// <param name="owner">The object's owner: <see cref="DeededToGroup"/> deeds it to G, zero makes it P's.</param>
    public TeleportRig(ITestOutputHelper output, UUID owner, Vector3 at, string threat = null,
        params (UUID Member, ulong Powers)[] members)
    {
        m_out = output;
        H = threat == null ? new SchedulerHarness()
            : new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat));
        Owner = owner.IsZero() ? Person : owner == DeededToGroup ? Group : owner;
        var land = new StripLand(H.Scene, (86, Group), (172, Person), (256, Stranger));
        land.Parcels[0].LandData.GroupID = Group;
        land.Parcels[0].LandData.IsGroupOwned = true;
        land.Parcels[2].LandData.GroupID = Group;
        H.Scene.LandChannel = land;
        H.Scene.RegisterModuleInterface<IGroupsModule>(PowerGroups.Create(Group, members.ToDictionary(m => m.Member, m => m.Powers)));
        H.Scene.Permissions.OnIsAdministrator += id => false;
        H.Prim.OwnerID = Owner;
        H.Prim.GroupID = Group;
        H.Prim.ParentGroup.UpdateGroupPosition(at);
        Tp = RecordingTransfer.InstallIn(H.Scene);
    }

    public void Dispose() => H.Dispose();

    public ScenePresence Avatar(UUID id, Vector3 at, bool god = false)
    {
        var sp = SceneHelpers.AddScenePresence(H.Scene, id);
        sp.AbsolutePosition = at;
        sp.IsViewerUIGod = god;
        return sp;
    }

    public bool PumpUntil(Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!H.PumpOnceBusy()) Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>Run <paramref name="body"/> in state_entry, then say "done"; waits for it (a sleep in the body included).</summary>
    public void Run(string body)
    {
        H.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        Assert.True(PumpUntil(() => H.Said.Contains("done")), "the script never finished: " + string.Join(" | ", H.Said));
        H.PumpFor(TimeSpan.FromMilliseconds(100));
        m_out.WriteLine("said=[" + string.Join(" | ", H.Said) + "] errors=[" + Errors + "] calls=[" +
                        string.Join(" | ", Tp.Calls.Select(c => c.Kind + " " + c.Agent + " " + c.Pos)) + "]");
    }

    public string Errors => string.Join(" | ", H.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    public bool Moved(UUID who, string kind = null) => Tp.Calls.Any(c => c.Agent == who && (kind == null || c.Kind == kind));

    public static string Q(UUID id) => "\"" + id + "\"";
}

/// <summary>SL llTeleportAgent (and llTeleportAgentGlobalCoords): permission, owner only, landmark by name, throttle.</summary>
public class LlTeleportAgentTests
{
    private readonly ITestOutputHelper _out;
    public LlTeleportAgentTests(ITestOutputHelper o) => _out = o;

    // "ask KEY" requests PERMISSION_TELEPORT from KEY; "tp KEY LANDMARK N" calls llTeleportAgent N times ("-" = "").
    private const string Driver = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""ready""); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                string cmd = llList2String(w, 0);
                if (cmd == ""ask"") llRequestPermissions(llList2Key(w, 1), PERMISSION_TELEPORT);
                else if (cmd == ""tp"") {
                    string lm = llList2String(w, 2); if (lm == ""-"") lm = """";
                    integer i; integer n = (integer)llList2String(w, 3); if (n < 1) n = 1;
                    for (i = 0; i < n; ++i) llTeleportAgent(llList2Key(w, 1), lm, <30, 40, 25>, <1, 0, 0>);
                    llSay(0, ""tp done"");
                }
                else if (cmd == ""global"") {
                    llTeleportAgentGlobalCoords(llList2Key(w, 1), <256000, 256000, 0>, <30, 40, 25>, <1, 0, 0>);
                    llSay(0, ""tp done"");
                }
            }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
        }";

    private static UUID Start(TeleportRig r)
    {
        var id = r.H.RezScript(Driver);
        Assert.True(r.PumpUntil(() => r.H.Said.Contains("ready")));
        return id;
    }

    private static void Say(TeleportRig r, string msg)
        => r.H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, r.H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

    /// <summary><paramref name="granter"/> answers yes to PERMISSION_TELEPORT.</summary>
    private static void Grant(TeleportRig r, UUID item, ScenePresence granter)
    {
        var client = (TestClient)granter.ControllingClient;
        int asked = client.ScriptQuestions.Count;
        Say(r, "ask " + granter.UUID);
        Assert.True(r.PumpUntil(() => client.ScriptQuestions.Count > asked), "no permission question was sent");
        client.FireScriptAnswer(r.H.Prim.UUID, item, TeleportRig.TELEPORT);
        Assert.True(r.PumpUntil(() => r.H.Said.Contains("rtp=" + TeleportRig.TELEPORT)));
    }

    private void Tp(TeleportRig r, UUID who, string landmark = "-", int times = 1, string cmd = "tp")
    {
        int before = r.H.Said.Count(s => s == "tp done");
        Say(r, cmd + " " + who + " " + landmark + " " + times);
        Assert.True(r.PumpUntil(() => r.H.Said.Count(s => s == "tp done") > before), "the call never returned");
        r.H.PumpFor(TimeSpan.FromMilliseconds(100));
        _out.WriteLine("said=[" + string.Join(" | ", r.H.Said) + "] errors=[" + r.Errors + "] calls=" + r.Tp.Calls.Count);
    }

    [Fact]
    public void TheOwnerWhoGrantedTeleportIsMovedToThePositionInThisRegionForAnEmptyLandmark()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.East);
        Grant(r, Start(r), owner);
        Tp(r, owner.UUID);
        var call = Assert.Single(r.Tp.Calls);
        Assert.Equal(("tp", owner.UUID, r.H.Scene.RegionInfo.RegionHandle, new Vector3(30, 40, 25)), call);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void WithoutThePermissionNobodyIsMovedAndTheErrorIsShoutedOnDebugChannel()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        Start(r);
        Tp(r, owner.UUID);
        Assert.Empty(r.Tp.Calls);
        Assert.Contains("PERMISSION_TELEPORT permission not set", r.Errors);
    }

    [Fact]
    public void ANonOwnerWhoGrantedTheirOwnPermissionIsStillRefused()
    {
        // SL: "This function can only teleport the owner of the object (unless part of an Experience)". The object's
        // owner also owns the land under both of them - YEngine's legacy land path would move them; SL does not.
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        r.Avatar(r.Owner, TeleportRig.East);
        var visitor = r.Avatar(UUID.Random(), TeleportRig.Middle);
        Grant(r, Start(r), visitor);
        Tp(r, visitor.UUID);
        Assert.Empty(r.Tp.Calls);
        Assert.Contains("can only teleport the owner", r.Errors);
    }

    [Fact]
    public void ThePermissionGrantedBySomeoneElseDoesNotTeleportTheOwner()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        var visitor = r.Avatar(UUID.Random(), TeleportRig.Middle);
        Grant(r, Start(r), visitor);
        Tp(r, owner.UUID);
        Assert.Empty(r.Tp.Calls);
        Assert.Contains("granted by someone other than the agent", r.Errors);
    }

    [Fact]
    public void ALandmarkNamedInThePrimsInventoryIsWhereTheOwnerGoes()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        ulong handle = OpenMetaverse.Utils.UIntsToLong(1000 * 256, 1000 * 256);
        AddLandmark(r, "Home", handle, new Vector3(10, 20, 30));
        Grant(r, Start(r), owner);
        Tp(r, owner.UUID, "Home");
        Assert.Equal(("landmark", owner.UUID, handle, new Vector3(10, 20, 30)), Assert.Single(r.Tp.Calls));
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void AMissingLandmarkOrAnItemThatIsNotALandmarkMovesNobodyAndShoutsAnError()
    {
        // SL: "If landmark is not an empty string and landmark is missing from the prim's inventory or it is not a
        // landmark then an error is shouted on DEBUG_CHANNEL." ("Nowhere" would be a region name to YEngine.)
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        TaskInventoryHelpers.AddNotecard(r.H.Scene.AssetService, r.H.Prim, "Notes", UUID.Random(), UUID.Random(), "not a landmark");
        Grant(r, Start(r), owner);
        Tp(r, owner.UUID, "Nowhere");
        Tp(r, owner.UUID, "Notes");
        Assert.Empty(r.Tp.Calls);
        Assert.Equal(2, r.H.SaidOn.Count(s => s.Channel == TeleportRig.DebugChannel && s.Message.Contains("Could not find landmark")));
    }

    [Fact]
    public void FourTeleportsGoAtOnceAndTheFifthIsThrottledWithAnError()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        Grant(r, Start(r), owner);
        Tp(r, owner.UUID, "-", 6);
        Assert.Equal(4, r.Tp.Calls.Count);
        Assert.Contains("throttled", r.Errors);
    }

    [Fact]
    public void AnExperienceTheAvatarGrantedTeleportsThemWithoutOwnershipOrPermission()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var visitor = r.Avatar(UUID.Random(), TeleportRig.East);
        var experience = UUID.Random();
        r.H.Scene.RegisterModuleInterface<IExperienceModule>(OneExperience.Create(experience, visitor.UUID));
        var item = Start(r);
        r.H.Prim.Inventory.GetInventoryItem(item).ExperienceID = experience;
        Tp(r, visitor.UUID);
        Assert.Equal(("tp", visitor.UUID, r.H.Scene.RegionInfo.RegionHandle, new Vector3(30, 40, 25)), Assert.Single(r.Tp.Calls));

        // someone who did not grant the experience is not moved
        var other = r.Avatar(UUID.Random(), TeleportRig.East);
        Tp(r, other.UUID);
        Assert.Single(r.Tp.Calls);
    }

    [Fact]
    public void ASittingOwnerIsNotTeleported()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        var item = Start(r);
        Grant(r, item, owner);
        var chair = SceneHelpers.AddSceneObject(r.H.Scene, "chair", r.Owner);
        chair.UpdateGroupPosition(TeleportRig.Middle + new Vector3(3, 0, 0));
        owner.AbsolutePosition = chair.AbsolutePosition + new Vector3(1, 1, 0);
        owner.HandleAgentRequestSit(owner.ControllingClient, owner.UUID, chair.RootPart.UUID, Vector3.Zero);
        Assert.True(owner.ParentID != 0, "the avatar did not sit");
        Tp(r, owner.UUID);
        Assert.Empty(r.Tp.Calls);
        Assert.Contains("Sitting avatars cannot be teleported", r.Errors);
    }

    [Fact]
    public void GlobalCoordsHasTheSameGateTheOwnerWithPermissionGoesAVisitorDoesNot()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle);
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        var visitor = r.Avatar(UUID.Random(), TeleportRig.Middle);
        var item = Start(r);
        Tp(r, owner.UUID, cmd: "global");
        Assert.Empty(r.Tp.Calls);
        Grant(r, item, owner);
        Tp(r, visitor.UUID, cmd: "global");
        Assert.Empty(r.Tp.Calls);
        Tp(r, owner.UUID, cmd: "global");
        var call = Assert.Single(r.Tp.Calls);
        Assert.Equal(owner.UUID, call.Agent);
        Assert.Equal(OpenMetaverse.Utils.UIntsToLong(1000 * 256, 1000 * 256), call.Handle);
    }

    private static void AddLandmark(TeleportRig r, string name, ulong handle, Vector3 pos)
    {
        var asset = new AssetBase(UUID.Random(), name, (sbyte)AssetType.Landmark, r.Owner.ToString())
        {
            Data = Encoding.UTF8.GetBytes($"Landmark version 2\nregion_id {UUID.Random()}\nlocal_pos {pos.X} {pos.Y} {pos.Z}\nregion_handle {handle}\n"),
        };
        r.H.Scene.AssetService.Store(asset);
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = asset.FullID, Name = name, OwnerID = r.Owner,
            Type = (int)AssetType.Landmark, InvType = (int)InventoryType.Landmark,
        }, false);
    }
}

/// <summary>Halcyon's iwTeleportAgent: owner, estate manager, land rights on both parcels (deeded objects only on group land), never a god.</summary>
public class IwTeleportAgentTests
{
    private readonly ITestOutputHelper _out;
    public IwTeleportAgentTests(ITestOutputHelper o) => _out = o;

    private static readonly UUID Member = new("52525252-0000-4000-8000-000000000001");

    private bool Teleports(TeleportRig r, ScenePresence target)
    {
        r.Run($"iwTeleportAgent({TeleportRig.Q(target.UUID)}, \"\", <30, 40, 25>, <1, 0, 0>);");
        return r.Moved(target.UUID, "tp");
    }

    [Fact]
    public void TheOwnerIsTeleportedAnywhereEvenAGodOwner()
    {
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.True(Teleports(r, r.Avatar(r.Owner, TeleportRig.East)));
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.True(Teleports(r, r.Avatar(r.Owner, TeleportRig.East, god: true)));   // "unless the god is the owner"
    }

    [Fact]
    public void AnEstateManagersObjectTeleportsAVisitorAnywhereButNotAGod()
    {
        var manager = UUID.Random();
        using (var r = new TeleportRig(_out, manager, TeleportRig.East))
        {
            r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
            Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle)));
        }
        using (var r = new TeleportRig(_out, manager, TeleportRig.East))
        {
            r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle, god: true)));
        }
    }

    [Fact]
    public void TheParcelOwnersObjectTeleportsAVisitorOnTheirParcelOnly()
    {
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle)));
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.East)));
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.East))      // the object stands on the stranger's land
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle)));
    }

    [Fact]
    public void AnObjectDeededToTheGroupTeleportsAVisitorOnTheGroupsLand()
    {
        using var r = new TeleportRig(_out, TeleportRig.DeededToGroup, TeleportRig.West);
        Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West)));
    }

    [Fact]
    public void AGroupTaggedObjectIsRefusedOnGroupLandUnlessItsOwnerHoldsEjectAndFreeze()
    {
        // The port admitted any object carrying the land's group tag. The correction: Halcyon's
        // HasLandPrivileges asks CanEditParcel(owner, parcel, LandEjectAndFreeze), and SL admits "The object owner must
        // have 'Eject and freeze Residents on parcels' ability in the group". Every other power is not enough.
        using (var r = new TeleportRig(_out, Member, TeleportRig.West, null, (Member, ulong.MaxValue & ~(ulong)GroupPowers.LandEjectAndFreeze)))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West)));
        using (var r = new TeleportRig(_out, UUID.Random(), TeleportRig.West))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West)));
    }

    [Fact]
    public void AMemberHoldingEjectAndFreezeTeleportsAVisitorOnTheGroupsLandOnly()
    {
        using (var r = new TeleportRig(_out, Member, TeleportRig.West, null, (Member, (ulong)GroupPowers.LandEjectAndFreeze)))
            Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West)));
        // East is only tagged to the group, not deeded: the power does not reach it.
        using (var r = new TeleportRig(_out, Member, TeleportRig.West, null, (Member, (ulong)GroupPowers.LandEjectAndFreeze)))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.East)));
        using (var r = new TeleportRig(_out, Member, TeleportRig.West, null, (Member, (ulong)GroupPowers.LandEjectAndFreeze)))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West, god: true)));
    }

    [Fact]
    public void AGodIsNeverTeleportedByTheParcelOwnerOrTheDeededObject()
    {
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle, god: true)));
        using (var r = new TeleportRig(_out, TeleportRig.DeededToGroup, TeleportRig.West))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West, god: true)));
    }
}

/// <summary>Halcyon's llTeleportAgentHome and llEjectFromLand (which is llTeleportAgentHome): the iwTeleportAgent rule, 5 s sleep.</summary>
public class TeleportHomeAndEjectTests
{
    private readonly ITestOutputHelper _out;
    public TeleportHomeAndEjectTests(ITestOutputHelper o) => _out = o;

    private static readonly UUID Member = new("52525252-0000-4000-8000-000000000002");

    public static IEnumerable<object[]> Functions() => new[] { new object[] { "llTeleportAgentHome" }, new object[] { "llEjectFromLand" } };

    /// <summary>Allowed calls: wait for the recorder (not the 5 s sleep).</summary>
    private static bool SentHome(TeleportRig r, string fn, ScenePresence target)
    {
        r.H.RezScript("default { state_entry() { " + fn + "(" + TeleportRig.Q(target.UUID) + "); llSay(0, \"done\"); } }");
        return r.PumpUntil(() => r.Moved(target.UUID, "home"), 30);
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public void TheOwnerTheParcelOwnerTheDeededObjectAndAnEstateManagerSendAVisitorHome(string fn)
    {
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.True(SentHome(r, fn, r.Avatar(r.Owner, TeleportRig.East)));
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle))
            Assert.True(SentHome(r, fn, r.Avatar(UUID.Random(), TeleportRig.Middle)));
        using (var r = new TeleportRig(_out, TeleportRig.DeededToGroup, TeleportRig.West))
            Assert.True(SentHome(r, fn, r.Avatar(UUID.Random(), TeleportRig.West)));
        var manager = UUID.Random();
        using (var r = new TeleportRig(_out, manager, TeleportRig.East))
        {
            r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
            Assert.True(SentHome(r, fn, r.Avatar(UUID.Random(), TeleportRig.Middle)));
        }
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public void AMemberHoldingEjectAndFreezeSendsAVisitorHome(string fn)
    {
        using var r = new TeleportRig(_out, Member, TeleportRig.West, null, (Member, (ulong)GroupPowers.LandEjectAndFreeze));
        Assert.True(SentHome(r, fn, r.Avatar(UUID.Random(), TeleportRig.West)));
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public void AGroupTaggedObjectWithoutEjectAndFreezeAndAnyoneAgainstAGodAreRefused(string fn)
    {
        using var r = new TeleportRig(_out, Member, TeleportRig.West, null, (Member, ulong.MaxValue & ~(ulong)GroupPowers.LandEjectAndFreeze));
        var visitor = r.Avatar(UUID.Random(), TeleportRig.West);
        r.Run($"{fn}({TeleportRig.Q(visitor.UUID)});");
        Assert.False(r.Moved(visitor.UUID));

        using var g = new TeleportRig(_out, TeleportRig.DeededToGroup, TeleportRig.West);
        var god = g.Avatar(UUID.Random(), TeleportRig.West, god: true);
        g.Run($"{fn}({TeleportRig.Q(god.UUID)});");
        Assert.False(g.Moved(god.UUID));
    }
}

/// <summary>osTeleportAgent as YEngine's OSSL: Severe on the region and grid forms, checkAllowAgentTPbyLandOwner, no god rule.</summary>
public class OsTeleportAgentTests
{
    private readonly ITestOutputHelper _out;
    public OsTeleportAgentTests(ITestOutputHelper o) => _out = o;

    private static readonly UUID Member = new("52525252-0000-4000-8000-000000000003");

    private bool Teleports(TeleportRig r, ScenePresence target, string form = "region", bool expectMoved = true)
    {
        string k = TeleportRig.Q(target.UUID);
        r.Run(form switch
        {
            "region" => $"osTeleportAgent({k}, \"\", <30, 40, 25>, <1, 0, 0>);",
            "local" => $"osTeleportAgent({k}, <30, 40, 25>, <1, 0, 0>);",
            _ => $"osTeleportAgent({k}, 1000, 1000, <30, 40, 25>, <1, 0, 0>);",
        });
        // YEngine fires the grid teleport on another thread. Up to 30 s to wait for a move; the old 2 s window
        // stays for a teleport that must not happen.
        if (form == "grid") r.PumpUntil(() => r.Moved(target.UUID), expectMoved ? 30 : 2);
        return r.Moved(target.UUID, "tp");
    }

    [Fact]
    public void TheRegionAndGridFormsAreSevereTheLocalFormAndOsTeleportOwnerAreNot()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle, threat: "VeryLow");
        var owner = r.Avatar(r.Owner, TeleportRig.Middle);
        r.H.RezScript($"default {{ state_entry() {{ osTeleportAgent({TeleportRig.Q(owner.UUID)}, \"\", <30, 40, 25>, <1, 0, 0>); llSay(0, \"after\"); }} }}");
        r.H.PumpFor(TimeSpan.FromSeconds(1));
        r.H.PumpUntil(() => r.Errors.Contains("osTeleportAgent permission denied"));
        Assert.DoesNotContain("after", r.H.Said);
        Assert.Contains("osTeleportAgent permission denied", r.Errors);
        Assert.Empty(r.Tp.Calls);

        Assert.True(Teleports(r, owner, "local"));
        r.Run("osTeleportOwner(<50, 60, 25>, <1, 0, 0>);");
        Assert.Contains(r.Tp.Calls, c => c.Agent == owner.UUID && c.Pos == new Vector3(50, 60, 25));
    }

    [Theory]
    [InlineData("region")]
    [InlineData("local")]
    [InlineData("grid")]
    public void TheParcelOwnersObjectTeleportsAVisitorOnTheirLandAndNotElsewhere(string form)
    {
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.East, threat: "Severe"))
            Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle), form));
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle, threat: "Severe"))
            Assert.False(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.East), form, expectMoved: false));
    }

    [Fact]
    public void AsInYEngineTheGroupTagAndAGodTargetAreAdmitted()
    {
        // OSSL_Api.cs:926-930 accepts the object's group tag on group land and has no god check; parity, noted for core.
        using (var r = new TeleportRig(_out, Member, TeleportRig.East, "Severe", (Member, 0UL)))
            Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.West)));
        using (var r = new TeleportRig(_out, UUID.Zero, TeleportRig.East, threat: "Severe"))
            Assert.True(Teleports(r, r.Avatar(UUID.Random(), TeleportRig.Middle, god: true)));
    }

    [Fact]
    public void AVisitorWhoGrantedTeleportIsMovedOffTheOwnersLand()
    {
        using var r = new TeleportRig(_out, UUID.Zero, TeleportRig.Middle, threat: "Severe");
        var visitor = r.Avatar(UUID.Random(), TeleportRig.East);
        Assert.False(Teleports(r, visitor));
        // OSSL_Api.cs:899-903: the PERMISSION_TELEPORT granter (the grant written on the item, as an answered dialog leaves it)
        var id = r.H.RezScript("default { state_entry() { llListen(8, \"\", NULL_KEY, \"\"); llSay(0, \"up\"); } " +
            $"listen(integer c, string n, key k, string m) {{ osTeleportAgent({TeleportRig.Q(visitor.UUID)}, \"\", <30, 40, 25>, <1, 0, 0>); llSay(0, \"moved\"); }} }}");
        Assert.True(r.PumpUntil(() => r.H.Said.Contains("up")));
        var own = r.H.Prim.Inventory.GetInventoryItem(id);
        own.PermsGranter = visitor.UUID;
        own.PermsMask = TeleportRig.TELEPORT;
        r.H.Scene.SimChat("go", ChatTypeEnum.Region, 8, r.H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(r.PumpUntil(() => r.H.Said.Contains("moved")));
        Assert.True(r.Moved(visitor.UUID, "tp"));
    }
}
