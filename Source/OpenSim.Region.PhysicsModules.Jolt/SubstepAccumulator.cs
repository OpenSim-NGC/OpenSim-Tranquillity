/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// How many fixed physics steps each heartbeat runs when [Jolt] PhysicsStepRate is on.
//
// Every physics step is exactly 1 / rate seconds. The heartbeat's time is added to an accumulator and as many
// whole steps as it holds are taken, so the remainder carries into the next heartbeat and the long-run rate is
// exact when a heartbeat is not a whole number of steps (11 Hz heartbeats at 45 Hz alternate 4 and 5 steps).
// A heartbeat never runs more than MaxStepsPerFrame steps; when it would, the time beyond the cap is dropped
// rather than carried, so a region that cannot keep up does not fall further behind every heartbeat.
// Pure: no scene, no backend, so it is tested directly.

using System;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal sealed class SubstepAccumulator
    {
        /// <summary>The most physics steps one heartbeat runs.</summary>
        internal const int MaxStepsPerFrame = 16;

        private readonly double _step;
        private double _carry;

        internal SubstepAccumulator(float rateHz)
        {
            if (!(rateHz > 0f) || float.IsInfinity(rateHz))
                throw new ArgumentOutOfRangeException(nameof(rateHz));
            RateHz = rateHz;
            _step = 1.0 / rateHz;
            StepSeconds = (float)_step;
        }

        internal float RateHz { get; }

        /// <summary>The length of every physics step, 1 / rate.</summary>
        internal float StepSeconds { get; }

        /// <summary>Physics steps taken in all.</summary>
        internal long Steps { get; private set; }

        /// <summary>Heartbeats that hit <see cref="MaxStepsPerFrame"/> and dropped time.</summary>
        internal long CappedFrames { get; private set; }

        /// <summary>Time carried into the next heartbeat, in seconds (always under one step).</summary>
        internal double Carry => _carry;

        /// <summary>Adds one heartbeat's time and returns the number of physics steps to run now.</summary>
        internal int Advance(double frameSeconds)
        {
            if (!(frameSeconds > 0.0) || double.IsInfinity(frameSeconds))
                return 0;
            _carry += frameSeconds;
            // The small epsilon keeps a heartbeat that is a whole number of steps (in decimal) from losing one to
            // binary rounding and taking it a heartbeat late.
            double whole = Math.Floor(_carry / _step + 1e-9);
            int n;
            if (whole > MaxStepsPerFrame)
            {
                n = MaxStepsPerFrame;
                _carry = 0.0;
                CappedFrames++;
            }
            else
            {
                n = (int)whole;
                _carry = Math.Max(0.0, _carry - n * _step);
            }
            Steps += n;
            return n;
        }
    }
}
