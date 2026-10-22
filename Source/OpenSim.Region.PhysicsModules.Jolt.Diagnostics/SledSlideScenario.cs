/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The sled's slide test (jolt sledtest slide): a documented sled (VEHICLE_TYPE_SLED, its presets, no motor) at rest on a
// 15 degree slope, let go there and not dropped onto it, so no landing can toss a coin: a drop from a metre up lands the
// sled on an edge, and a millimetre of start decides whether it slides or stops. The sled's speed is read at fixed times of
// physics time and compared with the exact solution of the controller's equation along the nose within 2%. This file is the layout and the pass line, which have no scene in them, so a test can run the
// same numbers against a scene without a region.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics
{
    internal static class SledSlideScenario
    {
        /// <summary>The slope, in degrees.</summary>
        public const float SlopeDegrees = 15f;

        /// <summary>The ramp that makes the slope where the region's ground has none: a box long enough for the whole test.</summary>
        public static readonly Vector3 RampSize = new Vector3(64f, 8f, 1f);

        /// <summary>The sled's box.</summary>
        public static readonly Vector3 SledSize = new Vector3(2f, 1f, 0.5f);

        /// <summary>The gap between the sled's underside and the ramp's top face when it is let go (metres).</summary>
        public const float Gap = 0.0005f;

        /// <summary>How far up the slope from the ramp's centre the sled starts (metres).</summary>
        public const float StartUphill = 20f;

        /// <summary>Physics seconds after the sled is let go at which its speed is read.</summary>
        public static readonly double[] Times = { 1.0, 2.0, 3.0, 4.0 };

        /// <summary>The documented sled's friction timescale along its nose (s).</summary>
        public const double NoseFrictionSeconds = 30.0;

        /// <summary>The prim damping the sled has on the module's shipped defaults, per second ([Jolt] PrimLinearDamping).</summary>
        public const double PrimLinearDamping = 0.02;

        /// <summary>
        /// The sled's speed (m/s) a time <paramref name="t"/> after it is let go at rest on the slope: the exact solution of the
        /// controller's equation along the nose with the prim damping, v' = a - v / T - c v, with a = g (sin slope + k sqrt(sin
        /// slope)) (gravity along the slope and the sled's slope assist k, 0.045), T the nose friction timescale and c the prim
        /// damping. With T' = 1 / (1 / T + c) it is a T' (1 - e^(-t / T')): 2.690 5.241 7.659 9.951 at <see cref="Times"/>.
        /// </summary>
        public static double Ideal(double t)
        {
            double sin = Math.Sin(SlopeDegrees * Math.PI / 180.0);
            double a = 9.80665 * (sin + 0.045 * Math.Sqrt(sin));
            double tau = 1.0 / (1.0 / NoseFrictionSeconds + PrimLinearDamping);
            return a * tau * (1.0 - Math.Exp(-t / tau));
        }

        /// <summary>
        /// The speeds the readings are compared with. They were the harness sled scenario's, which drops the sled from half a
        /// metre onto the slope and so carries about 0.05 m/s of the landing in every reading (2.741 5.291 7.709 10.001 with the
        /// prim damping); the sled let go at rest on the ramp follows the exact solution (<see cref="Ideal"/>) to 0.1% at 45 Hz
        /// with 2 collision steps, 64 Hz with 2 and 32 Hz with 4. The ramp the console test rezzes has friction 0: a sled's
        /// contact friction is 0, and two surfaces combine as their average, so a ramp of the prim default (wood, 0.6) would
        /// hold a sled that a 15 degree slope (tan 0.27) cannot move against 0.3.
        /// </summary>
        public static readonly double[] Speeds = { Ideal(Times[0]), Ideal(Times[1]), Ideal(Times[2]), Ideal(Times[3]) };

        /// <summary>The pass line: the largest error of a reading, as a percent of the figure it is read against.</summary>
        public const double TolerancePercent = 2.0;

        /// <summary>The ramp's and the sled's rotation: about the world's y axis, which turns the box's +x end down the slope.</summary>
        public static Quaternion Tilt()
            => Quaternion.CreateFromAxisAngle(Vector3.UnitY, SlopeDegrees * (float)(Math.PI / 180.0));

        /// <summary>
        /// Where the ramp and the sled go: the ramp centred at (<paramref name="x"/>, <paramref name="y"/>,
        /// <paramref name="rampZ"/>), the sled on its top face <see cref="StartUphill"/> metres up the slope from the centre,
        /// with the sled's underside <see cref="Gap"/> above the face and the same rotation, nose down the slope.
        /// </summary>
        public static (Vector3 Ramp, Vector3 Sled, Quaternion Rotation) Layout(float x, float y, float rampZ)
        {
            Quaternion q = Tilt();
            var ramp = new Vector3(x, y, rampZ);
            // In the ramp's own axes: up the slope is -x, and up from the face is z, half the ramp's thickness plus the
            // sled's half thickness plus the gap.
            var inRamp = new Vector3(-StartUphill, 0f, RampSize.Z * 0.5f + SledSize.Z * 0.5f + Gap);
            return (ramp, ramp + inRamp * q, q);
        }

        /// <summary>The lowest the ramp's far (downhill) end reaches below its centre, so a caller can keep it off the ground.</summary>
        public static float RampHalfDrop()
            => RampSize.X * 0.5f * (float)Math.Sin(SlopeDegrees * Math.PI / 180.0) + RampSize.Z * 0.5f;

        /// <summary>The speed at <paramref name="t"/> from samples of (physics time, speed), linearly between the two around it.</summary>
        public static double SpeedAt(IReadOnlyList<(double T, double Speed)> samples, double t)
        {
            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].T >= t - 1e-9)
                {
                    double span = samples[i].T - samples[i - 1].T;
                    if (span <= 0.0)
                        return samples[i].Speed;
                    return samples[i - 1].Speed + (samples[i].Speed - samples[i - 1].Speed) * (t - samples[i - 1].T) / span;
                }
            }
            return samples.Count > 0 ? samples[samples.Count - 1].Speed : 0.0;
        }

        /// <summary>
        /// Reads the samples at <see cref="Times"/> against <see cref="Speeds"/>. Returns true when every reading is within
        /// <see cref="TolerancePercent"/>, and one line for each reading for the log.
        /// </summary>
        public static bool Check(IReadOnlyList<(double T, double Speed)> samples, out List<string> lines)
        {
            lines = new List<string>();
            bool pass = samples.Count > 0 && samples[samples.Count - 1].T >= Times[Times.Length - 1] - 1e-9;
            for (int i = 0; i < Times.Length; i++)
            {
                double got = SpeedAt(samples, Times[i]);
                double error = Math.Abs(got - Speeds[i]) / Speeds[i] * 100.0;
                bool ok = error <= TolerancePercent;
                pass &= ok;
                lines.Add(string.Format(CultureInfo.InvariantCulture, "t = {0:0.0} s: {1:0.000} m/s against {2:0.000} ({3:0.00}% off) {4}",
                    Times[i], got, Speeds[i], error, ok ? "ok" : "OFF"));
            }
            return pass;
        }

        /// <summary>One sample for the log.</summary>
        public static string Describe((double T, double Speed) sample)
            => string.Format(CultureInfo.InvariantCulture, "t = {0:0.000} s  speed {1:0.000} m/s", sample.T, sample.Speed);
    }
}
