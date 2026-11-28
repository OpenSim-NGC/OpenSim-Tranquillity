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
    public class ProvisionViewerSessionGuardTests
    {
        // Initial first-provision case: no viewer_session at all -> route to CREATE.
        [Fact(Timeout = 60000)]
        public void MissingViewerSession_IsNotReal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var req = new OSDMap { { "channel_type", OSD.FromString("local") } };
            Assert.False(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _));
        }

        // The exact shape that caused the field failure: present OSDUUID(UUID.Zero).
        [Fact(Timeout = 60000)]
        public void ZeroUuidViewerSession_present_IsNotReal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var req = new OSDMap { { "viewer_session", OSD.FromUUID(UUID.Zero) } };

            // precondition: OSDMap reports the zero UUID as PRESENT (this is why the
            // original `if (TryGetString(...))` wrongly took the lookup branch).
            Assert.True(req.TryGetString("viewer_session", out string raw));
            Assert.Equal("00000000-0000-0000-0000-000000000000", raw);

            Assert.False(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _));
        }

        // Same zero id but delivered as an OSDString rather than OSDUUID.
        [Fact(Timeout = 60000)]
        public void ZeroUuidViewerSession_asString_IsNotReal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var req = new OSDMap { { "viewer_session", OSD.FromString(UUID.Zero.ToString()) } };
            Assert.False(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _));
        }

        [Fact(Timeout = 60000)]
        public void EmptyViewerSession_IsNotReal()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var req = new OSDMap { { "viewer_session", OSD.FromString(string.Empty) } };
            Assert.False(WebRtcVoiceServiceModule.HasRealViewerSession(req, out _));
        }

        // A real, registered-shaped session id must still route to LOOKUP.
        [Fact(Timeout = 60000)]
        public void RealUuidViewerSession_IsReal_AndPreservesId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var id = UUID.Random();
            var req = new OSDMap { { "viewer_session", OSD.FromUUID(id) } };

            Assert.True(WebRtcVoiceServiceModule.HasRealViewerSession(req, out string got));
            Assert.Equal(id.ToString(), got);
        }
    }
}
