/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 *
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 *
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

/*
 * The smallest and largest sizes a physical object may have, ported from InWorldz Halcyon
 * (InWorldz.PhysxPhysics/PhysxPrim.stability.cs: EnforceMinimumDynamicDimensions, EnforceMaximumDynamicDimensions;
 * InWorldz.PhysxPhysics/PhysxPrim.cs: MIN_SIZE_FOR_DYNAMIC, MAX_PHSYICAL_DIMENSION).
 * Adapted 2026 by Legion Builds
 */

using System;
using System.Numerics;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>
    /// What Halcyon allows for the size of a physical object, under <c>[Jolt] BasicForcesBehaviour = InWorldz</c>. The size
    /// is that of the whole object's box, lined up with the region's axes (Halcyon reads the actor's world bounds, which hold
    /// every part of a linkset): an object with two or more sides under 0.009 m, or with one side over 256 m or two over
    /// 128 m, is not simulated as a physical object. Pure functions, so the rule has one place.
    /// </summary>
    public static class HalcyonPhysicalLimits
    {
        /// <summary>The smallest a side may be before it counts as flat: two such sides and the object cannot be physical
        /// (PhysxPrim.MIN_SIZE_FOR_DYNAMIC, 0.009 m).</summary>
        public const float MinSizeForDynamic = 0.009f;

        /// <summary>The longest a side may be, when a second side is also over it; one side over twice this cannot be physical
        /// (PhysxPrim.MAX_PHSYICAL_DIMENSION, 128 m).</summary>
        public const float MaxPhysicalDimension = 128f;

        public enum Verdict { Ok, TooSmall, TooLarge }

        /// <summary>Halcyon's two checks on an object whose bounds are <paramref name="x"/> by <paramref name="y"/> by
        /// <paramref name="z"/> metres (PhysxPrim.EnforceMinimumDynamicDimensions first, then EnforceMaximumDynamicDimensions).</summary>
        public static Verdict Check(float x, float y, float z)
        {
            int tooSmall = 0, tooLarge = 0;
            if (x < MinSizeForDynamic) tooSmall++;
            if (y < MinSizeForDynamic) tooSmall++;
            if (z < MinSizeForDynamic) tooSmall++;
            if (tooSmall >= 2)
                return Verdict.TooSmall;

            if (x > MaxPhysicalDimension * 2f || y > MaxPhysicalDimension * 2f || z > MaxPhysicalDimension * 2f)
                return Verdict.TooLarge;

            if (x > MaxPhysicalDimension) tooLarge++;
            if (y > MaxPhysicalDimension) tooLarge++;
            if (z > MaxPhysicalDimension) tooLarge++;
            return tooLarge >= 2 ? Verdict.TooLarge : Verdict.Ok;
        }

        /// <summary>The message the owner is sent for a verdict other than <see cref="Verdict.Ok"/>.</summary>
        public static string Message(Verdict verdict) => verdict switch
        {
            Verdict.TooSmall => "Object was too small to be made physical: two or more sides are under 0.009 m",
            Verdict.TooLarge => "Object was too large to be made physical: a side is over 256 m, or two are over 128 m",
            _ => string.Empty,
        };

        /// <summary>Grows the box that holds the parts so far (<paramref name="min"/> to <paramref name="max"/>, start with
        /// +infinity and -infinity) by one part: its size box at its position and rotation.</summary>
        public static void Include(Vector3 position, Quaternion rotation, Vector3 size, ref Vector3 min, ref Vector3 max)
        {
            Vector3 half = size * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
                Vector3 p = position + Vector3.Transform(corner, rotation);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
    }
}
