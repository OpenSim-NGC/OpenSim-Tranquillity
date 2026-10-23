/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Decides whether the parcel a child agent names in its voice provision is plausibly where the avatar is.
 *
 * A viewer near a region border holds a child agent in the neighbouring region. That region has a position for
 * the child (the root avatar's position expressed in this region's frame, so it can lie outside 0..256), but the
 * position is coarse: the root region sends child updates only after the avatar has moved about 10 m
 * (ScenePresence.CHILDUPDATES_MOVEMENT is the squared distance, 100) and at most every 2 s
 * (CHILDUPDATES_TIME), and the child's velocity is discarded. No timestamp is kept, so age cannot be measured;
 * the tolerance below is the most the position can be out of date by under those two constants.
 *
 * The parcel's bounding box is used (LandData.AABBMin / AABBMax), so an irregular parcel is accepted a little
 * more freely than its true outline. Height is ignored.
 *
 * Pure and Scene-free so the decision unit-tests without a Scene.
 */

using System;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public static class ChildParcelProximity
    {
        /// <summary>Fixed allowance for measurement and transit, in metres.</summary>
        public const float MarginMetres = 5f;
        /// <summary>The root region resends a child's position only after about this much movement
        /// (sqrt of ScenePresence.CHILDUPDATES_MOVEMENT), so the stored position can be this far behind
        /// even if the avatar then stood still.</summary>
        public const float UnsentMovementMetres = 10f;
        /// <summary>Minimum gap between child updates (ScenePresence.CHILDUPDATES_TIME), in seconds.</summary>
        public const float UpdateIntervalSeconds = 2f;
        /// <summary>Speed assumed for travel during the update interval, in metres per second (a fast run).</summary>
        public const float SpeedBoundMetresPerSecond = 8f;

        /// <summary>How far from the child's stored position a hinted parcel may lie and still be accepted.</summary>
        public const float ToleranceMetres =
            MarginMetres + UnsentMovementMetres + UpdateIntervalSeconds * SpeedBoundMetresPerSecond;

        /// <summary>False for a position that was never set: not finite, or exactly the origin (a child agent
        /// that has not yet had an update from its root region).</summary>
        public static bool HasKnownPosition(Vector3 position)
            => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
               && !(position.X == 0f && position.Y == 0f && position.Z == 0f);

        /// <summary>Horizontal distance from the point to the nearest edge of the box; 0 when inside.</summary>
        public static float DistanceToBox(Vector3 position, Vector3 boxMin, Vector3 boxMax)
        {
            float dx = Math.Max(Math.Max(boxMin.X - position.X, 0f), position.X - boxMax.X);
            float dy = Math.Max(Math.Max(boxMin.Y - position.Y, 0f), position.Y - boxMax.Y);
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>True when the child agent has a known position and the parcel's box is within
        /// <see cref="ToleranceMetres"/> of it.</summary>
        public static bool IsNear(Vector3 childPosition, Vector3 parcelMin, Vector3 parcelMax)
            => HasKnownPosition(childPosition)
               && DistanceToBox(childPosition, parcelMin, parcelMax) <= ToleranceMetres;
    }
}
