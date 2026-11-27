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
using Xunit;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class NpcProvisionGuardTests
    {
        [Fact(Timeout = 60000)]
        public void Npc_Disallowed_NotConnector_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: false, pIsConnectorIdentity: false));
        }

        [Fact(Timeout = 60000)]
        public void Npc_AllowNpcVoiceTrue_IsAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: true, pIsConnectorIdentity: false));
        }

        [Fact(Timeout = 60000)]
        public void Npc_ConnectorIdentity_IsAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: false, pIsConnectorIdentity: true));
        }

        // The stock server creates no capabilities for an NPC (Scene.AddNewAgent, no CreateCaps), so an NPC cannot
        // provision voice there. The default of false keeps that; only an explicit AllowNpcVoice = true opens it.
        [Fact(Timeout = 60000)]
        public void AllowNpcVoice_DefaultsToFalse_WhenTheKeyIsMissing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(WebRtcVoiceServiceModule.DefaultAllowNpcVoice);
            IConfig empty = new IniConfigSource().AddConfig("WebRtcVoice");
            Assert.False(WebRtcVoiceServiceModule.ReadAllowNpcVoice(empty));
            Assert.True(WebRtcVoiceServiceModule.IsNpcProvisionRefused(
                pIsNpcPresence: true, pAllowNpcVoice: WebRtcVoiceServiceModule.ReadAllowNpcVoice(empty),
                pIsConnectorIdentity: false));

            empty.Set("AllowNpcVoice", "true");
            Assert.True(WebRtcVoiceServiceModule.ReadAllowNpcVoice(empty));
        }

        [Fact(Timeout = 60000)]
        public void Avatar_IsNeverRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A real-avatar presence is unaffected by every knob combination.
            Assert.False(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, false, false));
            Assert.False(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, true, false));
            Assert.False(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, false, true));
            Assert.False(WebRtcVoiceServiceModule.IsNpcProvisionRefused(false, true, true));
        }
    }
}
