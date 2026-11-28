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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void ComputeSessionId_IsTheViewerXor_AndSymmetric()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID ab = A2ARegistry.ComputeSessionId(Alice, Bob);
            UUID ba = A2ARegistry.ComputeSessionId(Bob, Alice);
            {
                var assertedValue = ab;
                TestAssert.WithMessage(() => Assert.Equal(ba, assertedValue), "XOR is commutative: both parties derive the same id");
            }
            {
                var assertedValue = ab;
                TestAssert.WithMessage(() => Assert.Equal(new UUID(Alice.ulonga ^ Bob.ulonga, Alice.ulongb ^ Bob.ulongb), assertedValue), "must match the viewer's computeSessionID (llimview.cpp:2561) and the old handler's formula");
            }
            Assert.NotEqual(UUID.Zero, ab);
        }

        // ---- record -----------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Record_CreatesInvitedSession_WithCallerCalleeAndTimestamps()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var clock = new FakeClock();
            var reg = NewRegistry(clock);

            A2ASession s = reg.Record(Alice, Bob, out bool created);

            Assert.True(created);
            Assert.Equal(A2ARegistry.ComputeSessionId(Alice, Bob), s.SessionId);
            Assert.Equal(Alice, s.Caller);
            Assert.Equal(Bob, s.Callee);
            Assert.Equal(A2ASessionState.Invited, s.State);
            {
                var assertedValue = s.Token;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no token until a party calls");
            }
            Assert.Equal(clock.Now, s.CreatedUtc);
            Assert.Equal(clock.Now, s.LastSeenUtc);
            Assert.Equal(s.SessionId.ToString(), s.ChannelUri);
            Assert.Equal(1, reg.Count);
            Assert.Equal(s.SessionId, reg.TryGet(s.SessionId).SessionId);
            {
                var assertedValue = reg.TryGetByChannel(s.ChannelUri);
                TestAssert.WithMessage(() => Assert.Equal(s.SessionId, assertedValue.SessionId), "lookup by the provision's channel string");
            }
        }

        [Fact(Timeout = 60000)]
        public void Record_SamePairAgain_RefreshesNotDuplicates_AndKeepsToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var clock = new FakeClock();
            var reg = NewRegistry(clock);
            A2ASession first = reg.Record(Alice, Bob, out _);
            string token = reg.IssueToken(first.SessionId, Alice).Token;

            clock.Now = clock.Now.AddSeconds(30);
            A2ASession again = reg.Record(Alice, Bob, out bool created);

            Assert.False(created);
            Assert.Equal(first.SessionId, again.SessionId);
            {
                var assertedValue = again.LastSeenUtc;
                TestAssert.WithMessage(() => Assert.Equal(clock.Now, assertedValue), "a retry refreshes the TTL");
            }
            {
                var assertedValue = again.Token;
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "a retry must not invalidate credentials already handed out");
            }
            Assert.Equal(1, reg.Count);
        }

        [Fact(Timeout = 60000)]
        public void Record_FromTheOtherDirection_IsTheSameSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var clock = new FakeClock();
            var reg = NewRegistry(clock);
            A2ASession ab = reg.Record(Alice, Bob, out _);
            A2ASession ba = reg.Record(Bob, Alice, out bool created);

            Assert.False(created);
            Assert.Equal(ab.SessionId, ba.SessionId);
            Assert.True(ab.IsParty(Alice) && ab.IsParty(Bob));
            Assert.Equal(Bob, ab.OtherParty(Alice));
            Assert.Equal(Alice, ab.OtherParty(Bob));
            Assert.Equal(UUID.Zero, ab.OtherParty(Carol));
        }

        // ---- token ------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void IssueToken_MintsOnce_ForEitherParty_AndRefusesStrangers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var clock = new FakeClock();
            var reg = NewRegistry(clock);
            A2ASession s = reg.Record(Alice, Bob, out _);

            A2ASession byAlice = reg.IssueToken(s.SessionId, Alice);
            Assert.Equal(s.SessionId, byAlice.SessionId);
            {
                var assertedValue = byAlice.Token;
                TestAssert.WithMessage(() =>
                {
                    Assert.NotNull(assertedValue);
                    Assert.Equal(A2ARegistry.TokenBytes * 2, assertedValue.Length);
                }, "32 random bytes as lowercase hex");
            }
            Assert.Matches("^[0-9a-f]+$", byAlice.Token);

            A2ASession byBob = reg.IssueToken(s.SessionId, Bob);
            {
                var assertedValue = byBob.Token;
                TestAssert.WithMessage(() => Assert.Equal(byAlice.Token, assertedValue), "one token per session, shared by both parties");
            }

            {
                var assertedValue = reg.IssueToken(s.SessionId, Carol);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a non-party gets nothing");
            }
            {
                var assertedValue = reg.IssueToken(UUID.Random(), Alice);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "an unknown session gets nothing");
            }
        }

        [Fact(Timeout = 60000)]
        public void Tokens_AreUniquePerSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = NewRegistry(new FakeClock());
            A2ASession ab = reg.Record(Alice, Bob, out _);
            A2ASession ac = reg.Record(Alice, Carol, out _);
            string t1 = reg.IssueToken(ab.SessionId, Alice).Token;
            string t2 = reg.IssueToken(ac.SessionId, Alice).Token;
            Assert.NotEqual(t2, t1);
        }

        // ---- decline / remove ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Remove_DropsTheRecord_AndIsFalseWhenAbsent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = NewRegistry(new FakeClock());
            A2ASession s = reg.Record(Alice, Bob, out _);
            Assert.True(reg.Remove(s.SessionId));
            Assert.Null(reg.TryGet(s.SessionId));
            Assert.Equal(0, reg.Count);
            Assert.False(reg.Remove(s.SessionId), "declining an absent invitation is a no-op");
            Assert.False(reg.Remove(UUID.Random()));
        }

        // ---- TTL --------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void UnansweredInvitation_ExpiresAfterTtl_ButNotBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var clock = new FakeClock();
            var reg = NewRegistry(clock, TimeSpan.FromMinutes(2));
            A2ASession s = reg.Record(Alice, Bob, out _);

            clock.Now = clock.Now.AddMinutes(2);                 // exactly at TTL: still alive
            Assert.NotNull(reg.TryGet(s.SessionId));

            clock.Now = clock.Now.AddSeconds(1);                 // past TTL: swept
            Assert.Null(reg.TryGet(s.SessionId));
            Assert.Equal(0, reg.Count);
            {
                var assertedValue = reg.IssueToken(s.SessionId, Alice);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "an expired invitation cannot be called");
            }
        }

        [Fact(Timeout = 60000)]
        public void Activity_RefreshesTtl()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var clock = new FakeClock();
            var reg = NewRegistry(clock, TimeSpan.FromMinutes(2));
            A2ASession s = reg.Record(Alice, Bob, out _);
            clock.Now = clock.Now.AddMinutes(1.5);
            reg.IssueToken(s.SessionId, Alice);                  // the caller's "call" refreshes
            clock.Now = clock.Now.AddMinutes(1.5);               // 3.0 after record, 1.5 after the call
            Assert.NotNull(reg.TryGet(s.SessionId));
            clock.Now = clock.Now.AddMinutes(0.6);
            Assert.Null(reg.TryGet(s.SessionId));
        }

        [Fact(Timeout = 60000)]
        public void Registry_IsSafeUnderConcurrentUse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.Null(failure);
        }
    }

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

        [Fact(Timeout = 60000)]
        public void StartP2PVoice_WithParams_RecordsPair_AndEchoesTheViewerSessionId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromUUID(Bob)), Alice, reg);

            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = o.Body;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no HTTP body; the reply is an event");
            }
            Assert.NotNull(o.Reply);
            {
                var assertedValue = o.Reply.SessionId;
                TestAssert.WithMessage(() => Assert.Equal(Xor, assertedValue), "session_id = the XOR");
            }
            {
                var assertedValue = o.Reply.TempSessionId;
                TestAssert.WithMessage(() => Assert.Equal(Xor, assertedValue), "temp_session_id echoes the viewer's session-id (llimview.cpp:4881)");
            }
            A2ASession s = reg.TryGet(Xor);
            Assert.NotNull(s);
            Assert.Equal(Alice, s.Caller);
            Assert.Equal(Bob, s.Callee);
            {
                var assertedValue = o.Instrument;
                Assert.StartsWith(ChatSessionRequestLogic.InstrumentTag, assertedValue);
                Assert.Contains("decision=recorded", assertedValue);
                Assert.Contains("match=true", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void StartP2PVoice_ParamsAbsent_Is400_NothingRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Xor), Alice, reg);

            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.BadRequest, assertedValue), "replaces the UUID.Random() fallback");
            }
            Assert.Null(o.Reply);
            Assert.Equal(0, reg.Count);
            {
                var assertedValue = o.Instrument;
                Assert.Contains("decision=refused-400", assertedValue);
                Assert.Contains("params", assertedValue);
            }
        }

        [InlineData("not-a-uuid")]
        [InlineData("")]
        [Theory(Timeout = 60000)]
        public void StartP2PVoice_ParamsUnparseable_Is400(string bad)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromString(bad)), Alice, reg);
            Assert.Equal(HttpStatusCode.BadRequest, o.Status);
            Assert.Equal(0, reg.Count);
        }

        [Fact(Timeout = 60000)]
        public void StartP2PVoice_ParamsIsSelf_Is400()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(Alice, OSD.FromUUID(Alice)), Alice, reg);
            Assert.Equal(HttpStatusCode.BadRequest, o.Status);
            Assert.Equal(0, reg.Count);
        }

        [Fact(Timeout = 60000)]
        public void StartP2PVoice_ViewerSessionIdMismatch_StillRecords_ButInstrumentSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The sim derives the id itself; a viewer sending a different session-id still gets its echo
            // (temp_session_id) so it can re-key (llimview.cpp:1712-1716), and the instrument flags it.
            var reg = new A2ARegistry();
            UUID odd = UUID.Random();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(odd, OSD.FromUUID(Bob)), Alice, reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.Equal(Xor, o.Reply.SessionId);
            Assert.Equal(odd, o.Reply.TempSessionId);
            Assert.Contains("match=false", o.Instrument);
        }

        // ---- "call" -----------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Call_ByAParty_Returns200_WithVoiceCredentialsInTheBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromUUID(Bob)), Alice, reg);

            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(Xor), Alice, reg);

            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = o.Reply;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "call is answered in the HTTP body, not by event (llvoicechannel.cpp:687)");
            }
            Assert.NotNull(o.Body);
            Assert.True(o.Body.ContainsKey("voice_credentials"));
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            // The viewer's caller stores this map as mChannelInfo and routes/compares by
            // voice_server_type (llvoicechannel.cpp:687, :465-469; llvoicewebrtc.cpp:1682-1687).
            Assert.Equal("webrtc", creds["voice_server_type"].AsString());
            Assert.False(creds.ContainsKey("sip_uri"));
            {
                var assertedValue = creds["channel_uri"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(Xor.ToString(), assertedValue), "channel_uri = the XOR session id as a string; becomes the provision's `channel`");
            }
            string token = creds["channel_credentials"].AsString();
            {
                var assertedValue = token;
                Assert.Equal(reg.TryGet(Xor).Token, assertedValue);
                Assert.Equal(64, assertedValue.Length);
            }
            {
                var assertedValue = o.Instrument;
                TestAssert.WithMessage(() =>
                {
                    Assert.Contains("decision=credentials-issued", assertedValue);
                    Assert.DoesNotContain(token, assertedValue);
                }, "the token never appears in the log");
            }

            // The callee's own "call" (after it accepts) yields the SAME credentials
            ChatSessionOutcome byBob = ChatSessionRequestLogic.Decide(CallBody(Xor), Bob, reg);
            Assert.Equal(token, ((OSDMap)byBob.Body["voice_credentials"])["channel_credentials"].AsString());

            // and the body serialises as LLSD XML the way the handler will send it
            string xml = OSDParser.SerializeLLSDXmlString(o.Body);
            {
                var assertedValue = xml;
                Assert.Contains("voice_credentials", assertedValue);
                Assert.Contains("channel_uri", assertedValue);
                Assert.Contains("channel_credentials", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_WithoutInvitation_Is404_NoBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(Xor), Alice, reg);
            Assert.Equal(HttpStatusCode.NotFound, o.Status);
            Assert.Null(o.Body);
            Assert.Contains("decision=refused-404", o.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void Call_ByAStranger_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A live invitation session that the caller is not a party of is 403, distinct from 404 unknown.
            var reg = new A2ARegistry();
            ChatSessionRequestLogic.Decide(StartBody(Xor, OSD.FromUUID(Bob)), Alice, reg);
            UUID carol = UUID.Random();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(Xor), carol, reg);
            Assert.Equal(HttpStatusCode.Forbidden, o.Status);
            Assert.Contains("decision=refused-403", o.Instrument);
            {
                var assertedValue = reg.TryGet(Xor).Token;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a stranger's call mints nothing");
            }
        }

        // ---- the rest of the switch: stubs (decline p2p voice has real behaviour, see A2ALifecycleTests) --

        [InlineData("decline invitation")]
        [InlineData("start conference")]
        [Theory(Timeout = 60000)]
        public void Stubs_Return200_NoBody(string method)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            var m = new OSDMap { ["method"] = OSD.FromString(method), ["session-id"] = OSD.FromUUID(Xor) };
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(m, Alice, reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.Null(o.Body);
            Assert.Null(o.Reply);
            Assert.Contains("decision=stub-ok", o.Instrument);
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
        [Fact(Timeout = 60000)]
        public void FetchHistory_AnswersAnEmptyArray_NotAnEmptyBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            var m = new OSDMap { ["method"] = OSD.FromString("fetch history"), ["session-id"] = OSD.FromUUID(Xor) };
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(m, Alice, reg);

            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = o.BodyArray;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "an ARRAY, or the viewer logs 'Bad array data'");
            }
            {
                var assertedValue = o.BodyArray.Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "we keep no history; empty is the truthful answer");
            }
            {
                var assertedValue = o.Body;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a MAP here would take the viewer's bad-data branch");
            }
            Assert.Null(o.Reply);
            Assert.Contains("decision=history-empty", o.Instrument);
        }

        [Fact(Timeout = 60000)]
        public void UnknownMethod_Is400_MissingFields_Are404_NoBody_Is204()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            Assert.Equal(HttpStatusCode.BadRequest, ChatSessionRequestLogic.Decide(new OSDMap { ["method"] = OSD.FromString("nope"), ["session-id"] = OSD.FromUUID(Xor) }, Alice, reg).Status);
            Assert.Equal(HttpStatusCode.NotFound, ChatSessionRequestLogic.Decide(new OSDMap { ["session-id"] = OSD.FromUUID(Xor) }, Alice, reg).Status);
            Assert.Equal(HttpStatusCode.NotFound, ChatSessionRequestLogic.Decide(new OSDMap { ["method"] = OSD.FromString("call") }, Alice, reg).Status);
            Assert.Equal(HttpStatusCode.NoContent, ChatSessionRequestLogic.Decide(null, Alice, reg).Status);
        }

        [Fact(Timeout = 60000)]
        public void MethodMatching_IsCaseInsensitive_LikeTheOldSwitch()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            var m = new OSDMap { ["method"] = OSD.FromString("Start P2P Voice"), ["session-id"] = OSD.FromUUID(Xor), ["params"] = OSD.FromUUID(Bob) };
            Assert.Equal(HttpStatusCode.OK, ChatSessionRequestLogic.Decide(m, Alice, reg).Status);
        }
    }
}
