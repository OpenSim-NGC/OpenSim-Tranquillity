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
using NUnit.Framework;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CarrierStartupLineTests
    {
        [Test]
        public void LocalStoreWithoutASecret_SaysNoCarrierIsSent()
        {
            (bool isError, string text) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret(string.Empty), gridStore: false);

            Assert.That(isError, Is.False);
            Assert.That(text, Does.StartWith("cross-process voice carriers are off: [WebRtcVoice] CarrierSecret is not set. None is sent"));
            Assert.That(text, Does.Contain("at least 32 characters"));
        }

        [Test]
        public void GridStoreWithoutASecret_SaysRingsAndTextStillCrossAndNoSecretIsNeeded()
        {
            (bool isError, string text) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret(string.Empty), gridStore: true);

            Assert.That(isError, Is.False);
            Assert.That(text, Does.Contain("not needed with SessionStoreMode grid"));
            Assert.That(text, Does.Contain("Rings, avatar-to-avatar calls and conference text cross processes"));
            Assert.That(text, Does.Contain("service verifies them"));
            Assert.That(text, Does.Not.Contain("None is sent"));
            Assert.That(text, Does.Not.Contain("are off"));
        }

        [Test]
        public void ATooShortSecretIsAnErrorInBothModes_AndGridDoesNotClaimCarriersAreOff()
        {
            (bool localError, string local) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret("short"), gridStore: false);
            (bool gridError, string grid) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret("short"), gridStore: true);

            Assert.That(localError, Is.True);
            Assert.That(local, Does.EndWith("cross-process voice carriers are off"));
            Assert.That(gridError, Is.True);
            Assert.That(grid, Does.Contain("shorter than 32 characters and is refused"));
            Assert.That(grid, Does.Not.Contain("carriers are off"));
        }

        [Test]
        public void AUsableSecret_SaysCarriersAreOn_InBothModes_AndNeverPrintsTheSecret()
        {
            const string secret = "example-carrier-secret-0123456789-abcdef";
            foreach (bool grid in new[] { false, true })
            {
                (bool isError, string text) = CarrierAuth.StartupLine(CarrierAuth.CheckSecret(secret), grid);

                Assert.That(isError, Is.False);
                Assert.That(text, Does.StartWith("cross-process voice carriers on"));
                Assert.That(text, Does.Not.Contain(secret));
            }
        }
    }
}
