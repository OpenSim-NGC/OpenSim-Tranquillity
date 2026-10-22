/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Two questions core asks the physics engine about a vehicle. A mouse drag of a vehicle is refused, as it has been, unless
    /// the engine says it runs one (<see cref="PhysicsActor.AllowsVehicleGrab"/>); an engine that does is told when a drag
    /// starts and ends (<see cref="PhysicsActor.Grabbed"/>), the first before the drag's move target is switched on. A script's
    /// llSetVehicleType ends the object's llMoveToTarget and llRotLookAt only when the engine says so
    /// (<see cref="PhysicsActor.VehicleTypeStopsTargets"/>).
    /// </summary>
    public class SceneObjectVehicleRulesTests : OpenSimTestCase
    {
        /// <summary>A stand-in prim actor that records, in order, what core asks of it.</summary>
        private class Body : NullPhysicsActor
        {
            public readonly List<string> Calls = new();
            public bool AllowsGrab;
            public bool StopsTargets;
            private bool m_pidActive;
            private bool m_apidActive;

            public override bool IsPhysical { get; set; }
            public override bool AllowsVehicleGrab => AllowsGrab;
            public override bool VehicleTypeStopsTargets => StopsTargets;
            public override bool Grabbed { set => Calls.Add("Grabbed=" + value); }
            public override Vector3 PIDTarget { set => Calls.Add("PIDTarget"); }
            public override float PIDTau { set => Calls.Add("PIDTau"); }
            public override bool PIDActive
            {
                get => m_pidActive;
                set { m_pidActive = value; Calls.Add("PIDActive=" + value); }
            }
            public override bool APIDActive
            {
                set { m_apidActive = value; Calls.Add("APIDActive=" + value); }
            }
            public bool RotationTargetOn => m_apidActive;
            public override int VehicleType
            {
                get => 0;
                set => Calls.Add("VehicleType=" + value);
            }
        }

        private static (TestScene, SceneObjectGroup, Body) NewVehicle(bool allowsGrab, bool stopsTargets)
        {
            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x1), "part", 0x10);
            var body = new Body { AllowsGrab = allowsGrab, StopsTargets = stopsTargets };
            so.RootPart.PhysActor = body;
            so.ScriptSetPhysicsStatus(true);
            body.Calls.Clear();
            return (scene, so, body);
        }

        [Fact]
        public void ADragOfAVehicleIsRefusedWhenTheEngineDoesNotRunOne()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Body body) = NewVehicle(allowsGrab: false, stopsTargets: false);
            so.RootPart.VehicleType = (int)Vehicle.TYPE_CAR;
            body.Calls.Clear();

            so.GrabMovement(so.RootPart.UUID, Vector3.Zero, new Vector3(130f, 130f, 30f), null);

            Assert.Empty(body.Calls);
            Assert.False(body.PIDActive);
        }

        [Fact]
        public void ADragOfAVehicleIsRunWhenTheEngineRunsOneAndTheEngineIsToldFirstAndLast()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Body body) = NewVehicle(allowsGrab: true, stopsTargets: false);
            so.RootPart.VehicleType = (int)Vehicle.TYPE_CAR;
            body.Calls.Clear();

            so.GrabMovement(so.RootPart.UUID, Vector3.Zero, new Vector3(130f, 130f, 30f), null);

            Assert.Equal(new[] { "Grabbed=True", "PIDTau", "PIDTarget", "PIDActive=True" }, body.Calls);

            body.Calls.Clear();
            so.ReleaseGrab(UUID.Zero);

            Assert.Equal(new[] { "PIDActive=False", "Grabbed=False" }, body.Calls);
        }

        [Fact]
        public void ADragOfAnObjectThatIsNotAVehicleTellsTheEngineToo()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Body body) = NewVehicle(allowsGrab: false, stopsTargets: false);

            so.GrabMovement(so.RootPart.UUID, Vector3.Zero, new Vector3(130f, 130f, 30f), null);
            Assert.Equal(new[] { "Grabbed=True", "PIDTau", "PIDTarget", "PIDActive=True" }, body.Calls);

            body.Calls.Clear();
            so.ReleaseGrab(UUID.Zero);
            Assert.Equal(new[] { "PIDActive=False", "Grabbed=False" }, body.Calls);
        }

        [Fact]
        public void AVehicleTypeEndsTheMoveTargetAndTheLookAtWhenTheEngineSaysSo()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Body body) = NewVehicle(allowsGrab: false, stopsTargets: true);
            so.MoveToTarget(new Vector3(140f, 140f, 30f), 1f);
            body.Calls.Clear();

            so.RootPart.SetVehicleType((int)Vehicle.TYPE_CAR);

            Assert.Equal("VehicleType=" + (int)Vehicle.TYPE_CAR, body.Calls[0]);   // the type first, then the targets end
            Assert.Contains("PIDActive=False", body.Calls);
            Assert.Contains("APIDActive=False", body.Calls);
            Assert.False(body.PIDActive);
        }

        [Fact]
        public void TypeNoneEndsThemToo()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Body body) = NewVehicle(allowsGrab: false, stopsTargets: true);
            so.MoveToTarget(new Vector3(140f, 140f, 30f), 1f);
            body.Calls.Clear();

            so.RootPart.SetVehicleType((int)Vehicle.TYPE_NONE);

            Assert.Equal("VehicleType=" + (int)Vehicle.TYPE_NONE, body.Calls[0]);
            Assert.Contains("PIDActive=False", body.Calls);
            Assert.Contains("APIDActive=False", body.Calls);
        }

        [Fact]
        public void AVehicleTypeLeavesTheTargetsAloneWhenTheEngineDoesNotSayOtherwise()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Body body) = NewVehicle(allowsGrab: false, stopsTargets: false);
            so.MoveToTarget(new Vector3(140f, 140f, 30f), 1f);
            body.Calls.Clear();

            so.RootPart.SetVehicleType((int)Vehicle.TYPE_CAR);

            Assert.Equal(new[] { "VehicleType=" + (int)Vehicle.TYPE_CAR }, body.Calls);
            Assert.True(body.PIDActive);
        }
    }
}
