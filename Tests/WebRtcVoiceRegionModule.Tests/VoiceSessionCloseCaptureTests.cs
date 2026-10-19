/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for close-time voice-session capture (CaptureSessionsForClose) and the closing-set
 * (remove-and-forget guard) on VoiceViewerSession — the registry side of the OnClientClosed
 * teardown (KnownDefects OnRemovePresence entry, external review 2026-08-22).
 *
 * Pure static-collection logic, no Scene. Fresh random region/agent UUIDs per test isolate the
 * process-wide static state, exactly as VoiceMembershipIndexTests does.
 */

using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceSessionCloseCaptureTests
    {
        private static VoiceViewerSession NewSession(UUID region, UUID agent, UUID generation)
        {
            VoiceViewerSession s = new VoiceViewerSession(null, region, agent)
            {
                ClientSessionId = generation
            };
            VoiceViewerSession.AddViewerSession(s);
            return s;
        }

        // The review's core hazard: after a relog, an orphan (old login) and a live session (new
        // login) coexist for the same agent in the same region. The old login's close must capture
        // ONLY its own generation and never the successor's.
        [Test]
        public void Capture_SelectsOnlyMatchingGeneration()
        {
            UUID region = UUID.Random(), agent = UUID.Random();
            UUID oldLogin = UUID.Random(), newLogin = UUID.Random();
            VoiceViewerSession orphan = NewSession(region, agent, oldLogin);
            VoiceViewerSession live = NewSession(region, agent, newLogin);

            List<IVoiceViewerSession> captured =
                VoiceViewerSession.CaptureSessionsForClose(region, agent, oldLogin);

            Assert.That(captured, Has.Count.EqualTo(1));
            Assert.That(captured[0], Is.SameAs(orphan));
            Assert.That(VoiceViewerSession.IsAgentInRegion(region, agent), Is.True,
                "the successor login's session must keep the agent a member");
            Assert.That(VoiceViewerSession.TryGetViewerSession(live.ViewerSessionID, out _), Is.True,
                "the live session must remain in the registry");
            VoiceViewerSession.CloseCompleted(orphan);   // static-state hygiene
        }

        // Two sessions of the SAME login (intra-login reconnect overlap) are both dying at close:
        // both are captured.
        [Test]
        public void Capture_TakesEverySessionOfTheDyingLogin()
        {
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s1 = NewSession(region, agent, login);
            VoiceViewerSession s2 = NewSession(region, agent, login);

            List<IVoiceViewerSession> captured =
                VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            Assert.That(captured, Has.Count.EqualTo(2));
            Assert.That(VoiceViewerSession.IsAgentInRegion(region, agent), Is.False,
                "capturing the last sessions clears membership");
            VoiceViewerSession.CloseCompleted(s1);
            VoiceViewerSession.CloseCompleted(s2);
        }

        // A UUID.Zero token means the capture failed at provision; such a voice session can only belong
        // to an already-dead or now-dying login, so any close for its agent sweeps it.
        [Test]
        public void Capture_SweepsZeroTokenSessions()
        {
            UUID region = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession untagged = NewSession(region, agent, UUID.Zero);

            List<IVoiceViewerSession> captured =
                VoiceViewerSession.CaptureSessionsForClose(region, agent, UUID.Random());

            Assert.That(captured, Has.Count.EqualTo(1));
            Assert.That(captured[0], Is.SameAs(untagged));
            VoiceViewerSession.CloseCompleted(untagged);
        }

        // Atomic unavailability: a captured session is gone from BOTH the registry and the
        // membership index the moment capture returns — no provision, hangup, or matrix read can
        // find it while its Janus cleanup is in flight.
        [Test]
        public void Capture_RemovesFromRegistryAndMembership()
        {
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);

            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            Assert.That(VoiceViewerSession.TryGetViewerSession(s.ViewerSessionID, out _), Is.False,
                "captured session must not be findable in the registry");
            Assert.That(VoiceViewerSession.IsAgentInRegion(region, agent), Is.False,
                "captured session must not keep the agent a matrix member");
            VoiceViewerSession.CloseCompleted(s);
        }

        // The remove-and-forget guard: captured sessions are parked (discoverable, with an age)
        // until CloseCompleted; a failed teardown therefore stays visible for the retry hooks.
        [Test]
        public void Capture_ParksInClosingSet_UntilCloseCompleted()
        {
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);

            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            List<(IVoiceViewerSession Session, long AgeMs)> parked =
                VoiceViewerSession.GetClosingSessions(agent);
            Assert.That(parked, Has.Count.EqualTo(1));
            Assert.That(parked[0].Session, Is.SameAs(s));
            Assert.That(parked[0].AgeMs, Is.GreaterThanOrEqualTo(0));

            VoiceViewerSession.CloseCompleted(s);
            Assert.That(VoiceViewerSession.GetClosingSessions(agent), Is.Empty,
                "a successful teardown must clear the parked entry");
        }

        [Test]
        public void Capture_UnknownAgent_ReturnsEmpty()
        {
            Assert.That(
                VoiceViewerSession.CaptureSessionsForClose(UUID.Random(), UUID.Random(), UUID.Random()),
                Is.Empty);
        }

        // The console snapshot ("show voice closing") records WHY a parked teardown failed.
        // RecordCloseFailure on a parked session surfaces in GetClosingSnapshot with the reason;
        // after CloseCompleted it is a silent no-op (a racing retry may complete first).
        [Test]
        public void RecordCloseFailure_SurfacesInSnapshot()
        {
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);
            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            VoiceViewerSession.RecordCloseFailure(s, "TestException: boom");

            List<(UUID AgentId, string SessionId, long AgeMs, string LastFailure)> snap =
                VoiceViewerSession.GetClosingSnapshot().Where(e => e.AgentId == agent).ToList();
            Assert.That(snap, Has.Count.EqualTo(1));
            Assert.That(snap[0].SessionId, Is.EqualTo(s.ViewerSessionID));
            Assert.That(snap[0].LastFailure, Is.EqualTo("TestException: boom"));

            VoiceViewerSession.CloseCompleted(s);
            Assert.DoesNotThrow(() => VoiceViewerSession.RecordCloseFailure(s, "late"),
                "recording a failure for an already-completed session must be a no-op");
            Assert.That(VoiceViewerSession.GetClosingSnapshot().Where(e => e.AgentId == agent), Is.Empty);
        }

        // A hangup arriving after a close-time capture (the two teardown paths racing) must be a
        // silent no-op: the capture already removed the voice session, and RemoveViewerSession tolerates
        // a missing id.
        [Test]
        public void HangupAfterCapture_IsNoOp()
        {
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);
            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            Assert.DoesNotThrow(() => VoiceViewerSession.RemoveViewerSession(s.ViewerSessionID));
            Assert.That(VoiceViewerSession.GetClosingSessions(agent), Has.Count.EqualTo(1),
                "the racing hangup must not disturb the parked closing entry");
            VoiceViewerSession.CloseCompleted(s);
        }
    }
}
