/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// InWorldz Halcyon's angular motor and angular friction on one axis, worked out apart from the controller for the tests to
/// compare it with (VehicleDynamics.cs:2733-2960 and 1402-1458; read, not run): the ramp by the growth rate with a stiction
/// start, the target velocity, the motor's share by e^(-index / Td) with the share under 0.002 taken as 0, and friction as a
/// change of velocity from the velocity at the start of the step, added on top of the motor's.
/// </summary>
internal static class HalcyonMotorReference
{
    // Halcyon's sentinel growth rate for a velocity crossing zero (ThresholdInverseCrossover).
    private const double CrossOver = -2.010203;

    /// <summary>The decay clock a motor starts at under InWorldz: a motor timescale vector shorter than 0.9 (its length) holds
    /// its grip for up to 0.06 s (VehicleDynamics.cs:3099-3105).</summary>
    public static double DecayStart(double motorTimescale)
    {
        double length = (float)motorTimescale * Math.Sqrt(3.0);
        double f = length < 0.9 ? 1.0 - length : 0.0;
        return (float)(0.0156 * (1.0 - f) - 0.06 * f);
    }

    /// <summary>Halcyon's physics step, over which its motors act (PhysxScene.cs:55-56).</summary>
    public const double HalcyonStep = 0.015;

    /// <summary>The velocity after each of <paramref name="steps"/> steps of <paramref name="dt"/> seconds, from rest, with the motor
    /// direction set at the start. Friction is worked out over each step the caller takes; the motor is Halcyon's, stepped on a
    /// clock of carried time: one motor step of 15 ms for every 15 ms that goes by, each reading the velocity the one before left
    /// and the decay clock as it read at the start of the interval it covers; a change under pi / 512 rad/s is not made.</summary>
    public static double[] Curve(double tm, double td, double tf, double dt, int steps, double direction = 1.0, double startIndex = 0.0)
    {
        double w = 0, target = 0, index = startIndex, carry = 0, threshold = Math.PI / 512.0;
        var curve = new double[steps];
        for (int i = 0; i < steps; i++)
        {
            double last0 = w;
            double carried = carry;
            carry += dt;
            int due = (int)Math.Floor(carry / HalcyonStep + 1e-6);
            if (due > 0) carry = Math.Max(0.0, carry - due * HalcyonStep);
            double v = w;
            for (int k = 0; k < due; k++)
            {
                double read = index - carried + k * HalcyonStep;
                double decay = read > 0 ? Math.Exp(-read / td) : 1.0;
                if (decay < 0.002) decay = 0.0;
                double last = v, adj = last;
                if (direction * (target - adj) > 0) adj = target;
                double ramp;
                double stepsPerTimescale = Math.Max(tm / HalcyonStep, 1.0);
                if (adj * direction > 0) ramp = Math.Log(Math.Abs(direction) / Math.Abs(adj)) / stepsPerTimescale;
                else if (adj == 0) ramp = Math.Log(1.0 + Math.Abs(direction)) / stepsPerTimescale;
                else ramp = Math.Abs(adj) > 0.3 ? -Math.Log(1.0 + Math.Abs(adj - direction)) / stepsPerTimescale : CrossOver;
                double next = adj + adj * ramp;
                if (ramp == CrossOver) ramp = -ramp;       // at the crossover point the velocity is growing the other way
                if (ramp > 0 && Math.Abs(next) < threshold) next = threshold * 8;
                if (direction * next < 0 || ramp < 0) target = next; else target = Math.Clamp(next, -Math.Abs(direction), Math.Abs(direction));
                if (ramp >= 0 && direction * (last - next) > 0) decay = 0;
                // A change of angular velocity under pi / 512 is not made (VehicleDynamics.cs:3029-3038).
                double blended = next * decay + last * (1.0 - decay);
                v = Math.Abs(blended - last) < threshold ? last : blended;
            }
            index += dt;
            double friction = -last0 * (1.0 - Math.Exp(-dt / Math.Max(tf, dt)));
            w = v + friction;
            curve[i] = w;
        }
        return curve;
    }
}
