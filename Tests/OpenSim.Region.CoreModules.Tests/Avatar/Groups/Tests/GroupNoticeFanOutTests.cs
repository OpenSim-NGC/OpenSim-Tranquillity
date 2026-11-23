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

using V2Groups = OpenSim.Groups;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// A notice sent through Groups Module V2: a member in the region gets it at once, the members elsewhere through the
/// transfer module (by way of the notice sender), and the members who turned notices off, or whose roles do not give
/// "Receive notices", get none.
/// </summary>
public class GroupNoticeFanOutTests : OpenSimTestCase
{
    private static readonly UUID GroupId = new("7a1d9c2e-4b3f-4e86-a5c1-0d2b84e6f019");
    private static readonly UUID SenderId = new("c3e1f7a2-9d48-4b07-8e5a-55f1d9027b61");
    private const ulong Powers = (ulong)(GroupPowers.SendNotices | GroupPowers.ReceiveNotices);

    [Fact]
    public void ANoticeReachesTheRegionAtOnceAndTheRestThroughTheTransferModule()
    {
        var elsewhere = Enumerable.Range(0, 20).Select(_ => UUID.Random()).ToList();
        UUID declined = UUID.Random(), noPower = UUID.Random();
        var members = new List<GroupMembersData>
        {
            new() { AgentID = SenderId, AcceptNotices = true, AgentPowers = Powers },
            new() { AgentID = declined, AcceptNotices = false, AgentPowers = Powers },
            new() { AgentID = noPower, AcceptNotices = true, AgentPowers = (ulong)GroupPowers.JoinChat },
        };
        members.AddRange(elsewhere.Select(id => new GroupMembersData { AgentID = id, AcceptNotices = true, AgentPowers = Powers }));

        var transferred = new System.Collections.Concurrent.ConcurrentBag<UUID>();
        TestScene scene = new SceneHelpers().SetupScene();
        scene.RegisterModuleInterface<IMessageTransferModule>(GroupInviteEjectResultTests.Stand<IMessageTransferModule>.Create((name, a) =>
        {
            if (name == "SendInstantMessage" && a[0] is GridInstantMessage im)
            {
                transferred.Add(new UUID(im.toAgentID));
                (a[1] as MessageResultNotification)?.Invoke(true);
            }
            return null;
        }));
        scene.RegisterModuleInterface<IUserManagement>(GroupInviteEjectResultTests.Stand<IUserManagement>.Create((_, _) => null));
        scene.RegisterModuleInterface<V2Groups.IGroupsServicesConnector>(
            GroupInviteEjectResultTests.Stand<V2Groups.IGroupsServicesConnector>.Create((name, a) => name switch
            {
                "GetAgentGroupMembership" => new V2Groups.ExtendedGroupMembershipData { GroupID = GroupId, GroupPowers = Powers },
                "AddGroupNotice" => true,
                "GetGroupMembers" => members,
                "GetGroupNotice" => new V2Groups.GroupNoticeInfo { GroupID = GroupId, Message = "body" },
                _ => null
            }));

        IConfigSource source = new IniConfigSource();
        IConfig config = source.AddConfig("Groups");
        config.Set("Enabled", true);
        config.Set("Module", "Groups Module V2");
        var gm = new V2Groups.GroupsModule();
        SceneHelpers.SetupSceneModules(scene, source, gm);

        ScenePresence sender = SceneHelpers.AddScenePresence(scene, SenderId);
        var received = new List<GridInstantMessage>();
        ((TestClient)sender.ControllingClient).OnReceivedInstantMessage += im => { lock (received) received.Add(im); };

        var notice = new GridInstantMessage
        {
            fromAgentID = SenderId.Guid, toAgentID = GroupId.Guid, fromAgentName = "Sender",
            dialog = (byte)InstantMessageDialog.GroupNotice, message = "Subject|body", binaryBucket = new byte[] { 0 },
        };
        typeof(V2Groups.GroupsModule).GetMethod("OnInstantMessage", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(gm, new object[] { sender.ControllingClient, notice });

        lock (received)
            Assert.Single(received, im => im.dialog == (byte)InstantMessageDialog.GroupNotice);

        var t = System.Diagnostics.Stopwatch.StartNew();
        while (transferred.Count < elsewhere.Count && t.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(20);
        Assert.Equal(elsewhere.OrderBy(u => u), transferred.OrderBy(u => u));
        Assert.DoesNotContain(SenderId, transferred);
        Assert.DoesNotContain(declined, transferred);
        Assert.DoesNotContain(noPower, transferred);

        gm.Close();
    }
}
