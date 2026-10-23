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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void TheFirstSeatStartsTheRing_AndNoLaterSeatDoes()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out ChatSessionOutcome first);
            Assert.That(first.StartedRinging, Is.True, "0 -> 1 rings");

            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out ChatSessionOutcome second);
            Assert.That(second.StartedRinging, Is.False, "1 -> 2 must not re-ring the group");
            GroupVoiceChatSession.TryHandleCall(Call(grp), Carol, p, e, RegionA, out ChatSessionOutcome third);
            Assert.That(third.StartedRinging, Is.False);
        }

        [Test]
        public void ConcurrentFirstJoiners_RingExactlyOnce()
        {
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

            Assert.That(rings, Is.EqualTo(1), "the 0 -> 1 claim is made inside the store mutation");
            Assert.That(e.Store.Get(grp).SeatsHeld, Is.EqualTo(agents.Length));
        }

        [Test]
        public void WhenTheRoomEmptiesAndSomeoneStartsItAgain_ItRingsAgain()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);

            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out ChatSessionOutcome a1);
            Assert.That(a1.StartedRinging, Is.True);
            e.MarkInvited(grp, new[] { Bob });
            Assert.That(e.Store.Get(grp).WasInvited(Bob), Is.True);

            e.Depart(grp, Alice, DepartureReason.VoiceTeardown);          // room empties
            Assert.That(e.Store.Get(grp).SeatsHeld, Is.EqualTo(0));
            Assert.That(e.Store.Get(grp).RingSent, Is.False, "the ring cycle ended with the call");
            Assert.That(e.Store.Get(grp).WasInvited(Bob), Is.False, "and the invited set was cleared");

            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out ChatSessionOutcome a2);
            Assert.That(a2.StartedRinging, Is.True, "a later start rings the group again");
        }

        [Test]
        public void ARoomThatMerelyThinsOut_DoesNotReRing()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            foreach (UUID a in new[] { Alice, Bob, Carol })
                GroupVoiceChatSession.TryHandleCall(Call(grp), a, p, e, RegionA, out _);

            e.Depart(grp, Carol, DepartureReason.ChatLeave);              // 3 -> 2, still live
            Assert.That(e.Store.Get(grp).RingSent, Is.True, "the cycle is still running");

            GroupVoiceChatSession.TryHandleCall(Call(grp), Carol, p, e, RegionA, out ChatSessionOutcome back);
            Assert.That(back.StartedRinging, Is.False, "rejoining a live call must not ring everyone again");
        }

        // ---- the target list -------------------------------------------------------------

        [Test]
        public void TargetsAreMembersWithBothPowers_ExcludingTheInitiator()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);                                            // initiator
            g.Join(Bob, grp);                                              // full powers -> rung
            g.Join(Carol, grp, GroupVoicePolicy.PowerJoinSession);         // no voice power -> not rung
            // Dave is present but not a member at all -> not rung
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            List<UUID> targets = GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol, Dave },
                                                          e.Store.Get(grp), Alice, p);

            Assert.That(targets, Is.EquivalentTo(new[] { Bob }));
        }

        [Test]
        public void AlreadySeatedMembersAreNotRung()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out _);   // Bob is in

            List<UUID> targets = GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol }, e.Store.Get(grp), Alice, p);

            Assert.That(targets, Is.EquivalentTo(new[] { Carol }));
        }

        [Test]
        public void AMemberAlreadyRungThisCycleIsNotRungAgain()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            e.MarkInvited(grp, new[] { Bob });

            Assert.That(GroupVoiceInvite.Targets(new[] { Alice, Bob, Carol }, e.Store.Get(grp), Alice, p),
                        Is.EquivalentTo(new[] { Carol }));
        }

        [Test]
        public void TargetsIsEmptyWhenGroupVoiceIsDisabled()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            Assert.That(GroupVoiceInvite.Targets(new[] { Bob }, e.Store.Get(grp), Alice, GroupVoicePolicy.Disabled), Is.Empty);
            Assert.That(GroupVoiceInvite.Targets(null, e.Store.Get(grp), Alice, p), Is.Empty);
        }

        // ---- the invitation body ---------------------------------------------------------

        [Test]
        public void TheBodyCarriesTheGroupIdAndTheVoiceKey()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            NonSpatialVoiceSession s = e.Store.Get(grp);
            string token = e.IssueToken(grp, Alice);

            OSDMap body = GroupVoiceInvite.BuildBody(s, token, Alice, "Test Caller", "The Group");

            Assert.That(body["session_id"].AsUUID(), Is.EqualTo(grp),
                        "the GROUP id is what makes the viewer choose VoiceInviteGroup");
            Assert.That(body.ContainsKey("voice"), Is.True, "no voice key means the viewer ignores it");
            Assert.That(body.ContainsKey("instantmessage"), Is.False, "that key would route to the IM branch");
            OSDMap voice = (OSDMap)body["voice"];
            Assert.That(voice["invitation_type"].AsInteger(), Is.EqualTo(0), "GROUP_CHAT_SESSION, not P2P(2)");
            Assert.That(voice["invitation_type"].AsInteger(), Is.Not.EqualTo(A2AInvitation.InvitationTypeP2P));
            Assert.That(voice["voice_server_type"].AsString(), Is.EqualTo("webrtc"));
            Assert.That(voice["channel_uri"].AsString(), Is.EqualTo(s.RoomKey));
            Assert.That(voice["channel_uri"].AsString(), Does.StartWith("nsv1:group:"));
            Assert.That(voice["channel_credentials"].AsString(), Is.EqualTo(token));
            Assert.That(body["from_id"].AsUUID(), Is.EqualTo(Alice));
            Assert.That(body["session_name"].AsString(), Is.EqualTo("The Group"));
        }

        [Test]
        public void TheBodyRefusesToBeBuiltWithoutAToken()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            Assert.Throws<InvalidOperationException>(
                () => GroupVoiceInvite.BuildBody(e.Store.Get(grp), null, Alice, "Test User", "G"));
        }

        [Test]
        public void TheBodyRefusesANonGroupSession()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _) = Setup();
            SessionOutcome adhoc = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.Throws<ArgumentException>(
                () => GroupVoiceInvite.BuildBody(adhoc.Session, "tok", Alice, "Test User", "G"));
        }

        // ---- accept invitation ------------------------------------------------------------

        [Test]
        public void AcceptInvitationAdmitsTheMemberAndTakesItsSeat()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp); g.Join(Bob, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            bool handled = GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, p, e, out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            Assert.That(o.Instrument, Does.Contain(GroupVoiceChatSession.DecisionAcceptAdmitted));
            Assert.That(e.Store.Get(grp).Find(Bob).HoldsSeat, Is.True, "accepting the popup IS the join");
            Assert.That(e.Store.Get(grp).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void AcceptInvitationIsRefusedWithoutTheVoicePower()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            g.Join(Bob, grp, GroupVoicePolicy.PowerJoinSession);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Bob, p, e, out ChatSessionOutcome o);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(e.Store.Get(grp).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void AcceptInvitationPastTheCapIs409()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 2);
            UUID grp = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Bob, p, e, RegionA, out _);

            GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Carol, p, e, out ChatSessionOutcome o);

            Assert.That((int)o.Status, Is.EqualTo(NonSpatialCaps.CapacityHttpStatus));
        }

        [Test]
        public void AcceptInvitationIsNotTakenForANonMemberOrAnUnknownSession()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);

            Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Dave, p, e, out _), Is.False,
                        "a non-member's accept falls through to the A2A arm untouched");
            Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(UUID.Random()), Alice, p, e, out _), Is.False,
                        "an accept for an unknown session is not ours");
            Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(grp), Alice, GroupVoicePolicy.Disabled, e, out _), Is.False,
                        "and nothing is taken when group voice is off");
        }

        [Test]
        public void AnAcceptForAnAdhocSessionIsNotTaken()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _) = Setup();
            SessionOutcome adhoc = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(Accept(adhoc.Session.SessionId), Alice, p, e, out _),
                        Is.False, "the ad-hoc arm owns the ad-hoc accept; this arm must not claim it");
        }

        [Test]
        public void OnlyTheAcceptMethodIsTakenByTheAcceptArm()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID grp = UUID.Random();
            g.Join(Alice, grp);
            GroupVoiceChatSession.TryHandleCall(Call(grp), Alice, p, e, RegionA, out _);
            foreach (string m in new[] { "call", "decline invitation", "start conference", "fetch history" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(grp) };
                Assert.That(GroupVoiceChatSession.TryHandleAcceptInvitation(body, Alice, p, e, out _), Is.False, m);
            }
        }

        // ---- the presence filter, one request for the whole list ------------------------------------------

        [Test]
        public void OnlineOnlyBatch_AsksOnceForAShortList_AndKeepsTheOnesReportedOnline()
        {
            List<UUID> candidates = Enumerable.Range(0, 35).Select(_ => UUID.Random()).ToList();
            int requests = 0;

            List<UUID> online = GroupVoiceInvite.OnlineOnlyBatch(candidates, ids =>
            {
                requests++;
                return ids.Take(20).ToDictionary(i => i, _ => UUID.Random());   // the first twenty are online
            });

            Assert.That(requests, Is.EqualTo(1));
            Assert.That(online, Is.EqualTo(candidates.Take(20)));
        }

        [Test]
        public void OnlineOnlyBatch_SplitsAVeryLongListIntoRequestsOfABoundedSize()
        {
            List<UUID> candidates = Enumerable.Range(0, GroupVoiceInvite.PresenceBatchSize * 2 + 5).Select(_ => UUID.Random()).ToList();
            List<int> sizes = new();

            GroupVoiceInvite.OnlineOnlyBatch(candidates, ids => { sizes.Add(ids.Count); return new Dictionary<UUID, UUID>(); });

            Assert.That(sizes, Is.EqualTo(new[] { GroupVoiceInvite.PresenceBatchSize, GroupVoiceInvite.PresenceBatchSize, 5 }));
        }

        [Test]
        public void OnlineOnlyBatch_TreatsARequestThatThrowsAsOffline_AndAMissingOrZeroRegionAsOffline()
        {
            UUID a = UUID.Random(), b = UUID.Random();

            Assert.That(GroupVoiceInvite.OnlineOnlyBatch(new[] { a, b }, _ => throw new InvalidOperationException("presence service down")), Is.Empty);
            Assert.That(GroupVoiceInvite.OnlineOnlyBatch(new[] { a, b }, _ => new Dictionary<UUID, UUID> { [a] = UUID.Zero }), Is.Empty);
            Assert.That(GroupVoiceInvite.OnlineOnlyBatch(new[] { a, b }, _ => null), Is.Empty);
            Assert.That(GroupVoiceInvite.OnlineOnlyBatch(new[] { a }, null), Is.Empty);
            Assert.That(GroupVoiceInvite.OnlineOnlyBatch(null, _ => new Dictionary<UUID, UUID>()), Is.Empty);
        }

        [Test]
        public void TargetsFromARoster_AreTheSameAsTargetsFromThePolicy()
        {
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

            Assert.That(fromRoster, Is.EqualTo(fromPolicy));
            Assert.That(GroupVoiceInvite.Targets(present, session, UUID.Zero, policy, null), Is.EqualTo(fromPolicy), "no roster: the policy is asked");
        }
    }
}
