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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void Logout_IsRecognisedBeforeChannelType()
        {
            var reg = new A2ARegistry();
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Logout("vs-1"), Alice, reg);
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Logout));
            Assert.That(a.Admitted, Is.True, "a logout body (channel_type absent) must be admitted so the service can see `logout`");
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionLogout));
            Assert.That(a.ChannelType, Is.Empty);
        }

        [Test]
        public void Logout_False_IsNotALogout()
        {
            OSDMap m = Logout("vs-1");
            m["logout"] = OSD.FromBoolean(false);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(m, Alice, new A2ARegistry());
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionOtherChannelType));
        }

        [Test]
        public void Local_IsAdmittedToTheParcelChecks()
        {
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Local(), Alice, new A2ARegistry());
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Local));
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionLocal));
            Assert.That(a.Session, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("group")]
        [TestCase("MULTIAGENT")]
        public void OtherChannelTypes_AreRefused(string channelType)
        {
            var m = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") };
            if (channelType != null) m["channel_type"] = OSD.FromString(channelType);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(m, Alice, new A2ARegistry());
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
            Assert.That(a.Admitted, Is.False);
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionOtherChannelType));
        }

        [Test]
        public void NullMap_IsRefused()
        {
            ProvisionAdmission a = A2AProvisionAdmission.Decide(null, Alice, new A2ARegistry());
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
        }

        // ---- multiagent refusal classes -----------------------------------------------------------

        [Test]
        public void Multiagent_UnknownChannel_RefusedNoSession()
        {
            var reg = new A2ARegistry();
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), "anything"), Alice, reg);
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionNoSession));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-uuid")]
        public void Multiagent_MissingOrMalformedChannel_RefusedNoSession(string channel)
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(channel, "anything"), Alice, reg);
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionNoSession));
            Assert.That(a.Channel, Is.EqualTo(channel is null or "" ? "-" : channel));
        }

        [Test]
        public void Multiagent_ChannelIdOnly_IsNotTheChannel()
        {
            // The viewer sends `channel`; a body carrying only channel_id names no session.
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            var m = Multiagent(null, s.Token);
            m["channel_id"] = OSD.FromString(Xor.ToString());
            ProvisionAdmission a = A2AProvisionAdmission.Decide(m, Alice, reg);
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionNoSession));
        }

        [Test]
        public void Multiagent_Stranger_RefusedNotParty()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), Mallory, reg);
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionNotParty), "even with the right token: the token is not the only gate");
        }

        [TestCase("wrong")]
        [TestCase("")]
        [TestCase(null)]
        public void Multiagent_WrongToken_RefusedBadToken(string creds)
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), creds), Bob, reg);
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Refused));
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionBadToken));
        }

        [Test]
        public void Multiagent_TokenDiffersOnlyInCase_RefusedBadToken()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token.ToUpperInvariant()), Bob, reg);
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionBadToken));
        }

        [Test]
        public void Multiagent_BeforeCall_NoTokenMinted_RefusedBadToken()
        {
            // "start p2p voice" recorded the pair but no "call" has minted a token: nothing can match.
            var reg = new A2ARegistry();
            reg.Record(Alice, Bob, out _);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), ""), Bob, reg);
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionBadToken));
        }

        // ---- the admit path -----------------------------------------------------------------------

        [Test]
        public void Multiagent_PartyWithToken_Admitted_BothDirections()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            foreach (UUID party in new[] { Alice, Bob })
            {
                ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), party, reg);
                Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Multiagent), party.ToString());
                Assert.That(a.Admitted, Is.True);
                Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionMultiagent));
                Assert.That(a.Session, Has.Property("SessionId").EqualTo(s.SessionId));
                Assert.That(a.Channel, Is.EqualTo(Xor.ToString()));
            }
        }

        [Test]
        public void Admission_DoesNotChangeState()
        {
            // Deciding is not joining: only the handler's MarkProvisioned (after the service said yes) moves state.
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), Bob, reg);
            A2ASession after = reg.TryGet(Xor);
            Assert.That(after.State, Is.EqualTo(A2ASessionState.Invited));
            Assert.That(after.CalleeProvisioned, Is.False);
        }

        // ---- accept transition ---------------------------------------------------------------------

        [Test]
        public void CallerProvision_StaysInvited_CalleeProvision_IsTheAccept()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);

            A2ASession r1 = reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");
            Assert.That(r1, Has.Property("SessionId").EqualTo(s.SessionId));
            Assert.That(r1.State, Is.EqualTo(A2ASessionState.Invited), "the caller joining the room is not the accept");
            Assert.That(r1.CallerProvisioned, Is.True);
            Assert.That(r1.CallerViewerSession, Is.EqualTo("vs-alice"));
            Assert.That(r1.IsProvisioned(Bob), Is.False);

            A2ASession r2 = reg.MarkProvisioned(s.SessionId, Bob, "vs-bob");
            Assert.That(r2.State, Is.EqualTo(A2ASessionState.Active));
            Assert.That(r2.CalleeProvisioned, Is.True);
            Assert.That(r2.CalleeViewerSession, Is.EqualTo("vs-bob"));
        }

        [Test]
        public void CalleeFirst_AlsoAccepts()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            Assert.That(reg.MarkProvisioned(s.SessionId, Bob, "vs-bob").State, Is.EqualTo(A2ASessionState.Active));
        }

        [Test]
        public void MarkProvisioned_UnknownOrStranger_ReturnsNull()
        {
            var reg = new A2ARegistry();
            Assert.That(reg.MarkProvisioned(Xor, Alice, "vs"), Is.Null);
            A2ASession s = Invited(reg);
            Assert.That(reg.MarkProvisioned(s.SessionId, Mallory, "vs"), Is.Null);
            Assert.That(reg.TryGet(Xor).State, Is.EqualTo(A2ASessionState.Invited));
        }

        [Test]
        public void FindByViewerSession_ResolvesTheJoinedRecord()
        {
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            Assert.That(reg.FindByViewerSession(Alice, "vs-alice"), Has.Property("SessionId").EqualTo(s.SessionId));
            Assert.That(reg.FindByViewerSession(Bob, "vs-bob"), Has.Property("SessionId").EqualTo(s.SessionId));
            Assert.That(reg.FindByViewerSession(Alice, "vs-bob"), Is.Null, "a viewer session is per party");
            Assert.That(reg.FindByViewerSession(Alice, null), Is.Null);
            Assert.That(reg.FindByViewerSession(Mallory, "vs-alice"), Is.Null);
        }

        // ---- Active suppresses re-invite ----------------------------------------------------------

        [Test]
        public void Call_WhileInvited_StillInvites()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Invite, Is.Not.Null);
            Assert.That(o.Invite.Callee, Is.EqualTo(Bob));
        }

        [Test]
        public void Call_WhileActive_ReturnsCredentials_ButDoesNotRing()
        {
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Body, Is.Not.Null, "the caller's viewer still needs voice_credentials to (re)provision");
            Assert.That(((OSDMap)o.Body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(s.Token));
            Assert.That(o.Invite, Is.Null, "the callee is already in the room; a second ChatterBoxInvitation would be a second ring");
            Assert.That(o.Instrument, Does.Contain("invite=suppressed-active"));
        }

        [Test]
        public void Call_ByStrangerOnLiveSession_403()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Mallory, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(o.Invite, Is.Null);
        }

        [Test]
        public void Call_OnUnknownSession_404()
        {
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, new A2ARegistry());
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound));
        }

        // ---- the invitation feedback loop -----------------------------------------------

        [Test]
        public void Call_ByCallee_IssuesCredentials_ButNeverInvites()
        {
            // The viewer answers a received ChatterBoxInvitation with its own bare "call", which would
            // re-invite the caller, whose viewer would call again, without bound.
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Bob, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Body, Is.Not.Null, "the callee's call still gets credentials");
            Assert.That(o.Invite, Is.Null, "a call from the CALLEE must ring nobody");
            Assert.That(o.Instrument, Does.Contain("invite=suppressed-callee"));
        }

        [Test]
        public void SecondCallerCall_AfterDeliveredInvite_DoesNotReRing()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ChatSessionOutcome first = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(first.Invite, Is.Not.Null);
            reg.MarkInviteSent(s.SessionId);                       // the module marks on decision=sent

            ChatSessionOutcome second = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.Body, Is.Not.Null);
            Assert.That(second.Invite, Is.Null, "one ring per Invited record");
            Assert.That(second.Instrument, Does.Contain("invite=suppressed-already-sent"));
        }

        [Test]
        public void FailedDelivery_LeavesTheRetryFreeToRing()
        {
            // The module marks InviteSent only on a confirmed enqueue; a callee-unreachable delivery
            // must not burn the one ring.
            var reg = new A2ARegistry();
            Invited(reg);
            Assert.That(ChatSessionRequestLogic.Decide(CallBody(), Alice, reg).Invite, Is.Not.Null);
            // no MarkInviteSent -> the retry still carries an invitation
            Assert.That(ChatSessionRequestLogic.Decide(CallBody(), Alice, reg).Invite, Is.Not.Null);
        }

        [Test]
        public void FreshRecordAfterRemoval_InvitesAgain()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            reg.MarkInviteSent(s.SessionId);
            ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);          // removed

            A2ASession fresh = Invited(reg);                                   // recreated pair
            Assert.That(fresh.InviteSent, Is.False, "the flag lives and dies with the record");
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(o.Invite, Is.Not.Null, "a fresh invitation rings again");
        }

        [Test]
        public void MarkInviteSent_UnknownSession_IsANoOp()
        {
            var reg = new A2ARegistry();
            reg.MarkInviteSent(Xor);                                           // nothing recorded
            Invited(reg);
            Assert.That(ChatSessionRequestLogic.Decide(CallBody(), Alice, reg).Invite, Is.Not.Null);
        }

        // ---- decline -------------------------------------------------------------------------------

        [Test]
        public void Decline_ByCallee_RemovesTheRecord()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Body, Is.Null);
            Assert.That(o.Invite, Is.Null);
            Assert.That(o.Instrument, Does.Contain("decision=removed"));
            Assert.That(reg.TryGet(Xor), Is.Null);
            Assert.That(reg.Count, Is.EqualTo(0));
        }

        [Test]
        public void Decline_ByCallee_CarriesTheRemovedCall_SoTheCallerCanBeTold()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);
            Assert.That(o.Declined, Is.Not.Null);
            Assert.That(o.Declined.Caller, Is.EqualTo(Alice));
            Assert.That(o.Declined.Callee, Is.EqualTo(Bob));
            Assert.That(o.Declined.SessionId, Is.EqualTo(Xor));
        }

        [Test]
        public void Decline_ByCaller_CarriesTheRemovedCall_ButTheCallerIsNotTheCallee()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Alice, reg);
            Assert.That(o.Declined, Is.Not.Null);
            Assert.That(o.Declined.Callee, Is.Not.EqualTo(Alice), "the module tells the caller only when the callee declined");
        }

        [Test]
        public void Decline_ByStrangerOrForNoCall_CarriesNothing()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            Assert.That(ChatSessionRequestLogic.Decide(DeclineBody(), Mallory, reg).Declined, Is.Null);
            Assert.That(ChatSessionRequestLogic.Decide(DeclineBody(), Bob, new A2ARegistry()).Declined, Is.Null);
        }

        [Test]
        public void Decline_ByCaller_AlsoRemoves()
        {
            // The caller cancelling its own outgoing call is a decline on the same session id.
            var reg = new A2ARegistry();
            Invited(reg);
            ChatSessionRequestLogic.Decide(DeclineBody(), Alice, reg);
            Assert.That(reg.TryGet(Xor), Is.Null);
        }

        [Test]
        public void Decline_ByStranger_IsIgnored()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Mallory, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), "still the stub's 200: a stranger learns nothing");
            Assert.That(o.Instrument, Does.Contain("decision=refused-not-party"));
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId));
        }

        [Test]
        public void Decline_NoSession_IsTheOldStub()
        {
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(DeclineBody(), Bob, new A2ARegistry());
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Body, Is.Null);
            Assert.That(o.Instrument, Does.Contain("decision=stub-ok"));
        }

        [Test]
        public void Decline_Registry_ReportsParty()
        {
            var reg = new A2ARegistry();
            Invited(reg);
            Assert.That(reg.Decline(Xor, Mallory, out bool p1), Is.False);
            Assert.That(p1, Is.False);
            Assert.That(reg.Decline(Xor, Bob, out bool p2), Is.True);
            Assert.That(p2, Is.True);
            Assert.That(reg.Decline(Xor, Bob, out bool p3), Is.False, "already gone");
            Assert.That(p3, Is.False);
        }

        [Test]
        public void Decline_AfterDecline_ProvisionIsRefusedNoSession()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            string token = s.Token;
            ChatSessionRequestLogic.Decide(DeclineBody(), Bob, reg);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), token), Alice, reg);
            Assert.That(a.Decision, Is.EqualTo(A2AProvisionAdmission.DecisionNoSession), "a declined call's credentials open nothing");
        }

        // ---- both-logout ---------------------------------------------------------------------------

        [Test]
        public void OneLogout_KeepsTheRecord_BothLogout_RemovesIt()
        {
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);

            List<UUID> r1 = reg.MarkGone(Alice, "vs-alice");
            Assert.That(r1, Is.Empty, "the other party is still in the room");
            A2ASession after = reg.TryGet(Xor);
            Assert.That(after, Has.Property("SessionId").EqualTo(s.SessionId));
            Assert.That(after.CallerProvisioned, Is.False);
            Assert.That(after.CallerViewerSession, Is.Null);
            Assert.That(after.CalleeProvisioned, Is.True);
            Assert.That(after.State, Is.EqualTo(A2ASessionState.Active));

            List<UUID> r2 = reg.MarkGone(Bob, "vs-bob");
            Assert.That(r2, Is.EqualTo(new[] { Xor }));
            Assert.That(reg.TryGet(Xor), Is.Null);
        }

        [Test]
        public void Logout_WithAnotherViewerSession_TouchesNothing()
        {
            // A spatial logout (its own viewer_session) must not count as leaving the A2A room.
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            Assert.That(reg.MarkGone(Alice, "vs-spatial"), Is.Empty);
            Assert.That(reg.TryGet(Xor).CallerProvisioned, Is.True);
            Assert.That(reg.MarkGone(Alice, "vs-alice"), Is.Empty);
            Assert.That(reg.MarkGone(Bob, "vs-spatial"), Is.Empty);
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId));
        }

        [Test]
        public void ClientClosed_IsThatPartyGoneFromEveryRecord()
        {
            // OnClientClosed -> MarkGone(agent, null): no viewer session to match on.
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            Assert.That(reg.MarkGone(Alice, null), Is.Empty);
            Assert.That(reg.TryGet(Xor).CallerProvisioned, Is.False);
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId), "Bob is still in the room");
            Assert.That(reg.MarkGone(Bob, null), Is.EqualTo(new[] { Xor }));
            Assert.That(reg.TryGet(Xor), Is.Null);
        }

        [Test]
        public void ClientClosed_OnInvited_LeavesItToTheTtl()
        {
            var reg = new A2ARegistry();
            A2ASession s = Invited(reg);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");     // caller joined, callee not yet
            Assert.That(reg.MarkGone(Alice, null), Is.Empty);
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId), "an unanswered invitation is not removed by the caller dropping; the invite TTL ends it");
            Assert.That(reg.TryGet(Xor).State, Is.EqualTo(A2ASessionState.Invited));
        }

        [Test]
        public void Gone_IsReversible_ByALaterAdmittedProvision()
        {
            // Crash then reconnect: the party re-provisions (same token) and is present again.
            var reg = new A2ARegistry();
            A2ASession s = Live(reg);
            reg.MarkGone(Alice, null);
            ProvisionAdmission a = A2AProvisionAdmission.Decide(Multiagent(Xor.ToString(), s.Token), Alice, reg);
            Assert.That(a.Kind, Is.EqualTo(ProvisionKind.Multiagent));
            A2ASession back = reg.MarkProvisioned(s.SessionId, Alice, "vs-alice-2");
            Assert.That(back.CallerProvisioned, Is.True);
            Assert.That(back.CallerViewerSession, Is.EqualTo("vs-alice-2"));
            Assert.That(reg.FindByViewerSession(Alice, "vs-alice"), Is.Null, "the old viewer session is forgotten");
        }

        [Test]
        public void AfterBothLogout_ACallRingsAgain()
        {
            var reg = new A2ARegistry();
            Live(reg);
            reg.MarkGone(Alice, "vs-alice");
            reg.MarkGone(Bob, "vs-bob");
            reg.Record(Alice, Bob, out bool created);
            Assert.That(created, Is.True, "a fresh record, so a fresh token");
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, reg);
            Assert.That(o.Invite, Is.Not.Null);
        }

        // ---- idle backstop -------------------------------------------------------------------------

        [Test]
        public void ActiveSession_ExpiresOnlyBeyondTheIdleBackstop()
        {
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            var reg = new A2ARegistry(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), () => now);
            A2ASession s = Live(reg);

            now = now.AddMinutes(30);
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId), "well past the invite TTL, but Active is measured against the idle backstop");

            now = now.AddMinutes(29);
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId));

            now = now.AddMinutes(2);
            Assert.That(reg.TryGet(Xor), Is.Null);
            Assert.That(reg.Count, Is.EqualTo(0));
        }

        [Test]
        public void Activity_RefreshesTheIdleBackstop()
        {
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            var reg = new A2ARegistry(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), () => now);
            A2ASession s = Live(reg);
            now = now.AddMinutes(50);
            reg.MarkGone(Alice, "vs-alice");                        // activity at +50
            now = now.AddMinutes(50);                               // +100: 50 since last activity
            Assert.That(reg.TryGet(Xor), Has.Property("SessionId").EqualTo(s.SessionId));
            now = now.AddMinutes(11);                               // +111: 61 since last activity
            Assert.That(reg.TryGet(Xor), Is.Null);
        }

        [Test]
        public void InvitedSession_StillExpiresOnTheInviteTtl()
        {
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            var reg = new A2ARegistry(TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), () => now);
            A2ASession s = Invited(reg);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-alice");    // caller in the room, still Invited
            now = now.AddMinutes(3);
            Assert.That(reg.TryGet(Xor), Is.Null, "an unanswered invitation does not inherit the 8h backstop");
        }

        [Test]
        public void DefaultBackstop_IsEightHours()
        {
            Assert.That(A2ARegistry.DefaultActiveIdleTtl, Is.EqualTo(TimeSpan.FromHours(8)));
            Assert.That(new A2ARegistry().ActiveIdleTtl, Is.EqualTo(TimeSpan.FromHours(8)));
            Assert.That(new A2ARegistry(TimeSpan.FromMinutes(1), null).ActiveIdleTtl, Is.EqualTo(TimeSpan.FromHours(8)), "the two-arg ctor keeps the default backstop");
        }

        // ---- room model ------------------------------------------------------------

        [TestCase(ProvisionKind.Local, true)]
        [TestCase(ProvisionKind.Multiagent, false)]
        [TestCase(ProvisionKind.Logout, false)]
        [TestCase(ProvisionKind.Refused, false)]
        public void OnlyASpatialProvision_RecordsTheListenerRoom(ProvisionKind kind, bool records)
        {
            Assert.That(A2AProvisionAdmission.RecordsListenerRoom(kind), Is.EqualTo(records));
        }
    }
}
