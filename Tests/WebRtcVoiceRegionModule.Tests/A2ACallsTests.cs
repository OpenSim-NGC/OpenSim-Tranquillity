/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the avatar-to-avatar invitation registry and the pure
 * ChatSessionRequest decision logic. No Scene, no HTTP: Decide() is the seam the handler adapts.
 *
 * Wire facts asserted here (SL and Firestorm llimview.cpp and llvoicechannel.cpp):
 *  - "start p2p voice" body: method, session-id (viewer XOR), params (other agent UUID), alt_params.
 *  - "call" expects voice_credentials { channel_uri, channel_credentials } in the HTTP response body.
 */
using System;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2ACallsTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");

        private sealed class FakeClock
        {
            public DateTime Now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            public DateTime Get() => Now;
        }

        private static A2ARegistry NewRegistry(FakeClock clock, TimeSpan? ttl = null)
            => new A2ARegistry(ttl ?? TimeSpan.FromMinutes(2), clock.Get);

        // ---- session id ------------------------------------------------------------------------

        [Test]
        public void ComputeSessionId_IsTheViewerXor_AndSymmetric()
        {
            UUID ab = A2ARegistry.ComputeSessionId(Alice, Bob);
            UUID ba = A2ARegistry.ComputeSessionId(Bob, Alice);
            Assert.That(ab, Is.EqualTo(ba), "XOR is commutative: both parties derive the same id");
            Assert.That(ab, Is.EqualTo(new UUID(Alice.ulonga ^ Bob.ulonga, Alice.ulongb ^ Bob.ulongb)),
                "must match the viewer's computeSessionID (llimview.cpp:2561) and the old handler's formula");
            Assert.That(ab, Is.Not.EqualTo(UUID.Zero));
        }

        // ---- record -----------------------------------------------------------------------------

        [Test]
        public void Record_CreatesInvitedSession_WithCallerCalleeAndTimestamps()
        {
            var clock = new FakeClock();
            var reg = NewRegistry(clock);

            A2ASession s = reg.Record(Alice, Bob, out bool created);

            Assert.That(created, Is.True);
            Assert.That(s.SessionId, Is.EqualTo(A2ARegistry.ComputeSessionId(Alice, Bob)));
            Assert.That(s.Caller, Is.EqualTo(Alice));
            Assert.That(s.Callee, Is.EqualTo(Bob));
            Assert.That(s.State, Is.EqualTo(A2ASessionState.Invited));
            Assert.That(s.Token, Is.Null, "no token until a party calls");
            Assert.That(s.CreatedUtc, Is.EqualTo(clock.Now));
            Assert.That(s.LastSeenUtc, Is.EqualTo(clock.Now));
            Assert.That(s.ChannelUri, Is.EqualTo(s.SessionId.ToString()));
            Assert.That(reg.Count, Is.EqualTo(1));
            Assert.That(reg.TryGet(s.SessionId), Has.Property("SessionId").EqualTo(s.SessionId));
            Assert.That(reg.TryGetByChannel(s.ChannelUri), Has.Property("SessionId").EqualTo(s.SessionId), "lookup by the provision's channel string");
        }

        [Test]
        public void Record_SamePairAgain_RefreshesNotDuplicates_AndKeepsToken()
        {
            var clock = new FakeClock();
            var reg = NewRegistry(clock);
            A2ASession first = reg.Record(Alice, Bob, out _);
            string token = reg.IssueToken(first.SessionId, Alice).Token;

            clock.Now = clock.Now.AddSeconds(30);
            A2ASession again = reg.Record(Alice, Bob, out bool created);

            Assert.That(created, Is.False);
            Assert.That(again, Has.Property("SessionId").EqualTo(first.SessionId));
            Assert.That(again.LastSeenUtc, Is.EqualTo(clock.Now), "a retry refreshes the TTL");
            Assert.That(again.Token, Is.EqualTo(token), "a retry must not invalidate credentials already handed out");
            Assert.That(reg.Count, Is.EqualTo(1));
        }

        [Test]
        public void Record_FromTheOtherDirection_IsTheSameSession()
        {
            var clock = new FakeClock();
            var reg = NewRegistry(clock);
            A2ASession ab = reg.Record(Alice, Bob, out _);
            A2ASession ba = reg.Record(Bob, Alice, out bool created);

            Assert.That(created, Is.False);
            Assert.That(ba, Has.Property("SessionId").EqualTo(ab.SessionId));
            Assert.That(ab.IsParty(Alice) && ab.IsParty(Bob), Is.True);
            Assert.That(ab.OtherParty(Alice), Is.EqualTo(Bob));
            Assert.That(ab.OtherParty(Bob), Is.EqualTo(Alice));
            Assert.That(ab.OtherParty(Carol), Is.EqualTo(UUID.Zero));
        }

        // ---- token ------------------------------------------------------------------------------

        [Test]
        public void IssueToken_MintsOnce_ForEitherParty_AndRefusesStrangers()
        {
            var clock = new FakeClock();
            var reg = NewRegistry(clock);
            A2ASession s = reg.Record(Alice, Bob, out _);

            A2ASession byAlice = reg.IssueToken(s.SessionId, Alice);
            Assert.That(byAlice, Has.Property("SessionId").EqualTo(s.SessionId));
            Assert.That(byAlice.Token, Is.Not.Null.And.Length.EqualTo(A2ARegistry.TokenBytes * 2), "32 random bytes as lowercase hex");
            Assert.That(byAlice.Token, Does.Match("^[0-9a-f]+$"));

            A2ASession byBob = reg.IssueToken(s.SessionId, Bob);
            Assert.That(byBob.Token, Is.EqualTo(byAlice.Token), "one token per session, shared by both parties");

            Assert.That(reg.IssueToken(s.SessionId, Carol), Is.Null, "a non-party gets nothing");
            Assert.That(reg.IssueToken(UUID.Random(), Alice), Is.Null, "an unknown session gets nothing");
        }

        [Test]
        public void Tokens_AreUniquePerSession()
        {
            var reg = NewRegistry(new FakeClock());
            A2ASession ab = reg.Record(Alice, Bob, out _);
            A2ASession ac = reg.Record(Alice, Carol, out _);
            string t1 = reg.IssueToken(ab.SessionId, Alice).Token;
            string t2 = reg.IssueToken(ac.SessionId, Alice).Token;
            Assert.That(t1, Is.Not.EqualTo(t2));
        }

        // ---- decline / remove ---------------------------------------------------------------------

        [Test]
        public void Remove_DropsTheRecord_AndIsFalseWhenAbsent()
        {
            var reg = NewRegistry(new FakeClock());
            A2ASession s = reg.Record(Alice, Bob, out _);
            Assert.That(reg.Remove(s.SessionId), Is.True);
            Assert.That(reg.TryGet(s.SessionId), Is.Null);
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(reg.Remove(s.SessionId), Is.False, "declining an absent invitation is a no-op");
            Assert.That(reg.Remove(UUID.Random()), Is.False);
        }

        // ---- TTL --------------------------------------------------------------------------------

        [Test]
        public void UnansweredInvitation_ExpiresAfterTtl_ButNotBefore()
        {
            var clock = new FakeClock();
            var reg = NewRegistry(clock, TimeSpan.FromMinutes(2));
            A2ASession s = reg.Record(Alice, Bob, out _);

            clock.Now = clock.Now.AddMinutes(2);                 // exactly at TTL: still alive
            Assert.That(reg.TryGet(s.SessionId), Is.Not.Null);

            clock.Now = clock.Now.AddSeconds(1);                 // past TTL: swept
            Assert.That(reg.TryGet(s.SessionId), Is.Null);
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(reg.IssueToken(s.SessionId, Alice), Is.Null, "an expired invitation cannot be called");
        }

        [Test]
        public void Activity_RefreshesTtl()
        {
            var clock = new FakeClock();
            var reg = NewRegistry(clock, TimeSpan.FromMinutes(2));
            A2ASession s = reg.Record(Alice, Bob, out _);
            clock.Now = clock.Now.AddMinutes(1.5);
            reg.IssueToken(s.SessionId, Alice);                  // the caller's "call" refreshes
            clock.Now = clock.Now.AddMinutes(1.5);               // 3.0 after record, 1.5 after the call
            Assert.That(reg.TryGet(s.SessionId), Is.Not.Null);
            clock.Now = clock.Now.AddMinutes(0.6);
            Assert.That(reg.TryGet(s.SessionId), Is.Null);
        }

        [Test]
        public void Registry_IsSafeUnderConcurrentUse()
        {
            var reg = new A2ARegistry();
            var threads = new System.Threading.Thread[8];
            Exception failure = null;
            for (int i = 0; i < threads.Length; i++)
            {
                int n = i;
                threads[i] = new System.Threading.Thread(() =>
                {
                    try
                    {
                        UUID other = new UUID($"aaaaaaaa-aaaa-aaaa-aaaa-{n:D12}");
                        for (int k = 0; k < 500; k++)
                        {
                            A2ASession s = reg.Record(Alice, other, out _);
                            reg.IssueToken(s.SessionId, other);
                            reg.TryGetByChannel(s.ChannelUri);
                            if (k % 50 == 49) reg.Remove(s.SessionId);
                        }
                    }
                    catch (Exception e) { failure ??= e; }
                });
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();
            Assert.That(failure, Is.Null);
        }
    }

    [TestFixture]
    public class ChatSessionRequestLogicTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Xor = A2ARegistry.ComputeSessionId(Alice, Bob);

        private static OSDMap StartBody(UUID sessionId, OSD @params = null)
        {
            var m = new OSDMap
            {
                ["method"] = OSD.FromString("start p2p voice"),
                ["session-id"] = OSD.FromUUID(sessionId),
                ["alt_params"] = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") },
            };
            if (@params != null) m["params"] = @params;
            return m;
        }

        private static OSDMap CallBody(UUID sessionId) => new OSDMap
        {
            ["method"] = OSD.FromString("call"),
            ["session-id"] = OSD.FromUUID(sessionId),
            ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
        };

        // ---- "start p2p voice" ----------------------------------------------------------------------

        [Test]
        public void StartP2PVoice_WithParams_RecordsPair_AndEchoesTheViewerSessionId()
        {
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromUUID(Bob)), Alice, reg);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Body, Is.Null, "no HTTP body; the reply is an event");
            Assert.That(o.Reply, Is.Not.Null);
            Assert.That(o.Reply.SessionId, Is.EqualTo(Xor), "session_id = the XOR");
            Assert.That(o.Reply.TempSessionId, Is.EqualTo(Xor), "temp_session_id echoes the viewer's session-id (llimview.cpp:4881)");
            A2ASession s = reg.TryGet(Xor);
            Assert.That(s, Is.Not.Null);
            Assert.That(s.Caller, Is.EqualTo(Alice));
            Assert.That(s.Callee, Is.EqualTo(Bob));
            Assert.That(o.Instrument, Does.StartWith(ChatSessionRequestLogic.InstrumentTag).And.Contain("decision=recorded").And.Contain("match=true"));
        }

        [Test]
        public void StartP2PVoice_ParamsAbsent_Is400_NothingRecorded()
        {
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Xor), Alice, reg);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.BadRequest), "replaces the UUID.Random() fallback");
            Assert.That(o.Reply, Is.Null);
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(o.Instrument, Does.Contain("decision=refused-400").And.Contain("params"));
        }

        [TestCase("not-a-uuid")]
        [TestCase("")]
        public void StartP2PVoice_ParamsUnparseable_Is400(string bad)
        {
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromString(bad)), Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(reg.Count, Is.EqualTo(0));
        }

        [Test]
        public void StartP2PVoice_ParamsIsSelf_Is400()
        {
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Alice, OSD.FromUUID(Alice)), Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(reg.Count, Is.EqualTo(0));
        }

        [Test]
        public void StartP2PVoice_ViewerSessionIdMismatch_StillRecords_ButInstrumentSaysSo()
        {
            // The sim derives the id itself; a viewer sending a different session-id still gets its echo
            // (temp_session_id) so it can re-key (llimview.cpp:1712-1716), and the instrument flags it.
            var reg = new A2ARegistry();
            UUID odd = UUID.Random();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(odd, OSD.FromUUID(Bob)), Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Reply.SessionId, Is.EqualTo(Xor));
            Assert.That(o.Reply.TempSessionId, Is.EqualTo(odd));
            Assert.That(o.Instrument, Does.Contain("match=false"));
        }

        // ---- "call" -----------------------------------------------------------------------------------

        [Test]
        public void Call_ByAParty_Returns200_WithVoiceCredentialsInTheBody()
        {
            var reg = new A2ARegistry();
            ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromUUID(Bob)), Alice, reg);

            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(Xor), Alice, reg);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Reply, Is.Null, "call is answered in the HTTP body, not by event (llvoicechannel.cpp:687)");
            Assert.That(o.Body, Is.Not.Null);
            Assert.That(o.Body.ContainsKey("voice_credentials"), Is.True);
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            // The viewer's caller stores this map as mChannelInfo and routes/compares by
            // voice_server_type (llvoicechannel.cpp:687, :465-469; llvoicewebrtc.cpp:1682-1687).
            Assert.That(creds["voice_server_type"].AsString(), Is.EqualTo("webrtc"));
            Assert.That(creds.ContainsKey("sip_uri"), Is.False);
            Assert.That(creds["channel_uri"].AsString(), Is.EqualTo(Xor.ToString()), "channel_uri = the XOR session id as a string; becomes the provision's `channel`");
            string token = creds["channel_credentials"].AsString();
            Assert.That(token, Is.EqualTo(reg.TryGet(Xor).Token).And.Length.EqualTo(64));
            Assert.That(o.Instrument, Does.Contain("decision=credentials-issued").And.Not.Contain(token), "the token never appears in the log");

            // The callee's own "call" (after it accepts) yields the SAME credentials
            ChatSessionOutcome byBob = ChatSessionRequestLogic.Decide(CallBody(Xor), Bob, reg);
            Assert.That(((OSDMap)byBob.Body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(token));

            // and the body serialises as LLSD XML the way the handler will send it
            string xml = OSDParser.SerializeLLSDXmlString(o.Body);
            Assert.That(xml, Does.Contain("voice_credentials").And.Contain("channel_uri").And.Contain("channel_credentials"));
        }

        [Test]
        public void Call_WithoutInvitation_Is404_NoBody()
        {
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(Xor), Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(o.Body, Is.Null);
            Assert.That(o.Instrument, Does.Contain("decision=refused-404"));
        }

        [Test]
        public void Call_ByAStranger_Is403()
        {
            // A live invitation session that the caller is not a party of is 403, distinct from 404 unknown.
            var reg = new A2ARegistry();
            ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromUUID(Bob)), Alice, reg);
            UUID carol = UUID.Random();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(Xor), carol, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(o.Instrument, Does.Contain("decision=refused-403"));
            Assert.That(reg.TryGet(Xor).Token, Is.Null, "a stranger's call mints nothing");
        }

        // ---- the rest of the switch: stubs (decline p2p voice has real behaviour, see A2ALifecycleTests) --

        [TestCase("decline invitation")]
        [TestCase("start conference")]
        public void Stubs_Return200_NoBody(string method)
        {
            var reg = new A2ARegistry();
            var m = new OSDMap { ["method"] = OSD.FromString(method), ["session-id"] = OSD.FromUUID(Xor) };
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(m, Alice, reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Body, Is.Null);
            Assert.That(o.Reply, Is.Null);
            Assert.That(o.Instrument, Does.Contain("decision=stub-ok"));
        }

        /// <summary>
        /// "fetch history" is not a stub. A stub would answer 200 with NO BODY, which the
        /// viewer's chatterBoxHistoryCoro type-checks and rejects -- LLSD history =
        /// result[HTTP_RESULTS_CONTENT]; if (session and history.isArray()) ... else
        /// LL_WARNS("Bad array data fetching chat history") (llimview.cpp:816-859). So every
        /// conference and every group chat that opened made the viewer log a complaint.
        ///
        /// We keep no history, so the honest answer is an EMPTY ARRAY: the viewer's size()==0
        /// branch is a silent no-op ("Empty history from chat server, nothing to add", :852-855).
        /// It says "none", which is true, in the one shape that says it without being malformed.
        /// </summary>
        [Test]
        public void FetchHistory_AnswersAnEmptyArray_NotAnEmptyBody()
        {
            var reg = new A2ARegistry();
            var m = new OSDMap { ["method"] = OSD.FromString("fetch history"), ["session-id"] = OSD.FromUUID(Xor) };
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(m, Alice, reg);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.BodyArray, Is.Not.Null, "an ARRAY, or the viewer logs 'Bad array data'");
            Assert.That(o.BodyArray.Count, Is.EqualTo(0), "we keep no history; empty is the truthful answer");
            Assert.That(o.Body, Is.Null, "a MAP here would take the viewer's bad-data branch");
            Assert.That(o.Reply, Is.Null);
            Assert.That(o.Instrument, Does.Contain("decision=history-empty"));
        }

        [Test]
        public void UnknownMethod_Is400_MissingFields_Are404_NoBody_Is204()
        {
            var reg = new A2ARegistry();
            Assert.That(ChatSessionRequestLogic.Decide(new OSDMap { ["method"] = OSD.FromString("nope"), ["session-id"] = OSD.FromUUID(Xor) }, Alice, reg).Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ChatSessionRequestLogic.Decide(new OSDMap { ["session-id"] = OSD.FromUUID(Xor) }, Alice, reg).Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(ChatSessionRequestLogic.Decide(new OSDMap { ["method"] = OSD.FromString("call") }, Alice, reg).Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(ChatSessionRequestLogic.Decide(null, Alice, reg).Status, Is.EqualTo(HttpStatusCode.NoContent));
        }

        [Test]
        public void MethodMatching_IsCaseInsensitive_LikeTheOldSwitch()
        {
            var reg = new A2ARegistry();
            var m = new OSDMap { ["method"] = OSD.FromString("Start P2P Voice"), ["session-id"] = OSD.FromUUID(Xor), ["params"] = OSD.FromUUID(Bob) };
            Assert.That(ChatSessionRequestLogic.Decide(m, Alice, reg).Status, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
