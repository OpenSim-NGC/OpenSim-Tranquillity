/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Group voice on the engine -- the "call" arm, the provision arm, the power gate and
 * the cap. Also the guarantee that matters most: with group voice ENABLED, the A2A
 * path still behaves identically, because the group arms only ever take bodies the A2A arms could
 * not have taken.
 *
 * Viewer facts (Firestorm viewer source):
 *   roles_constants.h:144  GP_SESSION_JOIN  = 0x1 << 16   "can join session"
 *   roles_constants.h:145  GP_SESSION_VOICE = 0x1 << 27   "can hear/talk"
 *   llimview.cpp:2535-2538 a group session id IS the group id
 *   llvoicechannel.cpp:631 "call" carries session-id; :687 reads voice_credentials from the body
 *   llvoicewebrtc.h:153-156 group voice provisions via startAdHocSession, i.e. channel_type multiagent
 */
using System;
using System.Collections.Generic;
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
    public class GroupVoiceTests
    {
        private const string Grid = "example-grid";

        private sealed class Grid_
        {
            public readonly HashSet<(UUID, UUID)> Members = new();
            public readonly Dictionary<(UUID, UUID), ulong> Powers = new();

            public void Join(UUID agent, UUID group, ulong powers = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice)
            {
                Members.Add((agent, group));
                Powers[(agent, group)] = powers;
            }

            public bool IsMember(UUID a, UUID g) => Members.Contains((a, g));
            public ulong Of(UUID a, UUID g) => Powers.TryGetValue((a, g), out ulong p) ? p : 0UL;
        }

        private static (GroupVoicePolicy, NonSpatialVoiceSessionEngine, Grid_) Setup(bool enabled = true,
                                                                                     bool requireVoice = true,
                                                                                     int cap = 0)
        {
            Grid_ grid = new Grid_();
            GroupVoicePolicy policy = new GroupVoicePolicy
            {
                Enabled = enabled,
                RequireVoicePower = requireVoice,
                Cap = cap > 0 ? cap : NonSpatialCaps.DefaultConferenceCap,
                IsMember = grid.IsMember,
                Powers = grid.Of,
            };
            NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), policy.ToAdmissionOrNull() },
                Grid);
            return (policy, engine, grid);
        }

        private static OSDMap CallBody(UUID groupID)
            => new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(groupID) };

        private static OSDMap ProvisionBody(string channel, string credentials)
            => new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["channel"] = OSD.FromString(channel),
                ["credentials"] = OSD.FromString(credentials ?? string.Empty),
            };

        // ---- the power constants are the viewer's ---------------------------------------

        [Test]
        public void ThePowerConstants_AreTheViewersLiterals_AndOpenMetaversesEnum()
        {
            Assert.That(GroupVoicePolicy.PowerJoinSession, Is.EqualTo(0x1UL << 16), "GP_SESSION_JOIN, roles_constants.h:144");
            Assert.That(GroupVoicePolicy.PowerVoice, Is.EqualTo(0x1UL << 27), "GP_SESSION_VOICE, roles_constants.h:145");
            Assert.That((ulong)GroupPowers.JoinChat, Is.EqualTo(GroupVoicePolicy.PowerJoinSession));
            Assert.That((ulong)GroupPowers.AllowVoiceChat, Is.EqualTo(GroupVoicePolicy.PowerVoice));
        }

        [Test]
        public void TheRequiredMaskIsBothPowersByDefault_AndJoinAloneWhenTheKnobIsOff()
        {
            // Both bits are in GroupsService.DefaultEveryonePowers (GroupsService.cs,
            // "JoinChat | ... | AllowVoiceChat"), so requiring both affects only a group that has
            // deliberately removed one. Asserted on the mask rather than by referencing the groups
            // assembly, which this test project does not and should not link.
            (GroupVoicePolicy both, _, _) = Setup();
            Assert.That(both.RequiredMask, Is.EqualTo(GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice));

            (GroupVoicePolicy joinOnly, _, _) = Setup(requireVoice: false);
            Assert.That(joinOnly.RequiredMask, Is.EqualTo(GroupVoicePolicy.PowerJoinSession));
        }

        // ---- the "call" arm --------------------------------------------------------------

        [Test]
        public void Call_WithBothPowers_ReturnsVoiceCredentialsCarryingTheGroupRoomKey()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);

            bool handled = GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            Assert.That(creds["voice_server_type"].AsString(), Is.EqualTo("webrtc"));
            Assert.That(creds["channel_uri"].AsString(),
                        Is.EqualTo(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, group)));
            Assert.That(creds["channel_credentials"].AsString(), Has.Length.EqualTo(64), "32 bytes of hex");
            Assert.That(creds["channel_uri"].AsString(), Is.Not.EqualTo(group.ToString()),
                        "the room is NOT the session id -- that is the A2A shortcut we are not repeating");
        }

        [Test]
        public void Call_IsIdempotent_TheSecondFindsTheSameRoomAndToken()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome a);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome b);

            Assert.That(((OSDMap)b.Body["voice_credentials"])["channel_uri"].AsString(),
                        Is.EqualTo(((OSDMap)a.Body["voice_credentials"])["channel_uri"].AsString()));
            Assert.That(((OSDMap)b.Body["voice_credentials"])["channel_credentials"].AsString(),
                        Is.EqualTo(((OSDMap)a.Body["voice_credentials"])["channel_credentials"].AsString()),
                        "a retried call must not invalidate credentials already handed out");
        }

        [Test]
        public void TwoMembersOfOneGroup_GetTheSameRoom()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            g.Join(Bob, group);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome a);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Bob, p, e, RegionB, out ChatSessionOutcome b);

            Assert.That(((OSDMap)b.Body["voice_credentials"])["channel_uri"].AsString(),
                        Is.EqualTo(((OSDMap)a.Body["voice_credentials"])["channel_uri"].AsString()));
        }

        [Test]
        public void Call_WithoutTheVoicePower_Is403()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group, GroupVoicePolicy.PowerJoinSession);   // may join, may not talk

            bool handled = GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.Forbidden), "403 -> the viewer's VoiceNotAllowed");
            Assert.That(o.Instrument, Does.Contain(GroupVoiceChatSession.DecisionNoPower));
            Assert.That(o.Body, Is.Null);
        }

        [Test]
        public void Call_WithoutTheJoinPower_Is403()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group, GroupVoicePolicy.PowerVoice);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(o.Instrument, Does.Contain(GroupVoiceChatSession.DecisionNoPower));
        }

        [Test]
        public void Call_WithJoinOnly_IsAdmittedWhenTheVoicePowerIsNotRequired()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(requireVoice: false);
            UUID group = UUID.Random();
            g.Join(Alice, group, GroupVoicePolicy.PowerJoinSession);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), "the knob is real, not decorative");
        }

        [Test]
        public void Call_FromANonMember_IsNotOurs_AndFallsThroughUntouched()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);

            bool handled = GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome o);

            Assert.That(handled, Is.False, "a non-member's call must reach the A2A arm and 404 there, as before");
            Assert.That(o, Is.Null);
            Assert.That(e.Store.All(), Is.Empty, "and must not have created a session");
        }

        [Test]
        public void Call_IsNotTakenWhenGroupVoiceIsDisabled()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(enabled: false);
            UUID group = UUID.Random();
            g.Join(Alice, group);

            Assert.That(GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _), Is.False);
            Assert.That(GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, GroupVoicePolicy.Disabled, e, RegionA, out _), Is.False);
        }

        [Test]
        public void OnlyTheCallMethodIsTaken()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            foreach (string m in new[] { "start p2p voice", "start conference", "accept invitation", "decline invitation", "fetch history" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(group) };
                Assert.That(GroupVoiceChatSession.TryHandleCall(body, Alice, p, e, RegionA, out _), Is.False, m);
            }
        }

        // ---- the cap, load-bearing for the first time -------------------------------------

        [Test]
        public void TheFiftyFirstCallerIsRefusedWith409_WhichTheViewerShowsAsChannelFull()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();

            for (int i = 0; i < NonSpatialCaps.DefaultConferenceCap; i++)
            {
                UUID a = UUID.Random();
                g.Join(a, group);
                GroupVoiceChatSession.TryHandleCall(CallBody(group), a, p, e, RegionA, out ChatSessionOutcome ok);
                Assert.That(ok.Status, Is.EqualTo(HttpStatusCode.OK), "seat " + i);
            }

            UUID late = UUID.Random();
            g.Join(late, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), late, p, e, RegionA, out ChatSessionOutcome full);

            Assert.That((int)full.Status, Is.EqualTo(NonSpatialCaps.CapacityHttpStatus));
            Assert.That((int)full.Status, Is.EqualTo(409));
            Assert.That(full.Instrument, Does.Contain(GroupVoiceChatSession.DecisionFull));
            Assert.That(full.Body, Is.Null, "no credentials are handed out to someone who has no seat");
        }

        [Test]
        public void TheConfiguredCapIsClampedToTheMixers()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 5000);
            UUID group = UUID.Random();
            g.Join(Alice, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            Assert.That(e.Store.All()[0].Cap, Is.EqualTo(NonSpatialCaps.MixerRoomCap));
        }

        [Test]
        public void ASeatFreedByDeparture_LetsTheNextCallerIn()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 2);
            UUID group = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, group);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Bob, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome full);
            Assert.That((int)full.Status, Is.EqualTo(409));

            e.Depart(group, Bob, DepartureReason.VoiceTeardown);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome now);
            Assert.That(now.Status, Is.EqualTo(HttpStatusCode.OK));
        }

        // ---- the provision arm ------------------------------------------------------

        private static (GroupVoicePolicy, NonSpatialVoiceSessionEngine, Grid_, UUID, string, string) Called(int cap = 0)
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: cap);
            UUID group = UUID.Random();
            g.Join(Alice, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            return (p, e, g, group, creds["channel_uri"].AsString(), creds["channel_credentials"].AsString());
        }

        [Test]
        public void AGroupProvisionIsRecognisedByItsChannel_AndAnA2AOneIsNot()
        {
            (_, _, _, _, string room, string token) = Called();
            Assert.That(NonSpatialProvisionAdmission.IsGroupProvision(ProvisionBody(room, token)), Is.True);
            Assert.That(NonSpatialProvisionAdmission.IsGroupProvision(ProvisionBody(UUID.Random().ToString(), token)), Is.False,
                        "a bare-UUID channel is an A2A channel and must reach the untouched A2A arm");
            OSDMap local = new OSDMap { ["channel_type"] = OSD.FromString("local") };
            Assert.That(NonSpatialProvisionAdmission.IsGroupProvision(local), Is.False);
            OSDMap logout = new OSDMap { ["logout"] = OSD.FromBoolean(true), ["viewer_session"] = OSD.FromString("vs") };
            Assert.That(NonSpatialProvisionAdmission.IsGroupProvision(logout), Is.False);
            Assert.That(NonSpatialProvisionAdmission.IsGroupProvision(null), Is.False);
        }

        [Test]
        public void ProvisionWithTheRightTokenIsAdmitted()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);
            Assert.That(r.Admitted, Is.True, r.Decision);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionAdmitted));
            Assert.That(r.Status, Is.EqualTo(200));
        }

        [Test]
        public void ProvisionWithTheWrongTokenIsRefused()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            foreach (string bad in new[] { null, string.Empty, new string('a', 64), token.Substring(1) })
            {
                NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, bad), Alice, p, e, RegionA);
                Assert.That(r.Admitted, Is.False, "token " + (bad ?? "<null>"));
                Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionBadToken));
                Assert.That(r.Status, Is.EqualTo(403));
            }
        }

        [Test]
        public void ProvisionFromANonMemberIsRefused_EvenWithAValidToken()
        {
            // The token is one string shared by everyone who can call, so a leak must not be enough.
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Carol, p, e, RegionA);
            Assert.That(r.Admitted, Is.False);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionNotMember));
        }

        [Test]
        public void ProvisionIsRefusedWhenThePowerWasRevokedAfterTheCall()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g, UUID group, string room, string token) = Called();
            g.Powers[(Alice, group)] = GroupVoicePolicy.PowerJoinSession;   // voice revoked between call and provision

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);

            Assert.That(r.Admitted, Is.False);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionNoPower));
        }

        [Test]
        public void ProvisionForAnUnknownRoomKeyIsRefused()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _) = Setup();
            string stranger = NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, UUID.Random());
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(stranger, "x"), Alice, p, e, RegionA);
            Assert.That(r.Admitted, Is.False);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionNoSession));
        }

        [Test]
        public void ProvisionIsRefusedWhenGroupVoiceIsDisabled()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(
                ProvisionBody(room, token), Alice, GroupVoicePolicy.Disabled, e, RegionA);
            Assert.That(r.Admitted, Is.False);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionNoSession));
        }

        [Test]
        public void ProvisionFromASecondRegion_RepointsTheSeatInsteadOfTakingAnother()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, UUID group, string room, string token) = Called();
            NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);
            NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionB);

            NonSpatialVoiceSession s = e.Store.Get(group);
            Assert.That(s.SeatsHeld, Is.EqualTo(1));
            Assert.That(s.Find(Alice).OriginRegion, Is.EqualTo(RegionB));
        }

        // ---- the guarantee: A2A is unchanged with group voice ON -----------------------------------

        [Test]
        public void AnA2AProvisionBehavesIdentically_WithGroupVoiceEnabled()
        {
            A2ARegistry live = new A2ARegistry();
            A2ASession s = live.Record(Alice, Bob, out _);
            string a2aToken = live.IssueToken(s.SessionId, Alice)?.Token;
            OSDMap body = ProvisionBody(s.ChannelUri, a2aToken);

            Assert.That(NonSpatialProvisionAdmission.IsGroupProvision(body), Is.False,
                        "the group arm must never take an A2A body, so A2AProvisionAdmission.Decide still runs");

            ProvisionAdmission d = A2AProvisionAdmission.Decide(body, Alice, live);
            Assert.That(d.Admitted, Is.True, d.Decision);
            Assert.That(d.Kind, Is.EqualTo(ProvisionKind.Multiagent));
        }

        // ---- seat inflation: what 56 failed provisions actually did ----------------------------

        [Test]
        public void RepeatedFailedProvisionsByOneAgent_DoNotInflateTheSeatCount()
        {
            // The live NRE produced 112 admitted provisions and 56 exceptions from ONE agent in four
            // minutes. Each admitted provision calls Accept. If Accept were not idempotent per agent,
            // the group would have burned through its 50 seats in under two minutes.
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, UUID group, string room, string token) = Called();
            for (int i = 0; i < 112; i++)
            {
                NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);
                Assert.That(r.Admitted, Is.True, "retry " + i);
            }
            NonSpatialVoiceSession s = e.Store.Get(group);
            Assert.That(s.SeatsHeld, Is.EqualTo(1), "one agent holds exactly one seat however many times it retries");
            Assert.That(s.Members.Count, Is.EqualTo(1));
            Assert.That(s.IsFull, Is.False);
        }

        [Test]
        public void DistinctAgentsRetrying_EachHoldExactlyOneSeat()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            UUID[] agents = { Alice, Bob, Carol };
            foreach (UUID a in agents) g.Join(a, group);
            foreach (UUID a in agents)
                GroupVoiceChatSession.TryHandleCall(CallBody(group), a, p, e, RegionA, out _);
            string room = NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, group);
            string token = e.IssueToken(group, Alice);
            for (int i = 0; i < 20; i++)
                foreach (UUID a in agents)
                    NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), a, p, e, RegionA);
            Assert.That(e.Store.Get(group).SeatsHeld, Is.EqualTo(3));
        }

        // ---- teardown: the same A2A assumption, found in the logout arm -------------------------

        [Test]
        public void AVoiceTeardownReleasesTheGroupSeat_KeyedByViewerSession()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            g.Join(Bob, group);
            foreach (UUID a in new[] { Alice, Bob })
                GroupVoiceChatSession.TryHandleCall(CallBody(group), a, p, e, RegionA, out _);
            // the service's success map is what carries viewer_session; the module tags the seat with it
            e.MarkPresent(group, Alice, RegionA, "vs-alice");
            e.MarkPresent(group, Bob, RegionA, "vs-bob");
            Assert.That(e.Store.Get(group).SeatsHeld, Is.EqualTo(2));

            var gone = e.DepartByViewerSession(Alice, "vs-alice", DepartureReason.VoiceTeardown);

            Assert.That(gone.Count, Is.EqualTo(1));
            Assert.That(e.Store.Get(group).SeatsHeld, Is.EqualTo(1), "Alice's seat is freed");
            Assert.That(e.Store.Get(group).Find(Bob).HoldsSeat, Is.True, "Bob keeps his");
            Assert.That(e.Store.Get(group).Find(Alice).Departure, Is.EqualTo(DepartureReason.VoiceTeardown));
        }

        [Test]
        public void ATeardownForAnotherViewerSessionFreesNothing()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            e.MarkPresent(group, Alice, RegionA, "vs-alice");

            Assert.That(e.DepartByViewerSession(Alice, "vs-someone-else", DepartureReason.VoiceTeardown), Is.Empty);
            Assert.That(e.DepartByViewerSession(Alice, null, DepartureReason.VoiceTeardown), Is.Empty,
                        "a null viewer session must match nothing - departing everything is DepartAll, a different lifecycle");
            Assert.That(e.DepartByViewerSession(Bob, "vs-alice", DepartureReason.VoiceTeardown), Is.Empty,
                        "and it is scoped to the agent, not just the session string");
            Assert.That(e.Store.Get(group).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void APresenceCloseReleasesEveryGroupSeatTheAgentHolds()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID g1 = UUID.Random(), g2 = UUID.Random();
            foreach (UUID grp in new[] { g1, g2 }) { g.Join(Alice, grp); g.Join(Bob, grp); }
            foreach (UUID grp in new[] { g1, g2 })
                foreach (UUID a in new[] { Alice, Bob })
                    GroupVoiceChatSession.TryHandleCall(CallBody(grp), a, p, e, RegionA, out _);

            var gone = e.DepartAll(Alice, DepartureReason.PresenceLost);

            Assert.That(gone.Count, Is.EqualTo(2));
            foreach (UUID grp in new[] { g1, g2 })
            {
                Assert.That(e.Store.Get(grp).SeatsHeld, Is.EqualTo(1));
                Assert.That(e.Store.Get(grp).Find(Alice).Departure, Is.EqualTo(DepartureReason.PresenceLost));
            }
        }

        [Test]
        public void ASeatFreedByTeardownIsImmediatelyReusableByTheCap()
        {
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 2);
            UUID group = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Bob, p, e, RegionA, out _);
            e.MarkPresent(group, Bob, RegionA, "vs-bob");
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome full);
            Assert.That((int)full.Status, Is.EqualTo(409));

            e.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome now);
            Assert.That(now.Status, Is.EqualTo(HttpStatusCode.OK), "the hung-up seat is reusable at once");
        }

        // ---- a group admission is its own kind, so the A2A bookkeeping never sees it ----------

        [Test]
        public void AGroupAdmissionIsItsOwnKind_AndCarriesNoA2ASession()
        {
            // A group provision is not an A2A multiagent one: it carries no A2ASession, so filing it as
            // ProvisionKind.Multiagent would make the A2A post-provision bookkeeping dereference null.
            ProvisionAdmission group = new ProvisionAdmission
            {
                Kind = ProvisionKind.Group,
                Decision = NonSpatialProvisionAdmission.DecisionAdmitted,
                ChannelType = "multiagent",
                Channel = "nsv1:group:" + UUID.Random(),
            };
            Assert.That(group.Admitted, Is.True, "a group admission is still an admission");
            Assert.That(group.Session, Is.Null, "and it never carries an A2ASession");
            Assert.That(A2AProvisionAdmission.RecordsA2ASession(group), Is.False,
                        "so it must never enter the A2A bookkeeping that dereferences Session");
        }

        [Test]
        public void RecordsA2ASession_IsFalseForEveryKindExceptAMultiagentWithASession()
        {
            foreach (ProvisionKind k in Enum.GetValues(typeof(ProvisionKind)))
            {
                ProvisionAdmission a = new ProvisionAdmission { Kind = k, Decision = "x" };
                Assert.That(A2AProvisionAdmission.RecordsA2ASession(a), Is.False,
                            k + " with a null Session must not record");
            }
            Assert.That(A2AProvisionAdmission.RecordsA2ASession(null), Is.False);

            A2ARegistry live = new A2ARegistry();
            A2ASession s = live.Record(Alice, Bob, out _);
            ProvisionAdmission real = new ProvisionAdmission
            {
                Kind = ProvisionKind.Multiagent, Decision = "multiagent-admitted", Session = s,
            };
            Assert.That(A2AProvisionAdmission.RecordsA2ASession(real), Is.True,
                        "the one case that must still work: a real A2A admission");
        }

        [Test]
        public void AGroupAdmissionNeverRecordsTheListenerRoom()
        {
            // A group room is not the agent's spatial room; recording it would send exclusion batches to the wrong room.
            Assert.That(A2AProvisionAdmission.RecordsListenerRoom(ProvisionKind.Group), Is.False);
        }

        [Test]
        public void AGroupRoomKeyIsNeverResolvableByTheA2ARegistry()
        {
            (_, _, _, _, string room, _) = Called();
            Assert.That(new A2ARegistry().TryGetByChannel(room), Is.Null);
            Assert.That(UUID.TryParse(room, out _), Is.False);
        }
    }
}
