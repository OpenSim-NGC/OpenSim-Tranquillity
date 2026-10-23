/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for ProvisionParcelResolver. For a ROOT agent the
 * server-derived parcel always wins and the viewer's parcel_local_id only decides what gets logged.
 * A CHILD agent's position is not in this region, so it never derives from position: its client
 * hint selects the parcel, or with no hint there is no parcel (estate room).
 */

using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ProvisionParcelResolverTests
    {
        [Test]
        public void Honest_SameId_NoMismatch()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(clientLocalId: 7, serverLocalId: 7);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ServerPosition));
            Assert.That(res.LocalId, Is.EqualTo(7));
            Assert.That(res.ServerLocalId, Is.EqualTo(7));
            Assert.That(res.ClientMismatch, Is.False);
            Assert.That(res.ClientAbsent, Is.False);
        }

        [Test]
        public void Spoof_DifferentId_MismatchAndServerIdWins()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(clientLocalId: 12, serverLocalId: 7);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ServerPosition));
            Assert.That(res.LocalId, Is.EqualTo(7));
            Assert.That(res.ServerLocalId, Is.EqualTo(7));
            Assert.That(res.ClientMismatch, Is.True);
            Assert.That(res.ClientAbsent, Is.False);
        }

        [Test]
        public void Absent_NullId_ClientAbsentAndServerIdWins()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(clientLocalId: null, serverLocalId: 7);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ServerPosition));
            Assert.That(res.LocalId, Is.EqualTo(7));
            Assert.That(res.ServerLocalId, Is.EqualTo(7));
            Assert.That(res.ClientMismatch, Is.False);
            Assert.That(res.ClientAbsent, Is.True);
        }

        [TestCase(7)]
        [TestCase(12)]
        [TestCase(null)]
        public void Root_AlwaysServerPosition_RegardlessOfHint(int? hint)
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(new ParcelResolveInput(ClientLocalId: hint, IsChildAgent: false), serverLocalId: 7);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ServerPosition));
            Assert.That(res.LocalId, Is.EqualTo(7));
        }

        [Test]
        public void Root_NoParcelAtPosition_ServerPositionWithNoId()
        {
            // The genuine failure: the handler refuses (WARN) on a ServerPosition result with no id.
            ParcelResolution res = ProvisionParcelResolver.Resolve(new ParcelResolveInput(ClientLocalId: 7, IsChildAgent: false), serverLocalId: null);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ServerPosition));
            Assert.That(res.LocalId, Is.Null);
            Assert.That(res.ClientMismatch, Is.False);
        }

        [Test]
        public void Child_WithHint_ClientHintWithHintId()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(new ParcelResolveInput(ClientLocalId: 12, IsChildAgent: true), serverLocalId: null);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ClientHint));
            Assert.That(res.LocalId, Is.EqualTo(12));
            Assert.That(res.ClientMismatch, Is.False);
            Assert.That(res.ClientAbsent, Is.False);
        }

        [Test]
        public void Child_WithHint_IgnoresAnyServerId()
        {
            // A child's position is outside this region; even if a caller passed a position-derived id it must not win.
            ParcelResolution res = ProvisionParcelResolver.Resolve(new ParcelResolveInput(ClientLocalId: 12, IsChildAgent: true), serverLocalId: 7);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.ClientHint));
            Assert.That(res.LocalId, Is.EqualTo(12));
        }

        [Test]
        public void Child_NoHint_NoneEstateRoom()
        {
            ParcelResolution res = ProvisionParcelResolver.Resolve(new ParcelResolveInput(ClientLocalId: null, IsChildAgent: true), serverLocalId: null);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.None));
            Assert.That(res.LocalId, Is.Null);
            Assert.That(res.ClientAbsent, Is.True);
        }

        [Test]
        public void RequiresPosition_RootOnly()
        {
            Assert.That(ProvisionParcelResolver.DerivesFromPosition(new ParcelResolveInput(null, IsChildAgent: false)), Is.True);
            Assert.That(ProvisionParcelResolver.DerivesFromPosition(new ParcelResolveInput(12, IsChildAgent: true)), Is.False);
            Assert.That(ProvisionParcelResolver.DerivesFromPosition(new ParcelResolveInput(null, IsChildAgent: true)), Is.False);
        }
    }
}
