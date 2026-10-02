using System.Globalization;
using BitOperations = System.Numerics.BitOperations;
using System.Reflection;
using InWorldz.Phlox.Compiler;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Halcyon's group-land power rules. Every land function in scope asks one
/// helper, Halcyon's CanEditParcel -> GenericParcelOwnerPermission (PermissionsModule.cs:1002-1042): the parcel owner;
/// on group-owned land the group itself (a deeded object) or a member holding ANY of the requested powers; on
/// group-tagged land AllowSetHome only; an estate owner or manager; a god. The pass and ban lists ask LandManageAllowed,
/// parcel music and media ChangeMedia, iwHasParcelPowers the power it is given. llUnSit (another object's sitter) is
/// the same helper with no group role qualifying (Halcyon LSLSystemAPI.cs:7981-7990).
/// The land is three strips: west (x &lt; 86) owned by the group G, middle (x &lt; 172) owned by the person P,
/// east owned by a stranger and tagged (not deeded) to G.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class GroupLandPowersTests
{
    private static readonly Vector3 West = new(40, 128, 25), Middle = new(128, 128, 25), East = new(210, 128, 25);

    private readonly ITestOutputHelper _out;
    public GroupLandPowersTests(ITestOutputHelper o) => _out = o;

    private sealed class Rig : IDisposable
    {
        public SchedulerHarness H;
        public StripLand Land;
        public UUID Group, Person, Stranger, God;
        public Dictionary<UUID, ulong> Members;
        public ILandObject GroupLand => Land.Parcels[0];
        public ILandObject PersonLand => Land.Parcels[1];
        public ILandObject TaggedLand => Land.Parcels[2];
        public void Dispose() => H.Dispose();
    }

    /// <summary>The object (owner <paramref name="owner"/>, group tag G) sits at <paramref name="at"/>.</summary>
    private static Rig NewRig(UUID owner, Vector3 at, params (UUID Member, ulong Powers)[] members)
    {
        var h = new SchedulerHarness();
        var r = new Rig
        {
            H = h, Group = UUID.Random(), Person = UUID.Random(), Stranger = UUID.Random(), God = UUID.Random(),
            Members = members.ToDictionary(m => m.Member, m => m.Powers),
        };
        if (owner.IsZero()) owner = r.Person;
        else if (owner == Owner.Group) owner = r.Group;
        r.Land = new StripLand(h.Scene, (86, r.Group), (172, r.Person), (256, r.Stranger));
        r.GroupLand.LandData.GroupID = r.Group;
        r.GroupLand.LandData.IsGroupOwned = true;
        r.TaggedLand.LandData.GroupID = r.Group;
        h.Scene.LandChannel = r.Land;
        h.Scene.RegisterModuleInterface<IGroupsModule>(PowerGroups.Create(r.Group, r.Members));
        // Without a permissions module Scene.Permissions.IsGod answers yes for everybody.
        h.Scene.Permissions.OnIsAdministrator += id => id == r.God;
        h.Prim.OwnerID = owner;
        h.Prim.GroupID = r.Group;
        h.Prim.ParentGroup.UpdateGroupPosition(at);
        return r;
    }

    private static readonly UUID Member = new("42424242-0000-4000-8000-000000000001");

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

    /// <summary>Run <paramref name="body"/> then say "done"; returns what the script said.</summary>
    private List<string> Run(Rig r, string body)
    {
        r.H.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        Assert.True(PumpUntil(r.H, () => r.H.Said.Contains("done"), TimeSpan.FromSeconds(30)),
            "the script never finished: " + string.Join(" | ", r.H.Said));
        _out.WriteLine(string.Join("\n", r.H.Said));
        return r.H.Said.ToList();
    }

    private int Powers(Rig r, string arg)
    {
        var said = Run(r, "llSay(0, \"p=\" + (string)iwHasParcelPowers(" + arg + "));");
        return int.Parse(said.First(s => s.StartsWith("p=")).Substring(2));
    }

    private static string Q(UUID id) => "\"" + id + "\"";

    private static bool OnList(ILandObject land, UUID who, AccessList kind)
        => land.LandData.ParcelAccessList.Any(e => e.AgentID == who && e.Flags == kind);

    // ── The pass and ban lists (LandManageAllowed) ─────────────────────────────

    /// <summary>Add a pass and a ban for two avatars; true when both landed on the parcel's list.</summary>
    private bool ListsWritable(Rig r, ILandObject land)
    {
        UUID a = UUID.Random(), b = UUID.Random();
        Run(r, $"llAddToLandPassList({Q(a)}, 1.0); llAddToLandBanList({Q(b)}, 1.0);");
        bool pass = OnList(land, a, AccessList.Access), ban = OnList(land, b, AccessList.Ban);
        Assert.Equal(pass, ban);
        return pass;
    }

    [Fact]
    public void ADeededObjectOnItsGroupsLandManagesTheListsWithoutAnyMemberPowers()
    {
        using var r = NewRig(Owner.Group, West);
        Assert.True(ListsWritable(r, r.GroupLand));
    }

    [Fact]
    public void AMembersObjectWithManageAllowedOnGroupLandManagesTheLists()
    {
        using var r = NewRig(Member, West, (Member, (ulong)GroupPowers.LandManageAllowed));
        Assert.True(ListsWritable(r, r.GroupLand));
    }

    [Fact]
    public void AMembersObjectWithoutManageAllowedIsRefusedEvenWithEveryOtherPower()
    {
        using var r = NewRig(Member, West, (Member, ~(ulong)GroupPowers.LandManageAllowed));
        Assert.False(ListsWritable(r, r.GroupLand));
    }

    [Fact]
    public void AnObjectNotDeededToTheGroupAndOwnedByANonMemberIsRefusedThoughItCarriesTheGroupTag()
    {
        using var r = NewRig(UUID.Random(), West, (Member, ulong.MaxValue));
        Assert.False(ListsWritable(r, r.GroupLand));
    }

    [Fact]
    public void TheParcelOwnersOwnObjectManagesTheLists()
    {
        using var r = NewRig(UUID.Zero, Middle);
        Assert.True(ListsWritable(r, r.PersonLand));
    }

    [Fact]
    public void GroupPowersDoNotReachLandThatIsOnlyTaggedToTheGroup()
    {
        using var r = NewRig(Member, East, (Member, ulong.MaxValue));
        Assert.False(ListsWritable(r, r.TaggedLand));
    }

    [Fact]
    public void AnEstateManagerAndAGodManageTheListsOnSomeoneElsesParcel()
    {
        UUID manager = UUID.Random();
        using (var r = NewRig(manager, East))
        {
            r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
            Assert.True(ListsWritable(r, r.TaggedLand));
        }
        using (var r = NewRig(UUID.Zero, East))
        {
            r.H.Prim.OwnerID = r.God;
            Assert.True(ListsWritable(r, r.TaggedLand));
        }
    }

    [Fact]
    public void RemovingAndResettingFollowTheSameRule()
    {
        UUID a = UUID.Random(), b = UUID.Random();
        foreach (var (powers, allowed) in new[] { ((ulong)GroupPowers.LandManageAllowed, true), (0UL, false) })
        {
            using var r = NewRig(Member, West, (Member, powers));
            var list = r.GroupLand.LandData.ParcelAccessList;
            list.Add(new LandAccessEntry { AgentID = a, Flags = AccessList.Access });
            list.Add(new LandAccessEntry { AgentID = b, Flags = AccessList.Ban });
            Run(r, $"llRemoveFromLandPassList({Q(a)}); llRemoveFromLandBanList({Q(b)});");
            Assert.Equal(!allowed, OnList(r.GroupLand, a, AccessList.Access));
            Assert.Equal(!allowed, OnList(r.GroupLand, b, AccessList.Ban));
            list.Add(new LandAccessEntry { AgentID = a, Flags = AccessList.Access });
            list.Add(new LandAccessEntry { AgentID = b, Flags = AccessList.Ban });
            r.H.ClearSaid(default);
            Run(r, "llResetLandPassList(); llResetLandBanList();");
            Assert.Equal(!allowed, OnList(r.GroupLand, a, AccessList.Access));
            Assert.Equal(!allowed, OnList(r.GroupLand, b, AccessList.Ban));
        }
    }

    // ── Parcel music and media (ChangeMedia) ───────────────────────────────────

    /// <summary>Sets the music and the media URL, then queries it; true when all three took effect.</summary>
    private bool MediaWritable(Rig r, ILandObject land)
    {
        var said = Run(r,
            "llSetParcelMusicURL(\"http://music.example/42\"); " +
            "llParcelMediaCommandList([PARCEL_MEDIA_COMMAND_URL, \"http://media.example/42\"]); " +
            "llSay(0, \"q=\" + llList2CSV(llParcelMediaQuery([PARCEL_MEDIA_COMMAND_URL])));");
        bool music = land.LandData.MusicURL == "http://music.example/42";
        bool media = land.LandData.MediaURL == "http://media.example/42";
        string q = said.First(s => s.StartsWith("q=")).Substring(2);
        bool query = q == "http://media.example/42";
        Assert.True(music == media && media == query, $"music {music}, media {media}, query '{q}'");
        return music;
    }

    [Fact]
    public void AMembersObjectWithChangeMediaOnGroupLandSetsMusicAndMedia()
    {
        using var r = NewRig(Member, West, (Member, (ulong)GroupPowers.ChangeMedia));
        Assert.True(MediaWritable(r, r.GroupLand));
    }

    [Fact]
    public void AMembersObjectWithoutChangeMediaIsRefusedMusicAndMedia()
    {
        using var r = NewRig(Member, West, (Member, ~(ulong)GroupPowers.ChangeMedia));
        Assert.False(MediaWritable(r, r.GroupLand));
    }

    [Fact]
    public void ADeededObjectAndTheParcelOwnerSetMusicAndMediaAndANonMemberCannot()
    {
        using (var r = NewRig(Owner.Group, West)) Assert.True(MediaWritable(r, r.GroupLand));
        using (var r = NewRig(UUID.Zero, Middle)) Assert.True(MediaWritable(r, r.PersonLand));
        using (var r = NewRig(UUID.Random(), West)) Assert.False(MediaWritable(r, r.GroupLand));
    }

    // ── iwHasParcelPowers ──────────────────────────────────────────────────────

    /// <summary>Every IW_POWER_* name and the OpenMetaverse group power it stands for.</summary>
    public static readonly (string Name, GroupPowers Power)[] IwPowers =
    {
        ("IW_POWER_INVITE", GroupPowers.Invite), ("IW_POWER_EJECT", GroupPowers.Eject),
        ("IW_POWER_GROUP_OPTIONS", GroupPowers.ChangeOptions), ("IW_POWER_CREATE_ROLE", GroupPowers.CreateRole),
        ("IW_POWER_DELETE_ROLE", GroupPowers.DeleteRole), ("IW_POWER_ROLE_PROPERTIES", GroupPowers.RoleProperties),
        ("IW_POWER_ASSIGN_LIMITED", GroupPowers.AssignMemberLimited), ("IW_POWER_ASSIGN_ANY", GroupPowers.AssignMember),
        ("IW_POWER_REMOVE_ANY", GroupPowers.RemoveMember), ("IW_POWER_CHANGE_ABILITIES", GroupPowers.ChangeActions),
        ("IW_POWER_GROUP_IDENTITY", GroupPowers.ChangeIdentity), ("IW_POWER_LAND_DEED", GroupPowers.LandDeed),
        ("IW_POWER_LAND_RELEASE", GroupPowers.LandRelease), ("IW_POWER_LAND_SALE", GroupPowers.LandSetSale),
        ("IW_POWER_LAND_JOIN", GroupPowers.LandDivideJoin), ("IW_POWER_CHAT_JOIN", GroupPowers.JoinChat),
        ("IW_POWER_FIND_PLACES", GroupPowers.FindPlaces), ("IW_POWER_LAND_IDENTITY", GroupPowers.LandChangeIdentity),
        ("IW_POWER_LANDING_POINT", GroupPowers.SetLandingPoint), ("IW_POWER_CHANGE_MEDIA", GroupPowers.ChangeMedia),
        ("IW_POWER_TERRAIN_OPTION", GroupPowers.LandEdit), ("IW_POWER_LAND_OPTIONS", GroupPowers.LandOptions),
        ("IW_POWER_ALLOW_TERRAIN", GroupPowers.AllowEditLand), ("IW_POWER_ALLOW_FLY", GroupPowers.AllowFly),
        ("IW_POWER_ALLOW_REZ", GroupPowers.AllowRez), ("IW_POWER_ALLOW_LANDMARK", GroupPowers.AllowLandmark),
        ("IW_POWER_ALLOW_VOICE", GroupPowers.AllowVoiceChat), ("IW_POWER_ALLOW_HOME", GroupPowers.AllowSetHome),
        ("IW_POWER_MANAGE_LAND", GroupPowers.LandManageAllowed), ("IW_POWER_MANAGE_BANNED", GroupPowers.LandManageBanned),
        ("IW_POWER_MANAGE_PASSES", GroupPowers.LandManagePasses), ("IW_POWER_FREEZE_EJECT", GroupPowers.LandEjectAndFreeze),
        ("IW_POWER_RETURN_GROUP", GroupPowers.ReturnGroupSet), ("IW_POWER_RETURN_NONGROUP", GroupPowers.ReturnNonGroup),
        ("IW_POWER_LANDSCAPE_PLANTS", GroupPowers.LandGardening), ("IW_POWER_OBJECT_DEED", GroupPowers.DeedObject),
        ("IW_POWER_CHAT_MODERATE", GroupPowers.ModerateChat), ("IW_POWER_OBJECT_MOVE", GroupPowers.ObjectManipulate),
        ("IW_POWER_OBJECT_SALE", GroupPowers.ObjectSetForSale), ("IW_POWER_ACCOUNTABLE", GroupPowers.Accountable),
        ("IW_POWER_HOST_EVENT", GroupPowers.HostEvent), ("IW_POWER_NOTICE_SEND", GroupPowers.SendNotices),
        ("IW_POWER_NOTICE_RECV", GroupPowers.ReceiveNotices), ("IW_POWER_PROPOSAL_ADD", GroupPowers.StartProposal),
        ("IW_POWER_PROPOSAL_VOTE", GroupPowers.VoteOnProposal), ("IW_POWER_VISIBLE", GroupPowers.MemberVisible),
        ("IW_POWER_OBJECT_RETURN", GroupPowers.ReturnGroupOwned),
    };

    /// <summary>LSLSystemAPI.IwGroupPowers (internal static), found by name so this file builds against any source.</summary>
    private static ulong IwGroupPowers(int value)
    {
        var m = typeof(global::Phlox.ScriptEngine.LSLSystemAPI).GetMethod("IwGroupPowers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.True(m != null, "LSLSystemAPI has no IwGroupPowers decoder");
        return (ulong)m.Invoke(null, new object[] { value });
    }

    private static int BitOf(GroupPowers p) => BitOperations.Log2((ulong)p);

    /// <summary>What a script sees: the bit value for bits 1-30, minus the bit number for bits 31-48.</summary>
    private static int ScriptValue(GroupPowers p) => BitOf(p) <= 30 ? (int)(ulong)p : -BitOf(p);

    [Fact]
    public void TheTableCoversEveryIwPowerConstantOnce()
    {
        var table = DefaultConstants.Constants.Keys.Where(k => k.StartsWith("IW_POWER_")).OrderBy(k => k).ToList();
        Assert.Equal(table, IwPowers.Select(p => p.Name).OrderBy(k => k).ToList());
        Assert.Equal(IwPowers.Length, IwPowers.Select(p => p.Power).Distinct().Count());
        foreach (var (name, p) in IwPowers)
        {
            Assert.Equal(1, BitOperations.PopCount((ulong)p));
            Assert.Equal(ScriptValue(p), ConstantLoadTests.ParseTableInt(DefaultConstants.Constants[name].ConstValue));
            Assert.Equal((ulong)p, IwGroupPowers(ScriptValue(p)));
        }
    }

    [Fact]
    public void EveryIwPowerConstantHasItsValueAsAScriptSeesIt()
    {
        using var r = NewRig(UUID.Zero, Middle);
        var said = Run(r, string.Concat(IwPowers.Select(p => $"llSay(0, \"{p.Name}=\" + (string){p.Name}); ")));
        foreach (var (name, p) in IwPowers)
        {
            string line = said.First(s => s.StartsWith(name + "="));
            Assert.Equal(name + "=" + ScriptValue(p).ToString(CultureInfo.InvariantCulture), line);
        }
        // Nothing below bit 31 is negative, and nothing loads as Halcyon's overflow -1 any more.
        Assert.DoesNotContain(said, s => s.StartsWith("IW_POWER_") && s.EndsWith("=-1"));
    }

    /// <summary>One run: iwHasParcelPowers for every IW_POWER_* constant, as "name=0|1".</summary>
    private Dictionary<string, int> AllPowers(Rig r)
    {
        var said = Run(r, string.Concat(IwPowers.Select(p => $"llSay(0, \"{p.Name}=\" + (string)iwHasParcelPowers({p.Name})); ")));
        return IwPowers.ToDictionary(p => p.Name,
            p => int.Parse(said.First(s => s.StartsWith(p.Name + "=")).Substring(p.Name.Length + 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void IwHasParcelPowersTestsExactlyTheAskedPowerForEveryConstant(int parity)
    {
        // The member holds every other power: each constant is asked once held and once not (the other parity).
        ulong held = IwPowers.Where(p => BitOf(p.Power) % 2 == parity).Aggregate(0UL, (a, p) => a | (ulong)p.Power);
        using var r = NewRig(Member, West, (Member, held));
        var got = AllPowers(r);
        foreach (var (name, p) in IwPowers)
            Assert.True(got[name] == ((held & (ulong)p) != 0 ? 1 : 0), $"{name} (bit {BitOf(p)}): {got[name]}, held 0x{held:X}");
    }

    [Fact]
    public void IwHasParcelPowersForTheParcelOwnerADeededObjectAManagerAndAGodIsOneForEveryPower()
    {
        UUID manager = UUID.Random();
        using (var r = NewRig(UUID.Zero, Middle)) Assert.All(AllPowers(r).Values, v => Assert.Equal(1, v));
        using (var r = NewRig(Owner.Group, West)) Assert.All(AllPowers(r).Values, v => Assert.Equal(1, v));
        using (var r = NewRig(manager, East))
        {
            r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
            Assert.All(AllPowers(r).Values, v => Assert.Equal(1, v));
        }
        using (var r = NewRig(UUID.Zero, East))
        {
            r.H.Prim.OwnerID = r.God;
            Assert.All(AllPowers(r).Values, v => Assert.Equal(1, v));
        }
    }

    [Fact]
    public void IwHasParcelPowersIsZeroForANonMemberWhoseObjectCarriesTheParcelsGroupTag()
    {
        // The port answered 1 whenever the object's group tag matched the parcel's group.
        using var r = NewRig(UUID.Random(), West);
        Assert.All(AllPowers(r).Values, v => Assert.Equal(0, v));
        r.H.ClearSaid(default);
        Assert.Equal(0, Powers(r, "0"));
    }

    [Fact]
    public void IwHasParcelPowersOnTaggedLandAdmitsOnlyAllowSetHome()
    {
        // Halcyon: "AllowSetHome has a special exception. It doesn't need to be group-owned land, just group-tagged."
        using var r = NewRig(Member, East, (Member, ulong.MaxValue));
        var got = AllPowers(r);
        foreach (var (name, _) in IwPowers)
            Assert.True(got[name] == (name == "IW_POWER_ALLOW_HOME" ? 1 : 0), $"{name}: {got[name]}");
    }

    [Fact]
    public void IwHasParcelPowersKeepsHalcyonsMeaningForZeroMinusOneAndCombinedPowers()
    {
        // Zero asks only for membership; any one of several requested bits is enough; -1 (what the high constants
        // loaded as under Halcyon, and still do in scripts compiled before this change) is every bit.
        using (var r = NewRig(Member, West, (Member, 0UL)))
        {
            Assert.Equal(1, Powers(r, "0"));
            r.H.ClearSaid(default);
            Assert.Equal(0, Powers(r, "-1"));
        }
        using (var r = NewRig(Member, West, (Member, (ulong)GroupPowers.LandManageAllowed)))
        {
            Assert.Equal(1, Powers(r, "IW_POWER_CHANGE_MEDIA | IW_POWER_MANAGE_LAND"));
            r.H.ClearSaid(default);
            Assert.Equal(1, Powers(r, "-1"));
            r.H.ClearSaid(default);
            Assert.Equal(0, Powers(r, "IW_POWER_CHANGE_MEDIA | IW_POWER_LAND_OPTIONS"));
        }
        using (var r = NewRig(Member, West, (Member, (ulong)GroupPowers.LandEjectAndFreeze)))
        {
            Assert.Equal(1, Powers(r, "IW_POWER_FREEZE_EJECT"));
            r.H.ClearSaid(default);
            Assert.Equal(0, Powers(r, "IW_POWER_MANAGE_PASSES"));
            r.H.ClearSaid(default);
            Assert.Equal(1, Powers(r, "-1"));
        }
    }

    // ── llUnSit (another object's sitter) ──────────────────────────────────────

    /// <summary>An avatar sits on a stranger's chair on <paramref name="at"/>; the script unsits them.</summary>
    private bool Unsat(Rig r, Vector3 at, bool expectUnsat)
    {
        var chair = SceneHelpers.AddSceneObject(r.H.Scene, "chair", r.Stranger);
        chair.UpdateGroupPosition(at + new Vector3(3, 0, 0));
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        var sp = SceneHelpers.AddScenePresence(r.H.Scene, acd);
        sp.AbsolutePosition = chair.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, chair.RootPart.UUID, Vector3.Zero);
        Assert.True(sp.ParentID != 0, "the avatar did not sit");
        Run(r, $"llUnSit({Q(sp.UUID)});");
        // A caller expecting the unsit waits for it; one expecting none keeps the fixed window that proves it did not happen.
        if (expectUnsat) r.H.PumpUntil(() => sp.ParentID == 0);
        else r.H.PumpFor(TimeSpan.FromMilliseconds(200));
        return sp.ParentID == 0;
    }

    [Fact]
    public void UnSitOnGroupLandNeedsADeededObjectNotAMembersPowers()
    {
        using (var r = NewRig(Owner.Group, West)) Assert.True(Unsat(r, West, expectUnsat: true));
        using (var r = NewRig(Member, West, (Member, ulong.MaxValue))) Assert.False(Unsat(r, West, expectUnsat: false));
        using (var r = NewRig(UUID.Random(), West)) Assert.False(Unsat(r, West, expectUnsat: false));
    }

    [Fact]
    public void UnSitWorksForTheParcelOwnerAManagerAndAGodButNotAStranger()
    {
        UUID manager = UUID.Random();
        using (var r = NewRig(UUID.Zero, Middle)) Assert.True(Unsat(r, Middle, expectUnsat: true));
        using (var r = NewRig(manager, East))
        {
            r.H.Scene.RegionInfo.EstateSettings.AddEstateManager(manager);
            Assert.True(Unsat(r, East, expectUnsat: true));
        }
        using (var r = NewRig(UUID.Zero, East))
        {
            r.H.Prim.OwnerID = r.God;
            Assert.True(Unsat(r, East, expectUnsat: true));
        }
        using (var r = NewRig(UUID.Zero, East)) Assert.False(Unsat(r, East, expectUnsat: false));
    }

    /// <summary>Owner shorthand: <see cref="Group"/> makes the object deeded to G (owner = G).</summary>
    private static class Owner
    {
        public static readonly UUID Group = new("42424242-0000-4000-8000-00000000000f");
    }
}

/// <summary>An IGroupsModule that knows one group and each member's powers (GetMembershipData).</summary>
internal class PowerGroups : DispatchProxy
{
    private UUID m_group;
    private Dictionary<UUID, ulong> m_members;

    public static IGroupsModule Create(UUID group, Dictionary<UUID, ulong> members)
    {
        var proxy = Create<IGroupsModule, PowerGroups>();
        var stub = (PowerGroups)(object)proxy;
        stub.m_group = group;
        stub.m_members = members;
        return proxy;
    }

    private GroupMembershipData For(UUID user)
        => m_members.TryGetValue(user, out ulong p) ? new GroupMembershipData { GroupID = m_group, GroupPowers = p } : null;

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "GetMembershipData" && args.Length == 2)
            return (UUID)args[0] == m_group ? For((UUID)args[1]) : null;
        if (targetMethod.Name == "GetMembershipData" && args.Length == 1)
            return For((UUID)args[0]) is { } m ? new[] { m } : Array.Empty<GroupMembershipData>();
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
