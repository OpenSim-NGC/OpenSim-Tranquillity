using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// When a script's permissions end, and what goes with them. Halcyon where it has the rule
/// (LSLSystemAPI OnScriptReset, ReleaseControlsInternal, llRequestPermissions, handleMustReleaseControls, the animation
/// calls), SL where it does not (wiki llTakeControls: "The script will also lose this permission on reset, or if the object
/// is deleted, detached, or dropped"; llReleaseControls: "If PERMISSION_TAKE_CONTROLS was previously granted, it will be
/// revoked"; llRequestPermissions: "PERMISSION_TELEPORT cannot be held by temporary attachments").
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class PermissionLifecycleTests
{
    private const int TAKE_CONTROLS = 0x4, TRIGGER_ANIMATION = 0x10, TELEPORT = 0x1000, CONTROL_CAMERA = 0x800;
    private const int SEAT_PERMS = TAKE_CONTROLS | TRIGGER_ANIMATION | CONTROL_CAMERA;
    private static readonly UUID Anim = new("aaaaaaaa-1111-2222-3333-444444444444");

    /// <summary>Driven over channel 7: "ask KEY MASK", "take", "passon", "release", "anim", "stopanim", "reset", "state", "perms".</summary>
    private static readonly string Lifecycle = @"
        report(string where) { llSay(0, where + "" perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); report(""start""); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                string cmd = llList2String(w, 0);
                if (cmd == ""ask"") llRequestPermissions(llList2Key(w, 1), (integer)llList2String(w, 2));
                else if (cmd == ""take"") { llTakeControls(CONTROL_FWD, TRUE, FALSE); report(""took""); }
                else if (cmd == ""passon"") { llTakeControls(CONTROL_FWD, FALSE, TRUE); report(""passon""); }
                else if (cmd == ""release"") { llReleaseControls(); report(""released""); }
                else if (cmd == ""anim"") { llStartAnimation(""" + Anim + @"""); report(""animated""); }
                else if (cmd == ""stopanim"") { llStopAnimation(""" + Anim + @"""); report(""stopped""); }
                else if (cmd == ""reset"") llResetScript();
                else if (cmd == ""resetother"") llResetOtherScript(llList2String(w, 1));
                else if (cmd == ""state"") state other;
                else if (cmd == ""perms"") report(""now"");
            }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
            control(key id, integer l, integer e) { }
        }
        state other {
            state_entry() { llListen(7, """", NULL_KEY, """"); report(""other""); }
            listen(integer c, string n, key k, string m) { if (m == ""perms"") report(""now""); }
        }";

    // ── harness helpers ───────────────────────────────────────────────────────

    private static TestClient Present(SchedulerHarness h, UUID id)
        => (TestClient)SceneHelpers.AddScenePresence(h.Scene, id).ControllingClient;

    private static void Say(SchedulerHarness h, string msg)
        => h.Scene.SimChat(msg, ChatTypeEnum.Region, 7, h.Prim.AbsolutePosition, "tester", UUID.Random(), false);

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            h.PumpOnce();
            System.Threading.Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>Say a command and pump until the script has said <paramref name="expect"/> (a new line, counted).</summary>
    private static void Command(SchedulerHarness h, string msg, string expect)
    {
        int before = h.Said.Count(s => s.StartsWith(expect, StringComparison.Ordinal));
        Say(h, msg);
        Assert.True(PumpUntil(h, () => h.Said.Count(s => s.StartsWith(expect, StringComparison.Ordinal)) > before),
            "no '" + expect + "' after '" + msg + "': said=[" + string.Join(" | ", h.Said.TakeLast(12)) + "]");
    }

    private static string Last(SchedulerHarness h, string prefix) => h.Said.Last(s => s.StartsWith(prefix, StringComparison.Ordinal));

    private static TaskInventoryItem Item(SceneObjectPart part, UUID id) => part.Inventory.GetInventoryItem(id);

    private static bool Holds(ScenePresence sp, UUID id) => sp.HasScriptControls(id);

    /// <summary>Phlox's own record of taken controls (RuntimeState.MiscAttr.Control), which a restart re-takes.</summary>
    private static bool HasControlRecord(SchedulerHarness h, UUID id)
    {
        var interp = (Interpreter)h.InterpreterFor(id);
        return interp.ScriptState.MiscAttributes.ContainsKey((int)RuntimeState.MiscAttr.Control);
    }

    /// <summary>A resident answers the dialog for <paramref name="mask"/>; the script is left with the grant.</summary>
    private static void Grant(SchedulerHarness h, TestClient client, UUID item, int mask)
    {
        int asked = client.ScriptQuestions.Count;
        Say(h, "ask " + client.AgentId + " " + mask);
        Assert.True(PumpUntil(h, () => client.ScriptQuestions.Count > asked), "no permission question was sent");
        int rtp = h.Said.Count(s => s == "rtp=" + mask);
        client.FireScriptAnswer(h.Prim.UUID, item, mask);
        Assert.True(PumpUntil(h, () => h.Said.Count(s => s == "rtp=" + mask) > rtp));
    }

    /// <summary>
    /// The animation the script starts, in the prim's inventory under its key as the name: llStartAnimation plays
    /// an inventory or built-in animation, never a raw key (SL wiki llStartAnimation; YEngine LSL_Api.llStartAnimation).
    /// </summary>
    private static void StockAnim(SchedulerHarness h)
    {
        if (h.Prim.Inventory.GetInventoryItem(Anim.ToString()) != null) return;
        h.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = Anim, Name = Anim.ToString(),
            Type = (int)AssetType.Animation, InvType = (int)InventoryType.Animation,
        }, false);
    }

    /// <summary>A script holding TAKE_CONTROLS | TRIGGER_ANIMATION | CONTROL_CAMERA from <paramref name="sp"/>, controls taken, the animation playing.</summary>
    private static UUID Armed(SchedulerHarness h, out ScenePresence sp, out TestClient client)
    {
        var id = h.RezScript(Lifecycle);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal))));
        client = Present(h, UUID.Random());
        sp = h.Scene.GetScenePresence(client.AgentId);
        Grant(h, client, id, SEAT_PERMS);
        Command(h, "take", "took");
        StockAnim(h);
        Command(h, "anim", "animated");
        Assert.True(Holds(sp, id));
        Assert.True(HasControlRecord(h, id));
        Assert.True(sp.Animator.HasAnimation(Anim));
        Assert.Equal(SEAT_PERMS, Item(h.Prim, id).PermsMask);
        return id;
    }

    // ── script reset ──────────────────────────────────────────────────────────

    public static IEnumerable<object[]> ResetPaths() => new[] { new object[] { "llResetScript" }, new object[] { "llResetOtherScript" }, new object[] { "viewer Reset" } };

    [Theory]
    [MemberData(nameof(ResetPaths))]
    public void AResetScriptHasNoPermissionsAndItsControlsAreReleased(string path)
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var sp, out _);
        int starts = h.Said.Count(s => s.StartsWith("start", StringComparison.Ordinal));

        switch (path)
        {
            case "llResetScript": Say(h, "reset"); break;
            case "llResetOtherScript":
                // a second script in the prim resets the first by name
                h.RezScript("default { state_entry() { llListen(9, \"\", NULL_KEY, \"\"); llSay(0, \"resetter ready\"); } " +
                            "listen(integer c, string n, key k, string m) { llResetOtherScript(m); } }");
                Assert.True(PumpUntil(h, () => h.Said.Contains("resetter ready")));
                h.Scene.SimChat(Item(h.Prim, id).Name, ChatTypeEnum.Region, 9, h.Prim.AbsolutePosition, "tester", UUID.Random(), false);
                break;
            case "viewer Reset": h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, id); break;
        }
        Assert.True(PumpUntil(h, () => h.Said.Count(s => s.StartsWith("start", StringComparison.Ordinal)) > starts), "the script was not reset (" + path + ")");

        Assert.Equal("start perms=0 key=" + UUID.Zero, Last(h, "start"));
        var item = Item(h.Prim, id);
        Assert.Equal(0, item.PermsMask);
        Assert.Equal(UUID.Zero, item.PermsGranter);
        Assert.False(Holds(sp, id), "the avatar still has controls taken by a reset script");
        Assert.False(HasControlRecord(h, id), "the Control record survived the reset: a restart would take the controls again");
        // SL's own example stops the animation BEFORE llResetScript ("release the avatar animation permissions"), and
        // Halcyon's OnScriptReset stops none: a reset ends the permission, not an animation already playing.
        Assert.True(sp.Animator.HasAnimation(Anim));
        // and with no permission the reset script can no longer stop it
        Command(h, "stopanim", "stopped");
        Assert.True(sp.Animator.HasAnimation(Anim));
    }

    [Fact]
    public void AStateChangeKeepsThePermissionsAndTheControls()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var sp, out _);
        Command(h, "state", "other");
        Assert.Equal("other perms=" + SEAT_PERMS + " key=" + sp.UUID, Last(h, "other"));
        Assert.True(Holds(sp, id));
    }

    // ── llRequestPermissions ──────────────────────────────────────────────────

    [Fact]
    public void ANewRequestForAnotherAvatarReleasesTheControlsAndTheAnswerReplacesTheOldGrant()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        var b = Present(h, UUID.Random());

        int asked = b.ScriptQuestions.Count;
        Say(h, "ask " + b.AgentId + " " + TRIGGER_ANIMATION);
        Assert.True(PumpUntil(h, () => b.ScriptQuestions.Count > asked));
        // Halcyon :4544: "item.PermsGranter != agentID" -> the controls and TAKE_CONTROLS go at once
        Assert.False(Holds(a, id), "A still has controls after the script asked B");
        Assert.False(HasControlRecord(h, id));
        Assert.Equal(TRIGGER_ANIMATION | CONTROL_CAMERA, Item(h.Prim, id).PermsMask);

        b.FireScriptAnswer(h.Prim.UUID, id, TRIGGER_ANIMATION);
        Assert.True(PumpUntil(h, () => h.Said.Contains("rtp=" + TRIGGER_ANIMATION)));
        Command(h, "perms", "now");
        Assert.Equal("now perms=" + TRIGGER_ANIMATION + " key=" + b.AgentId, Last(h, "now"));
        // "Scripts may hold permissions for only one agent at a time": A's animation grant is gone
        Command(h, "stopanim", "stopped");
        Assert.True(a.Animator.HasAnimation(Anim), "B's grant stopped an animation on A");
    }

    [Fact]
    public void ARequestToAnAbsentAvatarDropsTheOldGrant()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        Command(h, "ask " + UUID.Random() + " " + TRIGGER_ANIMATION, "rtp=0");
        Assert.False(Holds(a, id));
        Assert.Equal(0, Item(h.Prim, id).PermsMask);
        Assert.Equal(UUID.Zero, Item(h.Prim, id).PermsGranter);
    }

    [Fact]
    public void ARequestWithoutTakeControlsFromTheSameAvatarReleasesTheControls()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out var client);
        int asked = client.ScriptQuestions.Count;
        Say(h, "ask " + a.UUID + " " + TRIGGER_ANIMATION);
        Assert.True(PumpUntil(h, () => client.ScriptQuestions.Count > asked));
        Assert.False(Holds(a, id));
        Assert.Equal(TRIGGER_ANIMATION | CONTROL_CAMERA, Item(h.Prim, id).PermsMask);
    }

    [Fact]
    public void ReleasingWithNullKeyReleasesTheControlsAndEveryPermission()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        Command(h, "ask " + UUID.Zero + " " + SEAT_PERMS, "rtp=0");
        Assert.False(Holds(a, id));
        Assert.False(HasControlRecord(h, id));
        Assert.Equal(0, Item(h.Prim, id).PermsMask);
        Assert.Equal(UUID.Zero, Item(h.Prim, id).PermsGranter);
    }

    [Fact]
    public void ATemporaryAttachmentCannotAskForTeleport()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(Lifecycle);
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal)));   // Until it has started
        var client = Present(h, h.Prim.OwnerID);
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = 3;
        sog.FromItemID = UUID.Zero;   // temp: not from inventory (AttachmentsModule's own test)
        sp.AddAttachment(sog);

        // TELEPORT | TRIGGER_ANIMATION from the wearer: TELEPORT is stripped with Halcyon's error, the animation bit is
        // implicit for a wearer, so no dialog at all
        Command(h, "ask " + sp.UUID + " " + (TELEPORT | TRIGGER_ANIMATION), "rtp=" + TRIGGER_ANIMATION);
        Assert.Contains("Script error: Temporary attachments cannot request runtime permissions to teleport.", h.Said);
        Assert.Empty(client.ScriptQuestions);

        // TELEPORT alone becomes a request for nothing: a release (Halcyon: "possibly releasing them all")
        Command(h, "ask " + sp.UUID + " " + TELEPORT, "rtp=0");
        Assert.Equal(0, Item(h.Prim, id).PermsMask);
        Assert.Empty(client.ScriptQuestions);
    }

    [Fact]
    public void AnAttachmentFromInventoryMayStillAskForTeleport()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(Lifecycle);
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal)));   // Until it has started
        var client = Present(h, h.Prim.OwnerID);
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = 3;
        sog.FromItemID = UUID.Random();
        sp.AddAttachment(sog);

        Say(h, "ask " + sp.UUID + " " + (TELEPORT | TRIGGER_ANIMATION));
        Assert.True(PumpUntil(h, () => client.ScriptQuestions.Count == 1));
        Assert.Equal(TELEPORT | TRIGGER_ANIMATION, client.ScriptQuestions[0].Question);
        Assert.DoesNotContain(h.Said, s => s.Contains("Temporary attachments"));
    }

    /// <summary>An IMuteListService that answers MuteListRequest from a fixed list, in MuteListService's line format.</summary>
    private sealed class FakeMutes : IMuteListService
    {
        public readonly Dictionary<UUID, List<UUID>> Muted = new();
        public int Requests;
        public byte[] MuteListRequest(UUID agent, uint crc)
        {
            Requests++;
            if (!Muted.TryGetValue(agent, out var ids)) return Array.Empty<byte>();
            return System.Text.Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => "1 " + id + " somebody|0\n")));
        }
        public bool UpdateMute(MuteData mute) => false;
        public bool RemoveMute(UUID agentID, UUID muteID, string muteName) => false;
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("object")]
    public void NoDialogGoesToSomeoneWhoMutedTheOwnerOrTheObject(string muted)
    {
        using var h = new SchedulerHarness();
        var mutes = new FakeMutes();
        h.Scene.RegisterModuleInterface<IMuteListService>(mutes);
        var id = h.RezScript(Lifecycle);
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal)));   // Until it has started
        var client = Present(h, UUID.Random());
        mutes.Muted[client.AgentId] = new List<UUID> { muted == "owner" ? h.Prim.OwnerID : h.Prim.ParentGroup.UUID };

        Say(h, "ask " + client.AgentId + " " + TRIGGER_ANIMATION);
        h.Pump();
        Command(h, "perms", "now");
        Assert.Empty(client.ScriptQuestions);
        Assert.Equal("now perms=0 key=" + UUID.Zero, Last(h, "now"));
        // Halcyon returns without run_time_permissions
        Assert.DoesNotContain("rtp=0", h.Said);
        Assert.True(mutes.Requests > 0);
    }

    [Fact]
    public void SomeoneWhoMutedSomebodyElseStillGetsTheDialogAndTheOwnerIsNeverLookedUp()
    {
        using var h = new SchedulerHarness();
        var mutes = new FakeMutes();
        h.Scene.RegisterModuleInterface<IMuteListService>(mutes);
        var id = h.RezScript(Lifecycle);
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal)));   // Until it has started
        var client = Present(h, UUID.Random());
        mutes.Muted[client.AgentId] = new List<UUID> { UUID.Random() };
        Say(h, "ask " + client.AgentId + " " + TRIGGER_ANIMATION);
        Assert.True(PumpUntil(h, () => client.ScriptQuestions.Count == 1));

        // "For performance reasons, don't make this call if it's a dialog to yourself." (Halcyon IsScriptMuted)
        var owner = Present(h, h.Prim.OwnerID);
        int before = mutes.Requests;
        Say(h, "ask " + owner.AgentId + " " + TRIGGER_ANIMATION);
        Assert.True(PumpUntil(h, () => owner.ScriptQuestions.Count == 1));
        Assert.Equal(before, mutes.Requests);
    }

    // ── llReleaseControls ─────────────────────────────────────────────────────

    [Fact]
    public void LlReleaseControlsRevokesTakeControls()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        Command(h, "release", "released");
        Assert.Equal("released perms=" + (TRIGGER_ANIMATION | CONTROL_CAMERA) + " key=" + a.UUID, Last(h, "released"));
        Assert.False(Holds(a, id));
        Assert.False(HasControlRecord(h, id));
        // without the permission a new take fails
        Command(h, "take", "took");
        Assert.False(Holds(a, id));
    }

    [Fact]
    public void ReTakingOrPassingOnKeepsThePermission()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        Command(h, "take", "took");
        Assert.True(Holds(a, id));
        Assert.Equal("took perms=" + SEAT_PERMS + " key=" + a.UUID, Last(h, "took"));
        // llTakeControls(x, FALSE, TRUE) is the script's own call: the core removes the registration, the permission stays
        Command(h, "passon", "passon");
        h.Pump();
        Command(h, "perms", "now");
        Assert.Equal("now perms=" + SEAT_PERMS + " key=" + a.UUID, Last(h, "now"));
    }

    // ── owner change ──────────────────────────────────────────────────────────

    [Fact]
    public void AnOwnerChangeClearsThePermissionsAndReleasesTheControls()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        var sog = h.Prim.ParentGroup;
        UUID newOwner = UUID.Random();
        sog.SetOwner(newOwner, UUID.Zero);
        h.Prim.Inventory.ChangeInventoryOwner(newOwner);

        Assert.True(PumpUntil(h, () => !Holds(a, id)), "the old owner's avatar still has controls taken by a sold object");
        Assert.True(PumpUntil(h, () => !HasControlRecord(h, id)));
        Command(h, "perms", "now");
        Assert.Equal("now perms=0 key=" + UUID.Zero, Last(h, "now"));
    }

    [Fact]
    public void AGroupChangeAloneKeepsThePermissions()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        h.Prim.ParentGroup.SetGroup(UUID.Random(), null);
        h.Pump();
        Assert.True(Holds(a, id));
        Assert.Equal(SEAT_PERMS, Item(h.Prim, id).PermsMask);
    }

    // ── the granter leaves, stands, releases keys ─────────────────────────────

    [Fact]
    public void AnimatingAGranterWhoLeftRevokesTriggerAnimation()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(Lifecycle);
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal)));   // Until it has started
        var client = Present(h, UUID.Random());
        Grant(h, client, id, TRIGGER_ANIMATION | CONTROL_CAMERA);
        h.Scene.CloseAgent(client.AgentId, false);
        h.PumpUntil(() => { var p = h.Scene.GetScenePresence(client.AgentId); return p == null || p.IsChildAgent || p.IsDeleted; });
        var gone = h.Scene.GetScenePresence(client.AgentId);
        Assert.True(gone == null || gone.IsChildAgent || gone.IsDeleted, "the avatar is still here: deleted=" + gone?.IsDeleted);
        // leaving changes no permission by itself
        Command(h, "perms", "now");
        Assert.Equal("now perms=" + (TRIGGER_ANIMATION | CONTROL_CAMERA) + " key=" + client.AgentId, Last(h, "now"));

        // Halcyon :4117-4126, "Emulate SL's behavior of clearing this permission when this is called for an agent
        // outside this region": TRIGGER_ANIMATION goes and run_time_permissions says what is left
        StockAnim(h);
        Command(h, "anim", "animated");
        // the event comes after the call that raised it
        Assert.True(PumpUntil(h, () => h.Said.Contains("rtp=" + CONTROL_CAMERA)), "said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Equal("animated perms=" + CONTROL_CAMERA + " key=" + client.AgentId, Last(h, "animated"));
    }

    [Fact]
    public void StoppingAnAnimationOnAGranterWhoLeftClearsAnEmptyGrant()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(Lifecycle);
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal)));   // Until it has started
        var client = Present(h, UUID.Random());
        Grant(h, client, id, TRIGGER_ANIMATION);
        h.Scene.CloseAgent(client.AgentId, false);
        h.PumpUntil(() => { var p = h.Scene.GetScenePresence(client.AgentId); return p == null || p.IsChildAgent || p.IsDeleted; });
        Command(h, "stopanim", "stopped");
        Assert.True(PumpUntil(h, () => h.Said.Contains("rtp=0")), "said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Equal("stopped perms=0 key=" + UUID.Zero, Last(h, "stopped"));
    }

    [Fact]
    public void TheGranterLeavingKeepsThePermissionsAndTheControlRecord()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        h.Scene.CloseAgent(a.UUID, false);
        h.Pump();
        Assert.Equal(SEAT_PERMS, Item(h.Prim, id).PermsMask);
        Assert.True(HasControlRecord(h, id));
    }

    [Fact]
    public void ACrossingAvatarsControlsClearedByTheCoreKeepThePermissions()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        a.IsInTransit = true;         // EntityTransferModule sets it before ClearControls on a crossing
        a.ClearControls();
        a.IsInTransit = false;
        h.Pump();
        Assert.Equal(SEAT_PERMS, Item(h.Prim, id).PermsMask);
        Assert.True(HasControlRecord(h, id), "a crossing's release took the Control record the other side re-takes from");
    }

    /// <summary>A two-prim object; the script in the root, the sitter on the child. Core strips only the sat-on prim's items.</summary>
    private static (UUID id, ScenePresence sp, SceneObjectPart seat) SatOnAChild(SchedulerHarness h)
    {
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "seat", h.Prim.OwnerID));
        var seat = h.Prim.ParentGroup.GetLinkNumPart(2);
        var id = h.RezScript(Lifecycle);
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("start", StringComparison.Ordinal))));
        var sp = SceneHelpers.AddScenePresence(h.Scene, UUID.Random());
        sp.AbsolutePosition = seat.AbsolutePosition + new Vector3(1, 1, 0);   // within sit range (PrimParamsRuleWalkTests)
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.UUID, Vector3.Zero);
        Assert.True(sp.ParentID != 0, "the avatar did not sit");
        // implicit for a sitter: no dialog
        Command(h, "ask " + sp.UUID + " " + SEAT_PERMS, "rtp=" + SEAT_PERMS);
        Command(h, "take", "took");
        StockAnim(h);
        Command(h, "anim", "animated");
        Assert.True(Holds(sp, id));
        return (id, sp, seat);
    }

    [Fact]
    public void TheAvatarStandingReleasesTheControlsAndTakeControlsAndCameraInEveryPrim()
    {
        using var h = new SchedulerHarness();
        var (id, sp, _) = SatOnAChild(h);
        sp.StandUp();
        Assert.False(Holds(sp, id));
        Assert.True(PumpUntil(h, () => Item(h.Prim, id).PermsMask == TRIGGER_ANIMATION),
            "after the stand the root script has mask " + Item(h.Prim, id).PermsMask);
        Assert.Equal(sp.UUID, Item(h.Prim, id).PermsGranter);
        Assert.False(HasControlRecord(h, id));
        // SL's example stops its own animation on the stand; neither SL nor Halcyon stop it for the script
        Assert.True(sp.Animator.HasAnimation(Anim));
        Command(h, "stopanim", "stopped");
        Assert.False(sp.Animator.HasAnimation(Anim));   // TRIGGER_ANIMATION was kept, so the script still can
    }

    [Fact]
    public void ReleaseKeysRevokesTakeControlsAndCamera()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        var client = a.ControllingClient;
        var evt = client.GetType().GetField("OnForceReleaseControls", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((ForceReleaseControls)evt.GetValue(client)!).Invoke(client, a.UUID);
        Assert.False(Holds(a, id));
        Assert.True(PumpUntil(h, () => Item(h.Prim, id).PermsMask == TRIGGER_ANIMATION));
        Assert.False(HasControlRecord(h, id));
        Command(h, "perms", "now");
        Assert.Equal("now perms=" + TRIGGER_ANIMATION + " key=" + a.UUID, Last(h, "now"));
    }

    [Fact]
    public void TheCoreDetachStripDropsTheControlRecordToo()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        // AttachmentsModule on a drop and a detach: so.RemoveScriptsPermissions(4 | 2048)
        h.Prim.ParentGroup.RemoveScriptsPermissions(TAKE_CONTROLS | CONTROL_CAMERA);
        Assert.False(Holds(a, id));
        Assert.True(PumpUntil(h, () => !HasControlRecord(h, id)), "a dropped object would take the controls again on a restart");
        Assert.Equal(TRIGGER_ANIMATION, Item(h.Prim, id).PermsMask);
    }

    // ── unload ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnloadedScriptReleasesItsControls()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        h.Scene.EventManager.TriggerRemoveScript(h.Prim.LocalId, id);
        Assert.True(PumpUntil(h, () => !Holds(a, id)), "the avatar still has controls taken by a removed script");
    }

    private static LSLSystemAPI ApiOf(SchedulerHarness h, UUID item)
    {
        var exe = (PhloxExecutionScheduler)Field(h.Engine, "m_ExeScheduler");
        return ((Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item];
    }

    private static object Field(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o);

    /// <summary>
    /// A derez queues the engine's unload (Scene.DeleteSceneObject -> RemoveScriptInstances) and then disposes the
    /// object (SceneObjectGroup.Dispose), which takes each part's inventory away. The engine's unload can run after
    /// that, so the unload hook cannot rely on reading the script's item from the part.
    /// </summary>
    private static void DisposeLikeADerez(SchedulerHarness h)
    {
        h.Prim.ParentGroup.Dispose();
        Assert.Null(h.Prim.Inventory);
    }

    [Fact]
    public void TheUnloadHookReleasesControlsWhenThePartInventoryIsAlreadyGone()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        var api = ApiOf(h, id);
        DisposeLikeADerez(h);

        var thrown = Record.Exception(() => api.OnScriptUnloaded(ScriptUnloadReason.Unloaded, RuntimeState.LocalDisableFlag.None));

        Assert.Null(thrown);
        Assert.False(Holds(a, id), "the avatar still has controls taken by a script whose object was deleted");
    }

    [Fact]
    public void TheUnloadHookIsQuietWhenTheGrantingAvatarHasAlreadyLeft()
    {
        using var h = new SchedulerHarness();
        var id = Armed(h, out var a, out _);
        var api = ApiOf(h, id);
        h.Scene.CloseAgent(a.UUID, false);
        Assert.Null(h.Scene.GetScenePresence(a.UUID));
        DisposeLikeADerez(h);

        var thrown = Record.Exception(() => api.OnScriptUnloaded(ScriptUnloadReason.Unloaded, RuntimeState.LocalDisableFlag.None));

        Assert.Null(thrown);
    }
}
