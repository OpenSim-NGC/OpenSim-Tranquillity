/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The join popup. Send-once at the engine seam, the fan-out target list, the invitation
 * body, and the "accept invitation" handler that makes the popup more than decorative.
 *
 * Viewer facts (Firestorm viewer source):
 *   llimview.cpp:5196        the voice branch fires on a ChatterBoxInvitation body with a `voice` key
 *   llimview.cpp:5204        invitation_type == P2P_CHAT_SESSION picks IM_SESSION_P2P_INVITE
 *   llimview.cpp:119-125     GROUP_CHAT_SESSION = 0, CONFERENCE_SESSION = 1, P2P_CHAT_SESSION = 2
 *   llimview.cpp:4156-4161   gAgent.isInGroup(session_id) -> "VoiceInviteGroup", so session_id must
 *                            be the GROUP id
 *   llimview.cpp:3382-3385   Accept runs chatterBoxInvitationCoro -> POST "accept invitation" ->
 *                            startCall(voice_channel_info) only on success
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class GroupVoiceInviteTests
    {
        private const string Grid = "example-grid";
        private static readonly UUID Dave = new UUID("44444444-4444-4444-4444-444444444444");

        private sealed class Grid_
        {
            public readonly HashSet<(UUID, UUID)> Members = new();
            public readonly Dictionary<(UUID, UUID), ulong> Powers = new();
            public void Join(UUID a, UUID g, ulong p = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice)
            { Members.Add((a, g)); Powers[(a, g)] = p; }
            public bool IsMember(UUID a, UUID g) => Members.Contains((a, g));
            public ulong Of(UUID a, UUID g) => Powers.TryGetValue((a, g), out ulong v) ? v : 0UL;
        }

        private static (GroupVoicePolicy, NonSpatialVoiceSessionEngine, Grid_) Setup(int cap = 0)
        {
            Grid_ g = new Grid_();
            GroupVoicePolicy p = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = cap > 0 ? cap : NonSpatialCaps.DefaultConferenceCap,
                IsMember = g.IsMember,
                Powers = g.Of,
            };
            NonSpatialVoiceSessionEngine e = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), p.ToAdmissionOrNull() },
                Grid);
            return (p, e, g);
        }

        private static OSDMap Call(UUID group) => new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(group) };
        private static OSDMap Accept(UUID group) => new OSDMap { ["method"] = OSD.FromString("accept invitation"), ["session-id"] = OSD.FromUUID(group) };

        // ---- send-once -------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheFirstSeatStartsTheRing_AndNoLaterSeatDoes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out ChatSessionOutcome first);
            Assert.True(first.StartedRinging, "0 -> 1 rings");

            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out ChatSessionOutcome second);
            Assert.False(second.StartedRinging, "1 -> 2 must not re-ring the group");
            GroupVoiceChatSession.TryHandleCall(Call(grp), Carol, p, e, RegionA, out ChatSessionOutcome third);
            Assert.False(third.StartedRinging);
        }

        [Fact(Timeout = 60000)]
        public void ConcurrentFirstJoiners_RingExactlyOnce()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            UUID[] agents = Enumerable.Range(0, 12).Select(_ => UUID.Random()).ToArray();
            foreach (UUID a in agents) g.Join(a, grp);

            int rings = 0;
            System.Threading.Tasks.Parallel.ForEach(agents, a =>
            {
                GroupVoiceChatSession.TryHandleCall(Call(grp), a, p, e, RegionA, out ChatSessionOutcome o);
                if (o is not null && o.StartedRinging) System.Threading.Interlocked.Increment(ref rings);
            });

            {
                var assertedValue = rings;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the 0 -> 1 claim is made inside the store mutation");
            }
            Assert.Equal(agents.Length, e.Store.Get(grp).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void WhenTheRoomEmptiesAndSomeoneStartsItAgain_ItRingsAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out ChatSessionOutcome a1);
            Assert.True(a1.StartedRinging);
            e.MarkInvited(grp, new[] { Bob });
            Assert.True(e.Store.Get(grp).WasInvited(Bob));

            e.Depart(grp, Alice, DepartureReason.VoiceTeardown);          // room empties
            Assert.Equal(0, e.Store.Get(grp).SeatsHeld);
            Assert.False(e.Store.Get(grp).RingSent, "the ring cycle ended with the call");
            Assert.False(e.Store.Get(grp).WasInvited(Bob), "and the invited set was cleared");

            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out ChatSessionOutcome a2);
            Assert.True(a2.StartedRinging, "a later start rings the group again");
        }

        [Fact(Timeout = 60000)]
        public void ARoomThatMerelyThinsOut_DoesNotReRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            foreach (UUID a in new[] { Alice, Bob, Carol })
                GroupVoiceChatSession.TryHandleCall(Call(grp), a, p, e, RegionA, out _);

            e.Depart(grp, Carol, DepartureReason.ChatLeave);              // 3 -> 2, still live
            Assert.True(e.Store.Get(grp).RingSent, "the cycle is still running");

            GroupVoiceChatSession.TryHandleCall(Call(grp), Carol, p, e, RegionA, out ChatSessionOutcome back);
            Assert.False(back.StartedRinging, "rejoining a live call must not ring everyone again");
        }

        // ---- the target list -------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TargetsAreMembersWithBothPowers_ExcludingTheInitiator()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);                                            // initiator
            g.Join(Bob, grp);                                              // full powers -> rung
            g.Join(Carol, grp, GroupVoicePolicy.PowerJoinSession);         // no voice power -> not rung
            // Dave is present but not a member at all -> not rung
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            List<UUID> targets = GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol, Dave },
                                                          e.Store.Get(grp), Alice, p);

            Assert.Equivalent(new[] { Bob }, targets, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AlreadySeatedMembersAreNotRung()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out _);   // Bob is in

            List<UUID> targets = GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol }, e.Store.Get(grp), Alice, p);

            Assert.Equivalent(new[] { Carol }, targets, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AMemberAlreadyRungThisCycleIsNotRungAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            e.MarkInvited(grp, new[] { Bob });

            Assert.Equivalent(new[] { Carol }, GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol }, e.Store.Get(grp), Alice, p), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void TargetsIsEmptyWhenGroupVoiceIsDisabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            Assert.Empty(GroupVoiceInvite.Targets(new[] { Bob }, e.Store.Get(grp), Alice, GroupVoicePolicy.Disabled));
            Assert.Empty(GroupVoiceInvite.Targets(null, e.Store.Get(grp), Alice, p));
        }

        // ---- the invitation body ---------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheBodyCarriesTheGroupIdAndTheVoiceKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            NonSpatialVoiceSession s = e.Store.Get(grp);
            string token = e.IssueToken(grp, Alice);

            OSDMap body = GroupVoiceInvite.BuildBody(s, token, Alice, "Test Caller", "The Group");

            {
                var assertedValue = body["session_id"].AsUUID();
                TestAssert.WithMessage(() => Assert.Equal(grp, assertedValue), "the GROUP id is what makes the viewer choose VoiceInviteGroup");
            }
            Assert.True(body.ContainsKey("voice"), "no voice key means the viewer ignores it");
            Assert.False(body.ContainsKey("instantmessage"), "that key would route to the IM branch");
            OSDMap voice = (OSDMap)body["voice"];
            {
                var assertedValue = voice["invitation_type"].AsInteger();
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "GROUP_CHAT_SESSION, not P2P(2)");
            }
            Assert.NotEqual(A2AInvitation.InvitationTypeP2P, voice["invitation_type"].AsInteger());
            Assert.Equal("webrtc", voice["voice_server_type"].AsString());
            Assert.Equal(s.RoomKey, voice["channel_uri"].AsString());
            Assert.StartsWith("nsv1:group:", voice["channel_uri"].AsString());
            Assert.Equal(token, voice["channel_credentials"].AsString());
            Assert.Equal(Alice, body["from_id"].AsUUID());
            Assert.Equal("The Group", body["session_name"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void TheBodyRefusesToBeBuiltWithoutAToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            Assert.Throws<InvalidOperationException>(
                () => GroupVoiceInvite.BuildBody(e.Store.Get(grp), null, Alice, "Test User", "G"));
        }

        [Fact(Timeout = 60000)]
        public void TheBodyRefusesANonGroupSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _) = Setup();
            SessionOutcome adhoc = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.Throws<ArgumentException>(
                () => GroupVoiceInvite.BuildBody(adhoc.Session, "tok", Alice, "Test User", "G"));
        }

        // ---- accept invitation ------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AcceptInvitationAdmitsTheMemberAndTakesItsSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            bool handled = GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, p, e, out ChatSessionOutcome o);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            Assert.Contains(GroupVoiceChatSession.DecisionAcceptAdmitted, o.Instrument);
            Assert.True(e.Store.Get(grp).Find(Bob).HoldsSeat, "accepting the popup IS the join");
            Assert.Equal(2, e.Store.Get(grp).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void AcceptInvitationIsRefusedWithoutTheVoicePower()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            g.Join(Bob, grp, GroupVoicePolicy.PowerJoinSession);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, p, e, out ChatSessionOutcome o);

            Assert.Equal(HttpStatusCode.Forbidden, o.Status);
            Assert.Equal(1, e.Store.Get(grp).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void AcceptInvitationPastTheCapIs409()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 2);
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out _);

            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Carol, p, e, out ChatSessionOutcome o);

            Assert.Equal(NonSpatialCaps.CapacityHttpStatus, (int)o.Status);
        }

        [Fact(Timeout = 60000)]
        public void AcceptInvitationIsNotTakenForANonMemberOrAnUnknownSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            Assert.False(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Dave, p, e, out _), "a non-member's accept falls through to the A2A arm untouched");
            Assert.False(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(UUID.Random()), Alice, p, e, out _), "an accept for an unknown session is not ours");
            Assert.False(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Alice, GroupVoicePolicy.Disabled, e, out _), "and nothing is taken when group voice is off");
        }

        [Fact(Timeout = 60000)]
        public void AnAcceptForAnAdhocSessionIsNotTaken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _) = Setup();
            SessionOutcome adhoc = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.False(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(adhoc.Session.SessionId), Alice, p, e, out _), "the ad-hoc arm owns the ad-hoc accept; this arm must not claim it");
        }

        [Fact(Timeout = 60000)]
        public void OnlyTheAcceptMethodIsTakenByTheAcceptArm()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            foreach (string m in new[] { "call", "decline invitation", "start conference", "fetch history" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(grp) };
                Assert.False(GroupVoiceChatSession.TryHandleAcceptInvitation(body, Alice, p, e, out _), m);
            }
        }

        // ---- the presence filter, one request for the whole list ------------------------------------------

        [Fact(Timeout = 60000)]
        public void OnlineOnlyBatch_AsksOnceForAShortList_AndKeepsTheOnesReportedOnline()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<UUID> candidates = Enumerable.Range(0, 35).Select(_ => UUID.Random()).ToList();
            int requests = 0;

            List<UUID> online = GroupVoiceInvite.OnlineOnlyBatch(candidates, ids =>
            {
                requests++;
                return ids.Take(20).ToDictionary(i => i, _ => UUID.Random());   // the first twenty are online
            });

            Assert.Equal(1, requests);
            Assert.Equal(candidates.Take(20), online);
        }

        [Fact(Timeout = 60000)]
        public void OnlineOnlyBatch_SplitsAVeryLongListIntoRequestsOfABoundedSize()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<UUID> candidates = Enumerable.Range(0, GroupVoiceInvite.PresenceBatchSize * 2 + 5).Select(_ => UUID.Random()).ToList();
            List<int> sizes = new();

            GroupVoiceInvite.OnlineOnlyBatch(candidates, ids => { sizes.Add(ids.Count); return new Dictionary<UUID, UUID>(); });

            Assert.Equal(new[] { GroupVoiceInvite.PresenceBatchSize, GroupVoiceInvite.PresenceBatchSize, 5 }, sizes);
        }

        [Fact(Timeout = 60000)]
        public void OnlineOnlyBatch_TreatsARequestThatThrowsAsOffline_AndAMissingOrZeroRegionAsOffline()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID a = UUID.Random(), b = UUID.Random();

            Assert.Empty(GroupVoiceInvite.OnlineOnlyBatch(new[] { a, b }, _ => throw new InvalidOperationException("presence service down")));
            Assert.Empty(GroupVoiceInvite.OnlineOnlyBatch(new[] { a, b }, _ => new Dictionary<UUID, UUID> { [a] = UUID.Zero }));
            Assert.Empty(GroupVoiceInvite.OnlineOnlyBatch(new[] { a, b }, _ => null));
            Assert.Empty(GroupVoiceInvite.OnlineOnlyBatch(new[] { a }, null));
            Assert.Empty(GroupVoiceInvite.OnlineOnlyBatch(null, _ => new Dictionary<UUID, UUID>()));
        }

        [Fact(Timeout = 60000)]
        public void TargetsFromARoster_AreTheSameAsTargetsFromThePolicy()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID group = UUID.Random();
            UUID member = UUID.Random(), noPower = UUID.Random(), outsider = UUID.Random();
            GroupVoicePolicy policy = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = (a, g) => a == member || a == noPower,
                Powers = (a, g) => a == member ? GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice : GroupVoicePolicy.PowerJoinSession,
            };
            NonSpatialVoiceSessionEngine e = new NonSpatialVoiceSessionEngine(new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), policy.ToAdmissionOrNull() }, "example-grid");
            e.Start(NonSpatialSessionType.Group, member, group, group, RegionA);
            NonSpatialVoiceSession session = e.Get(group);
            Dictionary<UUID, ulong> roster = new()
            {
                [member] = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice,
                [noPower] = GroupVoicePolicy.PowerJoinSession,
            };
            UUID[] present = { member, noPower, outsider };

            List<UUID> fromRoster = GroupVoiceInvite.Targets(present, session, UUID.Zero, policy, roster);
            List<UUID> fromPolicy = GroupVoiceInvite.Targets(present, session, UUID.Zero, policy);

            Assert.Equal(fromPolicy, fromRoster);
            {
                var assertedValue = GroupVoiceInvite.Targets(present, session, UUID.Zero, policy, null);
                TestAssert.WithMessage(() => Assert.Equal(fromPolicy, assertedValue), "no roster: the policy is asked");
            }
        }
    }
}
