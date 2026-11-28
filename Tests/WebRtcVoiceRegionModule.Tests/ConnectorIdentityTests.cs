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
    public class ConnectorIdentityTests
    {
        private const string Grid = "http://grid.example.org:8002";

        private static readonly UUID NamespaceDns = new UUID("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        private static readonly UUID NamespaceUrl = new UUID("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

        [Fact(Timeout = 60000)]
        public void NameBasedV5_MatchesThePublishedVector()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Python: uuid.uuid5(uuid.NAMESPACE_DNS, "www.example.com")
            Assert.Equal(new UUID("2ed6657d-e927-568b-95e1-2665a8aea6a2"), ConnectorIdentity.NameBasedV5(NamespaceDns, "www.example.com"));
        }

        [Fact(Timeout = 60000)]
        public void ConnectorNamespace_IsTheV5OfItsDocumentedName()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ConnectorIdentity.ConnectorNamespace, ConnectorIdentity.NameBasedV5(NamespaceUrl, ConnectorIdentity.ConnectorNamespaceName));
            Assert.Equal(new UUID("ca7b653b-b665-5e77-9260-6674637f6134"), ConnectorIdentity.ConnectorNamespace);
        }

        [Fact(Timeout = 60000)]
        public void SameInputs_SameId_AcrossCalls_AndMatchesTheIndependentValue()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID first = ConnectorIdentity.DeriveAgentId(Grid, "Example Region", "Recorder");
            UUID second = ConnectorIdentity.DeriveAgentId(Grid, "Example Region", "Recorder");

            {
                var assertedValue = second;
                TestAssert.WithMessage(() => Assert.Equal(first, assertedValue), "a restart derives the same id");
            }
            {
                var assertedValue = first;
                TestAssert.WithMessage(() => Assert.Equal(new UUID("fbe7d778-2ba6-5d64-a859-e10181a25c60"), assertedValue), "uuid5(connector_ns, \"<grid>/Example Region/Recorder\")");
            }
            {
                var assertedValue = ConnectorIdentity.DeriveAgentId(string.Empty, "Example Region", "Recorder");
                TestAssert.WithMessage(() => Assert.Equal(new UUID("561d41d6-7747-5e70-8ab1-3dda83a2760b"), assertedValue), "a grid without GatekeeperURI: \"/Example Region/Recorder\"");
            }
            Assert.Equal(Grid + "/Example Region/Recorder", ConnectorIdentity.IdentityName(Grid, "Example Region", "Recorder"));
        }

        [InlineData("http://othergrid.example:8002","Example Region","Recorder")]
        [InlineData(Grid,"Other Region","Recorder")]
        [InlineData(Grid,"Example Region","Injector")]
        [InlineData(Grid,"Example Region","recorder")]   // case-sensitive
        [InlineData(Grid,"example region","Recorder")]
        [Theory(Timeout = 60000)]
        public void AnyOneInputDiffering_GivesADifferentId(string pGrid, string pRegion, string pRecord)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID baseline = ConnectorIdentity.DeriveAgentId(Grid, "Example Region", "Recorder");
            Assert.NotEqual(baseline, ConnectorIdentity.DeriveAgentId(pGrid, pRegion, pRecord));
        }

        [InlineData(Grid,"Example Region","Recorder")]
        [InlineData("","Elm","Injector")]
        [InlineData(Grid,"Other Region","Speaker")]
        [Theory(Timeout = 60000)]
        public void DerivedIds_AreVersion5_WithTheRfc4122Variant(string pGrid, string pRegion, string pRecord)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string s = ConnectorIdentity.DeriveAgentId(pGrid, pRegion, pRecord).ToString();
            {
                var assertedValue = s[14];
                TestAssert.WithMessage(() => Assert.Equal('5', assertedValue), "version nibble");
            }
            Assert.True("89ab".IndexOf(s[19]) >= 0, "variant bits 10xx");
        }
    }
}
