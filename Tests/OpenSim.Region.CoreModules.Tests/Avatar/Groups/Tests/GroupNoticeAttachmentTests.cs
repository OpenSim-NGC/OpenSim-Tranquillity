/*
 * Copyright (c) 2026 Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using OpenSim.Services.Interfaces;

using V2Groups = OpenSim.Groups;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// A notice's attachment: the group keeps its own copy when the notice is sent, so a member who opens the notice
/// after the sender deleted theirs still gets it; and a notice carries only an item of the sender's own.
/// </summary>
public class GroupNoticeAttachmentTests : OpenSimTestCase
{
    private static readonly UUID GroupId = new("3c9e1a7f-5b2d-4e60-8f14-a2d6c8b0e413");
    private const ulong Powers = (ulong)(GroupPowers.SendNotices | GroupPowers.ReceiveNotices);

    private sealed class Notices
    {
        public UUID NoticeID, ItemID;
        public string OwnerID;
        public string Name;
        public int Added;
    }

    private static (TestScene Scene, V2Groups.GroupsModule Groups, Notices Kept) Setup()
    {
        var kept = new Notices();
        TestScene scene = new SceneHelpers().SetupScene();
        scene.RegisterModuleInterface<IMessageTransferModule>(GroupInviteEjectResultTests.Stand<IMessageTransferModule>.Create((_, _) => null));
        scene.RegisterModuleInterface<IUserManagement>(GroupInviteEjectResultTests.Stand<IUserManagement>.Create((_, _) => null));
        scene.RegisterModuleInterface<V2Groups.IGroupsServicesConnector>(
            GroupInviteEjectResultTests.Stand<V2Groups.IGroupsServicesConnector>.Create((name, a) =>
            {
                switch (name)
                {
                    case "GetAgentGroupMembership":
                        return new V2Groups.ExtendedGroupMembershipData { GroupID = GroupId, GroupPowers = Powers };
                    case "AddGroupNotice":
                        // (RequestingAgentID, groupID, noticeID, fromName, subject, message, hasAttachment, attType, attName, attItemID, attOwnerID)
                        kept.NoticeID = (UUID)a[2];
                        kept.Name = (string)a[8];
                        kept.ItemID = (UUID)a[9];
                        kept.OwnerID = (string)a[10];
                        kept.Added++;
                        return true;
                    case "GetGroupMembers":
                        return new List<GroupMembersData>();
                    case "GetGroupNotice":
                        var info = new V2Groups.GroupNoticeInfo { GroupID = GroupId, Message = "body" };
                        info.noticeData.HasAttachment = true;
                        info.noticeData.AttachmentItemID = kept.ItemID;
                        info.noticeData.AttachmentOwnerID = kept.OwnerID;
                        info.noticeData.AttachmentName = kept.Name;
                        return info;
                    default:
                        return null;
                }
            }));
        IConfigSource source = new IniConfigSource();
        IConfig config = source.AddConfig("Groups");
        config.Set("Enabled", true);
        config.Set("Module", "Groups Module V2");
        var gm = new V2Groups.GroupsModule();
        SceneHelpers.SetupSceneModules(scene, source, gm);
        return (scene, gm, kept);
    }

    private static void Send(V2Groups.GroupsModule gm, IClientAPI client, GridInstantMessage im)
        => typeof(V2Groups.GroupsModule).GetMethod("OnInstantMessage", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(gm, new object[] { client, im });

    /// <summary>A notice as the viewer sends it, its attachment named in the bucket's LLSD.</summary>
    private static GridInstantMessage Notice(UUID from, UUID itemID, UUID ownerID)
    {
        OSDMap bucket = new OSDMap { ["item_id"] = OSD.FromUUID(itemID), ["owner_id"] = OSD.FromUUID(ownerID) };
        string llsd = "<? LLSD/XML ?>\n" + OSDParser.SerializeLLSDXmlString(bucket);
        return new GridInstantMessage
        {
            fromAgentID = from.Guid, toAgentID = GroupId.Guid, fromAgentName = "Sender",
            dialog = (byte)InstantMessageDialog.GroupNotice, message = "Subject|body",
            binaryBucket = Utils.StringToBytes(llsd),
        };
    }

    [Fact]
    public void TheAttachmentOutlivesTheSendersItem()
    {
        var (scene, gm, kept) = Setup();
        UserAccount sender = UserAccountHelpers.CreateUserWithInventory(scene, UUID.Random());
        UserAccount member = UserAccountHelpers.CreateUserWithInventory(scene, UUID.Random());
        ScenePresence senderSp = SceneHelpers.AddScenePresence(scene, sender.PrincipalID);
        ScenePresence memberSp = SceneHelpers.AddScenePresence(scene, member.PrincipalID);
        InventoryItemBase item = UserInventoryHelpers.CreateInventoryItem(scene, "Welcome notecard", sender.PrincipalID);

        Send(gm, senderSp.ControllingClient, Notice(sender.PrincipalID, item.ID, sender.PrincipalID));

        // the notice carries the group's copy, not the sender's item
        Assert.Equal(1, kept.Added);
        Assert.Equal(GroupId.ToString(), kept.OwnerID);
        Assert.NotEqual(item.ID, kept.ItemID);
        InventoryItemBase held = scene.InventoryService.GetItem(GroupId, kept.ItemID);
        Assert.NotNull(held);
        Assert.Equal(item.AssetID, held.AssetID);
        Assert.Equal("Welcome notecard", held.Name);

        // the sender deletes theirs; a member opens the notice's attachment and gets it
        Assert.True(scene.InventoryService.DeleteItems(sender.PrincipalID, new List<UUID> { item.ID }));
        Send(gm, memberSp.ControllingClient, new GridInstantMessage
        {
            fromAgentID = member.PrincipalID.Guid, toAgentID = GroupId.Guid, imSessionID = kept.NoticeID.Guid,
            dialog = (byte)InstantMessageDialog.GroupNoticeInventoryAccepted, binaryBucket = new byte[16],
        });

        InventoryFolderBase folder = scene.InventoryService.GetFolderForType(member.PrincipalID, FolderType.Notecard);
        List<InventoryItemBase> got = scene.InventoryService.GetFolderItems(member.PrincipalID, folder.ID);
        Assert.Contains(got, i => i.AssetID == item.AssetID && i.Name == "Welcome notecard");
        // the group still has its copy for the next member
        Assert.NotNull(scene.InventoryService.GetItem(GroupId, kept.ItemID));
    }

    [Fact]
    public void ANoticeCarriesOnlyTheSendersOwnItem()
    {
        var (scene, gm, kept) = Setup();
        UserAccount sender = UserAccountHelpers.CreateUserWithInventory(scene, UUID.Random());
        UserAccount other = UserAccountHelpers.CreateUserWithInventory(scene, UUID.Random());
        ScenePresence senderSp = SceneHelpers.AddScenePresence(scene, sender.PrincipalID);
        InventoryItemBase theirs = UserInventoryHelpers.CreateInventoryItem(scene, "Not yours", other.PrincipalID);

        Send(gm, senderSp.ControllingClient, Notice(sender.PrincipalID, theirs.ID, other.PrincipalID));

        Assert.Equal(0, kept.Added);
    }
}
