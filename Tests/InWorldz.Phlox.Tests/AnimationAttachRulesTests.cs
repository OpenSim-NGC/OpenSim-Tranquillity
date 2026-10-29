/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Animation;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Which animations a script may start, and the attach, detach and unsit rules.
/// <para>Starting an animation (SL wiki llStartAnimation): anim is "an item in the inventory of the prim this script is
/// in or built-in animation", and "If anim is missing from the prim's inventory then an error is shouted on
/// DEBUG_CHANNEL". Halcyon and YEngine both say "Do NOT try to parse UUID, animations cannot be triggered by ID"
/// (YEngine LSL_Api.llStartAnimation). The stop forms keep accepting a key, as in YEngine.</para>
/// <para>llAttachToAvatar appends (SL: "Attach points can be occupied by multiple attachments"; "If the object is
/// already attached the function fails silently"). llDetachFromAvatar needs the owner's PERMISSION_ATTACH (Halcyon
/// llDetachFromAvatar). llUnSit has no owner-stands-self case (SL, Halcyon, YEngine).</para>
/// </summary>
// No test reaches a network service. No process-wide state: the class runs in parallel.
public class AnimationAttachRulesTests
{
    private const int TRIGGER_ANIMATION = 0x10, ATTACH = 0x20;
    private const int LINK_SET = -1, LINK_ALL_OTHERS = -2, LINK_ROOT = 1;

    private static UUID Builtin(string name) => DefaultAvatarAnimations.GetDefaultAnimation(name);

    /// <summary>A rig with an avatar who granted PERMISSION_TRIGGER_ANIMATION.</summary>
    private static (ApiCallRig R, ScenePresence Sp) Animating(Action<IConfigSource> configure = null)
    {
        var r = new ApiCallRig(configure);
        ScenePresence sp = r.AddAvatar();
        r.Grant(sp.UUID, TRIGGER_ANIMATION);
        return (r, sp);
    }

    private static UUID AddAnimation(SceneObjectPart part, string name)
    {
        var item = new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = UUID.Random(), Name = name,
            Type = (int)AssetType.Animation, InvType = (int)InventoryType.Animation,
        };
        part.Inventory.AddInventoryItem(item, false);
        return item.AssetID;
    }

    // ---------------------------------------------------------------- llStartAnimation / llStopAnimation

    [Fact]
    public void ABuiltInAnimationNameStarts()
    {
        var (r, sp) = Animating();
        using (r)
        {
            Assert.NotEqual(UUID.Zero, Builtin("dance1"));
            r.Api.llStartAnimation("dance1");
            Assert.True(sp.Animator.HasAnimation(Builtin("dance1")));
            Assert.Empty(r.Errors);
        }
    }

    [Fact]
    public void ABuiltInAnimationNameStops()
    {
        var (r, sp) = Animating();
        using (r)
        {
            sp.Animator.AddAnimation(Builtin("dance1"), UUID.Zero);
            r.Api.llStopAnimation("dance1");
            Assert.False(sp.Animator.HasAnimation(Builtin("dance1")));
        }
    }

    [Fact]
    public void AnInventoryAnimationStartsAndStopsByName()
    {
        var (r, sp) = Animating();
        using (r)
        {
            UUID asset = AddAnimation(r.H.Prim, "wave here");
            r.Api.llStartAnimation("wave here");
            Assert.True(sp.Animator.HasAnimation(asset));
            r.Api.llStopAnimation("wave here");
            Assert.False(sp.Animator.HasAnimation(asset));
        }
    }

    [Fact]
    public void AnAnimationKeyDoesNotStartAndShoutsAnError()
    {
        var (r, sp) = Animating();
        using (r)
        {
            UUID someoneElses = UUID.Random();
            r.Api.llStartAnimation(someoneElses.ToString());
            Assert.False(sp.Animator.HasAnimation(someoneElses));
            Assert.Contains(r.Errors, e => e.Contains("Could not find animation '" + someoneElses + "'"));
        }
    }

    [Fact]
    public void AnUnknownNameShoutsAnError()
    {
        var (r, _) = Animating();
        using (r)
        {
            r.Api.llStartAnimation("no such dance");
            Assert.Contains(r.Errors, e => e.Contains("Could not find animation 'no such dance'"));
        }
    }

    [Fact]
    public void TheStopFormStillTakesAKey()
    {
        var (r, sp) = Animating();
        using (r)
        {
            UUID anim = UUID.Random();
            sp.Animator.AddAnimation(anim, UUID.Zero);
            r.Api.llStopAnimation(anim.ToString());
            Assert.False(sp.Animator.HasAnimation(anim));
        }
    }

    // ---------------------------------------------------------------- iwStart/StopLinkAnimation (Halcyon :4140-4146)

    [Fact]
    public void ALinkAnimationReadsOnePrimByNumber()
    {
        var (r, sp) = Animating();
        using (r)
        {
            UUID asset = AddAnimation(r.H.Prim, "sway");
            r.Api.iwStartLinkAnimation(LINK_ROOT, "sway");
            Assert.True(sp.Animator.HasAnimation(asset));
            r.Api.iwStopLinkAnimation(LINK_ROOT, "sway");
            Assert.False(sp.Animator.HasAnimation(asset));
        }
    }

    [Theory]
    [InlineData(LINK_SET)]
    [InlineData(LINK_ALL_OTHERS)]
    public void ANegativeLinkNumberStartsNothing(int link)
    {
        var (r, sp) = Animating();
        using (r)
        {
            UUID asset = AddAnimation(r.H.Prim, "sway");
            r.Api.iwStartLinkAnimation(link, "sway");
            Assert.False(sp.Animator.HasAnimation(asset));
        }
    }

    [Fact]
    public void ALinkAnimationByKeyDoesNotStartButABuiltInNameDoes()
    {
        var (r, sp) = Animating();
        using (r)
        {
            UUID key = UUID.Random();
            r.Api.iwStartLinkAnimation(LINK_ROOT, key.ToString());
            Assert.False(sp.Animator.HasAnimation(key));
            r.Api.iwStartLinkAnimation(LINK_ROOT, "dance2");
            Assert.True(sp.Animator.HasAnimation(Builtin("dance2")));
        }
    }

    // ---------------------------------------------------------------- osAvatarPlayAnimation, botStartAnimation

    [Fact]
    public void OsAvatarPlayAnimationRefusesAKey()
    {
        var (r, sp) = Animating(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "VeryHigh"));
        using (r)
        {
            UUID key = UUID.Random();
            r.Api.osAvatarPlayAnimation(sp.UUID.ToString(), key.ToString());
            Assert.False(sp.Animator.HasAnimation(key));
            Assert.Contains(r.Errors, e => e.Contains("Could not find animation"));
            r.Api.osAvatarPlayAnimation(sp.UUID.ToString(), "dance3");
            Assert.True(sp.Animator.HasAnimation(Builtin("dance3")));
        }
    }

    [Fact]
    public void BotStartAnimationRefusesAKeyAndResolvesABuiltInName()
    {
        using var r = new ApiCallRig();
        var started = new System.Collections.Concurrent.ConcurrentQueue<UUID>();
        r.H.Scene.RegisterModuleInterface<IBotManager>(Fake<IBotManager>.Create((m, a) =>
        {
            if (m.Name == nameof(IBotManager.StartBotAnimation)) started.Enqueue((UUID)a[1]);
            return null;
        }));
        UUID bot = UUID.Random(), key = UUID.Random();

        r.Api.botStartAnimation(bot.ToString(), key.ToString());
        Assert.Empty(started);
        Assert.Contains(r.Errors, e => e.Contains("Could not find animation"));

        r.Api.botStartAnimation(bot.ToString(), "dance4");
        Assert.Equal(new[] { Builtin("dance4") }, started.ToArray());
    }

    // ---------------------------------------------------------------- implicit TRIGGER_ANIMATION for the owner's bots

    private static ScenePresence Npc(ApiCallRig r, UUID owner)
    {
        ScenePresence npc = r.AddAvatar();
        ApiCallRig.SetPrivate(npc, nameof(ScenePresence.PresenceType), PresenceType.Npc);
        Assert.True(npc.IsNPC);
        UUID id = npc.UUID;
        r.H.Scene.RegisterModuleInterface<INPCModule>(Fake<INPCModule>.Create((m, a) =>
            m.Name == nameof(INPCModule.GetOwner) && (UUID)a[0] == id ? owner : (object)null));
        return npc;
    }

    private static string AskAnimation(SchedulerHarness h, UUID who)
    {
        h.RezScript("default { state_entry() { llRequestPermissions(\"" + who + "\", PERMISSION_TRIGGER_ANIMATION); }"
            + " run_time_permissions(integer p) { llSay(0, \"perms=\" + (string)p); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("perms=")), TimeSpan.FromSeconds(10));
        return h.Said.FirstOrDefault(s => s.StartsWith("perms="));
    }

    [Fact]
    public void TheOwnersBotGrantsAnimationWithoutAsking()
    {
        using var r = new ApiCallRig();
        ScenePresence npc = Npc(r, r.H.Prim.OwnerID);
        Assert.Equal("perms=16", AskAnimation(r.H, npc.UUID));
    }

    [Fact]
    public void AnotherOwnersBotIsAskedAndDoesNotAnswer()
    {
        using var r = new ApiCallRig();
        ScenePresence npc = Npc(r, UUID.Random());
        Assert.Null(AskAnimation(r.H, npc.UUID));
    }

    // ---------------------------------------------------------------- llDetachFromAvatar, llAttachToAvatar

    private static RecordingAttachments Attachments(ApiCallRig r)
    {
        r.H.Scene.RegisterModuleInterface(RecordingAttachments.Create(out var rec));
        return rec;
    }

    private static ScenePresence Wear(ApiCallRig r)
    {
        ScenePresence owner = r.AddAvatar(r.H.Prim.OwnerID);
        SceneObjectGroup g = r.H.Prim.ParentGroup;
        g.AttachedAvatar = owner.UUID;
        g.IsAttachment = true;
        return owner;
    }

    [Fact]
    public void DetachNeedsTheOwnersPermission()
    {
        using var r = new ApiCallRig();
        var rec = Attachments(r);
        Wear(r);
        r.Grant(UUID.Random(), ATTACH);   // granted by someone else
        r.Api.llDetachFromAvatar();
        Assert.DoesNotContain(nameof(IAttachmentsModule.DetachSingleAttachmentToInv), rec.Calls);
    }

    [Fact]
    public void DetachWithTheOwnersPermissionDetaches()
    {
        using var r = new ApiCallRig();
        var rec = Attachments(r);
        ScenePresence owner = Wear(r);
        r.Grant(owner.UUID, ATTACH);
        r.Api.llDetachFromAvatar();
        Assert.Contains(nameof(IAttachmentsModule.DetachSingleAttachmentToInv), rec.Calls);
    }

    [Fact]
    public void AttachAppendsToThePoint()
    {
        using var r = new ApiCallRig();
        var rec = Attachments(r);
        ScenePresence owner = r.AddAvatar(r.H.Prim.OwnerID);
        r.Grant(owner.UUID, ATTACH);
        r.Api.llAttachToAvatar(2);
        var call = rec.Invocations.Single(c => c.Name == nameof(IAttachmentsModule.AttachObject));
        Assert.Equal(true, call.Args[5]);   // append
    }

    [Fact]
    public void AnObjectAlreadyAttachedIsNotAttachedAgain()
    {
        using var r = new ApiCallRig();
        var rec = Attachments(r);
        ScenePresence owner = Wear(r);
        r.Grant(owner.UUID, ATTACH);
        r.Api.llAttachToAvatar(2);
        Assert.DoesNotContain(nameof(IAttachmentsModule.AttachObject), rec.Calls);
    }

    [Fact]
    public void OsForceAttachAppendsAndSkipsAnAttachedObject()
    {
        using var r = new ApiCallRig(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "High"));
        var rec = Attachments(r);
        r.AddAvatar(r.H.Prim.OwnerID);
        r.Api.osForceAttachToAvatar(2);
        Assert.Equal(true, rec.Invocations.Single(c => c.Name == nameof(IAttachmentsModule.AttachObject)).Args[5]);

        r.H.Prim.ParentGroup.IsAttachment = true;
        r.Api.osForceAttachToAvatar(2);
        Assert.Single(rec.Invocations, c => c.Name == nameof(IAttachmentsModule.AttachObject));
    }

    // ---------------------------------------------------------------- llUnSit

    [Fact]
    public void TheOwnerSeatedOnSomeoneElsesObjectIsNotStoodUp()
    {
        using var r = new ApiCallRig();
        ScenePresence owner = r.AddAvatar(r.H.Prim.OwnerID);
        SceneObjectGroup seat = OpenSim.Tests.Common.SceneHelpers.AddSceneObject(r.H.Scene, 1, UUID.Random(), "seat", 0x10);
        owner.ParentID = seat.RootPart.LocalId;
        owner.ParentPart = seat.RootPart;
        // On land the object's owner has no powers over, so only the removed owner case could stand them up.
        var land = r.H.Scene.LandChannel.GetLandObject(owner.AbsolutePosition.X, owner.AbsolutePosition.Y);
        land.LandData.OwnerID = UUID.Random();
        land.LandData.IsGroupOwned = false;
        r.H.Scene.Permissions.OnIsAdministrator += _ => false;   // the test scene has no permissions module: no one is a god
        r.Api.llUnSit(owner.UUID.ToString());
        Assert.Equal(seat.RootPart.LocalId, owner.ParentID);
    }
}
