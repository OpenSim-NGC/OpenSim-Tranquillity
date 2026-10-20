/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The region module's OnClientClosed handler used to mark an A2A party gone on EVERY
 * client close, including a CHILD agent closing in a neighbour region, which flipped the party's
 * provisioned flag on a live call. The handler now asks A2ARegistry.ShouldMarkGone (the guard
 * WebRtcVoiceServiceModule.Event_OnClientClosed already applies) before MarkGoneSessions. The module
 * delegate needs a live Scene holding a child presence, so these tests drive the decision and the
 * registry call in the order the handler runs them.
 */
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2AClientCloseGuardTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-1111111a0520");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-2222222a0520");

        private static (A2ARegistry Reg, A2ASession Session) ActiveCall()
        {
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-a");
            reg.MarkProvisioned(s.SessionId, Bob, "vs-b");
            s = reg.TryGet(s.SessionId);
            Assert.That(s.State, Is.EqualTo(A2ASessionState.Active), "precondition: both parties joined");
            return (reg, s);
        }

        // The handler's order: decide from the presence, then mark gone only if the decision says so.
        private static void HandlerClose(A2ARegistry reg, UUID agent, bool presenceFound, bool isChildAgent)
        {
            if (A2ARegistry.ShouldMarkGone(presenceFound, isChildAgent))
                reg.MarkGoneSessions(agent, null);
        }

        [Test]
        public void ShouldMarkGone_RootPresence_IsTrue()
        {
            Assert.That(A2ARegistry.ShouldMarkGone(presenceFound: true, isChildAgent: false), Is.True);
        }

        [Test]
        public void ShouldMarkGone_ChildAgent_IsFalse()
        {
            Assert.That(A2ARegistry.ShouldMarkGone(presenceFound: true, isChildAgent: true), Is.False);
        }

        [Test]
        public void ShouldMarkGone_NoPresence_IsFalse()
        {
            Assert.That(A2ARegistry.ShouldMarkGone(presenceFound: false, isChildAgent: false), Is.False);
        }

        [Test]
        public void ChildAgentClose_LeavesBothProvisionedFlagsSet()
        {
            var (reg, s) = ActiveCall();

            HandlerClose(reg, Alice, presenceFound: true, isChildAgent: true);

            A2ASession after = reg.TryGet(s.SessionId);
            Assert.That(after, Is.Not.Null, "the live call's record survives a child close");
            Assert.That(after.CallerProvisioned, Is.True, "a child close must not mark the caller gone");
            Assert.That(after.CalleeProvisioned, Is.True);
        }

        [Test]
        public void RootClose_MarksThePartyGone()
        {
            var (reg, s) = ActiveCall();

            HandlerClose(reg, Alice, presenceFound: true, isChildAgent: false);

            A2ASession after = reg.TryGet(s.SessionId);
            Assert.That(after, Is.Not.Null, "one party still in: the record stays (both-logout semantics)");
            Assert.That(after.CallerProvisioned, Is.False, "the root close marks the caller gone");
            Assert.That(after.CalleeProvisioned, Is.True);
        }
    }
}
