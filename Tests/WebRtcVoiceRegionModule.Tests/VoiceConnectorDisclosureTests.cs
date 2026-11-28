/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Disclosure-layer tests.
 * The three layers are pinned at the VoiceConnectorDisclosure seams (fake delegates capture what
 * was delivered) plus the registrar call-through for the attach/detach door notices. No Scene is
 * constructed; the module wires the real surfaces (SendNotificationToUsersInRegion,
 * SendAlertToUser, SendChatMessage) and stays a thin adapter.
 */
using Xunit;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class VoiceConnectorDisclosureTests : IAsyncLifetime
        {
        private const float Range = 20f;

        private List<string> m_alerts;
        private List<(UUID Agent, string Message)> m_notices;
        private List<(UUID NpcId, UUID Agent, string Message)> m_chats;
        private VoiceConnectorDisclosure m_disclosure;

        private static VoiceConnectorRecord Voiced(string name = "Speaker")
        {
            VoiceConnectorRecord r = new VoiceConnectorRecord(name, true, name, "NPC",
                new Vector3(128, 128, 25), VoiceConnectorScope.Estate, true, "Operator");
            r.NpcId = UUID.Random();
            return r;
        }

        private static VoiceConnectorRecord Recording(string name = "Recorder")
        {
            VoiceConnectorRecord r = new VoiceConnectorRecord(name, true, name, "NPC",
                new Vector3(128, 128, 25), VoiceConnectorScope.Estate, false, "Operator");
            r.NpcId = UUID.Random();
            return r;
        }
        private void SetUp()
        {
            m_alerts = new List<string>();
            m_notices = new List<(UUID, string)>();
            m_chats = new List<(UUID, UUID, string)>();
            m_disclosure = new VoiceConnectorDisclosure(
                msg => m_alerts.Add(msg),
                (agent, msg) => m_notices.Add((agent, msg)),
                (record, agent, msg) => m_chats.Add((record.NpcId, agent, msg)),
                Range);
        }

        // ---- Attach/detach door notices

        [Fact(Timeout = 60000)]
        public void AttachAndDetach_AlertWithTheRightText()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRecord r = Recording();
            m_disclosure.OnAttach(r);
            m_disclosure.OnDetach(r);
            Assert.Equal(2, m_alerts.Count());
            Assert.Equal("Voice connector Recorder NPC attached — an NPC (recording / automated voice) is present in this region's voice.", m_alerts[0]);
            Assert.Equal("Voice connector Recorder NPC detached — its NPC has left this region's voice.", m_alerts[1]);
        }

        [Fact(Timeout = 60000)]
        public void Registrar_CallsAttachOnSuccess_AndDetachOnTeardown()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRecord r = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC",
                new Vector3(1, 2, 3), VoiceConnectorScope.Estate, false, "Operator");
            UUID npc = UUID.Random();
            try
            {
                VoiceConnectorRegistrar.Register(r, 1, _ => npc,
                    id => new VoiceViewerSession(null, UUID.Random(), id),
                    (id, room) => { }, id => { }, null, m_disclosure);
                Assert.Single(m_alerts);
                Assert.Contains("attached", m_alerts[0]);

                VoiceConnectorRegistrar.Unregister(r, id => { }, null, m_disclosure);
                Assert.Equal(2, m_alerts.Count());
                Assert.Contains("detached", m_alerts[1]);

                // A second (idempotent) teardown of the now-inactive record stays silent.
                VoiceConnectorRegistrar.Unregister(r, id => { }, null, m_disclosure);
                Assert.Equal(2, m_alerts.Count());
            }
            finally
            {
                if (r.ViewerSessionId != null)
                    VoiceViewerSession.RemoveViewerSession(r.ViewerSessionId);
            }
        }

        [Fact(Timeout = 60000)]
        public void Registrar_NpcFailure_NoAttachAlert()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRecord r = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC",
                new Vector3(1, 2, 3), VoiceConnectorScope.Estate, false, "Operator");
            VoiceConnectorRegistrar.Register(r, 1, _ => UUID.Zero,
                id => new VoiceViewerSession(null, UUID.Random(), id),
                (id, room) => { }, id => { }, null, m_disclosure);
            {
                var assertedValue = m_alerts;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "an inactive record is never announced as attached");
            }
        }

        // ---- Entry notice

        [Fact(Timeout = 60000)]
        public void EntryNotice_OncePerLoginSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID agent = UUID.Random();
            UUID session = UUID.Random();
            List<VoiceConnectorRecord> attached = new List<VoiceConnectorRecord> { Recording(), Voiced() };

            m_disclosure.OnMakeRoot(agent, session, attached);
            Assert.Single(m_notices);
            Assert.Equal(agent, m_notices[0].Agent);
            Assert.Contains("Recorder NPC (recording)", m_notices[0].Message);
            Assert.Contains("Speaker NPC (voiced)", m_notices[0].Message);

            // Same login session again (teleport out and back, another root transition): silent.
            m_disclosure.OnMakeRoot(agent, session, attached);
            {
                var assertedValue = m_notices;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "not twice for one login session");
            }

            // A relog is a new SessionId: re-armed.
            m_disclosure.OnMakeRoot(agent, UUID.Random(), attached);
            Assert.Equal(2, m_notices.Count());
        }

        [Fact(Timeout = 60000)]
        public void EntryNotice_NothingWhenNoConnectorAttached()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID session = UUID.Random();
            m_disclosure.OnMakeRoot(UUID.Random(), session, new List<VoiceConnectorRecord>());
            Assert.Empty(m_notices);
            // And the quiet pass did NOT consume the one notice owed to this login session: an attach later
            // still yields the entry notice on the next root transition.
            m_disclosure.OnMakeRoot(UUID.Random(), session, new List<VoiceConnectorRecord> { Recording() });
            Assert.Single(m_notices);
        }

        // ---- Proximity notice

        [Fact(Timeout = 60000)]
        public void ProximityNotice_FirstApproachOnly_PerAgentPerNpc()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRecord speaker = Voiced();
            UUID agentA = UUID.Random(); UUID sessionA = UUID.Random();
            UUID agentB = UUID.Random(); UUID sessionB = UUID.Random();
            Vector3 near = speaker.Position + new Vector3(5, 0, 0);
            Vector3 far = speaker.Position + new Vector3(Range + 10, 0, 0);
            List<VoiceConnectorRecord> voiced = new List<VoiceConnectorRecord> { speaker };

            // Out of range: nothing.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)> { (agentA, sessionA, far) }, voiced);
            Assert.Empty(m_chats);

            // First approach: one chat line, from the NPC, with the proximity-notice text.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)> { (agentA, sessionA, near) }, voiced);
            Assert.Single(m_chats);
            Assert.Equal(speaker.NpcId, m_chats[0].NpcId);
            Assert.Equal(agentA, m_chats[0].Agent);
            Assert.Equal("Speaker NPC is an NPC — its voice is automated or remotely operated.", m_chats[0].Message);

            // Still in range next tick: not again for the same agent/NPC.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)> { (agentA, sessionA, near) }, voiced);
            Assert.Single(m_chats);

            // A different agent approaching: fires for them.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)>
                { (agentA, sessionA, near), (agentB, sessionB, near) }, voiced);
            Assert.Equal(2, m_chats.Count());
            Assert.Equal(agentB, m_chats[1].Agent);
        }

        [Fact(Timeout = 60000)]
        public void ProximityNotice_NeverForRecordingOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceConnectorRecord recorder = Recording();
            m_disclosure.ProximityTick(
                new List<(UUID, UUID, Vector3)> { (UUID.Random(), UUID.Random(), recorder.Position) },
                new List<VoiceConnectorRecord> { recorder });
            {
                var assertedValue = m_chats;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "MayInject=false has no voice, so no proximity notice");
            }
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
