/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for provision admission for "multiagent", the accept transition,
 * decline, both-logout and the idle backstop.
 *
 * Wire shapes asserted here: the multiagent provision body carries `channel` (NOT channel_id) and `credentials`
 * (SL llvoicewebrtc.cpp:3341-3343, Firestorm llvoicewebrtc.cpp:3695-3697); the teardown body is
 * {logout:true, viewer_session, voice_server_type} with NO channel_type (llvoicewebrtc.cpp:2809-2811).
 */
using System;
using System.Collections.Generic;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class A2ALifecycleTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Mallory = new UUID("33333333-3333-3333-3333-333333333333");
        private static readonly UUID Xor = A2ARegistry.ComputeSessionId(Alice, Bob);

        private static OSDMap Multiagent(string channel, string credentials)
        {
            var m = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
            };
            if (channel != null) m["channel"] = OSD.FromString(channel);
            if (credentials != null) m["credentials"] = OSD.FromString(credentials);
            return m;
        }

        private static OSDMap Local() => new OSDMap
        {
            ["channel_type"] = OSD.FromString("local"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
        };

        private static OSDMap Logout(string viewerSession) => new OSDMap
        {
            ["logout"] = OSD.FromBoolean(true),
            ["viewer_session"] = OSD.FromString(viewerSession),
            ["voice_server_type"] = OSD.FromString("webrtc"),
        };

        private static OSDMap CallBody() => new OSDMap
        {
            ["method"] = OSD.FromString("call"),
            ["session-id"] = OSD.FromUUID(Xor),
            ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
        };

        private static OSDMap DeclineBody() => new OSDMap
        {
            ["method"] = OSD.FromString("decline p2p voice"),
            ["session-id"] = OSD.FromUUID(Xor),
        };

        /// <summary>Record + mint: the state after the caller's "start p2p voice" and "call".</summary>
        private static A2ASession Invited(A2ARegistry reg)
        {
            A2ASession s = reg.Record(Alice, Bob, out _);
            return reg.IssueToken(s.SessionId, Alice);
        }

        /// <summary>Both parties admitted and joined: the state of a live call.</summary>
        private static A2ASession Live(A2ARegistry reg)
        {
            A2ASession s = Invited(reg);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");
            reg.MarkProvisioned(s.SessionId, Bob, "vs-bob");
            return s;
        }

        // ---- classification -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Logout_IsRecognisedBeforeChannelType()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Logout("vs-1"), Alice, reg);
            Assert.Equal(ProvisionKind.Logout, a.Kind);
            Assert.True(a.Admitted, "a logout body (channel_type absent) must be admitted so the service can see `logout`");
            Assert.Equal(A2AProvisionAdmission.DecisionLogout, a.Decision);
            Assert.Empty(a.ChannelType);
        }

        [Fact(Timeout = 60000)]
        public void Logout_False_IsNotALogout()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap m = Logout("vs-1");
            m["logout"] = OSD.FromBoolean(false);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(m, Alice, new A2ARegistry());
            Assert.Equal(ProvisionKind.Refused, a.Kind);
            Assert.Equal(A2AProvisionAdmission.DecisionOtherChannelType, a.Decision);
        }

        [Fact(Timeout = 60000)]
        public void Local_IsAdmittedToTheParcelChecks()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Local(), Alice, new A2ARegistry());
            Assert.Equal(ProvisionKind.Local, a.Kind);
            Assert.Equal(A2AProvisionAdmission.DecisionLocal, a.Decision);
            Assert.Null(a.Session);
        }

        [InlineData(null)]
        [InlineData("")]
        [InlineData("group")]
        [InlineData("MULTIAGENT")]
        [Theory(Timeout = 60000)]
        public void OtherChannelTypes_AreRefused(string channelType)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var m = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") };
            if (channelType != null) m["channel_type"] = OSD.FromString(channelType);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(m, Alice, new A2ARegistry());
            Assert.Equal(ProvisionKind.Refused, a.Kind);
            Assert.False(a.Admitted);
            Assert.Equal(A2AProvisionAdmission.DecisionOtherChannelType, a.Decision);
        }

        [Fact(Timeout = 60000)]
        public void NullMap_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ProvisionAdmission a = A2AProvisionAdmission.Decide(null, Alice, new A2ARegistry());
            Assert.Equal(ProvisionKind.Refused, a.Kind);
        }

        // ---- multiagent refusal classes -----------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Multiagent_UnknownChannel_RefusedNoSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), "anything"), Alice, reg);
            Assert.Equal(ProvisionKind.Refused, a.Kind);
            Assert.Equal(A2AProvisionAdmission.DecisionNoSession, a.Decision);
        }

        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-uuid")]
        [Theory(Timeout = 60000)]
        public void Multiagent_MissingOrMalformedChannel_RefusedNoSession(string channel)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(channel, "anything"), Alice, reg);
            Assert.Equal(ProvisionKind.Refused, a.Kind);
            Assert.Equal(A2AProvisionAdmission.DecisionNoSession, a.Decision);
            Assert.Equal(channel is null or "" ? "-" : channel, a.Channel);
        }

        [Fact(Timeout = 60000)]
        public void Multiagent_ChannelIdOnly_IsNotTheChannel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer sends `channel`; a body carrying only channel_id names no session.
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            var m = Multiagent(null, s.Token);
            m["channel_id"] = OSD.FromString(Xor.ToString());
            ProvisionAdmission a = A2AProvisionAdmission.Decide(m, Alice, reg);
            Assert.Equal(A2AProvisionAdmission.DecisionNoSession, a.Decision);
        }

        [Fact(Timeout = 60000)]
        public void Multiagent_Stranger_RefusedNotParty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), Mallory, reg);
            Assert.Equal(ProvisionKind.Refused, a.Kind);
            {
                var assertedValue = a.Decision;
                TestAssert.WithMessage(() => Assert.Equal(A2AProvisionAdmission.DecisionNotParty, assertedValue), "even with the right token: the token is not the only gate");
            }
        }

        [InlineData("wrong")]
        [InlineData("")]
        [InlineData(null)]
        [Theory(Timeout = 60000)]
        public void Multiagent_WrongToken_RefusedBadToken(string creds)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), creds), Bob, reg);
            Assert.Equal(ProvisionKind.Refused, a.Kind);
            Assert.Equal(A2AProvisionAdmission.DecisionBadToken, a.Decision);
        }

        [Fact(Timeout = 60000)]
        public void Multiagent_TokenDiffersOnlyInCase_RefusedBadToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token.ToUpperInvariant()), Bob, reg);
            Assert.Equal(A2AProvisionAdmission.DecisionBadToken, a.Decision);
        }

        [Fact(Timeout = 60000)]
        public void Multiagent_BeforeCall_NoTokenMinted_RefusedBadToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // "start p2p voice" recorded the pair but no "call" has minted a token: nothing can match.
            var reg = new A2ARegistry();
            reg.Record(Alice, Bob, out _);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), ""), Bob, reg);
            Assert.Equal(A2AProvisionAdmission.DecisionBadToken, a.Decision);
        }

        // ---- the admit path -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Multiagent_PartyWithToken_Admitted_BothDirections()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            foreach (UUID party in new[] { Alice, Bob })
            {
                ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), party, reg);
                {
                    var assertedValue = a.Kind;
                    TestAssert.WithMessage(() => Assert.Equal(ProvisionKind.Multiagent, assertedValue), party.ToString());
                }
                Assert.True(a.Admitted);
                Assert.Equal(A2AProvisionAdmission.DecisionMultiagent, a.Decision);
                Assert.Equal(s.SessionId, a.Session.SessionId);
                Assert.Equal(Xor.ToString(), a.Channel);
            }
        }

        [Fact(Timeout = 60000)]
        public void Admission_DoesNotChangeState()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Deciding is not joining: only the handler's MarkProvisioned (after the service said yes) moves state.
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), Bob, reg);
            A2ASession after = reg.TryGet(Xor);
            Assert.Equal(A2ASessionState.Invited, after.State);
            Assert.False(after.CalleeProvisioned);
        }

        // ---- accept transition ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void CallerProvision_StaysInvited_CalleeProvision_IsTheAccept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);

            A2ASession r1 = reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");
            Assert.Equal(s.SessionId, r1.SessionId);
            {
                var assertedValue = r1.State;
                TestAssert.WithMessage(() => Assert.Equal(A2ASessionState.Invited, assertedValue), "the caller joining the room is not the accept");
            }
            Assert.True(r1.CallerProvisioned);
            Assert.Equal("vs-alice", r1.CallerViewerSession);
            Assert.False(r1.IsProvisioned(Bob));

            A2ASession r2 = reg.MarkProvisioned(s.SessionId, Bob, "vs-bob");
            Assert.Equal(A2ASessionState.Active, r2.State);
            Assert.True(r2.CalleeProvisioned);
            Assert.Equal("vs-bob", r2.CalleeViewerSession);
        }

        [Fact(Timeout = 60000)]
        public void CalleeFirst_AlsoAccepts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            Assert.Equal(A2ASessionState.Active, reg.MarkProvisioned(s.SessionId, Bob, "vs-bob").State);
        }

        [Fact(Timeout = 60000)]
        public void MarkProvisioned_UnknownOrStranger_ReturnsNull()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Assert.Null(reg.MarkProvisioned(Xor, Alice, "vs"));
            A2ASession s = Invited(reg);
            Assert.Null(reg.MarkProvisioned(s.SessionId, Mallory, "vs"));
            Assert.Equal(A2ASessionState.Invited, reg.TryGet(Xor).State);
        }

        [Fact(Timeout = 60000)]
        public void FindByViewerSession_ResolvesTheJoinedRecord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            Assert.Equal(s.SessionId, reg.FindByViewerSession(Alice, "vs-alice").SessionId);
            Assert.Equal(s.SessionId, reg.FindByViewerSession(Bob, "vs-bob").SessionId);
            {
                var assertedValue = reg.FindByViewerSession(Alice, "vs-bob");
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a viewer session is per party");
            }
            Assert.Null(reg.FindByViewerSession(Alice, null));
            Assert.Null(reg.FindByViewerSession(Mallory, "vs-alice"));
        }

        // ---- Active suppresses re-invite ----------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Call_WhileInvited_StillInvites()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.NotNull(o.Invite);
            Assert.Equal(Bob, o.Invite.Callee);
        }

        [Fact(Timeout = 60000)]
        public void Call_WhileActive_ReturnsCredentials_ButDoesNotRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = o.Body;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the caller's viewer still needs voice_credentials to (re)provision");
            }
            Assert.Equal(s.Token, ((OSDMap)o.Body["voice_credentials"])["channel_credentials"].AsString());
            {
                var assertedValue = o.Invite;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the callee is already in the room; a second ChatterBoxInvitation would be a second ring");
            }
            Assert.Contains("invite=suppressed-active", o.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void Call_ByStrangerOnLiveSession_403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Mallory, reg);
            Assert.Equal(HttpStatusCode.Forbidden, o.Status);
            Assert.Null(o.Invite);
        }

        [Fact(Timeout = 60000)]
        public void Call_OnUnknownSession_404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, new A2ARegistry());
            Assert.Equal(HttpStatusCode.NotFound, o.Status);
        }

        // ---- the invitation feedback loop -----------------------------------------------

        [Fact(Timeout = 60000)]
        public void Call_ByCallee_IssuesCredentials_ButNeverInvites()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer answers a received ChatterBoxInvitation with its own bare "call", which would
            // re-invite the caller, whose viewer would call again, without bound.
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Bob, reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = o.Body;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the callee's call still gets credentials");
            }
            {
                var assertedValue = o.Invite;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a call from the CALLEE must ring nobody");
            }
            Assert.Contains("invite=suppressed-callee", o.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void SecondCallerCall_AfterDeliveredInvite_DoesNotReRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ChatSessionOutcome first = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.NotNull(first.Invite);
            reg.MarkInviteSent(s.SessionId);                       // the module marks on decision=sent

            ChatSessionOutcome second = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.Equal(HttpStatusCode.OK, second.Status);
            Assert.NotNull(second.Body);
            {
                var assertedValue = second.Invite;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "one ring per Invited record");
            }
            Assert.Contains("invite=suppressed-already-sent", second.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void FailedDelivery_LeavesTheRetryFreeToRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The module marks InviteSent only on a confirmed enqueue; a callee-unreachable delivery
            // must not burn the one ring.
            var reg = new A2ARegistry();
            Invited(reg);
            Assert.NotNull(ChatSessionRequestLogic.Decide(CallBody(), Alice, reg).Invite);
            // no MarkInviteSent -> the retry still carries an invitation
            Assert.NotNull(ChatSessionRequestLogic.Decide(CallBody(), Alice, reg).Invite);
        }

        [Fact(Timeout = 60000)]
        public void FreshRecordAfterRemoval_InvitesAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            reg.MarkInviteSent(s.SessionId);
            ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);          // removed

            A2ASession fresh = Invited(reg);                                   // recreated pair
            Assert.False(fresh.InviteSent, "the flag lives and dies with the record");
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            {
                var assertedValue = o.Invite;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "a fresh invitation rings again");
            }
        }

        [Fact(Timeout = 60000)]
        public void MarkInviteSent_UnknownSession_IsANoOp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            reg.MarkInviteSent(Xor);                                           // nothing recorded
            Invited(reg);
            Assert.NotNull(ChatSessionRequestLogic.Decide(CallBody(), Alice, reg).Invite);
        }

        // ---- decline -------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Decline_ByCallee_RemovesTheRecord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.Null(o.Body);
            Assert.Null(o.Invite);
            Assert.Contains("decision=removed", o.Instrument);
            Assert.Null(reg.TryGet(Xor));
            Assert.Equal(0, reg.Count);
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByCallee_CarriesTheRemovedCall_SoTheCallerCanBeTold()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);
            Assert.NotNull(o.Declined);
            Assert.Equal(Alice, o.Declined.Caller);
            Assert.Equal(Bob, o.Declined.Callee);
            Assert.Equal(Xor, o.Declined.SessionId);
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByCaller_CarriesTheRemovedCall_ButTheCallerIsNotTheCallee()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Alice, reg);
            Assert.NotNull(o.Declined);
            {
                var assertedValue = o.Declined.Callee;
                TestAssert.WithMessage(() => Assert.NotEqual(Alice, assertedValue), "the module tells the caller only when the callee declined");
            }
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByStrangerOrForNoCall_CarriesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            Assert.Null(ChatSessionRequestLogic.Decide(DeclineBody(), Mallory, reg).Declined);
            Assert.Null(ChatSessionRequestLogic.Decide(DeclineBody(), Bob, new A2ARegistry()).Declined);
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByCaller_AlsoRemoves()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The caller cancelling its own outgoing call is a decline on the same session id.
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionRequestLogic.Decide(DeclineBody(), Alice, reg);
            Assert.Null(reg.TryGet(Xor));
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByStranger_IsIgnored()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Mallory, reg);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), "still the stub's 200: a stranger learns nothing");
            }
            Assert.Contains("decision=refused-not-party", o.Instrument);
            Assert.Equal(s.SessionId, reg.TryGet(Xor).SessionId);
        }

        [Fact(Timeout = 60000)]
        public void Decline_NoSession_IsTheOldStub()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Bob, new A2ARegistry());
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.Null(o.Body);
            Assert.Contains("decision=stub-ok", o.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void Decline_Registry_ReportsParty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Invited(reg);
            Assert.False(reg.Decline(Xor, Mallory, out bool p1));
            Assert.False(p1);
            Assert.True(reg.Decline(Xor, Bob, out bool p2));
            Assert.True(p2);
            Assert.False(reg.Decline(Xor, Bob, out bool p3), "already gone");
            Assert.False(p3);
        }

        [Fact(Timeout = 60000)]
        public void Decline_AfterDecline_ProvisionIsRefusedNoSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            string token = s.Token;
            ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), token), Alice, reg);
            {
                var assertedValue = a.Decision;
                TestAssert.WithMessage(() => Assert.Equal(A2AProvisionAdmission.DecisionNoSession, assertedValue), "a declined call's credentials open nothing");
            }
        }

        // ---- both-logout ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void OneLogout_KeepsTheRecord_BothLogout_RemovesIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);

            List<UUID> r1 = reg.MarkGone(Alice, "vs-alice");
            {
                var assertedValue = r1;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the other party is still in the room");
            }
            A2ASession after = reg.TryGet(Xor);
            Assert.Equal(s.SessionId, after.SessionId);
            Assert.False(after.CallerProvisioned);
            Assert.Null(after.CallerViewerSession);
            Assert.True(after.CalleeProvisioned);
            Assert.Equal(A2ASessionState.Active, after.State);

            List<UUID> r2 = reg.MarkGone(Bob, "vs-bob");
            Assert.Equal(new[] { Xor }, r2);
            Assert.Null(reg.TryGet(Xor));
        }

        [Fact(Timeout = 60000)]
        public void Logout_WithAnotherViewerSession_TouchesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A spatial logout (its own viewer_session) must not count as leaving the A2A room.
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            Assert.Empty(reg.MarkGone(Alice, "vs-spatial"));
            Assert.True(reg.TryGet(Xor).CallerProvisioned);
            Assert.Empty(reg.MarkGone(Alice, "vs-alice"));
            Assert.Empty(reg.MarkGone(Bob, "vs-spatial"));
            Assert.Equal(s.SessionId, reg.TryGet(Xor).SessionId);
        }

        [Fact(Timeout = 60000)]
        public void ClientClosed_IsThatPartyGoneFromEveryRecord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // OnClientClosed -> MarkGone(agent, null): no viewer session to match on.
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            Assert.Empty(reg.MarkGone(Alice, null));
            Assert.False(reg.TryGet(Xor).CallerProvisioned);
            {
                var assertedValue = reg.TryGet(Xor);
                TestAssert.WithMessage(() => Assert.Equal(s.SessionId, assertedValue.SessionId), "Bob is still in the room");
            }
            Assert.Equal(new[] { Xor }, reg.MarkGone(Bob, null));
            Assert.Null(reg.TryGet(Xor));
        }

        [Fact(Timeout = 60000)]
        public void ClientClosed_OnInvited_LeavesItToTheTtl()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");     // caller joined, callee not yet
            Assert.Empty(reg.MarkGone(Alice, null));
            {
                var assertedValue = reg.TryGet(Xor);
                TestAssert.WithMessage(() => Assert.Equal(s.SessionId, assertedValue.SessionId), "an unanswered invitation is not removed by the caller dropping; the invite TTL ends it");
            }
            Assert.Equal(A2ASessionState.Invited, reg.TryGet(Xor).State);
        }

        [Fact(Timeout = 60000)]
        public void Gone_IsReversible_ByALaterAdmittedProvision()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Crash then reconnect: the party re-provisions (same token) and is present again.
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            reg.MarkGone(Alice, null);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), Alice, reg);
            Assert.Equal(ProvisionKind.Multiagent, a.Kind);
            A2ASession back = reg.MarkProvisioned(s.SessionId, Alice, "vs-alice-2");
            Assert.True(back.CallerProvisioned);
            Assert.Equal("vs-alice-2", back.CallerViewerSession);
            {
                var assertedValue = reg.FindByViewerSession(Alice, "vs-alice");
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the old viewer session is forgotten");
            }
        }

        [Fact(Timeout = 60000)]
        public void AfterBothLogout_ACallRingsAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Live(reg);
            reg.MarkGone(Alice, "vs-alice");
            reg.MarkGone(Bob, "vs-bob");
            reg.Record(Alice, Bob, out bool created);
            Assert.True(created, "a fresh record, so a fresh token");
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.NotNull(o.Invite);
        }

        // ---- idle backstop -------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ActiveSession_ExpiresOnlyBeyondTheIdleBackstop()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            var reg = new A2ARegistry(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), () => now);
            A2ASession s = Live(reg);

            now = now.AddMinutes(30);
            {
                var assertedValue = reg.TryGet(Xor);
                TestAssert.WithMessage(() => Assert.Equal(s.SessionId, assertedValue.SessionId), "well past the invite TTL, but Active is measured against the idle backstop");
            }

            now = now.AddMinutes(29);
            Assert.Equal(s.SessionId, reg.TryGet(Xor).SessionId);

            now = now.AddMinutes(2);
            Assert.Null(reg.TryGet(Xor));
            Assert.Equal(0, reg.Count);
        }

        [Fact(Timeout = 60000)]
        public void Activity_RefreshesTheIdleBackstop()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            var reg = new A2ARegistry(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), () => now);
            A2ASession s = Live(reg);
            now = now.AddMinutes(50);
            reg.MarkGone(Alice, "vs-alice");                        // activity at +50
            now = now.AddMinutes(50);                               // +100: 50 since last activity
            Assert.Equal(s.SessionId, reg.TryGet(Xor).SessionId);
            now = now.AddMinutes(11);                               // +111: 61 since last activity
            Assert.Null(reg.TryGet(Xor));
        }

        [Fact(Timeout = 60000)]
        public void InvitedSession_StillExpiresOnTheInviteTtl()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            var reg = new A2ARegistry(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), () => now);
            A2ASession s = Invited(reg);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");    // caller in the room, still Invited
            now = now.AddMinutes(3);
            {
                var assertedValue = reg.TryGet(Xor);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "an unanswered invitation does not inherit the 8h backstop");
            }
        }

        [Fact(Timeout = 60000)]
        public void DefaultBackstop_IsEightHours()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromHours(8), A2ARegistry.DefaultActiveIdleTtl);
            Assert.Equal(TimeSpan.FromHours(8), new A2ARegistry().ActiveIdleTtl);
            {
                var assertedValue = new A2ARegistry(TimeSpan.FromMinutes(1), null).ActiveIdleTtl;
                TestAssert.WithMessage(() => Assert.Equal(TimeSpan.FromHours(8), assertedValue), "the two-arg ctor keeps the default backstop");
            }
        }

        // ---- room model ------------------------------------------------------------

        [InlineData(ProvisionKind.Local,true)]
        [InlineData(ProvisionKind.Multiagent,false)]
        [InlineData(ProvisionKind.Logout,false)]
        [InlineData(ProvisionKind.Refused,false)]
        [Theory(Timeout = 60000)]
        public void OnlyASpatialProvision_RecordsTheListenerRoom(ProvisionKind kind, bool records)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(records, A2AProvisionAdmission.RecordsListenerRoom(kind));
        }
    }
}
