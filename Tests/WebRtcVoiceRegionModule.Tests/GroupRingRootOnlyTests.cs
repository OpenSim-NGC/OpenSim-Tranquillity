/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A group ring reaches a member through the process that holds their ROOT presence.
 *
 * Driven through the module's own ring fan-out with one scene standing for the calling process. A
 * member who is only a child presence there (a viewer next door) must not be rung locally: the ring
 * goes through the signed carrier to the process that holds their root, so that process records the
 * call before the member's accept arrives.
 */
using System.Collections.Generic;
using System.Linq;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CrossProcessHarness;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class GroupRingRootOnlyTests
    {
        private const string Grid = "example-grid";
        private const string Secret = "0123456789abcdef0123456789abcdef-example";
        private static readonly UUID Group = new UUID("99999999-9999-9999-9999-999999999999");

        /// <summary>The groups service: everyone listed is a member with the powers a group call needs.</summary>
        private sealed class FakeGroups : IGroupsModule
        {
            private readonly UUID[] _members;
            public FakeGroups(params UUID[] members) { _members = members; }
            public event NewGroupNotice OnNewGroupNotice { add { } remove { } }

            /// <summary>How many times the roster was asked for, and whether it is unavailable.</summary>
            public int RosterCalls;
            public bool NoRoster;

            public List<GroupMembersData> GroupMembersRequest(IClientAPI remoteClient, UUID groupID)
            {
                RosterCalls++;
                return NoRoster ? null : Roster();
            }

            private List<GroupMembersData> Roster()
                => _members.Select(m => new GroupMembersData
                {
                    AgentID = m,
                    AgentPowers = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice,
                }).ToList();

            public UUID CreateGroup(IClientAPI remoteClient, string name, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish) => throw new System.NotSupportedException();
            public GroupRecord GetGroupRecord(string name) => null;
            public GroupRecord GetGroupRecord(UUID GroupID) => null;
            public void ActivateGroup(IClientAPI remoteClient, UUID groupID) { }
            public List<GroupTitlesData> GroupTitlesRequest(IClientAPI remoteClient, UUID groupID) => null;
            public List<GroupRolesData> GroupRoleDataRequest(IClientAPI remoteClient, UUID groupID) => null;
            public List<GroupRoleMembersData> GroupRoleMembersRequest(IClientAPI remoteClient, UUID groupID) => null;
            public GroupProfileData GroupProfileRequest(IClientAPI remoteClient, UUID groupID) => default;
            public GroupMembershipData[] GetMembershipData(UUID UserID) => null;
            public GroupMembershipData GetMembershipData(UUID GroupID, UUID UserID) => null;
            public GroupMembershipData GetActiveMembershipData(UUID UserID) => null;
            public void UpdateGroupInfo(IClientAPI remoteClient, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish) { }
            public void SetGroupAcceptNotices(IClientAPI remoteClient, UUID groupID, bool acceptNotices, bool listInProfile) { }
            public void GroupTitleUpdate(IClientAPI remoteClient, UUID GroupID, UUID TitleRoleID) { }
            public GroupNoticeData[] GroupNoticesListRequest(IClientAPI remoteClient, UUID GroupID) => null;
            public string GetGroupTitle(UUID avatarID) => string.Empty;
            public void GroupRoleUpdate(IClientAPI remoteClient, UUID GroupID, UUID RoleID, string name, string description, string title, ulong powers, byte updateType) { }
            public void GroupRoleChanges(IClientAPI remoteClient, UUID GroupID, UUID RoleID, UUID MemberID, uint changes) { }
            public void GroupNoticeRequest(IClientAPI remoteClient, UUID groupNoticeID) { }
            public GridInstantMessage CreateGroupNoticeIM(UUID agentID, UUID groupNoticeID, byte dialog) => null;
            public void SendAgentGroupDataUpdate(IClientAPI remoteClient) { }
            public void JoinGroupRequest(IClientAPI remoteClient, UUID GroupID) { }
            public void LeaveGroupRequest(IClientAPI remoteClient, UUID GroupID) { }
            public void EjectGroupMemberRequest(IClientAPI remoteClient, UUID GroupID, UUID EjecteeID) { }
            public bool EjectGroupMember(IClientAPI remoteClient, UUID agentID, UUID GroupID, UUID EjecteeID) => false;
            public void InviteGroupRequest(IClientAPI remoteClient, UUID GroupID, UUID InviteeID, UUID RoleID) { }
            public bool InviteGroup(IClientAPI remoteClient, UUID agentID, UUID GroupID, UUID InviteeID, UUID RoleID) => false;
            public void NotifyChange(UUID GroupID) { }
            public ulong GetFullGroupPowers(UUID agentID, UUID groupID) => 0UL;
            public List<GroupRolesData> GroupRoleDataRequest(UUID agentID, UUID groupID) => null;
            public List<DirGroupsReplyData> FindGroups(IClientAPI remoteClient, string query) => null;
        }

        /// <summary>One calling process: Alice (the caller) is root here; Bob is whatever the test makes him.</summary>
        private static (WebRtcVoiceRegionModule Module, Process Process, NonSpatialVoiceSession Session) Calling(
            System.Action<Process> arrange, UUID[] members = null, Counts counts = null, bool noRoster = false)
        {
            members ??= new[] { Alice, Bob };
            counts ??= new Counts();
            Process p = new Process();
            p.AddRoot(Alice);
            p.Presence.SetOnline(Bob);
            arrange(p);
            counts.Groups = new FakeGroups(members) { NoRoster = noRoster };
            p.Scene.RegisterModuleInterface<IGroupsModule>(counts.Groups);

            GroupVoicePolicy policy = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = (a, g) => { counts.IsMember++; return g == Group && members.Contains(a); },
                Powers = (a, g) => { counts.Powers++; return GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice; },
            };
            NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), policy.ToAdmissionOrNull() },
                Grid);

            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_groupVoice", policy);
            SetPrivate(mod, "m_nonSpatial", engine);
            SetPrivate(mod, "m_carrierAuth", new CarrierAuth(Secret));
            ((List<Scene>)GetPrivate(mod, "m_scenes")).Add(p.Scene);

            GroupVoiceChatSession.TryHandleCall(
                new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) },
                Alice, policy, engine, RegionA, out _);
            return (mod, p, engine.Store.Get(Group));
        }

        /// <summary>What the policy and the groups service were asked.</summary>
        private sealed class Counts
        {
            public int IsMember;
            public int Powers;
            public FakeGroups Groups;
        }

        private static void Ring(WebRtcVoiceRegionModule mod, NonSpatialVoiceSession session)
            => Invoke(mod, "RingGroupMembers", session, Alice, "Test User", "Test Group");

        [Fact(Timeout = 60000)]
        public void MemberWithOnlyAChildPresenceHere_IsNotRungLocally_AndIsRungThroughTheCarrier()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session) = Calling(x => x.AddChild(Bob));

            Ring(mod, session);

            {
                var assertedValue = p.Queue.Enqueued;
                TestAssert.WithMessage(() => Assert.DoesNotContain(Bob, assertedValue), "the accept would go to a process that knows nothing of the call");
            }
            GridInstantMessage sent = p.Transfer.Sent.SingleOrDefault();
            {
                var assertedValue = sent;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "one carrier ring, to the process that holds Bob's root presence");
            }
            Assert.True(GroupVoiceRingTransport.TryParse(sent, out UUID target, out UUID group, out _));
            Assert.Equal(Bob, target);
            Assert.Equal(Group, group);
        }

        [Fact(Timeout = 60000)]
        public void TheCarrierRing_IsSentToRootOnly_SoAChildPresenceHereIsNotHandedTheCarrier()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Without the flag the transfer module delivers a message for an agent who is only a child presence in this
            // process to that agent's viewer as an instant message, and it never reaches the process where they are root.
            var (mod, p, session) = Calling(x => x.AddChild(Bob));

            Ring(mod, session);

            Assert.Single(p.Transfer.Sent);
            Assert.Equal(new[] { true }, p.Transfer.SentRootOnly);
        }

        // A busy region server: forty avatars here, twelve of them in the group, and thirty-five more members of the
        // group who are on other region servers, some or all of them online.
        private static (WebRtcVoiceRegionModule Module, Process Process, NonSpatialVoiceSession Session, Counts Counts,
            List<UUID> Local, List<UUID> Remote) Busy(bool noRoster = false, int remoteOnline = 35)
        {
            List<UUID> local = Enumerable.Range(0, 12).Select(_ => UUID.Random()).ToList();
            List<UUID> bystanders = Enumerable.Range(0, 27).Select(_ => UUID.Random()).ToList();
            List<UUID> remote = Enumerable.Range(0, 35).Select(_ => UUID.Random()).ToList();
            Counts counts = new Counts();
            var (mod, p, session) = Calling(x =>
            {
                foreach (UUID a in local.Concat(bystanders)) x.AddRoot(a);
                x.Presence.SetOnline(remote.Take(remoteOnline).ToArray());
            }, new[] { Alice }.Concat(local).Concat(remote).ToArray(), counts, noRoster);
            counts.IsMember = counts.Powers = 0;   // what it took to start the call is not the ring's
            counts.Groups.RosterCalls = 0;
            p.Presence.GetAgentsCalls = p.Presence.AgentsAsked = 0;
            return (mod, p, session, counts, local, remote);
        }

        [Fact(Timeout = 60000)]
        public void ARing_TakesMembershipFromTheRosterItAlreadyFetches_NotFromTheGroupsServiceForEveryAvatar()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session, counts, local, remote) = Busy();

            Ring(mod, session);

            {
                var assertedValue = counts.Groups.RosterCalls;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one roster for the whole ring");
            }
            {
                var assertedValue = counts.IsMember;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no membership lookup for any of the forty avatars");
            }
            {
                var assertedValue = counts.Powers;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and no powers lookup");
            }
            {
                var assertedValue = p.Queue.Enqueued;
                TestAssert.WithMessage(() => Assert.Equivalent(local, assertedValue, strict: true), "the twelve members here are rung, and no one else here");
            }
            {
                var assertedValue = p.Transfer.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(remote.Count, assertedValue), "and each of the thirty-five elsewhere gets a carrier");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARing_AsksThePresenceServiceOnce_ForTheWholeRemoteRoster()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session, _, _, remote) = Busy();

            Ring(mod, session);

            {
                var assertedValue = p.Presence.GetAgentsCalls;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one call, not one for each remote member");
            }
            Assert.Equal(remote.Count, p.Presence.AgentsAsked);
        }

        [Fact(Timeout = 60000)]
        public void ARing_StillRingsOnlyTheMembersThePresenceServiceReportsOnline()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session, _, _, _) = Busy(remoteOnline: 20);

            Ring(mod, session);

            {
                var assertedValue = p.Transfer.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(20, assertedValue), "fifteen of the thirty-five are offline and are not rung");
            }
        }

        [Fact(Timeout = 60000)]
        public void WithoutARoster_TheLocalWalkAsksTheGroupsServiceAsItDid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session, counts, local, _) = Busy(noRoster: true);

            Ring(mod, session);

            Assert.True(counts.IsMember >= 39, "every avatar here is asked about");
            {
                var assertedValue = p.Queue.Enqueued;
                TestAssert.WithMessage(() => Assert.Equivalent(local, assertedValue, strict: true), "and the members here are rung");
            }
        }

        [Fact(Timeout = 60000)]
        public void RootAgents_ListsRootPresencesOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Process p = new Process();
            UUID root = UUID.Random();
            UUID child = UUID.Random();
            p.AddRoot(root);
            p.AddChild(child);

            List<UUID> roots = osWebRtcVoice.NonSpatial.GroupVoiceInvite.RootAgents(new[] { p.Scene });

            Assert.Contains(root, roots);
            Assert.DoesNotContain(child, roots);
        }

        [Fact(Timeout = 60000)]
        public void MemberWithARootPresenceHere_IsRungLocally_AndNothingIsCarried()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session) = Calling(x => x.AddRoot(Bob));

            Ring(mod, session);

            Assert.Equal(new[] { Bob }, p.Queue.Enqueued);
            Assert.Empty(p.Transfer.Sent);
        }

        [Fact(Timeout = 60000)]
        public void MemberWithNoPresenceHere_IsRungThroughTheCarrier()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (mod, p, session) = Calling(_ => { });

            Ring(mod, session);

            Assert.Empty(p.Queue.Enqueued);
            GridInstantMessage sent = p.Transfer.Sent.SingleOrDefault();
            Assert.NotNull(sent);
            Assert.True(GroupVoiceRingTransport.TryParse(sent, out UUID target, out _, out _));
            Assert.Equal(Bob, target);
        }

        [Fact(Timeout = 60000)]
        public void ARingArrivingForAMemberWhoseRootIsNotHere_IsNotDeliveredToTheirChildPresence()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The receiving side of the same rule: a carrier that reaches a process where the member is
            // only a child presence adopts nothing visible and rings nobody.
            var (callMod, callProc, session) = Calling(x => x.AddChild(Bob));
            Ring(callMod, session);
            GridInstantMessage carried = callProc.Transfer.Sent.Single();

            Process far = new Process("Region Two", 1001, 1000);
            far.AddChild(Bob);
            GroupVoicePolicy policy = new GroupVoicePolicy
            {
                Enabled = true, RequireVoicePower = true, Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = (a, g) => true, Powers = (a, g) => GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice,
            };
            WebRtcVoiceRegionModule farMod = new WebRtcVoiceRegionModule();
            SetPrivate(farMod, "m_Enabled", true);
            SetPrivate(farMod, "m_groupVoice", policy);
            SetPrivate(farMod, "m_nonSpatial", new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), policy.ToAdmissionOrNull() }, Grid));
            SetPrivate(farMod, "m_carrierAuth", new CarrierAuth(Secret));
            ((List<Scene>)GetPrivate(farMod, "m_scenes")).Add(far.Scene);

            Invoke(farMod, "OnIncomingGroupRing", carried);

            Assert.Empty(far.Queue.Enqueued);
        }
    }
}
