/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Compares a sampled state with figures recorded on another CPU. Float code generation differs between x64 and Arm64
/// (fused multiply-add, and the order the compiler is allowed to keep), so the same source gives results that differ in
/// the last bit or two, and a sum of terms that cancel leaves a residue that differs by more than that against its own
/// size. The comparison therefore allows a relative error of 1e-5 per word, plus an absolute error of 1e-5 times the
/// largest word among those that are real quantities (the body's orientation and velocities) in the same sample: a
/// residue of 1e-9 beside a velocity of 1 is zero within float rounding. Bit-for-bit equality is kept where it can be
/// asked of one machine: two runs here must agree with each other exactly.
/// </summary>
internal static class FloatRounding
{
    public const double Relative = 1e-5;

    /// <summary>Words per sample in BehaviourRig.State(); the first <see cref="Dynamic"/> are orientation and velocities.</summary>
    private const int SampleWords = 12, Dynamic = 10;

    public static void AssertNear(uint[] recorded, float[] actual, string what)
    {
        Assert.True(recorded.Length == actual.Length, $"{what}: {actual.Length} words against {recorded.Length} recorded");
        for (int start = 0; start < actual.Length; start += SampleWords)
        {
            int end = Math.Min(start + SampleWords, actual.Length);
            double scale = 0;
            for (int i = start; i < Math.Min(start + Dynamic, end); i++)
                scale = Math.Max(scale, Math.Abs(BitConverter.UInt32BitsToSingle(recorded[i])));
            for (int i = start; i < end; i++)
            {
                double want = BitConverter.UInt32BitsToSingle(recorded[i]), got = actual[i];
                double allowed = Relative * Math.Max(Math.Abs(want), Math.Abs(got)) + (i - start < Dynamic ? Relative * scale : 0);
                Assert.True(Math.Abs(want - got) <= allowed, $"{what}, word {i} (sample {start / SampleWords}): {got:R} against recorded {want:R}");
            }
        }
    }
}
