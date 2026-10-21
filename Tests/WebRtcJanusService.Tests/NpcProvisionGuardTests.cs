/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the AllowNpcVoice guard.
 *
 * An NPC presence must not provision voice unless the operator opened the surface
 * ([WebRtcVoice] AllowNpcVoice=true) or the agent id is a registered voice-connector identity —
 * NPC voice exists only through a policy record. The decision is the pure, side-effect-free
 * WebRtcVoiceServiceModule.IsNpcProvisionRefused so it is unit-testable here (the full provision
 * path needs a live Scene and ScenePresence — the same reason IsProvisionableChannelType and
 * HasRealViewerSession are tested via their extracted predicates). The call site resolves
 * IVoiceConnectorRegistry per scene; a null registry contributes isConnectorIdentity=false,
 * identical to an empty one — covered by the false cases below.
 */
using Nini.Config;
using NUnit.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NpcProvisionGuardTests
    {
        [Test]
        public void Npc_Disallowed_NotConnector_IsRefused()
        {
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: false, pIsConnectorIdentity: false), Is.True);
        }

        [Test]
        public void Npc_AllowNpcVoiceTrue_IsAdmitted()
        {
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: true, pIsConnectorIdentity: false), Is.False);
        }

        [Test]
        public void Npc_ConnectorIdentity_IsAdmitted()
        {
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: false, pIsConnectorIdentity: true), Is.False);
        }

        // The stock server creates no capabilities for an NPC (Scene.AddNewAgent, no CreateCaps), so an NPC cannot
        // provision voice there. The default of false keeps that; only an explicit AllowNpcVoice = true opens it.
        [Test]
        public void AllowNpcVoice_DefaultsToFalse_WhenTheKeyIsMissing()
        {
            Assert.That(WebRtcVoiceServiceModule.DefaultAllowNpcVoice, Is.False);
            IConfig empty = new IniConfigSource().AddConfig("WebRtcVoice");
            Assert.That(WebRtcVoiceServiceModule.ReadAllowNpcVoice(empty), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: WebRtcVoiceServiceModule.ReadAllowNpcVoice(empty),
                pIsConnectorIdentity: false), Is.True);

            empty.Set("AllowNpcVoice", "true");
            Assert.That(WebRtcVoiceServiceModule.ReadAllowNpcVoice(empty), Is.True);
        }

        [Test]
        public void Avatar_IsNeverRefused()
        {
            // A real-avatar presence is unaffected by every knob combination.
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, false, false), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, true, false), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, false, true), Is.False);
            Assert.That(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, true, true), Is.False);
        }
    }
}
