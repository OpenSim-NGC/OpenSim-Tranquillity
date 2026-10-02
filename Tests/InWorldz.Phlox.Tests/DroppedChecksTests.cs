using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Checks Phlox had dropped, restored as Halcyon has them:
/// iwGroupInvite / iwGroupEject need the script's owner to be its creator (Halcyon ScriptOwnerIsCreator);
/// llAttachToAvatarTemp on a non-owner hands the object to the wearer, and refuses a no-transfer object
/// (Halcyon AttachInternal, https://wiki.secondlife.com/wiki/LlAttachToAvatarTemp);
/// llCreateLink needs an owner-granted PERMISSION_CHANGE_LINKS and both objects modifiable and of one owner
/// (https://wiki.secondlife.com/wiki/LlCreateLink);
/// llSetObjectPermMask sits behind YEngine's god-functions gate ([YEngine] AllowGodFunctions and an administrator owner).
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class DroppedChecksTests
{
    private const int ATTACH = 0x20, CHANGE_LINKS = 0x80;
    private const uint Modify = (uint)PermissionMask.Modify, Transfer = (uint)PermissionMask.Transfer, Copy = (uint)PermissionMask.Copy;

    private static TestClient Present(SchedulerHarness h, UUID id)
        => (TestClient)SceneHelpers.AddScenePresence(h.Scene, id).ControllingClient;

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

    private static string Said(SchedulerHarness h) => "[" + string.Join(" | ", h.Said) + "]";

    // ------------------------------------------------------------------ iwGroupInvite / iwGroupEject (Halcyon)

    private static readonly UUID Group = new UUID("5a5a5a5a-0000-4000-8000-0000000041a1");
    private static readonly UUID Invitee = new UUID("5a5a5a5a-0000-4000-8000-0000000041a2");

    private const string InviteRefusal = "LSL Runtime Error: iwGroupInvite requires the owner of the calling script to be the creator of the script.";
    private const string EjectRefusal = "LSL Runtime Error: iwGroupEject requires the owner of the calling script to be the creator of the script.";

    private static RecordingGroups FakeGroups(SchedulerHarness h)
    {
        var fake = RecordingGroups.Create(out var rec);
        h.Scene.RegisterModuleInterface<IGroupsModule>(fake);
        return rec;
    }

    /// <summary>Rez <paramref name="source"/> with the script item's creator set before it runs.</summary>
    private static void RezAsCreator(SchedulerHarness h, string source, UUID creator)
    {
        var id = h.RezScript(source);
        var item = h.Prim.Inventory.GetInventoryItem(id);
        item.OwnerID = h.Prim.OwnerID;
        item.CreatorID = creator;
    }

    private const string InviteAndEject =
        "default { state_entry() { llSay(0, \"inv=\" + (string)iwGroupInvite(\"5a5a5a5a-0000-4000-8000-0000000041a1\", \"5a5a5a5a-0000-4000-8000-0000000041a2\", \"\")); " +
        "llSay(0, \"ej=\" + (string)iwGroupEject(\"5a5a5a5a-0000-4000-8000-0000000041a1\", \"5a5a5a5a-0000-4000-8000-0000000041a2\")); } }";

    [Fact]
    public void GroupInviteAndEjectWorkWhenTheScriptsOwnerIsItsCreator()
    {
        using var h = new SchedulerHarness();
        var rec = FakeGroups(h);
        RezAsCreator(h, InviteAndEject, h.Prim.OwnerID);
        h.Pump();
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("ej=")));
        // Halcyon returns the groups module's result, GenericReturnCodes.SUCCESS (0) when it was done.
        Assert.True(h.Said.Contains("inv=0") && h.Said.Contains("ej=0"), Said(h));
        Assert.Contains("InviteGroup", rec.Calls);
        Assert.Contains("EjectGroupMember", rec.Calls);
        Assert.DoesNotContain(h.Said, s => s.Contains("requires the owner"));
    }

    [Fact]
    public void GroupInviteAndEjectAreRefusedWhenTheScriptsOwnerIsNotItsCreator()
    {
        using var h = new SchedulerHarness();
        var rec = FakeGroups(h);
        RezAsCreator(h, InviteAndEject, UUID.Random());
        h.Pump();
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("ej=")));
        // Halcyon: LSLError(...) and GenericReturnCodes.PERMISSION (5); nothing reaches the groups module.
        Assert.True(h.Said.Contains("inv=5") && h.Said.Contains("ej=5"), Said(h));
        Assert.Contains(h.Said, s => s.EndsWith(InviteRefusal));
        Assert.Contains(h.Said, s => s.EndsWith(EjectRefusal));
        Assert.DoesNotContain("InviteGroup", rec.Calls);
        Assert.DoesNotContain("EjectGroupMember", rec.Calls);
    }

    // The other return codes are Halcyon's Constants.GenericReturnCodes (SUCCESS 0, ERROR 2, PARAMETER 3,
    // PERMISSION 5), not Phlox's old -3 / -1 / 1.

    private const string G = "5a5a5a5a-0000-4000-8000-0000000041a1", U = "5a5a5a5a-0000-4000-8000-0000000041a2";

    /// <summary>Run one iwGroupInvite and one iwGroupEject with the given arguments as the script's creator; return what each said.</summary>
    private static (string Inv, string Ej) Codes(SchedulerHarness h, string group, string user, string role = "")
    {
        RezAsCreator(h,
            "default { state_entry() { llSay(0, \"inv=\" + (string)iwGroupInvite(\"" + group + "\", \"" + user + "\", \"" + role + "\")); " +
            "llSay(0, \"ej=\" + (string)iwGroupEject(\"" + group + "\", \"" + user + "\")); } }", h.Prim.OwnerID);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("ej=")), TimeSpan.FromSeconds(30)), Said(h));
        return (h.Said.First(s => s.StartsWith("inv=")), h.Said.First(s => s.StartsWith("ej=")));
    }

    [Fact]
    public void GroupInviteAndEjectAnswerParameterForABadGroupKey()
    {
        foreach (var bad in new[] { "not a key", UUID.Zero.ToString() })
        {
            using var h = new SchedulerHarness();
            var rec = FakeGroups(h);
            Assert.Equal(("inv=3", "ej=3"), Codes(h, bad, U));
            lock (rec.Calls) Assert.Empty(rec.Calls);
        }
    }

    [Fact]
    public void GroupInviteAndEjectAnswerParameterForABadUserKey()
    {
        using var h = new SchedulerHarness();
        var rec = FakeGroups(h);
        Assert.Equal(("inv=3", "ej=3"), Codes(h, G, "not a key"));
        lock (rec.Calls) Assert.Empty(rec.Calls);
    }

    [Fact]
    public void GroupInviteAnswersParameterForAnUnknownRoleOrNoRoles()
    {
        using var h = new SchedulerHarness();
        var rec = FakeGroups(h);
        Assert.Equal("inv=3", Codes(h, G, U, "Officers").Inv);
        Assert.DoesNotContain("InviteGroup", rec.Calls);

        using var h2 = new SchedulerHarness();
        var rec2 = FakeGroups(h2);
        rec2.NullRoles = true;   // Halcyon: "groupID bad, or internal/system error"
        Assert.Equal("inv=3", Codes(h2, G, U).Inv);
        Assert.DoesNotContain("InviteGroup", rec2.Calls);
    }

    [Fact]
    public void GroupInviteAndEjectAnswerErrorWithNoGroupsModule()
    {
        // Halcyon's ERROR (2): "generic error, internal server failure (like module not available)".
        using var h = new SchedulerHarness();
        Assert.Equal(("inv=2", "ej=2"), Codes(h, G, U));
    }

    [Fact]
    public void GroupInviteAndEjectAnswerErrorWhenTheGroupsModuleFails()
    {
        using var h = new SchedulerHarness();
        var rec = FakeGroups(h);
        rec.Throws = true;
        Assert.Equal(("inv=2", "ej=2"), Codes(h, G, U));
        Assert.Contains("InviteGroup", rec.Calls);
        Assert.Contains("EjectGroupMember", rec.Calls);
    }

    // ------------------------------------------------------------------ llAttachToAvatarTemp (Halcyon + SL)

    private static RecordingAttachments FakeAttachments(SchedulerHarness h)
    {
        var fake = RecordingAttachments.Create(out var rec);
        h.Scene.RegisterModuleInterface<IAttachmentsModule>(fake);
        return rec;
    }

    private static string TempAttacher(UUID agent) =>
        "default { state_entry() { llRequestPermissions(\"" + agent + "\", PERMISSION_ATTACH); } " +
        "run_time_permissions(integer p) { if (p & PERMISSION_ATTACH) { llAttachToAvatarTemp(ATTACH_CHEST); " +
        "llSay(0, \"perms=\" + (string)llGetPermissions()); llSay(0, \"owner=\" + (string)llGetOwner()); } } }";

    private static object[] AttachArgs(RecordingAttachments rec)
    {
        lock (rec.Calls) return rec.Invocations.LastOrDefault(i => i.Name == "AttachObject").Args;
    }

    [Fact]
    public void TempAttachingToSomeoneElseMakesThemTheOwnerAndCreatesNoInventory()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var owner = h.Prim.OwnerID;
        var wearerId = UUID.Random();
        var wearer = Present(h, wearerId);
        var item = h.RezScript(TempAttacher(wearerId));
        h.PumpUntil(() => wearer.ScriptQuestions.Count >= 1);
        Assert.Single(wearer.ScriptQuestions);
        wearer.FireScriptAnswer(h.Prim.UUID, item, ATTACH);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("owner=")), TimeSpan.FromSeconds(30)), Said(h));

        Assert.Equal(wearerId, h.Prim.OwnerID);
        Assert.Equal(wearerId, h.Prim.ParentGroup.OwnerID);
        Assert.Contains("owner=" + wearerId, h.Said);
        Assert.Equal(owner, h.Prim.LastOwnerID);
        // SL: "When object ownership changes, any granted permissions are reset."
        Assert.Contains("perms=0", h.Said);
        var args = AttachArgs(rec);
        Assert.NotNull(args);
        Assert.Equal(wearerId, ((ScenePresence)args[0]).UUID);
        Assert.False((bool)args[4], "llAttachToAvatarTemp asked for an inventory copy (addToInventory = true)");
    }

    [Fact]
    public void TempAttachingANoTransferObjectToSomeoneElseIsRefused()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var owner = h.Prim.OwnerID;
        h.Prim.OwnerMask &= ~Transfer;
        h.Prim.ParentGroup.InvalidateDeepEffectivePerms();
        Assert.Equal(0u, h.Prim.ParentGroup.EffectiveOwnerPerms & Transfer);
        var wearerId = UUID.Random();
        var wearer = Present(h, wearerId);
        var item = h.RezScript(TempAttacher(wearerId));
        h.PumpUntil(() => wearer.ScriptQuestions.Count >= 1);
        wearer.FireScriptAnswer(h.Prim.UUID, item, ATTACH);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("owner=")), TimeSpan.FromSeconds(30)), Said(h));

        Assert.Contains(h.Said, s => s.EndsWith("llAttachToAvatarTemp: No permission to transfer"));
        Assert.Equal(owner, h.Prim.OwnerID);
        Assert.DoesNotContain("AttachObject", rec.Calls);
    }

    [Fact]
    public void TempAttachingForTheOwnerKeepsTheOwnerAndCreatesNoInventory()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var owner = h.Prim.OwnerID;
        var client = Present(h, owner);
        var item = h.RezScript(TempAttacher(owner));
        h.PumpUntil(() => client.ScriptQuestions.Count >= 1);
        client.FireScriptAnswer(h.Prim.UUID, item, ATTACH);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("owner=")), TimeSpan.FromSeconds(30)), Said(h));

        Assert.Equal(owner, h.Prim.OwnerID);
        Assert.Contains("perms=" + ATTACH, h.Said);
        var args = AttachArgs(rec);
        Assert.NotNull(args);
        Assert.False((bool)args[4]);
    }

    [Fact]
    public void TempAttachingAnObjectThatIsAlreadyWornFailsSilently()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var owner = h.Prim.OwnerID;
        Present(h, owner);
        var sp = h.Scene.GetScenePresence(owner);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = owner;
        sog.IsAttachment = true;
        sog.AttachmentPoint = 3;
        sp.AddAttachment(sog);
        // The wearer's ATTACH is an implicit grant, so run_time_permissions arrives without a dialog.
        h.RezScript(TempAttacher(owner));
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("owner=")), TimeSpan.FromSeconds(30)), Said(h));

        Assert.DoesNotContain("AttachObject", rec.Calls);
        Assert.DoesNotContain(h.Said, s => s.Contains("llAttachToAvatarTemp"));
    }

    // ------------------------------------------------------------------ llCreateLink (SL, Halcyon's text)

    private const string NoPermissionText = "Script trying to link but PERMISSION_CHANGE_LINKS permission not set!";
    private const string NotOwnerText = "llCreateLink: PERMISSION_CHANGE_LINKS not set by script owner";
    private const string NotModifiableText = "llCreateLink: this object and the target must both be modifiable and have the same owner";
    private const string NoTargetText = "llCreateLink: the target is not a prim in this region, or is attached to an avatar";

    private static string Linker(UUID granter, UUID target, bool ask = true) =>
        "default { state_entry() { " +
        (ask ? "llRequestPermissions(\"" + granter + "\", PERMISSION_CHANGE_LINKS); } " +
               "run_time_permissions(integer p) { " : "") +
        "llCreateLink(\"" + target + "\", TRUE); llSay(0, \"prims=\" + (string)llGetNumberOfPrims()); } }";

    /// <summary>Owner-granted llCreateLink onto <paramref name="target"/>; returns once the script has reported.</summary>
    private static void LinkAsOwner(SchedulerHarness h, SceneObjectGroup target)
    {
        var client = Present(h, h.Prim.OwnerID);
        var item = h.RezScript(Linker(h.Prim.OwnerID, target.UUID));
        h.PumpUntil(() => client.ScriptQuestions.Count >= 1);
        client.FireScriptAnswer(h.Prim.UUID, item, CHANGE_LINKS);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("prims=")), TimeSpan.FromSeconds(30)), Said(h));
    }

    [Fact]
    public void CreateLinkLinksTwoModifiableObjectsOfTheOwner()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        LinkAsOwner(h, target);
        Assert.Contains("prims=2", h.Said);
        Assert.Equal(2, h.Prim.ParentGroup.PrimCount);
        Assert.DoesNotContain(h.Said, s => s.Contains("llCreateLink") || s.Contains("PERMISSION_CHANGE_LINKS"));
    }

    // The region's edit permission, as Halcyon's llCreateLink checks it (CanEditObject on both objects, for the
    // script's owner). A refusal there is silent in Halcyon: no error, no sleep, nothing linked.

    [Fact]
    public void CreateLinkFailsSilentlyWhenTheRegionWouldNotLetTheOwnerEditTheScriptsObject()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        var host = h.Prim.ParentGroup.UUID;
        h.Scene.Permissions.OnEditObjectByIDs += (obj, editor) => obj != host;
        LinkAsOwner(h, target);
        Assert.Contains("prims=1", h.Said);
        Assert.Equal(1, h.Prim.ParentGroup.PrimCount);
        Assert.Equal(1, target.PrimCount);
        Assert.DoesNotContain(h.Said, s => s.Contains("llCreateLink") || s.Contains("PERMISSION_CHANGE_LINKS"));
    }

    [Fact]
    public void CreateLinkFailsSilentlyWhenTheRegionWouldNotLetTheOwnerEditTheTarget()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        var targetId = target.UUID;
        h.Scene.Permissions.OnEditObjectByIDs += (obj, editor) => obj != targetId;
        LinkAsOwner(h, target);
        Assert.Contains("prims=1", h.Said);
        Assert.Equal(1, h.Prim.ParentGroup.PrimCount);
        Assert.Equal(1, target.PrimCount);
        Assert.DoesNotContain(h.Said, s => s.Contains("llCreateLink") || s.Contains("PERMISSION_CHANGE_LINKS"));
    }

    [Fact]
    public void CreateLinkLinksWhenTheRegionLetsTheOwnerEditBothObjects()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        var owner = h.Prim.OwnerID;
        var asked = new System.Collections.Concurrent.ConcurrentBag<(UUID Obj, UUID Editor)>();
        h.Scene.Permissions.OnEditObjectByIDs += (obj, editor) => { asked.Add((obj, editor)); return editor == owner; };
        var host = h.Prim.ParentGroup.UUID;
        var targetId = target.UUID;
        LinkAsOwner(h, target);
        Assert.Contains("prims=2", h.Said);
        Assert.Equal(2, h.Prim.ParentGroup.PrimCount);
        Assert.Contains((host, owner), asked);
        Assert.Contains((targetId, owner), asked);
        Assert.DoesNotContain(h.Said, s => s.Contains("llCreateLink") || s.Contains("PERMISSION_CHANGE_LINKS"));
    }

    [Fact]
    public void CreateLinkRefusesANoModifyTarget()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "no-mod target", h.Prim.OwnerID);
        target.RootPart.OwnerMask &= ~Modify;
        LinkAsOwner(h, target);
        Assert.Contains("prims=1", h.Said);
        Assert.Equal(1, h.Prim.ParentGroup.PrimCount);
        Assert.Contains(h.Said, s => s.EndsWith(NotModifiableText));
    }

    [Fact]
    public void CreateLinkRefusesWhenTheScriptsObjectIsNoModify()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        h.Prim.OwnerMask &= ~Modify;
        LinkAsOwner(h, target);
        Assert.Contains("prims=1", h.Said);
        Assert.Equal(1, target.PrimCount);
        Assert.Contains(h.Said, s => s.EndsWith(NotModifiableText));
    }

    [Fact]
    public void CreateLinkRefusesATargetOfAnotherOwner()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "someone else's", UUID.Random());
        LinkAsOwner(h, target);
        Assert.Contains("prims=1", h.Said);
        Assert.Contains(h.Said, s => s.EndsWith(NotModifiableText));
    }

    [Fact]
    public void CreateLinkShoutsForATargetThatIsNotHere()
    {
        using var h = new SchedulerHarness();
        var client = Present(h, h.Prim.OwnerID);
        var item = h.RezScript(Linker(h.Prim.OwnerID, UUID.Random()));
        h.PumpUntil(() => client.ScriptQuestions.Count >= 1);
        client.FireScriptAnswer(h.Prim.UUID, item, CHANGE_LINKS);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("prims=")), TimeSpan.FromSeconds(30)), Said(h));
        Assert.Contains("prims=1", h.Said);
        Assert.Contains(h.Said, s => s.EndsWith(NoTargetText));
    }

    [Fact]
    public void CreateLinkRefusesAPermissionGrantedBySomeoneOtherThanTheOwner()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        var otherId = UUID.Random();
        var other = Present(h, otherId);
        var item = h.RezScript(Linker(otherId, target.UUID));
        h.PumpUntil(() => other.ScriptQuestions.Count >= 1);
        other.FireScriptAnswer(h.Prim.UUID, item, CHANGE_LINKS);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("prims=")), TimeSpan.FromSeconds(30)), Said(h));
        Assert.Contains("prims=1", h.Said);
        Assert.Contains(h.Said, s => s.EndsWith(NotOwnerText));
    }

    [Fact]
    public void CreateLinkWithoutThePermissionShoutsHalcyonsError()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "link target", h.Prim.OwnerID);
        h.RezScript(Linker(h.Prim.OwnerID, target.UUID, ask: false));
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("prims=")), TimeSpan.FromSeconds(30)), Said(h));
        Assert.Contains("prims=1", h.Said);
        Assert.Contains(h.Said, s => s.EndsWith(NoPermissionText));
    }

    // ------------------------------------------------------------------ llSetObjectPermMask (YEngine's gate)

    private const string SetNextToCopy = "default { state_entry() { llSetObjectPermMask(MASK_NEXT, PERM_COPY); llSay(0, \"done\"); } }";
    private const uint CMT = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer);

    private static uint RunSetNextToCopy(SchedulerHarness h, bool admin)
    {
        var owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => admin && id == owner;
        h.Prim.NextOwnerMask = (uint)PermissionMask.All;
        h.RezScript(SetNextToCopy);
        h.PumpUntil(() => h.Said.Contains("done"));
        Assert.Contains("done", h.Said);
        return h.Prim.NextOwnerMask & CMT;
    }

    [Fact]
    public void SetObjectPermMaskWorksForAnAdministratorWhenYEnginesGateIsOn()
    {
        using var h = new SchedulerHarness(c => c.AddConfig("YEngine").Set("AllowGodFunctions", "true"));
        Assert.Equal(Copy, RunSetNextToCopy(h, admin: true));
    }

    [Fact]
    public void SetObjectPermMaskDoesNothingWhenTheGateIsOff()
    {
        // No AllowGodFunctions anywhere: YEngine's default (false), so not even an administrator may.
        using var h = new SchedulerHarness();
        Assert.Equal(CMT, RunSetNextToCopy(h, admin: true));
    }

    [Fact]
    public void SetObjectPermMaskDoesNothingForAnOrdinaryOwnerWithTheGateOn()
    {
        using var h = new SchedulerHarness(c => c.AddConfig("YEngine").Set("AllowGodFunctions", "true"));
        Assert.Equal(CMT, RunSetNextToCopy(h, admin: false));
    }

    [Fact]
    public void APhloxAllowGodFunctionsStillOverridesYEngines()
    {
        using var h = new SchedulerHarness(c =>
        {
            c.AddConfig("YEngine").Set("AllowGodFunctions", "true");
            c.Configs["InWorldz.Phlox"].Set("AllowGodFunctions", "false");
        });
        Assert.Equal(CMT, RunSetNextToCopy(h, admin: true));
    }
}

/// <summary>An IGroupsModule that records which members were called, knows one role ("Everyone"), and does nothing.</summary>
public class RecordingGroups : DispatchProxy
{
    public List<string> Calls { get; } = new();
    /// <summary>InviteGroup and EjectGroupMember throw, as a failing groups service would.</summary>
    public bool Throws;
    /// <summary>GroupRoleDataRequest answers null (Halcyon: "groupID bad, or internal/system error").</summary>
    public bool NullRoles;

    public static IGroupsModule Create(out RecordingGroups rec)
    {
        var p = Create<IGroupsModule, RecordingGroups>();
        rec = (RecordingGroups)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        lock (Calls) Calls.Add(targetMethod.Name);
        if (targetMethod.Name == "GroupRoleDataRequest")
            return NullRoles ? null : new List<GroupRolesData> { new GroupRolesData { Name = "Everyone", RoleID = UUID.Zero } };
        if (Throws && (targetMethod.Name == "InviteGroup" || targetMethod.Name == "EjectGroupMember"))
            throw new InvalidOperationException("groups service down");
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
