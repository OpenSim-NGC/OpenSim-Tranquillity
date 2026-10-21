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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceConnectorRegistrarTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-00000005c025");
        private const int EstateRoom = 226001844;

        private VoiceConnectorRecord m_record;
        private UUID m_npcId;
        private List<string> m_calls;
        private int? m_recordedRoom;

        [SetUp]
        public void SetUp()
        {
            m_record = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC",
                new Vector3(128, 128, 25), VoiceConnectorScope.Estate, false, "Operator", null);
            m_npcId = UUID.Random();
            m_calls = new List<string>();
            m_recordedRoom = null;
        }

        [TearDown]
        public void TearDown()
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

        [Test]
        public void Register_SetsSlots_Membership_Room_AndMute()
        {
            bool ok = VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => { m_calls.Add("createNpc"); return m_npcId; },
                NewSession,
                (id, room) => { m_calls.Add("recordRoom"); m_recordedRoom = room; },
                id => m_calls.Add("mute"),
                null);

            Assert.That(ok, Is.True);
            Assert.That(m_record.NpcId, Is.EqualTo(m_npcId));
            Assert.That(m_record.ViewerSessionId, Is.Not.Null);
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), Is.True, "membership on");
            Assert.That(VoiceViewerSession.TryGetViewerSession(m_record.ViewerSessionId, out IVoiceViewerSession s), Is.True);
            Assert.That(s.ClientSessionId, Is.Not.EqualTo(UUID.Zero), "the SessionId==Zero trap is closed");
            Assert.That(m_recordedRoom, Is.EqualTo(EstateRoom));
            Assert.That(m_calls, Is.EqualTo(new[] { "createNpc", "createSession", "recordRoom", "mute" }),
                "order: NPC, session, room, mute — and MayInject=false pushes the mute");
        }

        [Test]
        public void Register_MayInjectTrue_DoesNotMute()
        {
            VoiceConnectorRecord record = new VoiceConnectorRecord("Speaker", true, "Speaker", "NPC",
                new Vector3(1, 2, 3), VoiceConnectorScope.Estate, true, "Operator", null);
            try
            {
                bool ok = VoiceConnectorRegistrar.Register(record, EstateRoom,
                    r => m_npcId, NewSession, (id, room) => { }, id => m_calls.Add("mute"), null);
                Assert.That(ok, Is.True);
                Assert.That(m_calls, Does.Not.Contain("mute"));
            }
            finally
            {
                if (record.ViewerSessionId != null)
                    VoiceViewerSession.RemoveViewerSession(record.ViewerSessionId);
            }
        }

        [Test]
        public void Unregister_ClearsMembershipAndSlots_DeletesNpc()
        {
            VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => m_npcId, NewSession, (id, room) => { }, id => { }, null);
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), Is.True, "precondition");

            VoiceConnectorRegistrar.Unregister(m_record, id => m_calls.Add($"deleteNpc:{id}"), null);

            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), Is.False, "membership off");
            Assert.That(m_record.NpcId, Is.EqualTo(UUID.Zero));
            Assert.That(m_record.ViewerSessionId, Is.Null);
            Assert.That(m_calls, Does.Contain($"deleteNpc:{m_npcId}"));

            // Idempotent: a second teardown does nothing.
            m_calls.Clear();
            VoiceConnectorRegistrar.Unregister(m_record, id => m_calls.Add("deleteNpc-again"), null);
            Assert.That(m_calls, Is.Empty);
        }

        [Test]
        public void Register_CreateNpcFailure_LeavesRecordInactive()
        {
            bool ok = VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => UUID.Zero,   // the INPCModule failure contract
                NewSession,
                (id, room) => m_calls.Add("recordRoom"),
                id => m_calls.Add("mute"),
                null);

            Assert.That(ok, Is.False);
            Assert.That(m_record.NpcId, Is.EqualTo(UUID.Zero));
            Assert.That(m_record.ViewerSessionId, Is.Null, "no session was created");
            Assert.That(m_calls, Is.Empty, "no session, no room record, no mute");
            Assert.That(VoiceViewerSession.IsAgentInRegion(Region, m_npcId), Is.False);
        }

        [Test]
        public void Register_AlreadyRegistered_IsNoOp()
        {
            VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => m_npcId, NewSession, (id, room) => { }, id => { }, null);
            m_calls.Clear();
            bool ok = VoiceConnectorRegistrar.Register(m_record, EstateRoom,
                r => { m_calls.Add("createNpc"); return UUID.Random(); }, NewSession, (id, room) => { }, id => { }, null);
            Assert.That(ok, Is.True);
            Assert.That(m_calls, Is.Empty, "an active record is not re-registered");
        }
    }
}
