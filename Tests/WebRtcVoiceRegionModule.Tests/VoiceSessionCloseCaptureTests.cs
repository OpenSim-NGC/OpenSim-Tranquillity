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
        [Fact(Timeout = 60000)]
        public void Capture_SelectsOnlyMatchingGeneration()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random();
            UUID oldLogin = UUID.Random(), newLogin = UUID.Random();
            VoiceViewerSession orphan = NewSession(region, agent, oldLogin);
            VoiceViewerSession live = NewSession(region, agent, newLogin);

            List<IVoiceViewerSession> captured =
                VoiceViewerSession.CaptureSessionsForClose(region, agent, oldLogin);

            Assert.Single(captured);
            Assert.Same(orphan, captured[0]);
            Assert.True(VoiceViewerSession.IsAgentInRegion(region, agent), "the successor login's session must keep the agent a member");
            Assert.True(VoiceViewerSession.TryGetViewerSession(live.ViewerSessionID, out _), "the live session must remain in the registry");
            VoiceViewerSession.CloseCompleted(orphan);   // static-state hygiene
        }

        // Two sessions of the SAME login (intra-login reconnect overlap) are both dying at close:
        // both are captured.
        [Fact(Timeout = 60000)]
        public void Capture_TakesEverySessionOfTheDyingLogin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s1 = NewSession(region, agent, login);
            VoiceViewerSession s2 = NewSession(region, agent, login);

            List<IVoiceViewerSession> captured =
                VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            Assert.Equal(2, captured.Count());
            Assert.False(VoiceViewerSession.IsAgentInRegion(region, agent), "capturing the last sessions clears membership");
            VoiceViewerSession.CloseCompleted(s1);
            VoiceViewerSession.CloseCompleted(s2);
        }

        // A UUID.Zero token means the capture failed at provision; such a voice session can only belong
        // to an already-dead or now-dying login, so any close for its agent sweeps it.
        [Fact(Timeout = 60000)]
        public void Capture_SweepsZeroTokenSessions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession untagged = NewSession(region, agent, UUID.Zero);

            List<IVoiceViewerSession> captured =
                VoiceViewerSession.CaptureSessionsForClose(region, agent, UUID.Random());

            Assert.Single(captured);
            Assert.Same(untagged, captured[0]);
            VoiceViewerSession.CloseCompleted(untagged);
        }

        // Atomic unavailability: a captured session is gone from BOTH the registry and the
        // membership index the moment capture returns — no provision, hangup, or matrix read can
        // find it while its Janus cleanup is in flight.
        [Fact(Timeout = 60000)]
        public void Capture_RemovesFromRegistryAndMembership()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);

            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            Assert.False(VoiceViewerSession.TryGetViewerSession(s.ViewerSessionID, out _), "captured session must not be findable in the registry");
            Assert.False(VoiceViewerSession.IsAgentInRegion(region, agent), "captured session must not keep the agent a matrix member");
            VoiceViewerSession.CloseCompleted(s);
        }

        // The remove-and-forget guard: captured sessions are parked (discoverable, with an age)
        // until CloseCompleted; a failed teardown therefore stays visible for the retry hooks.
        [Fact(Timeout = 60000)]
        public void Capture_ParksInClosingSet_UntilCloseCompleted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);

            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            List<(IVoiceViewerSession Session, long AgeMs)> parked =
                VoiceViewerSession.GetClosingSessions(agent);
            Assert.Single(parked);
            Assert.Same(s, parked[0].Session);
            Assert.True(parked[0].AgeMs >= 0);

            VoiceViewerSession.CloseCompleted(s);
            {
                var assertedValue = VoiceViewerSession.GetClosingSessions(agent);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a successful teardown must clear the parked entry");
            }
        }

        [Fact(Timeout = 60000)]
        public void Capture_UnknownAgent_ReturnsEmpty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Empty(VoiceViewerSession.CaptureSessionsForClose(UUID.Random(), UUID.Random(), UUID.Random()));
        }

        // The console snapshot ("show voice closing") records WHY a parked teardown failed.
        // RecordCloseFailure on a parked session surfaces in GetClosingSnapshot with the reason;
        // after CloseCompleted it is a silent no-op (a racing retry may complete first).
        [Fact(Timeout = 60000)]
        public void RecordCloseFailure_SurfacesInSnapshot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);
            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            VoiceViewerSession.RecordCloseFailure(s, "TestException: boom");

            List<(UUID AgentId, string SessionId, long AgeMs, string LastFailure)> snap =
                VoiceViewerSession.GetClosingSnapshot().Where(e => e.AgentId == agent).ToList();
            Assert.Single(snap);
            Assert.Equal(s.ViewerSessionID, snap[0].SessionId);
            Assert.Equal("TestException: boom", snap[0].LastFailure);

            VoiceViewerSession.CloseCompleted(s);
            {
                var assertedValue = () => VoiceViewerSession.RecordCloseFailure(s, "late");
                TestAssert.WithMessage(() => Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                })), "recording a failure for an already-completed session must be a no-op");
            }
            Assert.DoesNotContain(VoiceViewerSession.GetClosingSnapshot(), e => e.AgentId == agent);
        }

        // A hangup arriving after a close-time capture (the two teardown paths racing) must be a
        // silent no-op: the capture already removed the voice session, and RemoveViewerSession tolerates
        // a missing id.
        [Fact(Timeout = 60000)]
        public void HangupAfterCapture_IsNoOp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random(), login = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent, login);
            VoiceViewerSession.CaptureSessionsForClose(region, agent, login);

            {
                var assertedValue = () => VoiceViewerSession.RemoveViewerSession(s.ViewerSessionID);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            {
                var assertedValue = VoiceViewerSession.GetClosingSessions(agent);
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the racing hangup must not disturb the parked closing entry");
            }
            VoiceViewerSession.CloseCompleted(s);
        }
    }
}
