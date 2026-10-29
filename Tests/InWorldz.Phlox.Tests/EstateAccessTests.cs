using System.Collections.Concurrent;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

// llManageEstateAccess follows Halcyon's EstateManagementModule (EstateBanUser,
// EstateAllowUser, EstateAllowGroup, the Estate*Query calls) and InWorldz LSLSystemAPI ManageEstateAccess /
// llManageEstateAccess: god, estate owner or estate manager may call; the estate owner, estate managers, the object's
// owner and gods are never banned; a ban clears the allowed entry and sends a present avatar home (or logs them out
// when home is this region); no-ops are FALSE; the InWorldz queries 11000-11003; the owner IM unless
// PERMISSION_SILENT_ESTATE_MANAGEMENT. The action numbers are Phlox's (0..5), unchanged.
// No test reaches a network service: the estate store, the grid-user service, the IM transfer and the teleport are all
// in-memory recorders.
// Test grouping: no process-wide state (no Clock seam, no shared files), so the class runs in parallel.

/// <summary>Who the object's owner is on the estate.</summary>
public enum EstateRole { Nobody, Manager, EstateOwner, God }

/// <summary>Records every instant message handed to the transfer module; delivers nothing.</summary>
internal class RecordingIms : DispatchProxy
{
    public readonly ConcurrentQueue<GridInstantMessage> Sent = new();

    public static IMessageTransferModule Create(out RecordingIms rec)
    {
        var p = Create<IMessageTransferModule, RecordingIms>();
        rec = (RecordingIms)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == nameof(IMessageTransferModule.SendInstantMessage)) Sent.Enqueue((GridInstantMessage)a[0]);
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>A grid-user service that knows each avatar's home region, from a table.</summary>
internal class HomeTable : DispatchProxy
{
    public ConcurrentDictionary<UUID, UUID> Homes;

    public static IGridUserService Create(ConcurrentDictionary<UUID, UUID> homes)
    {
        var p = Create<IGridUserService, HomeTable>();
        ((HomeTable)(object)p).Homes = homes;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == nameof(IGridUserService.GetGridUserInfo) && UUID.TryParse((string)a[0], out UUID id)
            && Homes.TryGetValue(id, out UUID home))
            return new GridUserInfo { UserID = id.ToString(), HomeRegionID = home };
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>
/// One region; the estate belongs to <see cref="EstateOwner"/> with one manager <see cref="OtherManager"/>. The object's
/// owner is <see cref="Owner"/>, made a manager, a god or nothing by the test. Gods are the ids in <see cref="Gods"/>
/// (without the hook Scene.Permissions.IsGod answers yes for everybody).
/// </summary>
internal sealed class EstateRig : IDisposable
{
    public const int DebugChannel = 0x7FFFFFFF;
    public const int ALLOWED_AGENT_ADD = 0, ALLOWED_AGENT_REMOVE = 1, ALLOWED_GROUP_ADD = 2, ALLOWED_GROUP_REMOVE = 3,
        BANNED_AGENT_ADD = 4, BANNED_AGENT_REMOVE = 5,
        QUERY_CAN_MANAGE = 11000, QUERY_ALLOWED_AGENT = 11001, QUERY_ALLOWED_GROUP = 11002, QUERY_BANNED_AGENT = 11003;

    public SchedulerHarness H;
    public RecordingTransfer Tp;
    public RecordingEstates Estates;
    public RecordingIms Ims;
    public readonly UUID EstateOwner = UUID.Random(), OtherManager = UUID.Random();
    public readonly UUID Owner;
    public readonly HashSet<UUID> Gods = new();
    public readonly ConcurrentDictionary<UUID, UUID> Homes = new();
    public UUID Script;
    private int m_seq;
    private readonly ITestOutputHelper m_out;

    private const string Driver = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""ready""); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                integer r = llManageEstateAccess((integer)llList2String(w, 0), llList2String(w, 1));
                llSay(0, ""r"" + llList2String(w, 2) + ""="" + (string)r);
            }
        }";



    public EstateRig(ITestOutputHelper output, EstateRole role)
    {
        m_out = output;
        H = new SchedulerHarness();
        Owner = H.Prim.OwnerID;
        var es = H.Scene.RegionInfo.EstateSettings;
        es.EstateOwner = role == EstateRole.EstateOwner ? Owner : EstateOwner;
        es.AddEstateManager(OtherManager);
        if (role == EstateRole.Manager) es.AddEstateManager(Owner);
        if (role == EstateRole.God) Gods.Add(Owner);
        H.Scene.Permissions.OnIsAdministrator += id => Gods.Contains(id);
        H.Scene.RegisterModuleInterface<IEstateDataService>(RecordingEstates.Create(out Estates));
        H.Scene.RegisterModuleInterface<IMessageTransferModule>(RecordingIms.Create(out Ims));
        H.Scene.RegisterModuleInterface<IGridUserService>(HomeTable.Create(Homes));
        Tp = RecordingTransfer.InstallIn(H.Scene);
        Script = H.RezScript(Driver);
        Assert.True(PumpUntil(() => H.Said.Contains("ready")), "driver never started");
    }

    public void Dispose() => H.Dispose();

    public EstateSettings Estate => H.Scene.RegionInfo.EstateSettings;

    public bool PumpUntil(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            if (!H.PumpOnceBusy()) Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>llManageEstateAccess(action, id) in the driver; its return value.</summary>
    public int Call(int action, string id)
    {
        int n = Interlocked.Increment(ref m_seq);
        H.Scene.SimChat($"{action} {id} {n}", ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        string prefix = "r" + n + "=";
        Assert.True(PumpUntil(() => H.Said.Any(s => s.StartsWith(prefix))), "no answer to call " + n + ": " + string.Join(" | ", H.Said));
        string said = H.Said.First(s => s.StartsWith(prefix));
        m_out.WriteLine($"llManageEstateAccess({action}, {id}) = {said.Substring(prefix.Length)} errors=[{Errors}]");
        return int.Parse(said.Substring(prefix.Length));
    }

    public int Call(int action, UUID id) => Call(action, id.ToString());

    public string Errors => string.Join(" | ", H.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    public bool Banned(UUID id) => Estate.EstateBans.Any(b => b.BannedUserID == id);

    public ScenePresence Avatar(UUID id) => SceneHelpers.AddScenePresence(H.Scene, id);

    public UUID ItemId => Script;
}

public class EstateAccessTests
{
    private readonly ITestOutputHelper _out;
    public EstateAccessTests(ITestOutputHelper o) => _out = o;

    // ---- who may call ----

    [Fact]
    public void AnOwnerWhoIsNeitherManagerNorGodIsRefusedWithTheShoutAndNothingChanges()
    {
        using var r = new EstateRig(_out, EstateRole.Nobody);
        var visitor = UUID.Random();
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_ADD, visitor));
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, visitor));
        Assert.Contains("must manage estate", r.Errors);
        Assert.Empty(r.Estate.EstateAccess);
        Assert.False(r.Banned(visitor));
        Assert.Equal(0, r.Estates.Stores);
    }

    [Theory]
    [InlineData(EstateRole.Manager)]
    [InlineData(EstateRole.EstateOwner)]
    [InlineData(EstateRole.God)]   // Halcyon CanIssueEstateCommand: IsGodUser
    public void AnEstateManagerTheEstateOwnerAndAGodMayManage(EstateRole role)
    {
        using var r = new EstateRig(_out, role);
        var visitor = UUID.Random();
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, visitor));
        Assert.Contains(visitor, r.Estate.EstateAccess);
        Assert.Equal(1, r.Estates.Stores);
        Assert.DoesNotContain("must manage estate", r.Errors);
    }

    // ---- invalid ids and actions ----

    [Fact]
    public void NullKeyAnInvalidKeyAndAnUnknownActionAreFalseAndStoreNothing()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        for (int action = 0; action <= 5; action++)
            Assert.Equal(0, r.Call(action, UUID.Zero));
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_ADD, "not-a-key"));
        Assert.Equal(0, r.Call(99, UUID.Random()));
        Assert.Empty(r.Estate.EstateAccess);
        Assert.Empty(r.Estate.EstateBans);
        Assert.Empty(r.Estate.EstateGroups);
        Assert.Equal(0, r.Estates.Stores);
    }

    // ---- who is never banned ----

    [Fact]
    public void TheEstateOwnerAManagerTheObjectOwnerAndAGodAreNeverBanned()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var god = UUID.Random();
        r.Gods.Add(god);
        foreach (var id in new[] { r.EstateOwner, r.OtherManager, r.Owner, god })
        {
            Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, id));
            Assert.False(r.Banned(id));
        }
        Assert.Equal(0, r.Estates.Stores);
        Assert.Empty(r.Tp.Calls);
    }

    [Fact]
    public void AGodCallerCannotBanThemselves()
    {
        using var r = new EstateRig(_out, EstateRole.God);
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, r.Owner));
        Assert.False(r.Banned(r.Owner));
    }

    // ---- a ban: the allowed entry goes, the avatar goes ----

    [Fact]
    public void ABanRemovesTheAllowedEntryAndSendsAPresentAvatarHome()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var visitor = r.Avatar(UUID.Random());
        r.Homes[visitor.UUID] = UUID.Random();                 // home elsewhere
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, visitor.UUID));
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor.UUID));
        Assert.True(r.Banned(visitor.UUID));
        Assert.DoesNotContain(visitor.UUID, r.Estate.EstateAccess);
        Assert.Contains(r.Tp.Calls, c => c.Kind == "home" && c.Agent == visitor.UUID);
    }

    [Fact]
    public void ABannedAvatarWhoseHomeIsThisRegionIsLoggedOutNotSentHome()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var visitor = r.Avatar(UUID.Random());
        r.Homes[visitor.UUID] = r.H.Scene.RegionInfo.RegionID;
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor.UUID));
        Assert.True(r.Banned(visitor.UUID));
        Assert.DoesNotContain(r.Tp.Calls, c => c.Agent == visitor.UUID);
        Assert.True(r.PumpUntil(() => r.H.Scene.GetScenePresence(visitor.UUID) == null, 10), "the banned avatar is still here");
    }

    [Fact]
    public void BanningAnAbsentAvatarRecordsTheBanAndMovesNobody()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var absent = UUID.Random();
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, absent));
        Assert.True(r.Banned(absent));
        Assert.Empty(r.Tp.Calls);
    }

    [Fact]
    public void AllowingABannedAvatarLiftsTheBan()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var visitor = UUID.Random();
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, visitor));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, visitor));
        Assert.False(r.Banned(visitor));
        Assert.Contains(visitor, r.Estate.EstateAccess);
    }

    // ---- no-ops are FALSE (Halcyon AlreadySet) ----

    [Fact]
    public void EveryNoOpIsFalse()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        UUID agent = UUID.Random(), group = UUID.Random();
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_REMOVE, agent));
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_GROUP_REMOVE, group));
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_REMOVE, agent));
        Assert.Equal(0, r.Estates.Stores);

        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, agent));
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_AGENT_ADD, agent));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_REMOVE, agent));
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_GROUP_ADD, group));
        Assert.Equal(0, r.Call(EstateRig.ALLOWED_GROUP_ADD, group));
        Assert.Contains(group, r.Estate.EstateGroups);
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_GROUP_REMOVE, group));
        Assert.DoesNotContain(group, r.Estate.EstateGroups);
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_ADD, agent));
        Assert.Equal(0, r.Call(EstateRig.BANNED_AGENT_ADD, agent));
        Assert.Equal(1, r.Call(EstateRig.BANNED_AGENT_REMOVE, agent));
        Assert.False(r.Banned(agent));
    }

    // ---- the InWorldz queries ----

    [Fact]
    public void QueryCanManageAnswersSilentlyForAnyone()
    {
        using (var r = new EstateRig(_out, EstateRole.Nobody))
        {
            Assert.Equal(0, r.Call(EstateRig.QUERY_CAN_MANAGE, UUID.Zero));
            Assert.Equal("", r.Errors);
        }
        using (var r = new EstateRig(_out, EstateRole.Manager))
            Assert.Equal(1, r.Call(EstateRig.QUERY_CAN_MANAGE, UUID.Zero));
    }

    [Fact]
    public void TheListQueriesReportMembershipAndChangeNothing()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        UUID allowed = UUID.Random(), banned = UUID.Random(), group = UUID.Random();
        Assert.Equal(0, r.Call(EstateRig.QUERY_ALLOWED_AGENT, allowed));
        Assert.Equal(0, r.Call(EstateRig.QUERY_ALLOWED_GROUP, group));
        Assert.Equal(0, r.Call(EstateRig.QUERY_BANNED_AGENT, banned));
        r.Call(EstateRig.ALLOWED_AGENT_ADD, allowed);
        r.Call(EstateRig.ALLOWED_GROUP_ADD, group);
        r.Call(EstateRig.BANNED_AGENT_ADD, banned);
        int stores = r.Estates.Stores;
        Assert.Equal(1, r.Call(EstateRig.QUERY_ALLOWED_AGENT, allowed));
        Assert.Equal(1, r.Call(EstateRig.QUERY_ALLOWED_GROUP, group));
        Assert.Equal(1, r.Call(EstateRig.QUERY_BANNED_AGENT, banned));
        Assert.Equal(0, r.Call(EstateRig.QUERY_BANNED_AGENT, allowed));
        Assert.Equal(stores, r.Estates.Stores);
    }

    [Fact]
    public void AListQueryFromANonManagerIsRefusedWithTheShout()
    {
        using var r = new EstateRig(_out, EstateRole.Nobody);
        Assert.Equal(0, r.Call(EstateRig.QUERY_BANNED_AGENT, UUID.Random()));
        Assert.Contains("must manage estate", r.Errors);
    }

    // ---- the owner IM ----

    [Fact]
    public void EachChangeIMsTheObjectOwnerAndQueriesAndRefusalsDoNot()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var visitor = UUID.Random();
        r.Call(EstateRig.BANNED_AGENT_ADD, visitor);
        r.Call(EstateRig.QUERY_BANNED_AGENT, visitor);
        r.Call(EstateRig.BANNED_AGENT_ADD, r.EstateOwner);
        var ims = r.Ims.Sent.ToArray();
        foreach (var im in ims) _out.WriteLine("IM to " + im.toAgentID + ": " + im.message);
        var only = Assert.Single(ims);
        Assert.Equal(r.Owner.Guid, only.toAgentID);
        Assert.Equal((byte)InstantMessageDialog.MessageFromObject, only.dialog);
        Assert.Equal(visitor + " has been banned from " + r.H.Scene.RegionInfo.RegionName, only.message);
    }

    [Fact]
    public void PermissionSilentEstateManagementSuppressesTheIM()
    {
        using var r = new EstateRig(_out, EstateRole.Manager);
        var item = r.H.Prim.Inventory.GetInventoryItem(r.ItemId);
        item.PermsGranter = r.Owner;
        item.PermsMask = 16384;   // PERMISSION_SILENT_ESTATE_MANAGEMENT
        Assert.Equal(1, r.Call(EstateRig.ALLOWED_AGENT_ADD, UUID.Random()));
        Assert.Empty(r.Ims.Sent);
    }
}
