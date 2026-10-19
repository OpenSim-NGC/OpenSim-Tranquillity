/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Regression tests for the ProvisionVoiceAccountRequest viewer-session guard
 * (WebRtcVoiceServiceModule.HasRealViewerSession).
 *
 * Defect: the handler routed to the Janus session-LOOKUP branch whenever a "viewer_session"
 * field was present. A viewer's first provision has no session yet and the field
 * arrives as the zero UUID; OSDMap.TryGetString returns true with
 * "00000000-0000-0000-0000-000000000000" for a present OSDUUID(UUID.Zero) (verified),
 * so the initial request never reached the CREATE branch -> the handler logged
 * "viewer session 00000000-... not found" and returned null ("[ProvisionVoice]: got
 * null response").
 *
 * NOTE (provenance): this is NOT the OSDToLong/libOMV missing-key family -- the whole
 * OSDMap TryGet* family correctly returns false for an ABSENT key. The defect is
 * treating a present zero/empty value as a real session. HasRealViewerSession treats
 * absent/empty/zero as "no session" so an initial request routes to CREATE.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionViewerSessionGuardTests
    {
        // Initial first-provision case: no viewer_session at all -> route to CREATE.
        [Test]
        public void MissingViewerSession_IsNotReal()
        {
            var req = new OSDMap { { "channel_type", OSD.FromString("local") } };
            Assert.That(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _), Is.False);
        }

        // The exact shape that caused the field failure: present OSDUUID(UUID.Zero).
        [Test]
        public void ZeroUuidViewerSession_present_IsNotReal()
        {
            var req = new OSDMap { { "viewer_session", OSD.FromUUID(UUID.Zero) } };

            // precondition: OSDMap reports the zero UUID as PRESENT (this is why the
            // original `if (TryGetString(...))` wrongly took the lookup branch).
            Assert.That(req.TryGetString("viewer_session", out string raw), Is.True);
            Assert.That(raw, Is.EqualTo("00000000-0000-0000-0000-000000000000"));

            Assert.That(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _), Is.False);
        }

        // Same zero id but delivered as an OSDString rather than OSDUUID.
        [Test]
        public void ZeroUuidViewerSession_asString_IsNotReal()
        {
            var req = new OSDMap { { "viewer_session", OSD.FromString(UUID.Zero.ToString()) } };
            Assert.That(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _), Is.False);
        }

        [Test]
        public void EmptyViewerSession_IsNotReal()
        {
            var req = new OSDMap { { "viewer_session", OSD.FromString(string.Empty) } };
            Assert.That(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _), Is.False);
        }

        // A real, registered-shaped session id must still route to LOOKUP.
        [Test]
        public void RealUuidViewerSession_IsReal_AndPreservesId()
        {
            var id = UUID.Random();
            var req = new OSDMap { { "viewer_session", OSD.FromUUID(id) } };

            Assert.That(WebRtcVoiceServiceModule.HasRealViewerSession(req, out string got), Is.True);
            Assert.That(got, Is.EqualTo(id.ToString()));
        }
    }
}
