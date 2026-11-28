/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Registration/teardown orchestration tests.
 * The registrar is the extracted, delegate-seamed core of VoiceConnectorModule (no Scene, no
 * INPCModule, no live Janus constructed here — the fakes stand in at exactly the module's real
 * integration seams). The REAL VoiceViewerSession statics are used, as ViewerSessionBindingTests
 * does, so membership (IsAgentInRegion) is proven against the actual registry.
 */
using Xunit;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class VoiceConnectorRegistrarTests : IAsyncLifetime
        {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-00000005c025");
        private const int EstateRoom = 226001844;

        private VoiceConnectorRecord m_record;
        private UUID m_npcId;
        private List<string> m_calls;
        private int? m_recordedRoom;
        private void SetUp()
        {
            m_record = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC",
                new Vector3(128, 128, 25), VoiceConnectorScope.Estate, false, "Operator");
            m_npcId = UUID.Random();
            m_calls = new List<string>();
            m_recordedRoom = null;
        }
        private void TearDown()
        {
            // Never leak a registered connector into the static registry across tests.
            if (m_record.ViewerSessionId != null)
                VoiceViewerSession.RemoveViewerSession(m_record.ViewerSessionId);
        }

        private IVoiceViewerSession NewSession(UUID npcId)
        {
            m_calls.Add("createSession");
            return new VoiceViewerSession(null, Region, npcId);
        }

        [Fact(Timeout = 60000)]
        public void Register_SetsSlots_Membership_Room_AndMute()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool ok = VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => { m_calls.Add("createNpc"); return m_npcId; },
                NewSession,
                (id, room) => { m_calls.Add("recordRoom"); m_recordedRoom = room; },
                id => m_calls.Add("mute"),
                null);

            Assert.True(ok);
            Assert.Equal(m_npcId, m_record.NpcId);
            Assert.NotNull(m_record.ViewerSessionId);
            Assert.True(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), "membership on");
            Assert.True(VoiceViewerSession.TryGetViewerSession(m_record.ViewerSessionId, out IVoiceViewerSession s));
            {
                var assertedValue = s.ClientSessionId;
                TestAssert.WithMessage(() => Assert.NotEqual(UUID.Zero, assertedValue), "the SessionId==Zero trap is closed");
            }
            Assert.Equal(EstateRoom, m_recordedRoom);
            {
                var assertedValue = m_calls;
                TestAssert.WithMessage(() => Assert.Equal(new[] { "createNpc", "createSession", "recordRoom", "mute" }, assertedValue), "order: NPC, session, room, mute — and MayInject=false pushes the mute");
            }
        }

        [Fact(Timeout = 60000)]
        public void Register_MayInjectTrue_DoesNotMute()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRecord record = new VoiceConnectorRecord("Speaker", true, "Speaker", "NPC",
                new Vector3(1, 2, 3), VoiceConnectorScope.Estate, true, "Operator");
            try
            {
                bool ok = VoiceConnectorRegistrar.Register(record, EstateRoom,
                    r => m_npcId, NewSession, (id, room) => { }, id => m_calls.Add("mute"), null);
                Assert.True(ok);
                Assert.DoesNotContain("mute", m_calls);
            }
            finally
            {
                if (record.ViewerSessionId != null)
                    VoiceViewerSession.RemoveViewerSession(record.ViewerSessionId);
            }
        }

        [Fact(Timeout = 60000)]
        public void Unregister_ClearsMembershipAndSlots_DeletesNpc()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => m_npcId, NewSession, (id, room) => { }, id => { }, null);
            Assert.True(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), "precondition");

            VoiceConnectorRegistrar.Unregister(m_record, id => m_calls.Add($"deleteNpc:{id}"), null);

            Assert.False(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), "membership off");
            Assert.Equal(UUID.Zero, m_record.NpcId);
            Assert.Null(m_record.ViewerSessionId);
            Assert.Contains($"deleteNpc:{m_npcId}", m_calls);

            // Idempotent: a second teardown does nothing.
            m_calls.Clear();
            VoiceConnectorRegistrar.Unregister(m_record, id => m_calls.Add("deleteNpc-again"), null);
            Assert.Empty(m_calls);
        }

        [Fact(Timeout = 60000)]
        public void Register_CreateNpcFailure_LeavesRecordInactive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool ok = VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => UUID.Zero,   // the INPCModule failure contract
                NewSession,
                (id, room) => m_calls.Add("recordRoom"),
                id => m_calls.Add("mute"),
                null);

            Assert.False(ok);
            Assert.Equal(UUID.Zero, m_record.NpcId);
            {
                var assertedValue = m_record.ViewerSessionId;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no session was created");
            }
            {
                var assertedValue = m_calls;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no session, no room record, no mute");
            }
            Assert.False(VoiceViewerSession.IsAgentInRegion(Region, m_npcId));
        }

        [Fact(Timeout = 60000)]
        public void Register_AlreadyRegistered_IsNoOp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => m_npcId, NewSession, (id, room) => { }, id => { }, null);
            m_calls.Clear();
            bool ok = VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => { m_calls.Add("createNpc"); return UUID.Random(); }, NewSession, (id, room) => { }, id => { }, null);
            Assert.True(ok);
            {
                var assertedValue = m_calls;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "an active record is not re-registered");
            }
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
