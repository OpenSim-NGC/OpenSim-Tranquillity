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

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A copied object (SceneObjectGroup.Copy, as used by SceneGraph.DuplicateObject) gets its own vehicle record:
    /// the same settings as the original, and a later llSetVehicle* call on one does not change the other.
    /// </summary>
    public class SceneObjectCopyVehicleTests : OpenSimTestCase
    {
        private static SceneObjectGroup NewVehicle()
        {
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
            so.RootPart.SetVehicleType((int)Vehicle.TYPE_CAR);
            so.RootPart.SetVehicleFloatParam((int)Vehicle.HOVER_HEIGHT, 2f);
            return so;
        }

        [Fact]
        public void TheCopyHasTheSameSettingsInItsOwnRecord()
        {
            TestHelpers.InMethod();
            SceneObjectGroup so = NewVehicle();

            SceneObjectGroup copy = so.Copy(true);

            Assert.NotSame(so.RootPart.VehicleParams, copy.RootPart.VehicleParams);
            Assert.Equal(so.RootPart.VehicleParams.vd, copy.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void AChangeOnTheCopyLeavesTheOriginalAlone()
        {
            TestHelpers.InMethod();
            SceneObjectGroup so = NewVehicle();
            VehicleData before = so.RootPart.VehicleParams.vd;
            SceneObjectGroup copy = so.Copy(true);

            copy.RootPart.SetVehicleFloatParam((int)Vehicle.HOVER_HEIGHT, 9f);
            copy.RootPart.SetVehicleFlags((int)VehicleFlag.HOVER_UP_ONLY, false);
            copy.RootPart.SetVehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(5, 0, 0));

            Assert.Equal(before, so.RootPart.VehicleParams.vd);
            Assert.Equal(9f, copy.RootPart.VehicleParams.vd.m_VhoverHeight);
        }

        [Fact]
        public void AChangeOnTheOriginalLeavesTheCopyAlone()
        {
            TestHelpers.InMethod();
            SceneObjectGroup so = NewVehicle();
            SceneObjectGroup copy = so.Copy(true);
            VehicleData copied = copy.RootPart.VehicleParams.vd;

            so.RootPart.SetVehicleRotationParam((int)Vehicle.REFERENCE_FRAME, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f));
            so.RootPart.SetVehicleFlags((int)VehicleFlag.LIMIT_ROLL_ONLY, false);

            Assert.Equal(copied, copy.RootPart.VehicleParams.vd);
        }

        [Fact]
        public void AnObjectWithNoVehicleCopiesWithNone()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);

            SceneObjectGroup copy = so.Copy(true);

            Assert.Null(copy.RootPart.VehicleParams);
        }
    }
}
