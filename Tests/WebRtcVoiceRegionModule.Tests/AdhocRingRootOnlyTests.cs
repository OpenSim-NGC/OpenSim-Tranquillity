/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A conference ring reaches an invitee through the process that holds their ROOT presence.
 *
 * Same rule as a group ring (see GroupRingRootOnlyTests): an invitee who is only a child presence in
 * the starting process is rung through the signed carrier, not on the child presence.
 */
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CrossProcessHarness;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class AdhocRingRootOnlyTests
    {
        private const string Grid = "example-grid";
        private const string Secret = "0123456789abcdef0123456789abcdef-example";

        private static (WebRtcVoiceRegionModule Module, Process Process, NonSpatialVoiceSession Session) Starting(
            System.Action<Process> arrange)
        {
            Process p = new Process();
            p.AddRoot(Alice);
            p.Presence.SetOnline(Bob);
            arrange(p);

            NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() },
                Grid);
            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_adhocVoiceEnabled", true);
            SetPrivate(mod, "m_nonSpatial", engine);
            SetPrivate(mod, "m_carrierAuth", new CarrierAuth(Secret));
            ((List<Scene>)GetPrivate(mod, "m_scenes")).Add(p.Scene);

            SessionOutcome start = engine.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            return (mod, p, start.Session);
        }

        private static void Ring(WebRtcVoiceRegionModule mod, NonSpatialVoiceSession session)
            => Invoke(mod, "RingAdhocMembers", session, Alice, "Test User", null);

        [Test]
        public void InviteeWithOnlyAChildPresenceHere_IsNotRungLocally_AndIsRungThroughTheCarrier()
        {
            var (mod, p, session) = Starting(x => x.AddChild(Bob));

            Ring(mod, session);

            Assert.That(p.Queue.Enqueued, Does.Not.Contain(Bob));
            GridInstantMessage sent = p.Transfer.Sent.SingleOrDefault();
            Assert.That(sent, Is.Not.Null);
            Assert.That(GroupVoiceRingTransport.TryParse(sent, out UUID target, out UUID id, out _), Is.True);
            Assert.That(target, Is.EqualTo(Bob));
            Assert.That(id, Is.EqualTo(session.SessionId));
        }

        [Test]
        public void TheCarrierRing_IsSentToRootOnly_SoAChildPresenceHereIsNotHandedTheCarrier()
        {
            var (mod, p, session) = Starting(x => x.AddChild(Bob));

            Ring(mod, session);

            Assert.That(p.Transfer.Sent, Has.Count.EqualTo(1));
            Assert.That(p.Transfer.SentRootOnly, Is.EqualTo(new[] { true }));
        }

        [Test]
        public void InviteeWithARootPresenceHere_IsRungLocally_AndNothingIsCarried()
        {
            var (mod, p, session) = Starting(x => x.AddRoot(Bob));

            Ring(mod, session);

            Assert.That(p.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
            Assert.That(p.Transfer.Sent, Is.Empty);
        }

        [Test]
        public void InviteeWithNoPresenceHere_IsRungThroughTheCarrier()
        {
            var (mod, p, session) = Starting(_ => { });

            Ring(mod, session);

            Assert.That(p.Queue.Enqueued, Is.Empty);
            Assert.That(p.Transfer.Sent, Has.Count.EqualTo(1));
        }
    }
}
