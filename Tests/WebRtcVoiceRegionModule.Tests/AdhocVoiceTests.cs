/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * VOICE on an ad-hoc conference -- the "call" and "accept invitation" arms, the room, the cap,
 * the provision, and the teardown.
 *
 * WHAT THESE TESTS COVER, because the arms look like copies of the group ones and are not:
 *
 *   1. AN AD-HOC "call" NEVER CREATES A voice session. A group "call" does, because for a group the voice session
 *      id IS the group id and the first caller is simply the first. A conference id is server-minted
 *      and means nothing until "start conference" produced it, so a "call" for a conference this
 *      store does not hold must fall through and 404 on the A2A arm -- how an unknown
 *      session was always answered, which the viewer already survives.
 *   2. THE PROVISION WAS ALREADY REACHING THE GROUP ARM AND BEING REFUSED. NonSpatialRoomKey.IsRoomKey
 *      matches "nsv1:adhoc:" as readily as "nsv1:group:", so a conference provision arrived at
 *      NonSpatialProvisionAdmission.Decide and was turned away with group-refused-no-session because
 *      Decide insisted on a Group session. That was a bug, and it is asserted
 *      below rather than described.
 *   3. THE SEAT HAS TO BE TAGGED WITH THE VIEWER SESSION or the logout teardown cannot find it. 
 *      Group voice had the same defect: a hang-up held its seat for eight hours. The teardown
 *      itself was always type-blind; what was missing was ad-hoc reaching the arm that tags it.
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
    public class AdhocVoiceTests
    {
        private const string Grid = "example-grid";
        private static readonly UUID Dave = new UUID("d0000000-0000-0000-0000-00000000000d");

        private static NonSpatialVoiceSessionEngine Engine()
            => new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(),
                                             new GroupAdmission((a, g) => true, (a, g) => true) },
                Grid);

        private static OSDMap Method(string m, UUID sessionId)
            => new OSDMap { ["method"] = OSD.FromString(m), ["session-id"] = OSD.FromUUID(sessionId) };

        private static OSDMap StartBody(UUID temp, params UUID[] invitees)
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

        /// <summary>Alice opens a conference inviting <paramref name="invitees"/>.</summary>
        private static (NonSpatialVoiceSessionEngine, NonSpatialVoiceSession) Conference(params UUID[] invitees)
        {
            NonSpatialVoiceSessionEngine e = Engine();
            AdhocVoiceChatSession.TryHandleStartConference(StartBody(UUID.Random(), invitees), Alice, e, RegionA, true,
                                                           out ChatSessionOutcome o);
            return (e, e.Store.Get(o.Reply.SessionId));
        }

        private static OSDMap Provision(string channel, string credentials, string viewerSession = null)
        {
            OSDMap m = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["channel"] = OSD.FromString(channel),
            };
            if (credentials is not null) m["credentials"] = OSD.FromString(credentials);
            if (viewerSession is not null) m["viewer_session"] = OSD.FromString(viewerSession);
            return m;
        }

        // ---- 1. "call" ---------------------------------------------------------------------------

        [Test]
        public void CallOnAConferenceReturnsCredentialsForTheAdhocRoom()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);

            bool handled = AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true,
                                                               out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            OSDMap creds = (OSDMap)((OSDMap)o.Body)["voice_credentials"];
            Assert.That(creds["channel_uri"].AsString(), Is.EqualTo(s.RoomKey));
            Assert.That(creds["channel_uri"].AsString(), Does.StartWith("nsv1:adhoc:"));
            Assert.That(creds["channel_credentials"].AsString(), Is.Not.Empty);
            Assert.That(creds["voice_server_type"].AsString(), Is.EqualTo(A2AInvitation.VoiceServerType));
        }

        [Test]
        public void ACallForAConferenceThatDoesNotExistIsNotOurs()
        {
            // The asymmetry with group, stated as a test. A group "call" creates the voice session; this one
            // must not, because a conference id means nothing until "start conference" minted it.
            NonSpatialVoiceSessionEngine e = Engine();
            Assert.That(AdhocVoiceChatSession.TryHandleCall(Method("call", UUID.Random()), Alice, e, RegionA, true, out _),
                        Is.False);
            Assert.That(e.Store.All(), Is.Empty, "and nothing was created on the way past");
        }

        [Test]
        public void ACallFromSomeoneNotInTheConferenceIsNotOurs()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Dave, e, RegionA, true, out _),
                        Is.False, "falls through to the A2A arm, which 404s as it always did");
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void ARetriedCallDoesNotTakeASecondSeat()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out ChatSessionOutcome o);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(2), "Alice plus Bob, once");
        }

        [Test]
        public void TheTokenIsStableAcrossCallsSoCredentialsAlreadyIssuedStayValid()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome a);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out ChatSessionOutcome b);
            string ta = ((OSDMap)((OSDMap)a.Body)["voice_credentials"])["channel_credentials"].AsString();
            string tb = ((OSDMap)((OSDMap)b.Body)["voice_credentials"])["channel_credentials"].AsString();
            Assert.That(tb, Is.EqualTo(ta), "a re-mint would invalidate credentials already in a viewer's hands");
        }

        // ---- 2. "accept invitation" --------------------------------------------------------------

        [Test]
        public void AcceptingTheInvitationTakesTheSeatAndAnswers200WithNoBody()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);

            bool handled = AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId),
                                                                           Bob, e, RegionB, true, out ChatSessionOutcome o);

            Assert.That(handled, Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK), o.Instrument);
            Assert.That(o.Body, Is.Null, "the coroutine reads the status; the channel info came with the ring");
            Assert.That(e.Store.Get(s.SessionId).Find(Bob).HoldsSeat, Is.True);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void AnUninvitedAgentCannotAcceptIntoTheConference()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId),
                                                                        Dave, e, RegionA, true, out _), Is.False);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void TheAcceptPathAndTheCallPathLandInTheSameRoom()
        {
            // Three people, two different routes in. If these ever produced different room keys the
            // call would be silently split in the mixer, which is the failure worth a test of its own.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out ChatSessionOutcome viaCall);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Carol, e, RegionB, true, out _);

            string room = ((OSDMap)((OSDMap)viaCall.Body)["voice_credentials"])["channel_uri"].AsString();
            Assert.That(room, Is.EqualTo(s.RoomKey));
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(3));
            Assert.That(e.Store.GetByRoomKey(room).SessionId, Is.EqualTo(s.SessionId));
        }

        // ---- 3. the cap --------------------------------------------------------------------------

        [Test]
        public void TheConferenceCapIsFiftyByDefaultAndClampedToTheMixerRoom()
        {
            (_, NonSpatialVoiceSession s) = Conference();
            Assert.That(s.Cap, Is.EqualTo(NonSpatialCaps.DefaultConferenceCap));
            Assert.That(s.Cap, Is.LessThanOrEqualTo(NonSpatialCaps.MixerRoomCap));
            Assert.That(NonSpatialCaps.CapsAgree(), Is.True);
        }

        [Test]
        public void TheSeatBeyondTheCapIsRefusedWith409NotAQuietFailure()
        {
            NonSpatialVoiceSessionEngine e = Engine();
            // The cap of 2 counts Alice, so a start can ring one person; Carol is invited afterwards, while the room still has a seat.
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }, requestedCap: 2);
            UUID sid = start.Session.SessionId;
            Assert.That(e.Invite(sid, Alice, Carol).Ok, Is.True);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Bob, e, RegionA, true, out _);

            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Carol, e, RegionA, true,
                                                            out ChatSessionOutcome o);
            Assert.That((int)o.Status, Is.EqualTo(NonSpatialCaps.CapacityHttpStatus),
                        "409 is what the viewer maps to ERROR_CHANNEL_FULL");
            Assert.That(o.Instrument, Does.Contain(AdhocVoiceChatSession.DecisionFull));
            Assert.That(e.Store.Get(sid).SeatsHeld, Is.EqualTo(2), "never cap+1");
        }

        // ---- 4. the provision ---------------------------------------------------------------------

        [Test]
        public void AConferenceProvisionIsRecognisedAsNonSpatialAndAdmitted()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome call);
            string token = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();

            OSDMap body = Provision(s.RoomKey, token);
            Assert.That(NonSpatialProvisionAdmission.IsNonSpatialProvision(body), Is.True);

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(body, Alice, null, e, RegionA,
                                                                              adhocEnabled: true);
            Assert.That(r.Admitted, Is.True, r.Decision);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionAdhocAdmitted));
            Assert.That(r.Session.Type, Is.EqualTo(NonSpatialSessionType.Adhoc));
            Assert.That(r.Status, Is.EqualTo(200));
        }

        [Test]
        public void ThisIsTheBugP14cCloses_AConferenceProvisionUsedToBeRefusedAsAMissingGroup()
        {
            // The gate was always type-blind, so the body reached Decide; Decide then insisted on a
            // Group session. With the ad-hoc arm absent (adhocEnabled false) the old answer is still
            // visible, and it is a refusal.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome call);
            string token = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();

            NonSpatialProvisionResult off = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, token), Alice, null, e,
                                                                                RegionA, adhocEnabled: false);
            Assert.That(off.Admitted, Is.False);
            Assert.That(off.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionAdhocDisabled),
                        "and the refusal now says WHY, instead of claiming the session does not exist");
        }

        [Test]
        public void AProvisionFromSomeoneNeverInvitedIsRefusedBeforeTheTokenIsEvenChecked()
        {
            // The token is one string shared by everyone in the conference, so it cannot be the only
            // thing standing here -- exactly the reasoning the group arm uses for powers.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome call);
            string leaked = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, leaked), Dave, null, e,
                                                                              RegionA, adhocEnabled: true);
            Assert.That(r.Admitted, Is.False);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionAdhocNotMember));
        }

        [Test]
        public void AProvisionWithTheWrongTokenIsRefused()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out _);
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, "not-the-token"), Alice,
                                                                              null, e, RegionA, adhocEnabled: true);
            Assert.That(r.Admitted, Is.False);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionAdhocBadToken));
        }

        [Test]
        public void AGroupProvisionIsUnaffectedByTheAdhocArm()
        {
            // 's path has to stay exactly as it was: same admission, same words.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            GroupVoicePolicy policy = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = false,
                IsMember = (a, g) => true,
                Powers = (a, g) => GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice,
            };
            NonSpatialVoiceSession g = e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA).Session;
            e.Accept(g.SessionId, Alice, RegionA);
            string token = e.IssueToken(g.SessionId, Alice);

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(g.RoomKey, token), Alice, policy, e,
                                                                              RegionA, adhocEnabled: true);
            Assert.That(r.Admitted, Is.True, r.Decision);
            Assert.That(r.Decision, Is.EqualTo(NonSpatialProvisionAdmission.DecisionAdmitted),
                        "the group word, not the ad-hoc one");
        }

        [Test]
        public void AnA2AProvisionIsStillNotANonSpatialOne()
        {
            OSDMap a2a = Provision(UUID.Random().ToString(), "tok");
            Assert.That(NonSpatialProvisionAdmission.IsNonSpatialProvision(a2a), Is.False,
                        "a bare UUID channel is an A2A room and must never reach this arm");
        }

        // ---- 5. seat and teardown -------------------------------------------------------------------

        [Test]
        public void TheProvisionTagsTheSeatWithTheViewerSessionSoTheLogoutCanFindIt()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e, RegionB, true, out _);
            e.MarkPresent(s.SessionId, Bob, RegionB, "vs-bob");

            IReadOnlyList<NonSpatialVoiceSession> gone = e.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);
            Assert.That(gone.Select(x => x.SessionId), Is.EquivalentTo(new[] { s.SessionId }));
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(1), "Bob's seat, and only Bob's");
            Assert.That(e.Store.Get(s.SessionId).Find(Alice).HoldsSeat, Is.True);
        }

        [Test]
        public void HangingUpReleasesExactlyOneSeatAndAnotherViewerSessionIsUntouched()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Carol, e, RegionB, true, out _);
            e.MarkPresent(s.SessionId, Bob, RegionA, "vs-bob");
            e.MarkPresent(s.SessionId, Carol, RegionB, "vs-carol");
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(3));

            e.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(2));
            Assert.That(e.Store.Get(s.SessionId).Find(Carol).HoldsSeat, Is.True);
        }

        [Test]
        public void APresenceCloseReleasesTheConferenceSeatToo()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e, RegionA, true, out _);
            e.DepartAll(Bob, DepartureReason.PresenceLost);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void WhenTheLastSeatGoesTheRingCycleEndsSoTheConferenceCanRingAgain()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.MarkInvited(s.SessionId, new[] { Bob });
            e.Depart(s.SessionId, Alice, DepartureReason.VoiceTeardown);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(0));
            Assert.That(e.Store.Get(s.SessionId).RingSent, Is.False);
            Assert.That(e.Store.Get(s.SessionId).WasInvited(Bob), Is.False);
        }

        // ---- 6. the arms take only what is theirs -----------------------------------------------------

        [Test]
        public void NeitherArmTakesAnythingWhenAdhocVoiceIsDisabled()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.That(AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, false, out _),
                        Is.False);
            Assert.That(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e,
                                                                        RegionA, false, out _), Is.False);
            Assert.That(e.Store.Get(s.SessionId).SeatsHeld, Is.EqualTo(1), "and no seat was taken on the way past");
        }

        [Test]
        public void NeitherArmTouchesAGroupSession()
        {
            // The discriminators are disjoint by construction, not by the order the module tries them.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA);
            Assert.That(AdhocVoiceChatSession.TryHandleCall(Method("call", grp), Alice, e, RegionA, true, out _), Is.False);
            Assert.That(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", grp), Alice, e,
                                                                        RegionA, true, out _), Is.False);
        }

        [Test]
        public void OnlyTheirOwnMethodsAreTaken()
        {
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            foreach (string m in new[] { "start p2p voice", "decline p2p voice", "fetch history", "mute update",
                                         "session update", "start conference", "invite", "decline invitation" })
            {
                Assert.That(AdhocVoiceChatSession.TryHandleCall(Method(m, s.SessionId), Alice, e, RegionA, true, out _),
                            Is.False, m);
                Assert.That(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method(m, s.SessionId), Bob, e, RegionA, true,
                                                                            out _), Is.False, m);
            }
        }

        // ---- 7. end to end ----------------------------------------------------------------------------

        [Test]
        public void ThreeWayConference_StartRingCallAcceptProvisionHangUp()
        {
            // The whole flow in one pass, in the order a viewer actually produces it.
            NonSpatialVoiceSessionEngine e = Engine();

            // Alice starts a conference with Bob and Carol; the re-key reply carries both ids.
            AdhocVoiceChatSession.TryHandleStartConference(StartBody(UUID.Random(), Bob, Carol), Alice, e, RegionA, true,
                                                           out ChatSessionOutcome start);
            Assert.That(start.StartedRinging, Is.True, "the creator's own seat is the 0 -> 1 transition");
            UUID sid = start.Reply.SessionId;
            NonSpatialVoiceSession s = e.Store.Get(sid);

            // Both invitees are ring targets; the initiator is not.
            Assert.That(AdhocVoiceInvite.Targets(s, Alice), Is.EquivalentTo(new[] { Bob, Carol }));
            e.MarkInvited(sid, new[] { Bob, Carol });

            // Alice's queued call arrives and gets the room; Bob and Carol accept their popups.
            AdhocVoiceChatSession.TryHandleCall(Method("call", sid), Alice, e, RegionA, true, out ChatSessionOutcome call);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Bob, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Carol, e, RegionB, true, out _);
            Assert.That(e.Store.Get(sid).SeatsHeld, Is.EqualTo(3));

            // All three provision into the SAME room and are admitted.
            string token = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();
            foreach ((UUID who, UUID where, string vs) in new[] { (Alice, RegionA, "vs-a"), (Bob, RegionA, "vs-b"), (Carol, RegionB, "vs-c") })
            {
                NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, token, vs), who, null,
                                                                                   e, where, adhocEnabled: true);
                Assert.That(r.Admitted, Is.True, $"{who}: {r.Decision}");
                Assert.That(r.Session.SessionId, Is.EqualTo(sid), "one conference, one room");
                e.MarkPresent(sid, who, where, vs);
            }
            Assert.That(e.Store.Get(sid).SeatsHeld, Is.EqualTo(3), "three provisions, three seats, not six");

            // Carol hangs up: exactly her seat goes.
            e.DepartByViewerSession(Carol, "vs-c", DepartureReason.VoiceTeardown);
            Assert.That(e.Store.Get(sid).SeatsHeld, Is.EqualTo(2));

            // Everyone else leaves; the room is empty and the ring cycle is reset.
            e.DepartByViewerSession(Alice, "vs-a", DepartureReason.VoiceTeardown);
            e.DepartByViewerSession(Bob, "vs-b", DepartureReason.VoiceTeardown);
            Assert.That(e.Store.Get(sid).SeatsHeld, Is.EqualTo(0));
            Assert.That(e.Store.Get(sid).RingSent, Is.False);
        }
    }
}
