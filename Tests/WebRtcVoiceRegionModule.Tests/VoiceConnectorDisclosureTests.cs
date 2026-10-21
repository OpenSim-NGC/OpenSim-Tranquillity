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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceConnectorDisclosureTests
    {
        private const float Range = 20f;

        private List<string> m_alerts;
        private List<(UUID Agent, string Message)> m_notices;
        private List<(UUID NpcId, UUID Agent, string Message)> m_chats;
        private VoiceConnectorDisclosure m_disclosure;

        private static VoiceConnectorRecord Voiced(string name = "Speaker")
        {
            VoiceConnectorRecord r = new VoiceConnectorRecord(name, true, name, "NPC",
                new Vector3(128, 128, 25), VoiceConnectorScope.Estate, true, "Operator", null);
            r.NpcId = UUID.Random();
            return r;
        }

        private static VoiceConnectorRecord Recording(string name = "Recorder")
        {
            VoiceConnectorRecord r = new VoiceConnectorRecord(name, true, name, "NPC",
                new Vector3(128, 128, 25), VoiceConnectorScope.Estate, false, "Operator", null);
            r.NpcId = UUID.Random();
            return r;
        }

        [SetUp]
        public void SetUp()
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

        [Test]
        public void AttachAndDetach_AlertWithTheRightText()
        {
            VoiceConnectorRecord r = Recording();
            m_disclosure.OnAttach(r);
            m_disclosure.OnDetach(r);
            Assert.That(m_alerts, Has.Count.EqualTo(2));
            Assert.That(m_alerts[0], Is.EqualTo(
                "Voice connector Recorder NPC attached — an NPC (recording / automated voice) is present in this region's voice."));
            Assert.That(m_alerts[1], Is.EqualTo(
                "Voice connector Recorder NPC detached — its NPC has left this region's voice."));
        }

        [Test]
        public void Registrar_CallsAttachOnSuccess_AndDetachOnTeardown()
        {
            VoiceConnectorRecord r = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC",
                new Vector3(1, 2, 3), VoiceConnectorScope.Estate, false, "Operator", null);
            UUID npc = UUID.Random();
            try
            {
                VoiceConnectorRegistrar.Register(r, 1, _ => npc,
                    id => new VoiceViewerSession(null, UUID.Random(), id),
                    (id, room) => { }, id => { }, null, m_disclosure);
                Assert.That(m_alerts, Has.Count.EqualTo(1));
                Assert.That(m_alerts[0], Does.Contain("attached"));

                VoiceConnectorRegistrar.Unregister(r, id => { }, null, m_disclosure);
                Assert.That(m_alerts, Has.Count.EqualTo(2));
                Assert.That(m_alerts[1], Does.Contain("detached"));

                // A second (idempotent) teardown of the now-inactive record stays silent.
                VoiceConnectorRegistrar.Unregister(r, id => { }, null, m_disclosure);
                Assert.That(m_alerts, Has.Count.EqualTo(2));
            }
            finally
            {
                if (r.ViewerSessionId != null)
                    VoiceViewerSession.RemoveViewerSession(r.ViewerSessionId);
            }
        }

        [Test]
        public void Registrar_NpcFailure_NoAttachAlert()
        {
            VoiceConnectorRecord r = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC",
                new Vector3(1, 2, 3), VoiceConnectorScope.Estate, false, "Operator", null);
            VoiceConnectorRegistrar.Register(r, 1, _ => UUID.Zero,
                id => new VoiceViewerSession(null, UUID.Random(), id),
                (id, room) => { }, id => { }, null, m_disclosure);
            Assert.That(m_alerts, Is.Empty, "an inactive record is never announced as attached");
        }

        // ---- Entry notice

        [Test]
        public void EntryNotice_OncePerLoginSession()
        {
            UUID agent = UUID.Random();
            UUID session = UUID.Random();
            List<VoiceConnectorRecord> attached = new List<VoiceConnectorRecord> { Recording(), Voiced() };

            m_disclosure.OnMakeRoot(agent, session, attached);
            Assert.That(m_notices, Has.Count.EqualTo(1));
            Assert.That(m_notices[0].Agent, Is.EqualTo(agent));
            Assert.That(m_notices[0].Message, Does.Contain("Recorder NPC (recording)"));
            Assert.That(m_notices[0].Message, Does.Contain("Speaker NPC (voiced)"));

            // Same login session again (teleport out and back, another root transition): silent.
            m_disclosure.OnMakeRoot(agent, session, attached);
            Assert.That(m_notices, Has.Count.EqualTo(1), "not twice for one login session");

            // A relog is a new SessionId: re-armed.
            m_disclosure.OnMakeRoot(agent, UUID.Random(), attached);
            Assert.That(m_notices, Has.Count.EqualTo(2));
        }

        [Test]
        public void EntryNotice_NothingWhenNoConnectorAttached()
        {
            UUID session = UUID.Random();
            m_disclosure.OnMakeRoot(UUID.Random(), session, new List<VoiceConnectorRecord>());
            Assert.That(m_notices, Is.Empty);
            // And the quiet pass did NOT consume the one notice owed to this login session: an attach later
            // still yields the entry notice on the next root transition.
            m_disclosure.OnMakeRoot(UUID.Random(), session, new List<VoiceConnectorRecord> { Recording() });
            Assert.That(m_notices, Has.Count.EqualTo(1));
        }

        // ---- Proximity notice

        [Test]
        public void ProximityNotice_FirstApproachOnly_PerAgentPerNpc()
        {
            VoiceConnectorRecord speaker = Voiced();
            UUID agentA = UUID.Random(); UUID sessionA = UUID.Random();
            UUID agentB = UUID.Random(); UUID sessionB = UUID.Random();
            Vector3 near = speaker.Position + new Vector3(5, 0, 0);
            Vector3 far = speaker.Position + new Vector3(Range + 10, 0, 0);
            List<VoiceConnectorRecord> voiced = new List<VoiceConnectorRecord> { speaker };

            // Out of range: nothing.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)> { (agentA, sessionA, far) }, voiced);
            Assert.That(m_chats, Is.Empty);

            // First approach: one chat line, from the NPC, with the proximity-notice text.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)> { (agentA, sessionA, near) }, voiced);
            Assert.That(m_chats, Has.Count.EqualTo(1));
            Assert.That(m_chats[0].NpcId, Is.EqualTo(speaker.NpcId));
            Assert.That(m_chats[0].Agent, Is.EqualTo(agentA));
            Assert.That(m_chats[0].Message, Is.EqualTo(
                "Speaker NPC is an NPC — its voice is automated or remotely operated."));

            // Still in range next tick: not again for the same agent/NPC.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)> { (agentA, sessionA, near) }, voiced);
            Assert.That(m_chats, Has.Count.EqualTo(1));

            // A different agent approaching: fires for them.
            m_disclosure.ProximityTick(new List<(UUID, UUID, Vector3)>
                { (agentA, sessionA, near), (agentB, sessionB, near) }, voiced);
            Assert.That(m_chats, Has.Count.EqualTo(2));
            Assert.That(m_chats[1].Agent, Is.EqualTo(agentB));
        }

        [Test]
        public void ProximityNotice_NeverForRecordingOnly()
        {
            VoiceConnectorRecord recorder = Recording();
            m_disclosure.ProximityTick(
                new List<(UUID, UUID, Vector3)> { (UUID.Random(), UUID.Random(), recorder.Position) },
                new List<VoiceConnectorRecord> { recorder });
            Assert.That(m_chats, Is.Empty, "MayInject=false has no voice, so no proximity notice");
        }
    }
}
