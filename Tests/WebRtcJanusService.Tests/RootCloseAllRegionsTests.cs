/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A ROOT client close tears down the agent's voice sessions in EVERY region of this instance.
 * Without that, when an agent quits, the root close in region A captures only region A's session, the
 * neighbour region B's (child) close is ignored by the child guard, and the viewer never logs the
 * region B session out - it stays a ghost participant holding a mixer slot, and the room cannot
 * grace-destroy. The decision is WebRtcVoiceServiceModule.CaptureForClientClose (the close handler feeds
 * it the presence's child flag and the regions where the agent is currently root elsewhere).
 *
 * Teleport ordering (EntityTransferModule.TransferAgent_V2): the source presence is flagged child
 * (:1194) before UpdateAgent waits for the destination to become root, made child (:1232), and only then
 * closed (:1260) - so the source's close is a CHILD close and captures nothing.
 */
using OpenMetaverse;

namespace osWebRtcVoice.Tests
{
    public class RootCloseAllRegionsTests : IAsyncLifetime
        {
        private static readonly UUID RegionA = new UUID("bbbbbbbb-0000-0000-0000-0000000a0074");
        private static readonly UUID RegionB = new UUID("bbbbbbbb-0000-0000-0000-0000000b0074");
        private static readonly UUID TestAgent = new UUID("aaaaaaaa-2222-2222-2222-000000000074");
        private static readonly UUID Login = new UUID("cccccccc-3333-3333-3333-000000000074");
        private static readonly UUID NextLogin = new UUID("dddddddd-4444-4444-4444-000000000074");
        private void TearDown()
        {
            foreach (UUID token in new[] { Login, NextLogin, UUID.Zero })
                foreach (UUID region in new[] { RegionA, RegionB })
                    foreach (IVoiceViewerSession s in VoiceViewerSession.CaptureSessionsForClose(region, TestAgent, token))
                        VoiceViewerSession.CloseCompleted(s);
        }

        private static FakeViewerSession Register(UUID region, UUID login)
        {
            var s = new FakeViewerSession
            {
                VoiceService = new FakeVoiceService(null),
                AgentId = TestAgent,
                RegionId = region,
                ClientSessionId = login,
            };
            VoiceViewerSession.AddViewerSession(s);
            return s;
        }

        private static void Release(IEnumerable<IVoiceViewerSession> captured)
        {
            foreach (IVoiceViewerSession s in captured)
                VoiceViewerSession.CloseCompleted(s);
        }

        [Fact(Timeout = 60000)]
        public void RootClose_CapturesTheAgentsSessionsInEveryRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeViewerSession root = Register(RegionA, Login);
            FakeViewerSession neighbour = Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                TestAgent, pClosingPresenceIsChild: false, Login, new HashSet<UUID>());

            {
                var assertedValue = captured;
                TestAssert.WithMessage(() => Assert.Equivalent(new IVoiceViewerSession[] { root, neighbour }, assertedValue, strict: true), "the root region's AND the neighbour region's session are captured");
            }
            Assert.False(VoiceViewerSession.IsAgentInRegion(RegionA, TestAgent));
            Assert.False(VoiceViewerSession.IsAgentInRegion(RegionB, TestAgent), "no ghost left in the neighbour region");
            {
                var assertedValue = VoiceViewerSession.CaptureSessionsForClose(RegionA, TestAgent, Login);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "registry empty for the agent in A");
            }
            {
                var assertedValue = VoiceViewerSession.CaptureSessionsForClose(RegionB, TestAgent, Login);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "registry empty for the agent in B");
            }
            Release(captured);
        }

        [Fact(Timeout = 60000)]
        public void ChildClose_CapturesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Register(RegionA, Login);
            Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                TestAgent, pClosingPresenceIsChild: true, Login, new HashSet<UUID>());

            {
                var assertedValue = captured;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a child close (border / draw distance) tears nothing down");
            }
            Assert.True(VoiceViewerSession.IsAgentInRegion(RegionA, TestAgent));
            Assert.True(VoiceViewerSession.IsAgentInRegion(RegionB, TestAgent));
        }

        [Fact(Timeout = 60000)]
        public void TeleportSourceClose_IsAChildClose_DestinationSessionSurvives()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Same login in both regions: the teleport keeps the client SessionId, so the token alone
            // could not protect the destination. The ordering does: the source is already child when
            // CloseAgent fires after the teleport.
            FakeViewerSession source = Register(RegionA, Login);
            FakeViewerSession destination = Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                TestAgent, pClosingPresenceIsChild: true, Login, new HashSet<UUID>());

            Assert.Empty(captured);
            Assert.True(VoiceViewerSession.IsAgentInRegion(RegionB, TestAgent), "the new root region's session is untouched");
            Assert.Equal(0, destination.ShutdownCalls);
            Assert.Equal(0, source.ShutdownCalls);
        }

        [Fact(Timeout = 60000)]
        public void RootClose_SkipsARegionWhereTheAgentIsCurrentlyRoot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Belt and braces for any path that closed a root while the agent is already root elsewhere.
            FakeViewerSession closing = Register(RegionA, Login);
            FakeViewerSession liveRoot = Register(RegionB, Login);

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                TestAgent, pClosingPresenceIsChild: false, Login, new HashSet<UUID> { RegionB });

            Assert.Equivalent(new IVoiceViewerSession[] { closing }, captured, strict: true);
            Assert.True(VoiceViewerSession.IsAgentInRegion(RegionB, TestAgent), "the region where the agent is root keeps its session");
            Assert.Equal(0, liveRoot.ShutdownCalls);
            Release(captured);
        }

        [Fact(Timeout = 60000)]
        public void RootClose_NeverCapturesASuccessorLoginsSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeViewerSession dying = Register(RegionA, Login);
            Register(RegionB, NextLogin);   // a relog racing the close, in the neighbour region

            List<IVoiceViewerSession> captured = WebRtcVoiceServiceModule.CaptureForClientClose(
                TestAgent, pClosingPresenceIsChild: false, Login, new HashSet<UUID>());

            Assert.Equivalent(new IVoiceViewerSession[] { dying }, captured, strict: true);
            Assert.True(VoiceViewerSession.IsAgentInRegion(RegionB, TestAgent), "the successor login's session survives");
            Release(captured);
        }

        public ValueTask InitializeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
