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
    /// A vehicle motor and friction on one axis of the vehicle frame, as Second Life documents them
    /// (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "Moving the Vehicle", "Steering the Vehicle" and
    /// "Friction Timescales"; LlSetVehicleFloatParam and LlSetVehicleVectorParam):
    ///
    ///   dv/dt = g(s) * (M - v) / Tm  -  v / Tf  +  a          g(s) = e^(-s / Td)
    ///
    /// - v: the velocity on the axis (linear, or angular about it);
    /// - M: the motor direction on the axis, the velocity the motor tries to reach;
    /// - Tm: the motor timescale, the time constant for the velocity to approach M exponentially;
    /// - Td: the motor decay timescale; the motor's grip g starts at 1 when the motor is set and decays
    ///   exponentially toward nothing; s is the time since that set;
    /// - Tf: the friction timescale, the time constant of an exponential decay of v that acts all the time;
    /// - a: a constant acceleration on the axis over the step: the vehicle's gravity along it (zero for the angular
    ///   motor). Gravity acts inside the step with the motor and friction, so a vehicle on a slope settles where the
    ///   three balance, whatever the step.
    ///
    /// <see cref="Step"/> is the exact solution over a step, so a span of time gives the same velocity however it
    /// is cut into steps. With the grip full the velocity settles at (M / Tm + a) / (1 / Tm + 1 / Tf), which is
    /// M * Tf / (Tf + Tm) with no gravity on the axis.
    /// </summary>
    public static class VehicleMotorSolver
    {
        // A friction timescale at or above this is no friction on that axis: the documented way to turn friction
        // off is a large timescale, and this is the largest a script can set.
        public const double FrictionOffTimescale = VehicleLimits.MaxTimescale;

        // The shortest timescale the solver takes: the lowest a region may set the shortest script timescale to
        // ([Jolt] VehicleMinTimescale). A timescale below it (only an unset preset has one) is taken as this.
        public const double MinTimescale = 0.001;

        // The motor-and-friction integral is taken on panels no longer than this share of the shortest of the motor,
        // decay and friction timescales, each with a four-point Gauss-Legendre rule: the integrand is a smooth
        // exponential, so this is exact to well under a part in a million.
        private const double PanelShareOfTimescale = 0.5;
        private const int MaxPanels = 256;

        // Four-point Gauss-Legendre nodes and weights on [-1, 1].
        private static readonly double[] GaussNodes = { -0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526 };
        private static readonly double[] GaussWeights = { 0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538 };

        /// <summary>The motor's grip s seconds after it was set: e^(-s / Td). Zero for a motor never set or
        /// released (s infinite).</summary>
        public static double Grip(double age, double decayTimescale)
            => double.IsPositiveInfinity(age) ? 0.0 : Math.Exp(-Math.Max(age, 0.0) / decayTimescale);

        /// <summary>The friction rate 1 / Tf on an axis, or 0 when its timescale is the "off" value.</summary>
        public static double FrictionRate(double frictionTimescale)
            => frictionTimescale >= FrictionOffTimescale ? 0.0 : 1.0 / frictionTimescale;

        /// <summary>
        /// The velocity on one axis after a step of h seconds of dv/dt = g(s) (M - v) / Tm - v / Tf + a, starting
        /// from v0 with the motor set age seconds before the step (infinite: no motor).
        /// </summary>
        public static double Step(double v0, double motor, double motorTimescale, double decayTimescale, double age,
                                  double frictionTimescale, double h, double accel = 0.0)
        {
            motorTimescale = Math.Max(motorTimescale, MinTimescale);
            decayTimescale = Math.Max(decayTimescale, MinTimescale);
            frictionTimescale = Math.Max(frictionTimescale, MinTimescale);
            double kf = FrictionRate(frictionTimescale);
            double grip0 = Grip(age, decayTimescale);
            if (h <= 0)
                return v0;

            // No motor: friction and gravity, v = v0 e^(-h / Tf) + a Tf (1 - e^(-h / Tf)); with no friction, v0 + a h.
            if (grip0 <= 0)
                return FrictionStep(v0, frictionTimescale, h, accel);

            // The motor's pull integrated over the step: A(t) = integral of g / Tm = g0 (Td / Tm) (1 - e^(-t / Td)).
            double pull = grip0 * decayTimescale / motorTimescale;
            double A(double t) => pull * (1.0 - Math.Exp(-t / decayTimescale));
            double Ah = A(h);

            // No friction and no gravity: v = M + (v0 - M) e^(-A(h)).
            if (kf == 0 && accel == 0)
                return motor + (v0 - motor) * Math.Exp(-Ah);

            // Otherwise: v(h) = v0 e^(-A(h) - h / Tf) + integral_0^h (m(t) M + a) e^(-(A(h) - A(t)) - (h - t) / Tf) dt,
            // with m(t) = g0 e^(-t / Td) / Tm, the motor's rate at time t into the step.
            double shortest = Math.Min(Math.Min(motorTimescale, decayTimescale), kf > 0 ? frictionTimescale : double.MaxValue);
            int panels = (int)Math.Min(MaxPanels, Math.Max(1.0, Math.Ceiling(h / (shortest * PanelShareOfTimescale))));
            double width = h / panels;
            double integral = 0;
            for (int p = 0; p < panels; p++)
            {
                double mid = (p + 0.5) * width;
                for (int k = 0; k < GaussNodes.Length; k++)
                {
                    double t = mid + 0.5 * width * GaussNodes[k];
                    double rate = grip0 * Math.Exp(-t / decayTimescale) / motorTimescale;
                    integral += GaussWeights[k] * 0.5 * width * (rate * motor + accel) * Math.Exp(-(Ah - A(t)) - kf * (h - t));
                }
            }
            return v0 * Math.Exp(-Ah - kf * h) + integral;
        }

        /// <summary>
        /// The velocity on one axis after h seconds of the motor at its whole grip (no decay yet), friction and a constant
        /// acceleration a: dv/dt = (M - v) / Tm - v / Tf + a, whose solution is v_inf + (v0 - v_inf) e^(-(1 / Tm + 1 / Tf) h)
        /// with v_inf = (M / Tm + a) / (1 / Tm + 1 / Tf). For the span before a motor's decay starts (InWorldz holds it
        /// back a few hundredths of a second for a quick motor).
        /// </summary>
        public static double StepFullGrip(double v0, double motor, double motorTimescale, double frictionTimescale, double h, double accel = 0.0)
        {
            if (h <= 0)
                return v0;
            motorTimescale = Math.Max(motorTimescale, MinTimescale);
            double rate = 1.0 / motorTimescale + FrictionRate(Math.Max(frictionTimescale, MinTimescale));
            double steady = (motor / motorTimescale + accel) / rate;
            return steady + (v0 - steady) * Math.Exp(-rate * h);
        }

        /// <summary>
        /// The velocity on one axis after a step of friction and a constant acceleration a, no motor:
        /// v0 e^(-h / Tf) + a Tf (1 - e^(-h / Tf)), or v0 + a h with no friction on the axis.
        /// </summary>
        public static double FrictionStep(double v0, double frictionTimescale, double h, double accel = 0.0)
        {
            double kf = FrictionRate(Math.Max(frictionTimescale, MinTimescale));
            if (kf == 0)
                return v0 + accel * h;
            double decay = Math.Exp(-kf * h);
            return v0 * decay + accel * (1.0 - decay) / kf;
        }
    }
}
