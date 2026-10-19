/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Session types, pluggable admission, and the two distinct identities.
 *
 * The viewer facts asserted here come from the Firestorm viewer's llimview.cpp:
 *   llimview.cpp:2535-2538  group  -> session id IS the group id ("slam group session_id to the group_id")
 *   llimview.cpp:2542       adhoc  -> session_id.generate(), a random id the server has never seen
 *   llimview.cpp:2551-2570  p2p    -> the XOR of the two agents
 *   llimview.cpp:4877-4887  the start reply carries {success, temp_session_id, session_id}
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialIdentityTests
    {
        internal static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        internal static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        internal static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");
        internal static readonly UUID Group = new UUID("99999999-9999-9999-9999-999999999999");
        internal static readonly UUID RegionA = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
        internal static readonly UUID RegionB = new UUID("aaaaaaaa-0000-0000-0000-000000000002");

        internal sealed class FakeClock
        {
            public DateTime Now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            public DateTime Get() => Now;
        }

        /// <summary>Everyone is in the group and holds GP_SESSION_JOIN unless a test says otherwise.</summary>
        internal static NonSpatialVoiceSessionEngine NewEngine(FakeClock clock = null,
                                                               Func<UUID, UUID, bool> isMember = null,
                                                               Func<UUID, UUID, bool> hasPower = null,
                                                               INonSpatialSessionStore store = null,
                                                               TimeSpan? inviteTtl = null,
                                                               TimeSpan? idleTtl = null,
                                                               NonSpatialLimits limits = null)
        {
            clock ??= new FakeClock();
            return new NonSpatialVoiceSessionEngine(
                store ?? new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[]
                {
                    new P2PAdmission(),
                    new AdhocAdmission(),
                    new GroupAdmission(isMember ?? ((a, g) => true), hasPower ?? ((a, g) => true)),
                },
                "example-grid", clock.Get, inviteTtl, idleTtl, limits);
        }

        // ---- the two ids -------------------------------------------------------------

        [Test]
        public void P2P_SessionId_IsTheViewerXor_AndNeedsNoRekey()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            UUID viewerId = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Alice, Bob);

            SessionOutcome o = e.Start(NonSpatialSessionType.P2P, Alice, Bob, viewerId, RegionA);

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(o.Session.SessionId, Is.EqualTo(viewerId));
            Assert.That(o.Session.TempSessionId, Is.EqualTo(viewerId));
            Assert.That(o.Session.RequiresRekey, Is.False, "p2p ids already agree, so the start reply is a no-op re-key");
        }

        [Test]
        public void P2P_SessionId_IsSymmetric_SoEitherPartyDerivesTheSame()
        {
            UUID ab = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Alice, Bob);
            UUID ba = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Bob, Alice);
            Assert.That(ab, Is.EqualTo(ba));
        }

        [Test]
        public void Group_SessionId_IsTheGroupId_AndNeedsNoRekey()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome o = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(o.Session.SessionId, Is.EqualTo(Group), "the viewer slams session_id to the group id");
            Assert.That(o.Session.Owner, Is.EqualTo(Group));
            Assert.That(o.Session.RequiresRekey, Is.False);
        }

        [Test]
        public void Adhoc_SessionId_IsServerOwned_AndForcesARekey()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            UUID viewerTemp = UUID.Random();          // llimview.cpp:2542, session_id.generate()

            SessionOutcome o = e.StartAdhoc(Alice, viewerTemp, RegionA);

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(o.Session.TempSessionId, Is.EqualTo(viewerTemp), "the reply must echo what the viewer is waiting on");
            Assert.That(o.Session.SessionId, Is.Not.EqualTo(viewerTemp), "the server owns the authoritative id");
            Assert.That(o.Session.RequiresRekey, Is.True, "this is the case the ChatterBoxSessionStartReply exists for");
        }

        [Test]
        public void Start_RejectsAdhoc_BecauseItsIdIsKeyedOnTheViewersTempId()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            Assert.Throws<ArgumentException>(() => e.Start(NonSpatialSessionType.Adhoc, Alice, Bob, UUID.Random(), RegionA));
        }

        // ---- pluggable admission ------------------------------------------------------

        [Test]
        public void Group_Admission_RefusesANonMember()
        {
            NonSpatialVoiceSessionEngine e = NewEngine(isMember: (a, g) => a != Carol);
            SessionOutcome start = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);
            Assert.That(start.Ok, Is.True);

            SessionOutcome join = e.Accept(start.Session.SessionId, Carol, RegionA);

            Assert.That(join.Ok, Is.False);
            Assert.That(join.Decision, Is.EqualTo(AdmissionVerdict.NotAMember));
        }

        [Test]
        public void Group_Admission_RefusesAMemberWithoutTheJoinPower()
        {
            // GP_SESSION_JOIN, roles_constants.h:144 -- the power the viewer gates its own button on.
            NonSpatialVoiceSessionEngine e = NewEngine(hasPower: (a, g) => a != Carol);
            SessionOutcome start = e.Start(NonSpatialSessionType.Group, Alice, Group, Group, RegionA);

            SessionOutcome join = e.Accept(start.Session.SessionId, Carol, RegionA);

            Assert.That(join.Ok, Is.False);
            Assert.That(join.Decision, Is.EqualTo(AdmissionVerdict.NoPower));
        }

        [Test]
        public void P2P_IsNeverWidened_ByAnInvite()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome start = e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Zero, RegionA);

            SessionOutcome invite = e.Invite(start.Session.SessionId, Alice, Carol);

            Assert.That(invite.Ok, Is.False);
            Assert.That(invite.Decision, Is.EqualTo(AdmissionVerdict.WrongType));
        }

        [Test]
        public void Adhoc_OnlyAnInvitedAgentMayTakeASeat()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), RegionA);

            SessionOutcome uninvited = e.Accept(start.Session.SessionId, Carol, RegionA);
            Assert.That(uninvited.Ok, Is.False, "knowing the session is not the same as being in it");
            Assert.That(uninvited.Decision, Is.EqualTo(AdmissionVerdict.NotAMember));

            Assert.That(e.Invite(start.Session.SessionId, Alice, Carol).Ok, Is.True);
            Assert.That(e.Accept(start.Session.SessionId, Carol, RegionA).Ok, Is.True);
        }

        [Test]
        public void Adhoc_AnySeatedMemberMayInvite_ButAnInviteeMayNot()
        {
            NonSpatialVoiceSessionEngine e = NewEngine();
            SessionOutcome s = e.StartAdhoc(Alice, UUID.Random(), RegionA);
            UUID id = s.Session.SessionId;

            e.Invite(id, Alice, Bob);
            Assert.That(e.Invite(id, Bob, Carol).Ok, Is.False, "invited-but-not-seated may not invite");

            e.Accept(id, Bob, RegionA);
            Assert.That(e.Invite(id, Bob, Carol).Ok, Is.True, "a seated member may");
        }

        [Test]
        public void Engine_ThrowsIfATypeHasNoAdmissionPolicy()
        {
            NonSpatialVoiceSessionEngine e = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(), new INonSpatialAdmission[] { new P2PAdmission() }, "g");
            Assert.Throws<InvalidOperationException>(() => e.StartAdhoc(Alice, UUID.Random(), RegionA));
        }
    }
}
