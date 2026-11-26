/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for the refcounted, per-region voice-membership index on VoiceViewerSession — the
 * O(1) gate FeederWorldFromScene.SnapshotAgents uses to admit a presence into the matrix.
 *
 * No Scene is built here: this is pure static-collection logic, so it sidesteps the ScenePresence
 * finalizer fragility that keeps FeederWorldFromSceneTests from creating presences.
 *
 * VoiceViewerSession.ViewerSessions (and the index beside it) is process-wide static state. Each
 * test uses fresh region/agent UUIDs so nothing leaks across tests: the index is region-scoped, so
 * distinct random regions are fully isolated from one another regardless of run order.
 */

using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class VoiceMembershipIndexTests
    {
        // VoiceService is only stored, never dereferenced by the index paths, so null is fine.
        private static VoiceViewerSession NewSession(UUID region, UUID agent)
            => new VoiceViewerSession(null, region, agent);

        [Fact(Timeout = 60000)]
        public void AddThenQuery_AgentIsPresent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession.AddViewerSession(NewSession(region, agent));
            Assert.True(VoiceViewerSession.IsAgentInRegion(region, agent));
        }

        [Fact(Timeout = 60000)]
        public void RemoveThenQuery_AgentIsAbsent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent);
            VoiceViewerSession.AddViewerSession(s);
            VoiceViewerSession.RemoveViewerSession(s.ViewerSessionID);
            Assert.False(VoiceViewerSession.IsAgentInRegion(region, agent));
        }

        // Refcount: while a second session for the same agent is still live, removing the first must
        // NOT blink the agent out of the matrix. Only the last session leaving clears membership.
        [Fact(Timeout = 60000)]
        public void TwoSessionsOneRemoved_AgentStaysPresent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession s1 = NewSession(region, agent);
            VoiceViewerSession s2 = NewSession(region, agent);
            VoiceViewerSession.AddViewerSession(s1);
            VoiceViewerSession.AddViewerSession(s2);

            VoiceViewerSession.RemoveViewerSession(s1.ViewerSessionID);
            Assert.True(VoiceViewerSession.IsAgentInRegion(region, agent), "agent must remain a member while its second session is live");

            VoiceViewerSession.RemoveViewerSession(s2.ViewerSessionID);
            Assert.False(VoiceViewerSession.IsAgentInRegion(region, agent), "membership clears once the last session leaves");
        }

        // Teardown paths genuinely re-arrive for the same session (the registry leaks and cleanup is
        // best-effort). A second remove of a now-cleared id must be a silent no-op, never throwing
        // and never driving the refcount negative.
        [Fact(Timeout = 60000)]
        public void DoubleRemove_IsNoOpAndDoesNotThrow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession s = NewSession(region, agent);
            VoiceViewerSession.AddViewerSession(s);
            VoiceViewerSession.RemoveViewerSession(s.ViewerSessionID);

            {
                var assertedValue = () => VoiceViewerSession.RemoveViewerSession(s.ViewerSessionID);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            Assert.False(VoiceViewerSession.IsAgentInRegion(region, agent), "a second remove must not resurrect or corrupt membership");
        }

        // A remove for a session id the index never saw. Removes genuinely re-arrive for entries
        // already cleared (a hangup racing a close-time capture) or never registered. That must be
        // a no-op on the teardown path, not an exception.
        [Fact(Timeout = 60000)]
        public void RemoveNeverAdded_IsNoOp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = () => VoiceViewerSession.RemoveViewerSession(UUID.Random().ToString());
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
        }

        // Region scoping: the index answers per-region. An agent voiced in region A must not read as
        // present in region B — the exact case (adjacent-region child agents) that a global check
        // would wrongly admit as a spurious matrix column.
        [Fact(Timeout = 60000)]
        public void RegionScoping_AgentInRegionA_NotPresentInRegionB()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID regionA = UUID.Random(), regionB = UUID.Random(), agent = UUID.Random();
            VoiceViewerSession.AddViewerSession(NewSession(regionA, agent));

            Assert.True(VoiceViewerSession.IsAgentInRegion(regionA, agent), "present in its own region");
            Assert.False(VoiceViewerSession.IsAgentInRegion(regionB, agent), "an adjacent region must not see the agent as a voice participant");
        }
    }
}
