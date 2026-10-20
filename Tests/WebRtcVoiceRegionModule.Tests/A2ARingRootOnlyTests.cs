/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * An invitation is delivered to a ROOT presence of the invitee in this process, and only to one.
 *
 * A viewer that stands next to a region it is not in holds a child presence there. Ringing that child
 * shows the viewer an incoming call, but the viewer's answer goes to the region it is standing in,
 * which may be another process that has never heard of the call. So a child presence is not a place
 * to ring. These tests cover the delivery function every ring goes through.
 */
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using osWebRtcVoice;
using static osWebRtcVoice.Tests.CrossProcessHarness;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2ARingRootOnlyTests
    {
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");

        private static OSDMap SomeBody()
            => new OSDMap { ["session_id"] = OSD.FromUUID(UUID.Random()), ["voice"] = new OSDMap() };

        [Test]
        public void ChildPresenceOnly_IsNotRung_AndTheDecisionSaysSo()
        {
            Process p = new Process();
            p.AddChild(Bob);

            string decision = A2AInviteDelivery.Deliver(new[] { p.Scene }, Bob, SomeBody(), null, out _);

            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionChildOnly));
            Assert.That(p.Queue.Enqueued, Is.Empty, "the answer to a ring on a child presence goes to another process");
            Assert.That(A2AInviteDelivery.Line(Bob, UUID.Random(), UUID.Random(), "-", decision),
                Does.Contain("child-presence-only"));
        }

        [Test]
        public void RootPresenceHere_IsRungAsBefore()
        {
            Process p = new Process();
            p.AddRoot(Bob);

            string decision = A2AInviteDelivery.Deliver(new[] { p.Scene }, Bob, SomeBody(), null, out _);

            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionSent));
            Assert.That(p.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
        }

        [Test]
        public void ChildInOneSceneAndRootInAnother_IsRungOnTheRootScene()
        {
            Process a = new Process("Region One", 1000, 1000);
            Process b = new Process("Region Two", 1001, 1000);
            a.AddChild(Bob);
            b.AddRoot(Bob);

            string decision = A2AInviteDelivery.Deliver(new[] { a.Scene, b.Scene }, Bob, SomeBody(), null, out string region);

            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionSent));
            Assert.That(region, Is.EqualTo("Region Two"));
            Assert.That(a.Queue.Enqueued, Is.Empty);
            Assert.That(b.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
        }

        [Test]
        public void NoPresenceHere_IsNotRung_AsBefore()
        {
            Process p = new Process();

            string decision = A2AInviteDelivery.Deliver(new[] { p.Scene }, Bob, SomeBody(), null, out _);

            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionNoPresence));
            Assert.That(p.Queue.Enqueued, Is.Empty);
        }
    }
}
