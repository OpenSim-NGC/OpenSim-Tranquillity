/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Vehicles
{
    /// <summary>
    /// A running vehicle motor, as it is carried across a region border: the time since the motor was set (its decay clock),
    /// the direction it is driving toward and the velocity its ramp has reached. The InWorldz simulator carries the same
    /// three values of each motor over a crossing (Dynamics: DynamicsSimulationData.cs, merged by VehicleProperties.Merge),
    /// so a vehicle with a held throttle keeps driving across the border.
    /// </summary>
    public readonly struct MotorRunningState
    {
        public MotorRunningState(float age, Vector3 direction, Vector3 target)
        {
            Age = age;
            Direction = direction;
            Target = target;
        }

        /// <summary>Seconds on the motor's decay clock; negative while a quick motor's decay is held back.</summary>
        public float Age { get; }
        public Vector3 Direction { get; }
        public Vector3 Target { get; }
    }
}
