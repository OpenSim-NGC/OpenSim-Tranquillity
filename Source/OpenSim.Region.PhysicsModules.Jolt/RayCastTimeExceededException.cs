/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>
    /// A ray cast the region had no ray cast time left for, or that ran out of it ([Jolt] RayCastBudgetMs,
    /// RayCastMaxTestedHits). Thrown to the caller of the 4-argument <c>RaycastWorld</c>, whose llCastRay reports an
    /// exception as RCERR_CAST_TIME_EXCEEDED.
    /// </summary>
    public sealed class RayCastTimeExceededException : Exception
    {
        public RayCastStatus Status { get; }

        public RayCastTimeExceededException(RayCastStatus status)
            : base(status == RayCastStatus.Refused
                ? "ray cast refused: the region has spent its ray cast time for this frame"
                : "ray cast cut short: it ran past the region's ray cast time or the hits one cast may test")
        {
            Status = status;
        }
    }
}
