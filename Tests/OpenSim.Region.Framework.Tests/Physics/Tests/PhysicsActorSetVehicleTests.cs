/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.PhysicsModules.SharedBase.Tests
{
    /// <summary>
    /// The base PhysicsActor.SetVehicle, used by an engine that does not override it, hands the actor the saved
    /// vehicle's flags and no others. Engines read VehicleFlags(-1, ...) two ways: ubODE's ODEDynamics adds or
    /// removes the bits it is given (so -1 with remove = false sets every flag), BulletS's BSDynamics clears all on -1
    /// whatever remove says. The stand-in actors below follow each.
    /// </summary>
    public class PhysicsActorSetVehicleTests : OpenSimTestCase
    {
        /// <summary>Adds or removes the bits given, as ODEDynamics.ProcessVehicleFlags.</summary>
        private sealed class MaskActor : NullPhysicsActor
        {
            public VehicleFlag Flags;
            public override void VehicleFlags(int param, bool remove)
            {
                if (remove)
                    Flags &= ~(VehicleFlag)param;
                else
                    Flags |= (VehicleFlag)param;
            }
        }

        /// <summary>Clears all on -1, otherwise adds or removes, as BSDynamics.ProcessVehicleFlags.</summary>
        private sealed class ClearOnMinusOneActor : NullPhysicsActor
        {
            public VehicleFlag Flags;
            public override void VehicleFlags(int param, bool remove)
            {
                if (param == -1)
                    Flags = 0;
                else if (remove)
                    Flags &= ~(VehicleFlag)param;
                else
                    Flags |= (VehicleFlag)param;
            }
        }

        private static VehicleData Saved(VehicleFlag flags)
            => new VehicleData { m_type = Vehicle.TYPE_CAR, m_flags = flags, m_referenceFrame = Quaternion.Identity };

        [Fact]
        public void AnActorThatMasksGetsOnlyTheSavedFlags()
        {
            TestHelpers.InMethod();
            var actor = new MaskActor { Flags = VehicleFlag.MOUSELOOK_STEER };
            VehicleFlag saved = VehicleFlag.HOVER_UP_ONLY | VehicleFlag.LIMIT_ROLL_ONLY;

            actor.SetVehicle(Saved(saved));

            Assert.Equal(saved, actor.Flags);
        }

        [Fact]
        public void AnActorThatClearsOnMinusOneGetsOnlyTheSavedFlags()
        {
            TestHelpers.InMethod();
            var actor = new ClearOnMinusOneActor { Flags = VehicleFlag.MOUSELOOK_STEER };
            VehicleFlag saved = VehicleFlag.HOVER_UP_ONLY | VehicleFlag.LIMIT_ROLL_ONLY;

            actor.SetVehicle(Saved(saved));

            Assert.Equal(saved, actor.Flags);
        }

        [Fact]
        public void ASavedVehicleWithNoFlagsLeavesNone()
        {
            TestHelpers.InMethod();
            var actor = new MaskActor { Flags = VehicleFlag.MOUSELOOK_STEER };

            actor.SetVehicle(Saved(0));

            Assert.Equal((VehicleFlag)0, actor.Flags);
        }
    }
}
