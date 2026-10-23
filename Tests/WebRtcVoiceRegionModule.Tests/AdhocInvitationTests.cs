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
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void TheRingCarriesConferenceInvitationTypeAndTheAuthoritativeSessionId()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            OSDMap body = AdhocVoiceInvite.BuildBody(s, e.IssueToken(s.SessionId, Alice), Alice, "Alice Resident");

            Assert.That(body["session_id"].AsUUID(), Is.EqualTo(s.SessionId),
                        "the temp id names a session the invitee has never heard of");
            Assert.That(body["session_id"].AsUUID(), Is.Not.EqualTo(s.TempSessionId));
            OSDMap voice = (OSDMap)body["voice"];
            Assert.That(voice["invitation_type"].AsInteger(), Is.EqualTo(1),
                        "CONFERENCE_SESSION; 0 would send the viewer down the group branch of inviteToSession");
            Assert.That(voice["channel_uri"].AsString(), Is.EqualTo(s.RoomKey));
            Assert.That(voice["channel_credentials"].AsString(), Is.Not.Empty);
        }

        [Test]
        public void TheRingBodyIsShapedExactlyLikeTheGroupOneApartFromTypeAndId()
        {
            // A drift between the two bodies would show up as one of them failing in a viewer, months
            // apart. Same keys, same nesting: the only differences are the two that are meant to differ.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            OSDMap adhoc = AdhocVoiceInvite.BuildBody(s, e.IssueToken(s.SessionId, Alice), Alice, "Alice");

            NonSpatialVoiceSessionEngine ge = Engine();
            UUID grp = UUID.Random();
            SessionOutcome gs = ge.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA);
            OSDMap group = GroupVoiceInvite.BuildBody(gs.Session, ge.IssueToken(grp, Alice), Alice, "Alice", "G");

            Assert.That(adhoc.Keys.OrderBy(k => k), Is.EquivalentTo(group.Keys.OrderBy(k => k)));
            Assert.That(((OSDMap)adhoc["voice"]).Keys.OrderBy(k => k),
                        Is.EquivalentTo(((OSDMap)group["voice"]).Keys.OrderBy(k => k)));
        }

        [Test]
        public void ARingIsRefusedForANonAdhocSessionAndWithoutAToken()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            NonSpatialVoiceSession g = e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;
            Assert.That(() => AdhocVoiceInvite.BuildBody(g, "tok", Alice, "Alice"), Throws.ArgumentException);

            (NonSpatialVoiceSessionEngine e2, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(() => AdhocVoiceInvite.BuildBody(s, null, Alice, "Alice"), Throws.InvalidOperationException);
        }

        // ---- 2. who gets rung -----------------------------------------------------------------------

        [Test]
        public void RingTargetsAreTheInviteesAndNeverTheInitiator()
        {
            (_, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            List<UUID> targets = AdhocVoiceInvite.Targets(s, Alice);
            Assert.That(targets, Is.EquivalentTo(new[] { Bob, Carol }));
            Assert.That(targets, Does.Not.Contain(Alice), "the initiator is already in the call");
        }

        [Test]
        public void AnUninvitedAgentIsNeitherRungNorAdmitted()
        {
            // The two halves of "invitation only" stated together, because either alone is a hole.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(AdhocVoiceInvite.Targets(s, Alice), Does.Not.Contain(Dave));
            Assert.That(e.Accept(s.SessionId, Dave, RegionA).Ok, Is.False,
                        "AdhocAdmission.CanJoin must refuse an agent with no member record");
        }

        [Test]
        public void SomeoneSeatedIsNotRung()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            e.Accept(s.SessionId, Bob, RegionA);
            Assert.That(AdhocVoiceInvite.Targets(e.Store.Get(s.SessionId), Alice), Is.EquivalentTo(new[] { Carol }));
        }

        [Test]
        public void NobodyIsRungTwiceInOneCycle()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            List<UUID> first = AdhocVoiceInvite.Targets(s, Alice);
            e.MarkInvited(s.SessionId, first);
            Assert.That(AdhocVoiceInvite.Targets(e.Store.Get(s.SessionId), Alice), Is.Empty);
        }

        [Test]
        public void OnlineOnlyDropsAnInviteeThePresenceServiceCannotPlace()
        {
            // An offline invitee cannot answer, and a stored ring announces a call that ended hours
            // ago. Reuses 's filter rather than a second implementation.
            List<UUID> kept = GroupVoiceInvite.OnlineOnly(new[] { Bob, Carol },
                                                           a => a == Bob ? RegionA : UUID.Zero);
            Assert.That(kept, Is.EquivalentTo(new[] { Bob }));
        }

        // ---- 3. "invite" into a running call ---------------------------------------------------------

        [Test]
        public void InviteAddsTheNewPersonAndReturnsOnlyThem()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.Accept(s.SessionId, Bob, RegionA);
            e.MarkInvited(s.SessionId, new[] { Bob });

            bool handled = AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Carol), Alice, e, true,
                                                                 out ChatSessionOutcome o, out List<UUID> ring);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            Assert.That(ring, Is.EquivalentTo(new[] { Carol }), "only the person just added");
            Assert.That(e.Store.Get(s.SessionId).Find(Carol)?.State, Is.EqualTo(MemberState.Invited));
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(2), "an invitation takes no seat");
        }

        [Test]
        public void InvitingSomeoneAlreadyInTheCallRingsNobody()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.Accept(s.SessionId, Bob, RegionA);

            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out ChatSessionOutcome o,
                                                   out List<UUID> ring);

            Assert.That(ring, Is.Empty, "Bob is sitting in the call; a second popup would be a bug");
            Assert.That(o.Instrument, Does.Contain(AdhocVoiceChatSession.DecisionInviteNothingNew));
            Assert.That(e.Store.Get(s.SessionId).Find(Bob).HoldsSeat, Is.True, "and he is not demoted back to Invited");
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void ReInvitingSomeoneWhoseRingIsAlreadyUpDoesNotRingAgain()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference();
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out _, out List<UUID> first);
            e.MarkInvited(s.SessionId, first);
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out _, out List<UUID> second);

            Assert.That(first, Is.EquivalentTo(new[] { Bob }));
            Assert.That(second, Is.Empty);
        }

        [Test]
        public void InviteNeverRingsTheInviterEvenIfTheyNameThemselves()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference();
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Alice, Bob), Alice, e, true, out _,
                                                   out List<UUID> ring);
            Assert.That(ring, Is.EquivalentTo(new[] { Bob }));
        }

        [Test]
        public void AStrangerCannotInviteIntoAConferenceTheyAreNotIn()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Dave), Carol, e, true, out ChatSessionOutcome o,
                                                   out List<UUID> ring);
            Assert.That(ring, Is.Empty);
            Assert.That(o.Instrument, Does.Contain("refused="), o.Instrument);
            Assert.That(e.Store.Get(s.SessionId).Find(Dave), Is.Null, "and no membership is created");
        }

        [Test]
        public void InviteIsRefusedIntoAFullRoom()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA, null, requestedCap: 2);
            UUID sid = start.Session.SessionId;
            e.Invite(sid, Alice, Bob);
            e.Accept(sid, Bob, RegionA);        // 2/2

            AdhocVoiceChatSession.TryHandleInvite(Invite(sid, Carol), Alice, e, true, out ChatSessionOutcome o,
                                                   out List<UUID> ring);
            Assert.That(ring, Is.Empty, "ringing into a full room is a disappointment we can spare them");
            Assert.That(o.Instrument, Does.Contain(SessionOutcome.Capacity));
        }

        // ---- 4. "decline invitation" ------------------------------------------------------------------

        [Test]
        public void DeclineRemovesThePendingInvitationWithoutTouchingASeat()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            e.Accept(s.SessionId, Bob, RegionA);
            int seatsBefore = e.Store.Get(s.SessionId).SeatsHeld;

            bool handled = AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Carol, e, true,
                                                                            out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            NonSpatialVoiceSession after = e.Store.Get(s.SessionId);
            Assert.That(after.Find(Carol).State, Is.EqualTo(MemberState.Declined));
            Assert.That(after.Find(Carol).HoldsSeat, Is.False);
            Assert.That(after.SeatsHeld, Is.EqualTo(seatsBefore), "an invitation never held a seat to release");
            Assert.That(AdhocVoiceInvite.Targets(after, Alice), Is.Empty, "and a decliner is not a ring target");
        }

        [Test]
        public void ADeclinerCanBeInvitedAgainAndIsRungAgain()
        {
            // The mis-click case. Without clearing the per-agent ring latch, declining once would make
            // someone unreachable for the rest of the call.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.MarkInvited(s.SessionId, new[] { Bob });
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true, out _);

            Assert.That(e.Store.Get(s.SessionId).WasInvited(Bob), Is.False, "the ring latch is cleared");
            AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Bob), Alice, e, true, out _, out List<UUID> ring);
            Assert.That(ring, Is.EquivalentTo(new[] { Bob }));
        }

        [Test]
        public void DecliningThenAcceptingIsStillAdmitted()
        {
            // Declined leaves the member record in place, so the agent is still an invitee. Clicking
            // the conference again is a reasonable thing to do and must work.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true, out _);
            Assert.That(e.Accept(s.SessionId, Bob, RegionA).Ok, Is.True);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void ADoubleDeclineIsIdempotent()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true, out _);
            AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, true,
                                                             out ChatSessionOutcome o);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(1));
        }

        // ---- 5. the arms take only what is theirs -----------------------------------------------------

        [Test]
        public void NeitherArmTakesAnythingWhenAdhocVoiceIsDisabled()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(AdhocVoiceChatSession.TryHandleInvite(Invite(s.SessionId, Carol), Alice, e, false, out _, out _),
                        Is.False);
            Assert.That(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Bob, e, false, out _),
                        Is.False);
            Assert.That(e.Store.Get(s.SessionId).Find(Carol), Is.Null);
        }

        [Test]
        public void NeitherArmTouchesAGroupSessionOrAnUnknownId()
        {
            // The viewer sends "decline invitation" for group invitations too (llimview.cpp:3429-3438).
            // Taking those would change behaviour for groups.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA);

            Assert.That(AdhocVoiceChatSession.TryHandleInvite(Invite(grp, Carol), Alice, e, true, out _, out _), Is.False);
            Assert.That(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(grp), Bob, e, true, out _), Is.False);
            Assert.That(AdhocVoiceChatSession.TryHandleInvite(Invite(UUID.Random(), Carol), Alice, e, true, out _, out _),
                        Is.False);
            Assert.That(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(UUID.Random()), Bob, e, true, out _),
                        Is.False);
        }

        [Test]
        public void DeclineIsNotTakenFromSomeoneWhoIsNotAPartyToTheConference()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(AdhocVoiceChatSession.TryHandleDeclineInvitation(DeclineBody(s.SessionId), Dave, e, true, out _),
                        Is.False);
        }

        [Test]
        public void OnlyTheirOwnMethodsAreTaken()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            foreach (string m in new[] { "call", "start p2p voice", "accept invitation", "decline p2p voice",
                                         "fetch history", "start conference", "mute update", "session update" })
            {
                OSDMap body = new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(s.SessionId) };
                Assert.That(AdhocVoiceChatSession.TryHandleInvite(body, Alice, e, true, out _, out _), Is.False, m);
                Assert.That(AdhocVoiceChatSession.TryHandleDeclineInvitation(body, Bob, e, true, out _), Is.False, m);
            }
        }

        // ---- 6. cross-instance, through 's carrier ---------------------------------------------

        [Test]
        public void AConferenceRingCrossesTheCarrierAndTheAcceptIsAdmittedOnTheFarSide()
        {
            // The whole cross-host claim in one test: instance A rings, the carrier crosses, instance B
            // adopts, and Bob's accept -- which lands on B, where the voice session was never started -- is
            // ADMITTED into the same mixer room.
            NonSpatialVoiceSessionEngine a = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true,
                                                           out ChatSessionOutcome started);
            NonSpatialVoiceSession sa = a.Store.Get(started.Reply.SessionId);
            OSDMap body = AdhocVoiceInvite.BuildBody(sa, a.IssueToken(sa.SessionId, Alice), Alice, "Alice");

            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", sa.SessionId, RegionA, body);
            Assert.That(GroupVoiceRingTransport.TryParse(im, out UUID target, out UUID carried, out OSDMap got), Is.True);
            Assert.That(target, Is.EqualTo(Bob));
            Assert.That(carried, Is.EqualTo(sa.SessionId));

            NonSpatialVoiceSessionEngine b = Engine();
            OSDMap voice = (OSDMap)got["voice"];
            SessionOutcome adopted = b.AdoptRemoteRing(carried, voice["channel_uri"].AsString(),
                                                        voice["channel_credentials"].AsString(),
                                                        NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob },
                                                        NonSpatialSessionType.Adhoc);

            Assert.That(adopted.Ok, Is.True, adopted.Decision);
            Assert.That(adopted.Session.Type, Is.EqualTo(NonSpatialSessionType.Adhoc));
            Assert.That(adopted.Session.RoomKey, Is.EqualTo(sa.RoomKey), "both instances must derive the same room");
            Assert.That(b.Accept(carried, Bob, RegionB).Ok, Is.True,
                        "this is the whole point: the accept lands on B, which never started the session");
        }

        [Test]
        public void TheAdoptedConferenceAdmitsOnlyTheAgentTheRingWasFor()
        {
            NonSpatialVoiceSessionEngine a = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true, out ChatSessionOutcome st);
            NonSpatialVoiceSession sa = a.Store.Get(st.Reply.SessionId);
            OSDMap body = AdhocVoiceInvite.BuildBody(sa, a.IssueToken(sa.SessionId, Alice), Alice, "Alice");

            NonSpatialVoiceSessionEngine b = Engine();
            OSDMap voice = (OSDMap)body["voice"];
            b.AdoptRemoteRing(sa.SessionId, voice["channel_uri"].AsString(), voice["channel_credentials"].AsString(),
                              NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob }, NonSpatialSessionType.Adhoc);

            Assert.That(b.Accept(sa.SessionId, Dave, RegionB).Ok, Is.False,
                        "adopting a ring must not turn the far side into an open room");
        }

        [Test]
        public void AnAdoptedConferenceNeverRingsAgainFromTheAdoptingInstance()
        {
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
            Assert.That(adopted.Session.RingSent, Is.True);
            Assert.That(AdhocVoiceInvite.Targets(adopted.Session, Alice), Is.Empty);
            Assert.That(b.Accept(sa.SessionId, Bob, RegionB).StartedRinging, Is.False);
        }

        [Test]
        public void AGridIdMismatchRefusesTheAdoptionRatherThanSplittingTheCall()
        {
            NonSpatialVoiceSessionEngine a = Engine("example-grid");
            AdhocVoiceChatSession.TryHandleStartConference(Start(UUID.Random(), Bob), Alice, a, RegionA, true, out ChatSessionOutcome st);
            NonSpatialVoiceSession sa = a.Store.Get(st.Reply.SessionId);

            NonSpatialVoiceSessionEngine b = Engine("someone-elses-grid");
            SessionOutcome adopted = b.AdoptRemoteRing(sa.SessionId, sa.RoomKey, "tok",
                                                        NonSpatialCaps.DefaultConferenceCap, Alice, new[] { Bob },
                                                        NonSpatialSessionType.Adhoc);
            Assert.That(adopted.Ok, Is.False, "two mixer rooms and an inaudible call is the failure being prevented");
            Assert.That(adopted.Decision, Does.Contain("room-key-mismatch"));
        }

        [Test]
        public void AGroupRingStillAdoptsAsAGroupSession()
        {
            // The type parameter defaults to Group, so a group ring is adopted as before.
            NonSpatialVoiceSessionEngine a = Engine();
            UUID grp = UUID.Random();
            NonSpatialVoiceSession g = a.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;

            NonSpatialVoiceSessionEngine b = Engine();
            SessionOutcome adopted = b.AdoptRemoteRing(grp, g.RoomKey, "tok", 50, Alice, new[] { Bob });
            Assert.That(adopted.Ok, Is.True, adopted.Decision);
            Assert.That(adopted.Session.Type, Is.EqualTo(NonSpatialSessionType.Group));
            Assert.That(adopted.Session.Owner, Is.EqualTo(grp));
            Assert.That(adopted.Session.Find(Bob), Is.Null,
                        "group admission re-checks the roster, so no member record is seeded");
        }

        [Test]
        public void TheCarrierIsStillClaimedByNothingElse()
        {
            // Restating 's discriminator for the conference body, because an ad-hoc ring now
            // rides the same rails: SessionSend with fromGroup FALSE, plus the magic header.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            OSDMap body = AdhocVoiceInvite.BuildBody(s, e.IssueToken(s.SessionId, Alice), Alice, "Alice");
            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", s.SessionId, RegionA, body);

            Assert.That(im.dialog, Is.EqualTo(GroupVoiceRingTransport.DialogSessionSend));
            Assert.That(im.fromGroup, Is.False, "GroupsMessagingModule claims SessionSend only when fromGroup is true");
            Assert.That(im.offline, Is.EqualTo(0), "a ring must never be stored and replayed at next login");

            im.fromGroup = true;
            Assert.That(GroupVoiceRingTransport.TryParse(im, out _, out _, out _), Is.False);
        }
    }
}
