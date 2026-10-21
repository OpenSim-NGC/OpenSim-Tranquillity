/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * ConnectorIdentity - the connector NPC's derived agent id (RFC 4122 v5 under the voice-connector namespace).
 * Expected values were computed independently with Python's uuid.uuid5.
 */

using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ConnectorIdentityTests
    {
        private const string Grid = "http://grid.example.org:8002";

        private static readonly UUID NamespaceDns = new UUID("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        private static readonly UUID NamespaceUrl = new UUID("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

        [Test]
        public void NameBasedV5_MatchesThePublishedVector()
        {
            // Python: uuid.uuid5(uuid.NAMESPACE_DNS, "www.example.com")
            Assert.That(ConnectorIdentity.NameBasedV5(NamespaceDns, "www.example.com"),
                Is.EqualTo(new UUID("2ed6657d-e927-568b-95e1-2665a8aea6a2")));
        }

        [Test]
        public void ConnectorNamespace_IsTheV5OfItsDocumentedName()
        {
            Assert.That(ConnectorIdentity.NameBasedV5(NamespaceUrl, ConnectorIdentity.ConnectorNamespaceName),
                Is.EqualTo(ConnectorIdentity.ConnectorNamespace));
            Assert.That(ConnectorIdentity.ConnectorNamespace, Is.EqualTo(new UUID("ca7b653b-b665-5e77-9260-6674637f6134")));
        }

        [Test]
        public void SameInputs_SameId_AcrossCalls_AndMatchesTheIndependentValue()
        {
            UUID first = ConnectorIdentity.DeriveAgentId(Grid, "Example Region", "Recorder");
            UUID second = ConnectorIdentity.DeriveAgentId(Grid, "Example Region", "Recorder");

            Assert.That(second, Is.EqualTo(first), "a restart derives the same id");
            Assert.That(first, Is.EqualTo(new UUID("fbe7d778-2ba6-5d64-a859-e10181a25c60")), "uuid5(connector_ns, \"<grid>/Example Region/Recorder\")");
            Assert.That(ConnectorIdentity.DeriveAgentId(string.Empty, "Example Region", "Recorder"),
                Is.EqualTo(new UUID("561d41d6-7747-5e70-8ab1-3dda83a2760b")), "a grid without GatekeeperURI: \"/Example Region/Recorder\"");
            Assert.That(ConnectorIdentity.IdentityName(Grid, "Example Region", "Recorder"), Is.EqualTo(Grid + "/Example Region/Recorder"));
        }

        [TestCase("http://othergrid.example:8002", "Example Region", "Recorder")]
        [TestCase(Grid, "Other Region", "Recorder")]
        [TestCase(Grid, "Example Region", "Injector")]
        [TestCase(Grid, "Example Region", "recorder")]   // case-sensitive
        [TestCase(Grid, "example region", "Recorder")]
        public void AnyOneInputDiffering_GivesADifferentId(string pGrid, string pRegion, string pRecord)
        {
            UUID baseline = ConnectorIdentity.DeriveAgentId(Grid, "Example Region", "Recorder");
            Assert.That(ConnectorIdentity.DeriveAgentId(pGrid, pRegion, pRecord), Is.Not.EqualTo(baseline));
        }

        [TestCase(Grid, "Example Region", "Recorder")]
        [TestCase("", "Elm", "Injector")]
        [TestCase(Grid, "Other Region", "Speaker")]
        public void DerivedIds_AreVersion5_WithTheRfc4122Variant(string pGrid, string pRegion, string pRecord)
        {
            string s = ConnectorIdentity.DeriveAgentId(pGrid, pRegion, pRecord).ToString();
            Assert.That(s[14], Is.EqualTo('5'), "version nibble");
            Assert.That("89ab".IndexOf(s[19]), Is.GreaterThanOrEqualTo(0), "variant bits 10xx");
        }
    }
}
