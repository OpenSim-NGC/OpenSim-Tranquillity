/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The start-up line about [WebRtcVoice] CarrierSecret has to say what really happens. With the in-process store a
 * carrier needs the secret, so without one none is sent. With the grid session service the receiving region asks the
 * service whether a ring is genuine and the service keeps the conference text key, so rings and text cross processes
 * without a secret. Pure text, no process-wide state: the fixture runs in parallel.
 */
using Xunit;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    public class CarrierStartupLineTests
    {
        [Fact(Timeout = 60000)]
        public void LocalStoreWithoutASecret_SaysNoCarrierIsSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (bool isError, string text) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret(string.Empty), gridStore: false);

            Assert.False(isError);
            Assert.StartsWith("cross-process voice carriers are off: [WebRtcVoice] CarrierSecret is not set. None is sent", text);
            Assert.Contains("at least 32 characters", text);
        }

        [Fact(Timeout = 60000)]
        public void GridStoreWithoutASecret_SaysRingsAndTextStillCrossAndNoSecretIsNeeded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (bool isError, string text) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret(string.Empty), gridStore: true);

            Assert.False(isError);
            Assert.Contains("not needed with SessionStoreMode grid", text);
            Assert.Contains("Rings, avatar-to-avatar calls and conference text cross processes", text);
            Assert.Contains("service verifies them", text);
            Assert.DoesNotContain("None is sent", text);
            Assert.DoesNotContain("are off", text);
        }

        [Fact(Timeout = 60000)]
        public void ATooShortSecretIsAnErrorInBothModes_AndGridDoesNotClaimCarriersAreOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (bool localError, string local) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret("short"), gridStore: false);
            (bool gridError, string grid) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret("short"), gridStore: true);

            Assert.True(localError);
            Assert.EndsWith("cross-process voice carriers are off", local);
            Assert.True(gridError);
            Assert.Contains("shorter than 32 characters and is refused", grid);
            Assert.DoesNotContain("carriers are off", grid);
        }

        [Fact(Timeout = 60000)]
        public void AUsableSecret_SaysCarriersAreOn_InBothModes_AndNeverPrintsTheSecret()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const string secret = "example-carrier-secret-0123456789-abcdef";
            foreach (bool grid in new[] { false, true })
            {
                (bool isError, string text) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret(secret), grid);

                Assert.False(isError);
                Assert.StartsWith("cross-process voice carriers on", text);
                Assert.DoesNotContain(secret, text);
            }
        }
    }
}
