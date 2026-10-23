/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the sim tells the mixer about how far a participant's claimed position may be from the sim's.
 *
 * Two numbers go with every point. The speed allowance is how fast the participant can really be moving:
 * the sim's own velocity for the avatar, or for what it is sitting on, with headroom for acceleration,
 * never below a floor and never above what the physics engine will allow any body to do. The tolerance is
 * the margin the mixer should accept at the moment of the sample.
 *
 * A root agent's position is measured by this region every frame. A child agent in a neighbouring region
 * is different: its position is whatever the root region last sent across, and core only sends one after
 * the avatar has moved about 10 m and at least 2 s since the previous one (ScenePresence.CHILDUPDATES_MOVEMENT,
 * a squared distance of 100, and CHILDUPDATES_TIME, 2000 ms), and the velocity that comes with it is discarded
 * on arrival. So a child's tolerance is widened by exactly what core can leave unreported.
 */

using System;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// The speed allowance and tolerance for one participant, in the wire's integer units.
    public readonly struct PositionAllowance
    {
        public readonly int SpeedCmS;
        public readonly int ToleranceCm;

        public PositionAllowance(int speedCmS, int toleranceCm)
        {
            SpeedCmS = speedCmS;
            ToleranceCm = toleranceCm;
        }
    }

    public static class PositionTolerance
    {
        /// Distance a child agent can move before core reports it again: the square root of
        /// ScenePresence.CHILDUPDATES_MOVEMENT (100), in centimetres.
        public const int ChildMoveSlackCm = 1000;

        /// Shortest time between two reports of a child agent: ScenePresence.CHILDUPDATES_TIME.
        public const int ChildUpdateIntervalMs = 2000;

        /// The allowance is this multiple of the measured speed, because an avatar can accelerate between
        /// the sample and the check.
        public const double SpeedHeadroom = 2.0;

        /// The sim's speed for a participant, in cm/s: the avatar's own velocity or, when it is seated, the
        /// velocity of what it sits on, whichever is greater.
        public static double SpeedCmS(Vector3 avatarVelocity, Vector3? seatedOnVelocity)
        {
            double own = Length(avatarVelocity);
            double seat = seatedOnVelocity.HasValue ? Length(seatedOnVelocity.Value) : 0.0;
            return Math.Max(own, seat) * 100.0;
        }

        /// <param name="isChild">A child agent in this region, not a root agent.</param>
        /// <param name="speedCmS">Measured speed in cm/s (for a child, the speed estimated from its reports).</param>
        /// <param name="engineMaxCmS">The physics engine's maximum speed in cm/s, or zero when it declares none.</param>
        public static PositionAllowance For(bool isChild, double speedCmS, double engineMaxCmS, PositionSettings settings)
        {
            double ceiling = engineMaxCmS > 0.0 ? engineMaxCmS : settings.FallbackSpeedMaxCmS;
            double speed = double.IsNaN(speedCmS) || speedCmS < 0.0 ? 0.0 : speedCmS;
            double allowance = Math.Min(Math.Max(speed * SpeedHeadroom, settings.SpeedFloorCmS), ceiling);

            double tolerance = settings.MarginCm;
            if (isChild)
                tolerance += ChildMoveSlackCm + allowance * (ChildUpdateIntervalMs / 1000.0);

            return new PositionAllowance(ToInt(allowance), ToInt(tolerance));
        }

        private static double Length(Vector3 v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            double len = Math.Sqrt(x * x + y * y + z * z);
            return double.IsNaN(len) || double.IsInfinity(len) ? 0.0 : len;
        }

        private static int ToInt(double value)
            => value >= int.MaxValue ? int.MaxValue : (int)Math.Round(value);
    }
}
