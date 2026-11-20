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
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

using XmlRpcGroups = OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups;
using V2Groups = OpenSim.Groups;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// A member ejected through another region: that region sends the member's region an eject notice (dialog 210, the
/// group as its session), and the member's viewer is told to drop that group. It used to be told to drop the nil
/// group, the session being cleared before it was read, so the group stayed in the member's list.
/// </summary>
public class GroupEjectElsewhereTests : OpenSimTestCase
{
    private static readonly UUID GroupId = new("2b6e4d1a-9c3f-4e70-8a15-c0d7e9f31a52");
    private static readonly UUID EjectorId = new("8d2f6a1c-3e4b-4c97-b0a8-1f5e7c9d2b46");
    private static readonly UUID EjecteeId = new("e5c3a9f1-7b2d-4a68-9e10-4d8b2c6f7a39");

    public static IEnumerable<object[]> Modules() => new[] { new object[] { "XmlRpc" }, new object[] { "V2" } };

    private static IConfigSource GroupsConfig(string module)
    {
        IConfigSource source = new IniConfigSource();
        IConfig config = source.AddConfig("Groups");
        config.Set("Enabled", true);
        config.Set("Module", module);
        return source;
    }

    private static object Module(string which, TestScene scene)
    {
        scene.RegisterModuleInterface<IMessageTransferModule>(GroupInviteEjectResultTests.Stand<IMessageTransferModule>.Create((_, _) => null));
        scene.RegisterModuleInterface<IUserManagement>(GroupInviteEjectResultTests.Stand<IUserManagement>.Create((_, _) => null));
        if (which == "XmlRpc")
        {
            scene.RegisterModuleInterface<XmlRpcGroups.IGroupsServicesConnector>(
                GroupInviteEjectResultTests.Stand<XmlRpcGroups.IGroupsServicesConnector>.Create((name, _) => name switch
                {
                    "GetAgentGroupMemberships" => new List<GroupMembershipData>(),
                    _ => null
                }));
            var gm = new XmlRpcGroups.GroupsModule();
            SceneHelpers.SetupSceneModules(scene, GroupsConfig("GroupsModule"), gm);
            return gm;
        }
        scene.RegisterModuleInterface<V2Groups.IGroupsServicesConnector>(
            GroupInviteEjectResultTests.Stand<V2Groups.IGroupsServicesConnector>.Create((name, _) => name switch
            {
                "GetAgentGroupMemberships" => new List<GroupMembershipData>(),
                _ => null
            }));
        var v2 = new V2Groups.GroupsModule();
        SceneHelpers.SetupSceneModules(scene, GroupsConfig("Groups Module V2"), v2);
        return v2;
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AMemberEjectedElsewhereIsToldToDropThatGroup(string which)
    {
        TestScene scene = new SceneHelpers().SetupScene();
        object gm = Module(which, scene);
        ScenePresence ejectee = SceneHelpers.AddScenePresence(scene, EjecteeId);
        var dropped = new List<UUID>();
        ((TestClient)ejectee.ControllingClient).OnReceivedAgentDropGroup += g => dropped.Add(g);

        // as EjectGroupMember sends it from the ejector's region when the ejectee is not there
        var notice = new GridInstantMessage
        {
            imSessionID = GroupId.Guid, dialog = 210, fromAgentID = EjectorId.Guid, toAgentID = EjecteeId.Guid,
            fromAgentName = "Ejector", message = "You have been ejected from 'Example Group' by Ejector.",
            binaryBucket = Array.Empty<byte>(),
        };
        gm.GetType().GetMethod("OnGridInstantMessage", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(gm, new object[] { notice });

        Assert.Equal(new[] { GroupId }, dropped);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AnEjectNoticeGoesToTheRegionTheMemberIsIn(string which)
    {
        // the member is not here as a root agent (a child agent of a neighbour region, perhaps): the notice goes
        // through the transfer module to root agents only, so it reaches the groups module of the member's region
        TestScene scene = new SceneHelpers().SetupScene();
        var sent = new List<(byte Dialog, bool RootOnly)>();
        scene.RegisterModuleInterface<IMessageTransferModule>(GroupInviteEjectResultTests.Stand<IMessageTransferModule>.Create((name, a) =>
        {
            if (name == "SendInstantMessage" && a[0] is GridInstantMessage im)
                lock (sent)
                    sent.Add((im.dialog, a.Length > 2 && a[2] is bool b && b));
            return null;
        }));
        scene.RegisterModuleInterface<IUserManagement>(GroupInviteEjectResultTests.Stand<IUserManagement>.Create((_, _) => null));
        bool removed = false;
        object gm;
        if (which == "XmlRpc")
        {
            scene.RegisterModuleInterface<XmlRpcGroups.IGroupsServicesConnector>(
                GroupInviteEjectResultTests.Stand<XmlRpcGroups.IGroupsServicesConnector>.Create((name, _) => name switch
                {
                    "RemoveAgentFromGroup" => Removed(ref removed),
                    "GetAgentGroupMembership" => null,
                    "GetGroupRecord" => new GroupRecord { GroupID = GroupId, GroupName = "Example Group" },
                    _ => null
                }));
            var x = new XmlRpcGroups.GroupsModule();
            SceneHelpers.SetupSceneModules(scene, GroupsConfig("GroupsModule"), x);
            gm = x;
        }
        else
        {
            scene.RegisterModuleInterface<V2Groups.IGroupsServicesConnector>(
                GroupInviteEjectResultTests.Stand<V2Groups.IGroupsServicesConnector>.Create((name, _) => name switch
                {
                    "RemoveAgentFromGroup" => Removed(ref removed),
                    "GetAgentGroupMembership" => null,
                    "GetGroupRecord" => new V2Groups.ExtendedGroupRecord { GroupID = GroupId, GroupName = "Example Group" },
                    _ => null
                }));
            var v2 = new V2Groups.GroupsModule();
            SceneHelpers.SetupSceneModules(scene, GroupsConfig("Groups Module V2"), v2);
            gm = v2;
        }

        ((OpenSim.Region.Framework.Interfaces.IGroupsModule)gm).EjectGroupMember(null, EjectorId, GroupId, EjecteeId);

        lock (sent)
            Assert.Contains(sent, s => s.Dialog == 210 && s.RootOnly);
    }

    private static object Removed(ref bool removed)
    {
        removed = true;
        return null;
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void AViewerCannotSendAnEjectNotice(string which)
    {
        // a viewer's own IM with dialog 210, to an avatar in the region: nothing is dropped
        TestScene scene = new SceneHelpers().SetupScene();
        object gm = Module(which, scene);
        ScenePresence sender = SceneHelpers.AddScenePresence(scene, EjectorId);
        ScenePresence target = SceneHelpers.AddScenePresence(scene, EjecteeId);
        var dropped = new List<UUID>();
        ((TestClient)target.ControllingClient).OnReceivedAgentDropGroup += g => dropped.Add(g);

        var forged = new GridInstantMessage
        {
            imSessionID = GroupId.Guid, dialog = 210, fromAgentID = EjectorId.Guid, toAgentID = EjecteeId.Guid,
            fromAgentName = "Someone", message = "You have been ejected", binaryBucket = Array.Empty<byte>(),
        };
        gm.GetType().GetMethod("OnInstantMessage", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(gm, new object[] { sender.ControllingClient, forged });

        Assert.Empty(dropped);
    }
}
