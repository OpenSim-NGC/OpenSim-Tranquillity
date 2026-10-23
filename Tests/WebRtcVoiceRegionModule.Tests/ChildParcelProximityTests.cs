/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for ChildParcelProximity and ProvisionParcelResolver.RejectHint: a child agent's parcel_local_id is
 * accepted only when the parcel lies near the position this region holds for the child; otherwise the request
 * falls back to the estate room and is never refused.
 *
 * The child positions are in the receiving region's own frame, so a viewer standing just west of the region
 * has a negative X.
 */

using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ChildParcelProximityTests
    {
        // A parcel along the west edge of the region, 64 m square.
        private static readonly Vector3 WestMin = new Vector3(0f, 64f, 0f);
        private static readonly Vector3 WestMax = new Vector3(64f, 128f, 0f);
        // A parcel in the far east of the region.
        private static readonly Vector3 EastMin = new Vector3(192f, 0f, 0f);
        private static readonly Vector3 EastMax = new Vector3(256f, 64f, 0f);

        [Test]
        public void NearbyParcel_IsAccepted()
        {
            // Viewer 5 m west of this region, level with the west parcel.
            Assert.That(ChildParcelProximity.IsNear(new Vector3(-5f, 100f, 25f), WestMin, WestMax), Is.True);
        }

        [Test]
        public void PositionInsideParcel_IsAccepted()
        {
            Assert.That(ChildParcelProximity.IsNear(new Vector3(10f, 100f, 25f), WestMin, WestMax), Is.True);
        }

        [Test]
        public void FarParcel_IsRejected()
        {
            // The same viewer naming a parcel on the far side of the region.
            Assert.That(ChildParcelProximity.IsNear(new Vector3(-5f, 100f, 25f), EastMin, EastMax), Is.False);
        }

        [Test]
        public void ParcelJustInsideTolerance_IsAccepted_JustOutsideIsRejected()
        {
            float tol = ChildParcelProximity.ToleranceMetres;
            Assert.That(ChildParcelProximity.IsNear(new Vector3(-tol, 100f, 25f), WestMin, WestMax), Is.True);
            Assert.That(ChildParcelProximity.IsNear(new Vector3(-(tol + 1f), 100f, 25f), WestMin, WestMax), Is.False);
        }

        [Test]
        public void Tolerance_CoversTheStalenessCoreAllows()
        {
            // 10 m unsent movement + 2 s at 8 m/s + 5 m margin.
            Assert.That(ChildParcelProximity.ToleranceMetres, Is.EqualTo(31f));
        }

        [Test]
        public void DiagonalDistance_UsesBothAxes()
        {
            // 25 m west and 25 m south of the west parcel's corner: about 35 m, outside 31 m.
            Assert.That(ChildParcelProximity.IsNear(new Vector3(-25f, 39f, 25f), WestMin, WestMax), Is.False);
        }

        [Test]
        public void ChildWithNoKnownPosition_IsRejected()
        {
            // A child that has had no update from its root region sits at the origin, which is also inside the
            // west parcel's box, so the origin must count as unknown rather than as a position.
            Assert.That(ChildParcelProximity.IsNear(Vector3.Zero, new Vector3(0f, 0f, 0f), new Vector3(64f, 64f, 0f)), Is.False);
            Assert.That(ChildParcelProximity.HasKnownPosition(Vector3.Zero), Is.False);
        }

        [Test]
        public void ChildWithNonFinitePosition_IsRejected()
        {
            Assert.That(ChildParcelProximity.IsNear(new Vector3(float.NaN, 100f, 25f), WestMin, WestMax), Is.False);
            Assert.That(ChildParcelProximity.IsNear(new Vector3(-5f, float.PositiveInfinity, 25f), WestMin, WestMax), Is.False);
        }

        [Test]
        public void RejectHint_DropsAChildsHintedParcel_ToTheEstateRoom()
        {
            ParcelResolution hinted = ProvisionParcelResolver.Resolve(new ParcelResolveInput(12, IsChildAgent: true), serverLocalId: null);
            Assert.That(hinted.Source, Is.EqualTo(ParcelSource.ClientHint));

            ParcelResolution res = ProvisionParcelResolver.RejectHint(hinted);

            Assert.That(res.Source, Is.EqualTo(ParcelSource.None));
            Assert.That(res.LocalId, Is.Null);
            Assert.That(res.HintRejected, Is.True);
        }

        [Test]
        public void RejectHint_LeavesOtherResolutionsAlone()
        {
            ParcelResolution root = ProvisionParcelResolver.Resolve(clientLocalId: 12, serverLocalId: 7);
            ParcelResolution none = ProvisionParcelResolver.Resolve(new ParcelResolveInput(null, IsChildAgent: true), serverLocalId: null);

            Assert.That(ProvisionParcelResolver.RejectHint(root), Is.EqualTo(root));
            Assert.That(ProvisionParcelResolver.RejectHint(none), Is.EqualTo(none));
        }
    }
}
