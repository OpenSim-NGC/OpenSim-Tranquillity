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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void ThePowerConstants_AreTheViewersLiterals_AndOpenMetaversesEnum()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = GroupVoicePolicy.PowerJoinSession;
                TestAssert.WithMessage(() => Assert.Equal(0x1UL << 16, assertedValue), "GP_SESSION_JOIN, roles_constants.h:144");
            }
            {
                var assertedValue = GroupVoicePolicy.PowerVoice;
                TestAssert.WithMessage(() => Assert.Equal(0x1UL << 27, assertedValue), "GP_SESSION_VOICE, roles_constants.h:145");
            }
            Assert.Equal(GroupVoicePolicy.PowerJoinSession, (ulong)GroupPowers.JoinChat);
            Assert.Equal(GroupVoicePolicy.PowerVoice, (ulong)GroupPowers.AllowVoiceChat);
        }

        [Fact(Timeout = 60000)]
        public void TheRequiredMaskIsBothPowersByDefault_AndJoinAloneWhenTheKnobIsOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Both bits are in GroupsService.DefaultEveryonePowers (GroupsService.cs,
            // "JoinChat | ... | AllowVoiceChat"), so requiring both affects only a group that has
            // deliberately removed one. Asserted on the mask rather than by referencing the groups
            // assembly, which this test project does not and should not link.
            (GroupVoicePolicy both, _, _) = Setup();
            Assert.Equal(GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice, both.RequiredMask);

            (GroupVoicePolicy joinOnly, _, _) = Setup(requireVoice: false);
            Assert.Equal(GroupVoicePolicy.PowerJoinSession, joinOnly.RequiredMask);
        }

        // ---- the "call" arm --------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Call_WithBothPowers_ReturnsVoiceCredentialsCarryingTheGroupRoomKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);

            bool handled = GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            Assert.Equal("webrtc", creds["voice_server_type"].AsString());
            Assert.Equal(NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, group), creds["channel_uri"].AsString());
            {
                var assertedValue = creds["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(64, assertedValue.Length), "32 bytes of hex");
            }
            {
                var assertedValue = creds["channel_uri"].AsString();
                TestAssert.WithMessage(() => Assert.NotEqual(group.ToString(), assertedValue), "the room is NOT the session id -- that is the A2A shortcut we are not repeating");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_IsIdempotent_TheSecondFindsTheSameRoomAndToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome a);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome b);

            Assert.Equal(((OSDMap)a.Body["voice_credentials"])["channel_uri"].AsString(), ((OSDMap)b.Body["voice_credentials"])["channel_uri"].AsString());
            {
                var assertedValue = ((OSDMap)b.Body["voice_credentials"])["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(((OSDMap)a.Body["voice_credentials"])["channel_credentials"].AsString(), assertedValue), "a retried call must not invalidate credentials already handed out");
            }
        }

        [Fact(Timeout = 60000)]
        public void TwoMembersOfOneGroup_GetTheSameRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            g.Join(Bob, group);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome a);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Bob, p, e, RegionB, out ChatSessionOutcome b);

            Assert.Equal(((OSDMap)a.Body["voice_credentials"])["channel_uri"].AsString(), ((OSDMap)b.Body["voice_credentials"])["channel_uri"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void Call_WithoutTheVoicePower_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group, GroupVoicePolicy.PowerJoinSession);   // may join, may not talk

            bool handled = GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.Forbidden, assertedValue), "403 -> the viewer's VoiceNotAllowed");
            }
            Assert.Contains(GroupVoiceChatSession.DecisionNoPower, o.Instrument);
            Assert.Null(o.Body);
        }

        [Fact(Timeout = 60000)]
        public void Call_WithoutTheJoinPower_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group, GroupVoicePolicy.PowerVoice);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            Assert.Equal(HttpStatusCode.Forbidden, o.Status);
            Assert.Contains(GroupVoiceChatSession.DecisionNoPower, o.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void Call_WithJoinOnly_IsAdmittedWhenTheVoicePowerIsNotRequired()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(requireVoice: false);
            UUID group = UUID.Random();
            g.Join(Alice, group, GroupVoicePolicy.PowerJoinSession);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out ChatSessionOutcome o);

            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), "the knob is real, not decorative");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_FromANonMember_IsNotOurs_AndFallsThroughUntouched()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);

            bool handled = GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome o);

            Assert.False(handled, "a non-member's call must reach the A2A arm and 404 there, as before");
            Assert.Null(o);
            {
                var assertedValue = e.Store.All();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and must not have created a session");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_IsNotTakenWhenGroupVoiceIsDisabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(enabled: false);
            UUID group = UUID.Random();
            g.Join(Alice, group);

            Assert.False(GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _));
            Assert.False(GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, GroupVoicePolicy.Disabled, e, RegionA, out _));
        }

        [Fact(Timeout = 60000)]
        public void OnlyTheCallMethodIsTaken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            foreach (string m in new[] { "start p2p voice", "start conference", "accept invitation", "decline invitation", "fetch history" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(group) };
                Assert.False(GroupVoiceChatSession.TryHandleCall(body, Alice, p, e, RegionA, out _), m);
            }
        }

        // ---- the cap, load-bearing for the first time -------------------------------------

        [Fact(Timeout = 60000)]
        public void TheFiftyFirstCallerIsRefusedWith409_WhichTheViewerShowsAsChannelFull()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();

            for (int i = 0; i < NonSpatialCaps.DefaultConferenceCap; i++)
            {
                UUID a = UUID.Random();
                g.Join(a, group);
                GroupVoiceChatSession.TryHandleCall(CallBody(group), a, p, e, RegionA, out ChatSessionOutcome ok);
                {
                    var assertedValue = ok.Status;
                    TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), "seat " + i);
                }
            }

            UUID late = UUID.Random();
            g.Join(late, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), late, p, e, RegionA, out ChatSessionOutcome full);

            Assert.Equal(NonSpatialCaps.CapacityHttpStatus, (int)full.Status);
            Assert.Equal(409, (int)full.Status);
            Assert.Contains(GroupVoiceChatSession.DecisionFull, full.Instrument);
            {
                var assertedValue = full.Body;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no credentials are handed out to someone who has no seat");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheConfiguredCapIsClampedToTheMixers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 5000);
            UUID group = UUID.Random();
            g.Join(Alice, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            Assert.Equal(NonSpatialCaps.MixerRoomCap, e.Store.All()[0].Cap);
        }

        [Fact(Timeout = 60000)]
        public void ASeatFreedByDeparture_LetsTheNextCallerIn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 2);
            UUID group = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, group);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Bob, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome full);
            Assert.Equal(409, (int)full.Status);

            e.Depart(group, Bob, DepartureReason.VoiceTeardown);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome now);
            Assert.Equal(HttpStatusCode.OK, now.Status);
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

        [Fact(Timeout = 60000)]
        public void AGroupProvisionIsRecognisedByItsChannel_AndAnA2AOneIsNot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (_, _, _, _, string room, string token) = Called();
            Assert.True(NonSpatialProvisionAdmission.IsGroupProvision(ProvisionBody(room, token)));
            Assert.False(NonSpatialProvisionAdmission.IsGroupProvision(ProvisionBody(UUID.Random().ToString(), token)), "a bare-UUID channel is an A2A channel and must reach the untouched A2A arm");
            OSDMap local = new OSDMap { ["channel_type"] = OSD.FromString("local") };
            Assert.False(NonSpatialProvisionAdmission.IsGroupProvision(local));
            OSDMap logout = new OSDMap { ["logout"] = OSD.FromBoolean(true), ["viewer_session"] = OSD.FromString("vs") };
            Assert.False(NonSpatialProvisionAdmission.IsGroupProvision(logout));
            Assert.False(NonSpatialProvisionAdmission.IsGroupProvision(null));
        }

        [Fact(Timeout = 60000)]
        public void ProvisionWithTheRightTokenIsAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);
            Assert.True(r.Admitted, r.Decision);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionAdmitted, r.Decision);
            Assert.Equal(200, r.Status);
        }

        [Fact(Timeout = 60000)]
        public void ProvisionWithTheWrongTokenIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            foreach (string bad in new[] { null, string.Empty, new string('a', 64), token.Substring(1) })
            {
                NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, bad), Alice, p, e, RegionA);
                Assert.False(r.Admitted, "token " + (bad ?? "<null>"));
                Assert.Equal(NonSpatialProvisionAdmission.DecisionBadToken, r.Decision);
                Assert.Equal(403, r.Status);
            }
        }

        [Fact(Timeout = 60000)]
        public void ProvisionFromANonMemberIsRefused_EvenWithAValidToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The token is one string shared by everyone who can call, so a leak must not be enough.
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Carol, p, e, RegionA);
            Assert.False(r.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionNotMember, r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ProvisionIsRefusedWhenThePowerWasRevokedAfterTheCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g, UUID group, string room, string token) = Called();
            g.Powers[(Alice, group)] = GroupVoicePolicy.PowerJoinSession;   // voice revoked between call and provision

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);

            Assert.False(r.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionNoPower, r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ProvisionForAnUnknownRoomKeyIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _) = Setup();
            string stranger = NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, UUID.Random());
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(stranger, "x"), Alice, p, e, RegionA);
            Assert.False(r.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionNoSession, r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ProvisionIsRefusedWhenGroupVoiceIsDisabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, _, string room, string token) = Called();
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(
                ProvisionBody(room, token), Alice, GroupVoicePolicy.Disabled, e, RegionA);
            Assert.False(r.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionNoSession, r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ProvisionFromASecondRegion_RepointsTheSeatInsteadOfTakingAnother()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, UUID group, string room, string token) = Called();
            NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);
            NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionB);

            NonSpatialVoiceSession s = e.Store.Get(group);
            Assert.Equal(1, s.SeatsHeld);
            Assert.Equal(RegionB, s.Find(Alice).OriginRegion);
        }

        // ---- the guarantee: A2A is unchanged with group voice ON -----------------------------------

        [Fact(Timeout = 60000)]
        public void AnA2AProvisionBehavesIdentically_WithGroupVoiceEnabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            A2ARegistry live = new A2ARegistry();
            A2ASession s = live.Record(Alice, Bob, out _);
            string a2aToken = live.IssueToken(s.SessionId, Alice)?.Token;
            OSDMap body = ProvisionBody(s.ChannelUri, a2aToken);

            Assert.False(NonSpatialProvisionAdmission.IsGroupProvision(body), "the group arm must never take an A2A body, so A2AProvisionAdmission.Decide still runs");

            ProvisionAdmission d = A2AProvisionAdmission.Decide(body, Alice, live);
            Assert.True(d.Admitted, d.Decision);
            Assert.Equal(ProvisionKind.Multiagent, d.Kind);
        }

        // ---- seat inflation: what 56 failed provisions actually did ----------------------------

        [Fact(Timeout = 60000)]
        public void RepeatedFailedProvisionsByOneAgent_DoNotInflateTheSeatCount()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The live NRE produced 112 admitted provisions and 56 exceptions from ONE agent in four
            // minutes. Each admitted provision calls Accept. If Accept were not idempotent per agent,
            // the group would have burned through its 50 seats in under two minutes.
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, _, UUID group, string room, string token) = Called();
            for (int i = 0; i < 112; i++)
            {
                NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(ProvisionBody(room, token), Alice, p, e, RegionA);
                Assert.True(r.Admitted, "retry " + i);
            }
            NonSpatialVoiceSession s = e.Store.Get(group);
            {
                var assertedValue = s.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one agent holds exactly one seat however many times it retries");
            }
            Assert.Single(s.Members);
            Assert.False(s.IsFull);
        }

        [Fact(Timeout = 60000)]
        public void DistinctAgentsRetrying_EachHoldExactlyOneSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.Equal(3, e.Store.Get(group).SeatsHeld);
        }

        // ---- teardown: the same A2A assumption, found in the logout arm -------------------------

        [Fact(Timeout = 60000)]
        public void AVoiceTeardownReleasesTheGroupSeat_KeyedByViewerSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            g.Join(Bob, group);
            foreach (UUID a in new[] { Alice, Bob })
                GroupVoiceChatSession.TryHandleCall(CallBody(group), a, p, e, RegionA, out _);
            // the service's success map is what carries viewer_session; the module tags the seat with it
            e.MarkPresent(group, Alice, RegionA, "vs-alice");
            e.MarkPresent(group, Bob, RegionA, "vs-bob");
            Assert.Equal(2, e.Store.Get(group).SeatsHeld);

            var gone = e.DepartByViewerSession(Alice, "vs-alice", DepartureReason.VoiceTeardown);

            Assert.Single(gone);
            {
                var assertedValue = e.Store.Get(group).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "Alice's seat is freed");
            }
            Assert.True(e.Store.Get(group).Find(Bob).HoldsSeat, "Bob keeps his");
            Assert.Equal(DepartureReason.VoiceTeardown, e.Store.Get(group).Find(Alice).Departure);
        }

        [Fact(Timeout = 60000)]
        public void ATeardownForAnotherViewerSessionFreesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID group = UUID.Random();
            g.Join(Alice, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            e.MarkPresent(group, Alice, RegionA, "vs-alice");

            Assert.Empty(e.DepartByViewerSession(Alice, "vs-someone-else", DepartureReason.VoiceTeardown));
            {
                var assertedValue = e.DepartByViewerSession(Alice, null, DepartureReason.VoiceTeardown);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a null viewer session must match nothing - departing everything is DepartAll, a different lifecycle");
            }
            {
                var assertedValue = e.DepartByViewerSession(Bob, "vs-alice", DepartureReason.VoiceTeardown);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and it is scoped to the agent, not just the session string");
            }
            Assert.Equal(1, e.Store.Get(group).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void APresenceCloseReleasesEveryGroupSeatTheAgentHolds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup();
            UUID g1 = UUID.Random(), g2 = UUID.Random();
            foreach (UUID grp in new[] { g1, g2 }) { g.Join(Alice, grp); g.Join(Bob, grp); }
            foreach (UUID grp in new[] { g1, g2 })
                foreach (UUID a in new[] { Alice, Bob })
                    GroupVoiceChatSession.TryHandleCall(CallBody(grp), a, p, e, RegionA, out _);

            var gone = e.DepartAll(Alice, DepartureReason.PresenceLost);

            Assert.Equal(2, gone.Count);
            foreach (UUID grp in new[] { g1, g2 })
            {
                Assert.Equal(1, e.Store.Get(grp).SeatsHeld);
                Assert.Equal(DepartureReason.PresenceLost, e.Store.Get(grp).Find(Alice).Departure);
            }
        }

        [Fact(Timeout = 60000)]
        public void ASeatFreedByTeardownIsImmediatelyReusableByTheCap()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, Grid_ g) = Setup(cap: 2);
            UUID group = UUID.Random();
            foreach (UUID a in new[] { Alice, Bob, Carol }) g.Join(a, group);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Alice, p, e, RegionA, out _);
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Bob, p, e, RegionA, out _);
            e.MarkPresent(group, Bob, RegionA, "vs-bob");
            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome full);
            Assert.Equal(409, (int)full.Status);

            e.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);

            GroupVoiceChatSession.TryHandleCall(CallBody(group), Carol, p, e, RegionA, out ChatSessionOutcome now);
            {
                var assertedValue = now.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), "the hung-up seat is reusable at once");
            }
        }

        // ---- a group admission is its own kind, so the A2A bookkeeping never sees it ----------

        [Fact(Timeout = 60000)]
        public void AGroupAdmissionIsItsOwnKind_AndCarriesNoA2ASession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A group provision is not an A2A multiagent one: it carries no A2ASession, so filing it as
            // ProvisionKind.Multiagent would make the A2A post-provision bookkeeping dereference null.
            ProvisionAdmission group = new ProvisionAdmission
            {
                Kind = ProvisionKind.Group,
                Decision = NonSpatialProvisionAdmission.DecisionAdmitted,
                ChannelType = "multiagent",
                Channel = "nsv1:group:" + UUID.Random(),
            };
            Assert.True(group.Admitted, "a group admission is still an admission");
            {
                var assertedValue = group.Session;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "and it never carries an A2ASession");
            }
            Assert.False(A2AProvisionAdmission.RecordsA2ASession(group), "so it must never enter the A2A bookkeeping that dereferences Session");
        }

        [Fact(Timeout = 60000)]
        public void RecordsA2ASession_IsFalseForEveryKindExceptAMultiagentWithASession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            foreach (ProvisionKind k in Enum.GetValues(typeof(ProvisionKind)))
            {
                ProvisionAdmission a = new ProvisionAdmission { Kind = k, Decision = "x" };
                Assert.False(A2AProvisionAdmission.RecordsA2ASession(a), k + " with a null Session must not record");
            }
            Assert.False(A2AProvisionAdmission.RecordsA2ASession(null));

            A2ARegistry live = new A2ARegistry();
            A2ASession s = live.Record(Alice, Bob, out _);
            ProvisionAdmission real = new ProvisionAdmission
            {
                Kind = ProvisionKind.Multiagent, Decision = "multiagent-admitted", Session = s,
            };
            Assert.True(A2AProvisionAdmission.RecordsA2ASession(real), "the one case that must still work: a real A2A admission");
        }

        [Fact(Timeout = 60000)]
        public void AGroupAdmissionNeverRecordsTheListenerRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A group room is not the agent's spatial room; recording it would send exclusion batches to the wrong room.
            Assert.False(A2AProvisionAdmission.RecordsListenerRoom(ProvisionKind.Group));
        }

        [Fact(Timeout = 60000)]
        public void AGroupRoomKeyIsNeverResolvableByTheA2ARegistry()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (_, _, _, _, string room, _) = Called();
            Assert.Null(new A2ARegistry().TryGetByChannel(room));
            Assert.False(UUID.TryParse(room, out _));
        }
    }
}
