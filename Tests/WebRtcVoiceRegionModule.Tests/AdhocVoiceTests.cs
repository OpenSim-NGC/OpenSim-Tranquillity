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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void CallOnAConferenceReturnsCredentialsForTheAdhocRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);

            bool handled = AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true,
                                                               out ChatSessionOutcome o);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            OSDMap creds = (OSDMap)((OSDMap)o.Body)["voice_credentials"];
            Assert.Equal(s.RoomKey, creds["channel_uri"].AsString());
            Assert.StartsWith("nsv1:adhoc:", creds["channel_uri"].AsString());
            Assert.NotEmpty(creds["channel_credentials"].AsString());
            Assert.Equal(A2AInvitation.VoiceServerType, creds["voice_server_type"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void ACallForAConferenceThatDoesNotExistIsNotOurs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The asymmetry with group, stated as a test. A group "call" creates the voice session; this one
            // must not, because a conference id means nothing until "start conference" minted it.
            NonSpatialVoiceSessionEngine e = Engine();
            Assert.False(AdhocVoiceChatSession.TryHandleCall(Method("call", UUID.Random()), Alice, e, RegionA, true, out _));
            {
                var assertedValue = e.Store.All();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and nothing was created on the way past");
            }
        }

        [Fact(Timeout = 60000)]
        public void ACallFromSomeoneNotInTheConferenceIsNotOurs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.False(AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Dave, e, RegionA, true, out _), "falls through to the A2A arm, which 404s as it always did");
            Assert.Equal(1, e.Store.Get(s.SessionId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void ARetriedCallDoesNotTakeASecondSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out ChatSessionOutcome o);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = e.Store.Get(s.SessionId).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "Alice plus Bob, once");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheTokenIsStableAcrossCallsSoCredentialsAlreadyIssuedStayValid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome a);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out ChatSessionOutcome b);
            string ta = ((OSDMap)((OSDMap)a.Body)["voice_credentials"])["channel_credentials"].AsString();
            string tb = ((OSDMap)((OSDMap)b.Body)["voice_credentials"])["channel_credentials"].AsString();
            {
                var assertedValue = tb;
                TestAssert.WithMessage(() => Assert.Equal(ta, assertedValue), "a re-mint would invalidate credentials already in a viewer's hands");
            }
        }

        // ---- 2. "accept invitation" --------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AcceptingTheInvitationTakesTheSeatAndAnswers200WithNoBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);

            bool handled = AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId),
                                                                           Bob, e, RegionB, true, out ChatSessionOutcome o);

            Assert.True(handled);
            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.OK, assertedValue), o.Instrument);
            }
            {
                var assertedValue = o.Body;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the coroutine reads the status; the channel info came with the ring");
            }
            Assert.True(e.Store.Get(s.SessionId).Find(Bob).HoldsSeat);
            Assert.Equal(2, e.Store.Get(s.SessionId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void AnUninvitedAgentCannotAcceptIntoTheConference()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.False(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId),
                                                                        Dave, e, RegionA, true, out _));
            Assert.Equal(1, e.Store.Get(s.SessionId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void TheAcceptPathAndTheCallPathLandInTheSameRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Three people, two different routes in. If these ever produced different room keys the
            // call would be silently split in the mixer, which is the failure worth a test of its own.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Bob, e, RegionA, true, out ChatSessionOutcome viaCall);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Carol, e, RegionB, true, out _);

            string room = ((OSDMap)((OSDMap)viaCall.Body)["voice_credentials"])["channel_uri"].AsString();
            Assert.Equal(s.RoomKey, room);
            Assert.Equal(3, e.Store.Get(s.SessionId).SeatsHeld);
            Assert.Equal(s.SessionId, e.Store.GetByRoomKey(room).SessionId);
        }

        // ---- 3. the cap --------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheConferenceCapIsFiftyByDefaultAndClampedToTheMixerRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (_, NonSpatialVoiceSession s) = Conference();
            Assert.Equal(NonSpatialCaps.DefaultConferenceCap, s.Cap);
            Assert.True(s.Cap <= NonSpatialCaps.MixerRoomCap);
            Assert.True(NonSpatialCaps.CapsAgree());
        }

        [Fact(Timeout = 60000)]
        public void TheSeatBeyondTheCapIsRefusedWith409NotAQuietFailure()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = Engine();
            // The cap of 2 counts Alice, so a start can ring one person; Carol is invited afterwards, while the room still has a seat.
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }, requestedCap: 2);
            UUID sid = start.Session.SessionId;
            Assert.True(e.Invite(sid, Alice, Carol).Ok);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Bob, e, RegionA, true, out _);

            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Carol, e, RegionA, true,
                                                            out ChatSessionOutcome o);
            {
                var assertedValue = (int)o.Status;
                TestAssert.WithMessage(() => Assert.Equal(NonSpatialCaps.CapacityHttpStatus, assertedValue), "409 is what the viewer maps to ERROR_CHANNEL_FULL");
            }
            Assert.Contains(AdhocVoiceChatSession.DecisionFull, o.Instrument);
            {
                var assertedValue = e.Store.Get(sid).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "never cap+1");
            }
        }

        // ---- 4. the provision ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AConferenceProvisionIsRecognisedAsNonSpatialAndAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome call);
            string token = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();

            OSDMap body = Provision(s.RoomKey, token);
            Assert.True(NonSpatialProvisionAdmission.IsNonSpatialProvision(body));

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(body, Alice, null, e, RegionA,
                                                                              adhocEnabled: true);
            Assert.True(r.Admitted, r.Decision);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionAdhocAdmitted, r.Decision);
            Assert.Equal(NonSpatialSessionType.Adhoc, r.Session.Type);
            Assert.Equal(200, r.Status);
        }

        [Fact(Timeout = 60000)]
        public void ThisIsTheBugP14cCloses_AConferenceProvisionUsedToBeRefusedAsAMissingGroup()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The gate was always type-blind, so the body reached Decide; Decide then insisted on a
            // Group session. With the ad-hoc arm absent (adhocEnabled false) the old answer is still
            // visible, and it is a refusal.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome call);
            string token = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();

            NonSpatialProvisionResult off = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, token), Alice, null, e,
                                                                                RegionA, adhocEnabled: false);
            Assert.False(off.Admitted);
            {
                var assertedValue = off.Decision;
                TestAssert.WithMessage(() => Assert.Equal(NonSpatialProvisionAdmission.DecisionAdhocDisabled, assertedValue), "and the refusal now says WHY, instead of claiming the session does not exist");
            }
        }

        [Fact(Timeout = 60000)]
        public void AProvisionFromSomeoneNeverInvitedIsRefusedBeforeTheTokenIsEvenChecked()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The token is one string shared by everyone in the conference, so it cannot be the only
            // thing standing here -- exactly the reasoning the group arm uses for powers.
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out ChatSessionOutcome call);
            string leaked = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();

            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, leaked), Dave, null, e,
                                                                              RegionA, adhocEnabled: true);
            Assert.False(r.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionAdhocNotMember, r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void AProvisionWithTheWrongTokenIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, true, out _);
            NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, "not-the-token"), Alice,
                                                                              null, e, RegionA, adhocEnabled: true);
            Assert.False(r.Admitted);
            Assert.Equal(NonSpatialProvisionAdmission.DecisionAdhocBadToken, r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void AGroupProvisionIsUnaffectedByTheAdhocArm()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.True(r.Admitted, r.Decision);
            {
                var assertedValue = r.Decision;
                TestAssert.WithMessage(() => Assert.Equal(NonSpatialProvisionAdmission.DecisionAdmitted, assertedValue), "the group word, not the ad-hoc one");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnA2AProvisionIsStillNotANonSpatialOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap a2a = Provision(UUID.Random().ToString(), "tok");
            Assert.False(NonSpatialProvisionAdmission.IsNonSpatialProvision(a2a), "a bare UUID channel is an A2A room and must never reach this arm");
        }

        // ---- 5. seat and teardown -------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheProvisionTagsTheSeatWithTheViewerSessionSoTheLogoutCanFindIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e, RegionB, true, out _);
            e.MarkPresent(s.SessionId, Bob, RegionB, "vs-bob");

            IReadOnlyList<NonSpatialVoiceSession> gone = e.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);
            Assert.Equivalent(new[] { s.SessionId }, gone.Select(x => x.SessionId), strict: true);
            {
                var assertedValue = e.Store.Get(s.SessionId).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "Bob's seat, and only Bob's");
            }
            Assert.True(e.Store.Get(s.SessionId).Find(Alice).HoldsSeat);
        }

        [Fact(Timeout = 60000)]
        public void HangingUpReleasesExactlyOneSeatAndAnotherViewerSessionIsUntouched()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob, Carol);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Carol, e, RegionB, true, out _);
            e.MarkPresent(s.SessionId, Bob, RegionA, "vs-bob");
            e.MarkPresent(s.SessionId, Carol, RegionB, "vs-carol");
            Assert.Equal(3, e.Store.Get(s.SessionId).SeatsHeld);

            e.DepartByViewerSession(Bob, "vs-bob", DepartureReason.VoiceTeardown);
            Assert.Equal(2, e.Store.Get(s.SessionId).SeatsHeld);
            Assert.True(e.Store.Get(s.SessionId).Find(Carol).HoldsSeat);
        }

        [Fact(Timeout = 60000)]
        public void APresenceCloseReleasesTheConferenceSeatToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e, RegionA, true, out _);
            e.DepartAll(Bob, DepartureReason.PresenceLost);
            Assert.Equal(1, e.Store.Get(s.SessionId).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void WhenTheLastSeatGoesTheRingCycleEndsSoTheConferenceCanRingAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            e.MarkInvited(s.SessionId, new[] { Bob });
            e.Depart(s.SessionId, Alice, DepartureReason.VoiceTeardown);
            Assert.Equal(0, e.Store.Get(s.SessionId).SeatsHeld);
            Assert.False(e.Store.Get(s.SessionId).RingSent);
            Assert.False(e.Store.Get(s.SessionId).WasInvited(Bob));
        }

        // ---- 6. the arms take only what is theirs -----------------------------------------------------

        [Fact(Timeout = 60000)]
        public void NeitherArmTakesAnythingWhenAdhocVoiceIsDisabled()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            Assert.False(AdhocVoiceChatSession.TryHandleCall(Method("call", s.SessionId), Alice, e, RegionA, false, out _));
            Assert.False(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", s.SessionId), Bob, e,
                                                                        RegionA, false, out _));
            {
                var assertedValue = e.Store.Get(s.SessionId).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and no seat was taken on the way past");
            }
        }

        [Fact(Timeout = 60000)]
        public void NeitherArmTouchesAGroupSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The discriminators are disjoint by construction, not by the order the module tries them.
            NonSpatialVoiceSessionEngine e = Engine();
            UUID grp = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, grp, grp, RegionA);
            Assert.False(AdhocVoiceChatSession.TryHandleCall(Method("call", grp), Alice, e, RegionA, true, out _));
            Assert.False(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", grp), Alice, e,
                                                                        RegionA, true, out _));
        }

        [Fact(Timeout = 60000)]
        public void OnlyTheirOwnMethodsAreTaken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (NonSpatialVoiceSessionEngine e, NonSpatialVoiceSession s) = Conference(Bob);
            foreach (string m in new[] { "start p2p voice", "decline p2p voice", "fetch history", "mute update",
                                         "session update", "start conference", "invite", "decline invitation" })
            {
                Assert.False(AdhocVoiceChatSession.TryHandleCall(Method(m, s.SessionId), Alice, e, RegionA, true, out _), m);
                Assert.False(AdhocVoiceChatSession.TryHandleAcceptInvitation(Method(m, s.SessionId), Bob, e, RegionA, true, out _), m);
            }
        }

        // ---- 7. end to end ----------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ThreeWayConference_StartRingCallAcceptProvisionHangUp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The whole flow in one pass, in the order a viewer actually produces it.
            NonSpatialVoiceSessionEngine e = Engine();

            // Alice starts a conference with Bob and Carol; the re-key reply carries both ids.
            AdhocVoiceChatSession.TryHandleStartConference(StartBody(UUID.Random(), Bob, Carol), Alice, e, RegionA, true,
                                                           out ChatSessionOutcome start);
            Assert.True(start.StartedRinging, "the creator's own seat is the 0 -> 1 transition");
            UUID sid = start.Reply.SessionId;
            NonSpatialVoiceSession s = e.Store.Get(sid);

            // Both invitees are ring targets; the initiator is not.
            Assert.Equivalent(new[] { Bob, Carol }, AdhocVoiceInvite.Targets(s, Alice), strict: true);
            e.MarkInvited(sid, new[] { Bob, Carol });

            // Alice's queued call arrives and gets the room; Bob and Carol accept their popups.
            AdhocVoiceChatSession.TryHandleCall(Method("call", sid), Alice, e, RegionA, true, out ChatSessionOutcome call);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Bob, e, RegionA, true, out _);
            AdhocVoiceChatSession.TryHandleAcceptInvitation(Method("accept invitation", sid), Carol, e, RegionB, true, out _);
            Assert.Equal(3, e.Store.Get(sid).SeatsHeld);

            // All three provision into the SAME room and are admitted.
            string token = ((OSDMap)((OSDMap)call.Body)["voice_credentials"])["channel_credentials"].AsString();
            foreach ((UUID who, UUID where, string vs) in new[] { (Alice, RegionA, "vs-a"), (Bob, RegionA, "vs-b"), (Carol, RegionB, "vs-c") })
            {
                NonSpatialProvisionResult r = NonSpatialProvisionAdmission.Decide(Provision(s.RoomKey, token, vs), who, null,
                                                                                   e, where, adhocEnabled: true);
                Assert.True(r.Admitted, $"{who}: {r.Decision}");
                {
                    var assertedValue = r.Session.SessionId;
                    TestAssert.WithMessage(() => Assert.Equal(sid, assertedValue), "one conference, one room");
                }
                e.MarkPresent(sid, who, where, vs);
            }
            {
                var assertedValue = e.Store.Get(sid).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "three provisions, three seats, not six");
            }

            // Carol hangs up: exactly her seat goes.
            e.DepartByViewerSession(Carol, "vs-c", DepartureReason.VoiceTeardown);
            Assert.Equal(2, e.Store.Get(sid).SeatsHeld);

            // Everyone else leaves; the room is empty and the ring cycle is reset.
            e.DepartByViewerSession(Alice, "vs-a", DepartureReason.VoiceTeardown);
            e.DepartByViewerSession(Bob, "vs-b", DepartureReason.VoiceTeardown);
            Assert.Equal(0, e.Store.Get(sid).SeatsHeld);
            Assert.False(e.Store.Get(sid).RingSent);
        }
    }
}
