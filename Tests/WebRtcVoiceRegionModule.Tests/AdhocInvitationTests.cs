/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * ad-hoc conference invitations -- the ring, "invite" into a running call, and the unwind on
 * "decline invitation".
 *
 * The three things that had to be got right, and are each asserted below rather than reasoned about:
 *
 *   1. WHO GETS RUNG. An ad-hoc conference has no roster. Being invited IS the membership, so the
 *      ring list and AdhocAdmission.CanJoin must agree -- an uninvited agent must neither be rung nor
 *      admitted, or a conference is a public room with extra steps.
 *   2. INVITE RINGS ONLY THE NEW PERSON. Adding someone to a call in progress must not re-pop the
 *      incoming-call dialog for the people already sitting in it.
 *   3. DECLINE UNWINDS CLEANLY. No seat was ever held, so none is released; but the ring latch must
 *      be cleared, or a mis-clicked decline makes that agent unreachable for the rest of the call.
 *
 * Cross-instance is covered at the same seam GroupVoiceCrossHostTests uses: build the carrier, parse
 * it on the "other" engine, and prove the accept that follows is ADMITTED there. That is the half
 * that a single simulator process cannot demonstrate live.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class AdhocInvitationTests
    {
        private const string Grid = "example-grid";
        private static readonly UUID Dave = new UUID("d0000000-0000-0000-0000-00000000000d");

        private static NonSpatialVoiceSessionEngine Engine(string grid = Grid)
            => new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(),
                                             new GroupAdmission((a, g) => true, (a, g) => true) },
                grid);

        private static OSDMap Start(UUID temp, params UUID[] invitees)
        {
            OSDArray arr = new OSDArray();
            foreach (UUID i in invitees) arr.Add(OSD.FromUUID(i));
            return new OSDMap
            {
                ["method"] = OSD.FromString("start conference"),
                ["session-id"] = OSD.FromUUID(temp),
                ["params"] = arr,
            };
        }

        private static OSDMap Invite(UUID sessionId, params UUID[] agents)
        {
            OSDArray arr = new OSDArray();
            foreach (UUID a in agents) arr.Add(OSD.FromUUID(a));
            return new OSDMap
            {
                ["method"] = OSD.FromString("invite"),
                ["session-id"] = OSD.FromUUID(sessionId),
                ["params"] = arr,
            };
        }

        private static OSDMap DeclineBody(UUID sessionId)
            => new OSDMap
            {
                ["method"] = OSD.FromString("decline invitation"),
                ["session-id"] = OSD.FromUUID(sessionId),
            };

        /// <summary>Open a conference with Alice as creator and return (engine, session).</summary>
        private static (NonSpatialVoiceSessionEngine, NonSpatialVoiceSession) Conference(params UUID[] invitees)
        {
            NonSpatialVoiceSessionEngine e = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), invitees), Alice, e, RegionA, true,
                                                           out ChatSessionOutcome o);
            return (e, e.Store.Get(o.Reply.SessionId));
        }

        // ---- 1. the ring body -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheRingCarriesConferenceInvitationTypeAndTheAuthoritativeSessionId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            OSDMap body = AdhocVoiceInvite.BuildBody(s, e.IssueToken(s.SessionId, Alice), Alice, "Alice Resident");

            {
                var assertedValue = body["session_id"].AsUUID();
                TestAssert.WithMessage(() => Assert.Equal(s.SessionId, assertedValue), "the temp id names a session the invitee has never heard of");
            }
            Assert.NotEqual(s.TempSessionId, body["session_id"].AsUUID());
            OSDMap voice = (OSDMap)body["voice"];
            {
                var assertedValue = voice["invitation_type"].AsInteger();
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "CONFERENCE_SESSION; 0 would send the viewer down the group branch of inviteToSession");
            }
            Assert.Equal(s.RoomKey, voice["channel_uri"].AsString());
            Assert.NotEmpty(voice["channel_credentials"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void TheRingBodyIsShapedExactlyLikeTheGroupOneApartFromTypeAndId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A drift between the two bodies would show up as one of them failing in a viewer, months
            // apart. Same keys, same nesting: the only differences are the two that are meant to differ.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            OSDMap adhoc = AdhocVoiceInvite.BuildBody(s, e.IssueToken(s.SessionId, Alice), Alice, "Alice");

            NonSpatialVoiceSessionEngine ge = Engine();
            UUID grp = UUID.Random();
            SessionOutcome gs = ge.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA);
            OSDMap group = GroupVoiceInvite.BuildBody(gs.Session, ge.IssueToken(grp, Alice), Alice, "Alice", "G");

            Assert.Equivalent(group.Keys.OrderBy(k => k), adhoc.Keys.OrderBy(k => k), strict: true);
            Assert.Equivalent(((OSDMap)group["voice"]).Keys.OrderBy(k => k), ((OSDMap)adhoc["voice"]).Keys.OrderBy(k => k), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void ARingIsRefusedForANonAdhocSessionAndWithoutAToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            NonSpatialVoiceSession g = e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;
            {
                var assertedValue = () => AdhocVoiceInvite.BuildBody(g, "tok", Alice, "Alice");
                Assert.Throws<ArgumentException>(() =>
                {
                    assertedValue();
                });
            }

            (NonSpatialVoiceSessionEngine e2, NonSpatialVoiceSession s) = Conference(Bob);
            {
                var assertedValue = () => AdhocVoiceInvite.BuildBody(s, null, Alice, "Alice");
                Assert.Throws<InvalidOperationException>(() =>
                {
                    assertedValue();
                });
            }
        }

        // ---- 2. who gets rung -----------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void RingTargetsAreTheInviteesAndNeverTheInitiator()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (_, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            List<UUID> targets = AdhocVoiceInvite.Targets(s, Alice);
            Assert.Equivalent(new[] { Bob, Carol }, targets, strict: true);
            {
                var assertedValue = targets;
                TestAssert.WithMessage(() => Assert.DoesNotContain(Alice, assertedValue), "the initiator is already in the call");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnUninvitedAgentIsNeitherRungNorAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The two halves of "invitation only" stated together, because either alone is a hole.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.DoesNotContain(Dave, AdhocVoiceInvite.Targets(s, Alice));
            Assert.False(e.Accept(s.SessionId, Dave, RegionA).Ok, "AdhocAdmission.CanJoin must refuse an agent with no member record");
        }

        [Fact(Timeout = 60000)]
        public void SomeoneSeatedIsNotRung()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            e.Accept(s.SessionId, Bob, RegionA);
            Assert.Equivalent(new[] { Carol }, AdhocVoiceInvite.Targets(e.Store.Get(s.SessionId), Alice), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void NobodyIsRungTwiceInOneCycle()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            List<UUID> first = AdhocVoiceInvite.Targets(s, Alice);
            e.MarkInvited(s.SessionId, first);
            Assert.Empty(AdhocVoiceInvite.Targets(e.Store.Get(s.SessionId), Alice));
        }

        [Fact(Timeout = 60000)]
        public void OnlineOnlyDropsAnInviteeThePresenceServiceCannotPlace()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // An offline invitee cannot answer, and a stored ring announces a call that ended hours
            // ago. Reuses 's filter rather than a second implementation.
            List<UUID> kept = GroupVoiceInvite.OnlineOnly(new[] { Bob, Carol },
                                                           a => a == Bob ? RegionA : UUID.Zero);
            Assert.Equivalent(new[] { Bob }, kept, strict: true);
        }

        // ---- 3. "invite" into a running call ---------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void InviteAddsTheNewPersonAndReturnsOnlyThem()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.Accept(s.SessionId, Bob, RegionA);
            e.MarkInvited(s.SessionId, new[] { Bob });

            bool handled = AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Carol), Alice, e, true,
                                                                 out ChatSessionOutcome o, out List<UUID> ring);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            {
                var assertedValue = ring;
                TestAssert.WithMessage(() => Assert.Equivalent(new[] { Carol }, assertedValue, strict: true), "only the person just added");
            }
            Assert.Equal(MemberState.Invited, e.Store.Get(s.SessionId).Find(Carol)?.State);
            {
                var assertedValue = e.Store.Get(s.SessionId).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "an invitation takes no seat");
            }
        }

        [Fact(Timeout = 60000)]
        public void InvitingSomeoneAlreadyInTheCallRingsNobody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.Accept(s.SessionId, Bob, RegionA);

            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out ChatSessionOutcome o,
                                                   out List<UUID> ring);

            {
                var assertedValue = ring;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "Bob is sitting in the call; a second popup would be a bug");
            }
            Assert.Contains(AdhocVoiceChatSession.DecisionInviteNothingNew, o.Instrument);
            Assert.True(e.Store.Get(s.SessionId).Find(Bob).HoldsSeat, "and he is not demoted back to Invited");
            Assert.Equal(2, e.Store.Get(s.SessionId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void ReInvitingSomeoneWhoseRingIsAlreadyUpDoesNotRingAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference();
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out _, out List<UUID> first);
            e.MarkInvited(s.SessionId, first);
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out _, out List<UUID> second);

            Assert.Equivalent(new[] { Bob }, first, strict: true);
            Assert.Empty(second);
        }

        [Fact(Timeout = 60000)]
        public void InviteNeverRingsTheInviterEvenIfTheyNameThemselves()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference();
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Alice, Bob), Alice, e, true, out _,
                                                   out List<UUID> ring);
            Assert.Equivalent(new[] { Bob }, ring, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AStrangerCannotInviteIntoAConferenceTheyAreNotIn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Dave), Carol, e, true, out ChatSessionOutcome o,
                                                   out List<UUID> ring);
            Assert.Empty(ring);
            {
                var assertedValue = o.Instrument;
                TestAssert.WithMessage(() => Assert.Contains("refused=", assertedValue), o.Instrument);
            }
            {
                var assertedValue = e.Store.Get(s.SessionId).Find(Dave);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "and no membership is created");
            }
        }

        [Fact(Timeout = 60000)]
        public void InviteIsRefusedIntoAFullRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA, null, requestedCap: 2);
            UUID sid = start.Session.SessionId;
            e.Invite(sid, Alice, Bob);
            e.Accept(sid, Bob, RegionA);        // 2/2

            AdhocVoiceChatSession.TryHandleInvite(Invite(sid, Carol), Alice, e, true, out ChatSessionOutcome o,
                                                   out List<UUID> ring);
            {
                var assertedValue = ring;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "ringing into a full room is a disappointment we can spare them");
            }
            Assert.Contains(SessionOutcome.Capacity, o.Instrument);
        }

        // ---- 4. "decline invitation" ------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void DeclineRemovesThePendingInvitationWithoutTouchingASeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            e.Accept(s.SessionId, Bob, RegionA);
            int seatsBefore = e.Store.Get(s.SessionId).SeatsHeld;

            bool handled = AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Carol, e, true,
                                                                            out ChatSessionOutcome o);

            Assert.True(handled);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            NonSpatialVoiceSession after = e.Store.Get(s.SessionId);
            Assert.Equal(MemberState.Declined, after.Find(Carol).State);
            Assert.False(after.Find(Carol).HoldsSeat);
            {
                var assertedValue = after.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(seatsBefore, assertedValue), "an invitation never held a seat to release");
            }
            {
                var assertedValue = AdhocVoiceInvite.Targets(after, Alice);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and a decliner is not a ring target");
            }
        }

        [Fact(Timeout = 60000)]
        public void ADeclinerCanBeInvitedAgainAndIsRungAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The mis-click case. Without clearing the per-agent ring latch, declining once would make
            // someone unreachable for the rest of the call.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.MarkInvited(s.SessionId, new[] { Bob });
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true, out _);

            Assert.False(e.Store.Get(s.SessionId).WasInvited(Bob), "the ring latch is cleared");
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out _, out List<UUID> ring);
            Assert.Equivalent(new[] { Bob }, ring, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void DecliningThenAcceptingIsStillAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Declined leaves the member record in place, so the agent is still an invitee. Clicking
            // the conference again is a reasonable thing to do and must work.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true, out _);
            Assert.True(e.Accept(s.SessionId, Bob, RegionA).Ok);
            Assert.Equal(2, e.Store.Get(s.SessionId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void ADoubleDeclineIsIdempotent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true, out _);
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true,
                                                             out ChatSessionOutcome o);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.Equal(1, e.Store.Get(s.SessionId).SeatsHeld);
        }

        // ---- 5. the arms take only what is theirs -----------------------------------------------------

        [Fact(Timeout = 60000)]
        public void NeitherArmTakesAnythingWhenAdhocVoiceIsDisabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.False(AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Carol), Alice, e, false, out _, out _));
            Assert.False(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, false, out _));
            Assert.Null(e.Store.Get(s.SessionId).Find(Carol));
        }

        [Fact(Timeout = 60000)]
        public void NeitherArmTouchesAGroupSessionOrAnUnknownId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer sends "decline invitation" for group invitations too (llimview.cpp:3429-3438).
            // Taking those would change behaviour for groups.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA);

            Assert.False(AdhocVoiceChatSession.TryHandleInvite(Invite(grp, Carol), Alice, e, true, out _, out _));
            Assert.False(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(grp), Bob, e, true, out _));
            Assert.False(AdhocVoiceChatSession.TryHandleInvite(Invite(UUID.Random(), Carol), Alice, e, true, out _, out _));
            Assert.False(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(UUID.Random()), Bob, e, true, out _));
        }

        [Fact(Timeout = 60000)]
        public void DeclineIsNotTakenFromSomeoneWhoIsNotAPartyToTheConference()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.False(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Dave, e, true, out _));
        }

        [Fact(Timeout = 60000)]
        public void OnlyTheirOwnMethodsAreTaken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            foreach (string m in new[] { "call", "start p2p voice", "accept invitation", "decline p2p voice",
                                         "fetch history", "start conference", "mute update", "session update" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(s.SessionId) };
                Assert.False(AdhocVoiceChatSession.TryHandleInvite(body, Alice, e, true, out _, out _), m);
                Assert.False(AdhocVoiceChatSession.TryHandleDeclineInvitation(body, Bob, e, true, out _), m);
            }
        }

        // ---- 6. cross-instance, through 's carrier ---------------------------------------------

        [Fact(Timeout = 60000)]
        public void AConferenceRingCrossesTheCarrierAndTheAcceptIsAdmittedOnTheFarSide()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The whole cross-host claim in one test: instance A rings, the carrier crosses, instance B
            // adopts, and Bob's accept -- which lands on B, where the voice session was never started -- is
            // ADMITTED into the same mixer room.
            NonSpatialVoiceSessionEngine a = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true,
                                                           out ChatSessionOutcome started);
            NonSpatialVoiceSession sa = a.Store.Get(started.Reply.SessionId);
            OSDMap body = AdhocVoiceInvite.BuildBody(sa, a.IssueToken(sa.SessionId, Alice), Alice, "Alice");

            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", sa.SessionId, RegionA, body);
            Assert.True(GroupVoiceRingTransport.TryParse(im, out UUID target, out UUID carried, out OSDMap got));
            Assert.Equal(Bob, target);
            Assert.Equal(sa.SessionId, carried);

            NonSpatialVoiceSessionEngine b = Engine();
            OSDMap voice = (OSDMap)got["voice"];
            SessionOutcome adopted = b.AdoptRemoteRing(carried, voice["channel_uri"].AsString(),
                                                        voice["channel_credentials"].AsString(),
                                                        NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob },
                                                        NonSpatialSessionType.Adhoc);

            Assert.True(adopted.Ok, adopted.Decision);
            Assert.Equal(NonSpatialSessionType.Adhoc, adopted.Session.Type);
            {
                var assertedValue = adopted.Session.RoomKey;
                TestAssert.WithMessage(() => Assert.Equal(sa.RoomKey, assertedValue), "both instances must derive the same room");
            }
            Assert.True(b.Accept(carried, Bob, RegionB).Ok, "this is the whole point: the accept lands on B, which never started the session");
        }

        [Fact(Timeout = 60000)]
        public void TheAdoptedConferenceAdmitsOnlyTheAgentTheRingWasFor()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true, out ChatSessionOutcome st);
            NonSpatialVoiceSession sa = a.Store.Get(st.Reply.SessionId);
            OSDMap body = AdhocVoiceInvite.BuildBody(sa, a.IssueToken(sa.SessionId, Alice), Alice, "Alice");

            NonSpatialVoiceSessionEngine b = Engine();
            OSDMap voice = (OSDMap)body["voice"];
            b.AdoptRemoteRing(sa.SessionId, voice["channel_uri"].AsString(), voice["channel_credentials"].AsString(),
                              NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob }, NonSpatialSessionType.Adhoc);

            Assert.False(b.Accept(sa.SessionId, Dave, RegionB).Ok, "adopting a ring must not turn the far side into an open room");
        }

        [Fact(Timeout = 60000)]
        public void AnAdoptedConferenceNeverRingsAgainFromTheAdoptingInstance()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Send-once across instances: B must not fan the call out a second time.
            NonSpatialVoiceSessionEngine a = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true, out ChatSessionOutcome st);
            NonSpatialVoiceSession sa = a.Store.Get(st.Reply.SessionId);
            OSDMap voice = (OSDMap)AdhocVoiceInvite.BuildBody(sa, a.IssueToken(sa.SessionId, Alice), Alice, "Alice")["voice"];

            NonSpatialVoiceSessionEngine b = Engine();
            SessionOutcome adopted = b.AdoptRemoteRing(sa.SessionId, voice["channel_uri"].AsString(),
                                                        voice["channel_credentials"].AsString(),
                                                        NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob },
                                                        NonSpatialSessionType.Adhoc);
            Assert.True(adopted.Session.RingSent);
            Assert.Empty(AdhocVoiceInvite.Targets(adopted.Session, Alice));
            Assert.False(b.Accept(sa.SessionId, Bob, RegionB).StartedRinging);
        }

        [Fact(Timeout = 60000)]
        public void AGridIdMismatchRefusesTheAdoptionRatherThanSplittingTheCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine a = Engine("example-grid");
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true, out ChatSessionOutcome st);
            NonSpatialVoiceSession sa = a.Store.Get(st.Reply.SessionId);

            NonSpatialVoiceSessionEngine b = Engine("someone-elses-grid");
            SessionOutcome adopted = b.AdoptRemoteRing(sa.SessionId, sa.RoomKey, "tok",
                                                        NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob },
                                                        NonSpatialSessionType.Adhoc);
            Assert.False(adopted.Ok, "two mixer rooms and an inaudible call is the failure being prevented");
            Assert.Contains("room-key-mismatch", adopted.Decision);
        }

        [Fact(Timeout = 60000)]
        public void AGroupRingStillAdoptsAsAGroupSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The type parameter defaults to Group, so a group ring is adopted as before.
            NonSpatialVoiceSessionEngine a = Engine();
            UUID grp = UUID.Random();
            NonSpatialVoiceSession g = a.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;

            NonSpatialVoiceSessionEngine b = Engine();
            SessionOutcome adopted = b.AdoptRemoteRing(grp, g.RoomKey, "tok", 50, Alice, new[] { Bob });
            Assert.True(adopted.Ok, adopted.Decision);
            Assert.Equal(NonSpatialSessionType.Group, adopted.Session.Type);
            Assert.Equal(grp, adopted.Session.Owner);
            {
                var assertedValue = adopted.Session.Find(Bob);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "group admission re-checks the roster, so no member record is seeded");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheCarrierIsStillClaimedByNothingElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Restating 's discriminator for the conference body, because an ad-hoc ring now
            // rides the same rails: SessionSend with fromGroup FALSE, plus the magic header.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            OSDMap body = AdhocVoiceInvite.BuildBody(s, e.IssueToken(s.SessionId, Alice), Alice, "Alice");
            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", s.SessionId, RegionA, body);

            Assert.Equal(GroupVoiceRingTransport.DialogSessionSend, im.dialog);
            Assert.False(im.fromGroup, "GroupsMessagingModule claims SessionSend only when fromGroup is true");
            {
                var assertedValue = im.offline;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a ring must never be stored and replayed at next login");
            }

            im.fromGroup = true;
            Assert.False(GroupVoiceRingTransport.TryParse(im, out _, out _, out _));
        }
    }
}
