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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using osWebRtcVoice;
using static osWebRtcVoice.Tests.CrossProcessHarness;

namespace osWebRtcVoice.Tests
{
    public class A2ARingRootOnlyTests
    {
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");

        private static OSDMap SomeBody()
            => new OSDMap { ["session_id"] = OSD.FromUUID(UUID.Random()), ["voice"] = new OSDMap() };

        [Fact(Timeout = 60000)]
        public void ChildPresenceOnly_IsNotRung_AndTheDecisionSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Process p = new Process();
            p.AddChild(Bob);

            string decision = A2AInviteDelivery.Deliver(new[] { p.Scene }, Bob, SomeBody(), null, out _);

            Assert.Equal(A2AInviteDelivery.DecisionChildOnly, decision);
            {
                var assertedValue = p.Queue.Enqueued;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the answer to a ring on a child presence goes to another process");
            }
            Assert.Contains("child-presence-only", A2AInviteDelivery.Line(Bob, UUID.Random(), UUID.Random(), "-", decision));
        }

        [Fact(Timeout = 60000)]
        public void RootPresenceHere_IsRungAsBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Process p = new Process();
            p.AddRoot(Bob);

            string decision = A2AInviteDelivery.Deliver(new[] { p.Scene }, Bob, SomeBody(), null, out _);

            Assert.Equal(A2AInviteDelivery.DecisionSent, decision);
            Assert.Equal(new[] { Bob }, p.Queue.Enqueued);
        }

        [Fact(Timeout = 60000)]
        public void ChildInOneSceneAndRootInAnother_IsRungOnTheRootScene()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Process a = new Process("Region One", 1000, 1000);
            Process b = new Process("Region Two", 1001, 1000);
            a.AddChild(Bob);
            b.AddRoot(Bob);

            string decision = A2AInviteDelivery.Deliver(new[] { a.Scene, b.Scene }, Bob, SomeBody(), null, out string region);

            Assert.Equal(A2AInviteDelivery.DecisionSent, decision);
            Assert.Equal("Region Two", region);
            Assert.Empty(a.Queue.Enqueued);
            Assert.Equal(new[] { Bob }, b.Queue.Enqueued);
        }

        [Fact(Timeout = 60000)]
        public void NoPresenceHere_IsNotRung_AsBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Process p = new Process();

            string decision = A2AInviteDelivery.Deliver(new[] { p.Scene }, Bob, SomeBody(), null, out _);

            Assert.Equal(A2AInviteDelivery.DecisionNoPresence, decision);
            Assert.Empty(p.Queue.Enqueued);
        }
    }
}
