/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// A damped spring, as Second Life documents hover and the vertical attractor: a timescale and an efficiency that
    /// slides from 0 (bouncy: the spring wobbles around its rest) to 1 (critically damped: it reaches its rest with
    /// exponential decay). For an error e from the rest:
    ///
    ///   e'' = -w^2 e - 2 z w e'          w = 1 / T,  z = efficiency
    ///
    /// With z = 1 the error decays as e^(-t / T) (1 + t / T). <see cref="Step"/> is the exact solution over a step,
    /// so a span of time gives the same motion however it is cut into steps.
    /// </summary>
    public static class VehicleSpring
    {
        /// <summary>The error and its rate of change after h seconds, from e0 and v0.</summary>
        public static (double e, double v) Step(double e0, double v0, double timescale, double efficiency, double h)
        {
            double w = 1.0 / timescale;
            return StepGeneral(e0, v0, w * w, Math.Clamp(efficiency, 0.0, 1.0) * w, h);
        }

        /// <summary>
        /// The general form, e'' = -k e - 2 b e': the error and its rate after h seconds. k = w^2 is the stiffness
        /// (0 for no spring, damping alone), b = z w the damping; b above w is over-damped.
        /// </summary>
        public static (double e, double v) StepGeneral(double e0, double v0, double k, double b, double h)
        {
            double decay = Math.Exp(-b * h);
            double d = b * b - k;

            // Critically damped (d = 0): e = e^(-b t) (e0 + (v0 + b e0) t).
            if (Math.Abs(d) < 1e-12 * Math.Max(k, 1e-12))
            {
                double c = v0 + b * e0;
                return (decay * (e0 + c * h), decay * (v0 - b * c * h));
            }

            if (d < 0)
            {
                // Under-damped: e = e^(-b t) (e0 cos(wd t) + (v0 + b e0) / wd sin(wd t)), wd = sqrt(k - b^2).
                double wd = Math.Sqrt(-d);
                double cos = Math.Cos(wd * h), sin = Math.Sin(wd * h);
                return (decay * (e0 * cos + (v0 + b * e0) / wd * sin),
                        decay * (v0 * cos - (k * e0 + b * v0) / wd * sin));
            }

            // Over-damped: e = e^(-b t) (e0 cosh(s t) + (v0 + b e0) / s sinh(s t)), s = sqrt(b^2 - k).
            double sd = Math.Sqrt(d);
            double cosh = Math.Cosh(sd * h), sinh = Math.Sinh(sd * h);
            return (decay * (e0 * cosh + (v0 + b * e0) / sd * sinh),
                    decay * (v0 * cosh - (k * e0 + b * v0) / sd * sinh));
        }

        /// <summary>
        /// The step for a body that moves at the velocity it is given over a step (the engine's x += v h): the
        /// velocity to give it so that it ends the step where the spring would, and what the spring's own velocity
        /// will be there minus that (to add back to the velocity read at the next step, so the spring carries on from
        /// its true state).
        /// </summary>
        public static (double move, double carry) MoveOver(double e0, double v0, double timescale, double efficiency, double h)
        {
            double w = 1.0 / timescale;
            return MoveOverGeneral(e0, v0, w * w, Math.Clamp(efficiency, 0.0, 1.0) * w, h);
        }

        /// <summary><see cref="MoveOver"/> for the general form (<see cref="StepGeneral"/>).</summary>
        public static (double move, double carry) MoveOverGeneral(double e0, double v0, double k, double b, double h)
        {
            (double e, double v) = StepGeneral(e0, v0, k, b, h);
            double move = (e - e0) / h;
            return (move, v - move);
        }
    }
}
