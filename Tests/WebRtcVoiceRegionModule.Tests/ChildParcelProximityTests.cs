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
    public class ChildParcelProximityTests
    {
        // A parcel along the west edge of the region, 64 m square.
        private static readonly Vector3 WestMin = new Vector3(0f, 64f, 0f);
        private static readonly Vector3 WestMax = new Vector3(64f, 128f, 0f);
        // A parcel in the far east of the region.
        private static readonly Vector3 EastMin = new Vector3(192f, 0f, 0f);
        private static readonly Vector3 EastMax = new Vector3(256f, 64f, 0f);

        [Fact(Timeout = 60000)]
        public void NearbyParcel_IsAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Viewer 5 m west of this region, level with the west parcel.
            Assert.True(ChildParcelProximity.IsNear(new Vector3(-5f, 100f, 25f), WestMin, WestMax));
        }

        [Fact(Timeout = 60000)]
        public void PositionInsideParcel_IsAccepted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(ChildParcelProximity.IsNear(new Vector3(10f, 100f, 25f), WestMin, WestMax));
        }

        [Fact(Timeout = 60000)]
        public void FarParcel_IsRejected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The same viewer naming a parcel on the far side of the region.
            Assert.False(ChildParcelProximity.IsNear(new Vector3(-5f, 100f, 25f), EastMin, EastMax));
        }

        [Fact(Timeout = 60000)]
        public void ParcelJustInsideTolerance_IsAccepted_JustOutsideIsRejected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            float tol = ChildParcelProximity.ToleranceMetres;
            Assert.True(ChildParcelProximity.IsNear(new Vector3(-tol, 100f, 25f), WestMin, WestMax));
            Assert.False(ChildParcelProximity.IsNear(new Vector3(-(tol + 1f), 100f, 25f), WestMin, WestMax));
        }

        [Fact(Timeout = 60000)]
        public void Tolerance_CoversTheStalenessCoreAllows()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // 10 m unsent movement + 2 s at 8 m/s + 5 m margin.
            Assert.Equal(31f, ChildParcelProximity.ToleranceMetres);
        }

        [Fact(Timeout = 60000)]
        public void DiagonalDistance_UsesBothAxes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // 25 m west and 25 m south of the west parcel's corner: about 35 m, outside 31 m.
            Assert.False(ChildParcelProximity.IsNear(new Vector3(-25f, 39f, 25f), WestMin, WestMax));
        }

        [Fact(Timeout = 60000)]
        public void ChildWithNoKnownPosition_IsRejected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A child that has had no update from its root region sits at the origin, which is also inside the
            // west parcel's box, so the origin must count as unknown rather than as a position.
            Assert.False(ChildParcelProximity.IsNear(Vector3.Zero, new Vector3(0f, 0f, 0f), new Vector3(64f, 64f, 0f)));
            Assert.False(ChildParcelProximity.HasKnownPosition(Vector3.Zero));
        }

        [Fact(Timeout = 60000)]
        public void ChildWithNonFinitePosition_IsRejected()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(ChildParcelProximity.IsNear(new Vector3(float.NaN, 100f, 25f), WestMin, WestMax));
            Assert.False(ChildParcelProximity.IsNear(new Vector3(-5f, float.PositiveInfinity, 25f), WestMin, WestMax));
        }

        [Fact(Timeout = 60000)]
        public void RejectHint_DropsAChildsHintedParcel_ToTheEstateRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ParcelResolution hinted = ProvisionParcelResolver.Resolve(new ParcelResolveInput(12, IsChildAgent: true), serverLocalId: null);
            Assert.Equal(ParcelSource.ClientHint, hinted.Source);

            ParcelResolution res = ProvisionParcelResolver.RejectHint(hinted);

            Assert.Equal(ParcelSource.None, res.Source);
            Assert.Null(res.LocalId);
            Assert.True(res.HintRejected);
        }

        [Fact(Timeout = 60000)]
        public void RejectHint_LeavesOtherResolutionsAlone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ParcelResolution root = ProvisionParcelResolver.Resolve(clientLocalId: 12, serverLocalId: 7);
            ParcelResolution none = ProvisionParcelResolver.Resolve(new ParcelResolveInput(null, IsChildAgent: true), serverLocalId: null);

            Assert.Equal(root, ProvisionParcelResolver.RejectHint(root));
            Assert.Equal(none, ProvisionParcelResolver.RejectHint(none));
        }
    }
}
