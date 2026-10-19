/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A vehicle's step force provider on a real backend: attached while the prim is a vehicle and gone with its type, called
/// once per collision step for a vehicle that is stepped and not at all for one the Update-rate path leaves asleep, and it
/// never faults. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleStepProviderSceneTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;

    private static readonly FieldInfo BackendField = typeof(JoltScene).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance);

    private static JoltScene NewScene(float rate, int collisionSteps)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", rate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("PhysicsStepCollisionSteps", collisionSteps.ToString(CultureInfo.InvariantCulture));
        OpenSim.Region.PhysicsModules.Jolt.Harness.MaterialPins.Apply(jolt);
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 0f, Heartbeat);
        scene.Simulate(Heartbeat);
        return scene;
    }

    private static JoltPhysicsBackend BackendOf(JoltScene scene) => (JoltPhysicsBackend)BackendField.GetValue(scene);

    private static JoltPrim AddCar(JoltScene scene, Vector3 position)
        => Assert.IsType<JoltPrim>(scene.AddPrimShape("car", PrimitiveBaseShape.CreateBox(), position, new Vector3(2f, 1f, 0.5f), Quaternion.Identity, true, 910u));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_driven_vehicle_is_stepped_once_in_each_collision_step_of_each_Update(int n)
    {
        JoltScene scene = NewScene(64f, n);
        try
        {
            JoltPrim car = AddCar(scene, new Vector3(40f, 128f, Ground + 0.26f));
            car.VehicleType = (int)Vehicle.TYPE_CAR;
            car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
            JoltPhysicsBackend backend = BackendOf(scene);
            Assert.Equal(1, backend.StepForceProviderCount);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            long updates0 = scene.VehicleStepCalls, steps0 = scene.VehicleOnStepCalls;
            for (int i = 0; i < 22; i++)
                scene.Simulate(Heartbeat);
            long updates = scene.VehicleStepCalls - updates0, steps = scene.VehicleOnStepCalls - steps0;
            Assert.True(updates >= 100, $"only {updates} Updates in two seconds at 64 Hz");
            Assert.Equal(updates * n, steps);
            Assert.Equal(0, backend.StepForceProviderFaults);
            Assert.True(car.Velocity.X > 3f, $"the car did not drive: {car.Velocity}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_provider_is_gone_when_the_prim_stops_being_a_vehicle()
    {
        JoltScene scene = NewScene(64f, 2);
        try
        {
            JoltPrim car = AddCar(scene, new Vector3(40f, 128f, Ground + 0.26f));
            JoltPhysicsBackend backend = BackendOf(scene);
            Assert.Equal(0, backend.StepForceProviderCount);
            Assert.False(backend.HasStepListener);   // a region with no vehicle has no listener, so its empty Updates stay cheap
            car.VehicleType = (int)Vehicle.TYPE_CAR;
            Assert.Equal(1, backend.StepForceProviderCount);
            scene.Simulate(Heartbeat);
            car.VehicleType = (int)Vehicle.TYPE_NONE;
            Assert.Equal(0, backend.StepForceProviderCount);
            Assert.False(backend.HasStepListener);
            for (int i = 0; i < 5; i++)
                scene.Simulate(Heartbeat);
            Assert.Equal(0, backend.StepForceProviderFaults);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_vehicle_the_Update_path_holds_at_rest_is_not_stepped_in_the_collision_steps()
    {
        JoltScene scene = NewScene(64f, 2);
        try
        {
            JoltPrim car = AddCar(scene, new Vector3(40f, 128f, Ground + 0.26f));
            car.VehicleType = (int)Vehicle.TYPE_CAR;   // no motor: it rests on the ground and goes to sleep
            for (int i = 0; i < 66; i++)
                scene.Simulate(Heartbeat);   // six seconds
            long before = scene.VehicleOnStepCalls;
            for (int i = 0; i < 22; i++)
                scene.Simulate(Heartbeat);
            Assert.Equal(before, scene.VehicleOnStepCalls);
            Assert.Equal(0, BackendOf(scene).StepForceProviderFaults);
        }
        finally { scene.Dispose(); }
    }

    // The provider follows the body when it is made anew (a prim moved far, resized or welded gets a new engine body).
    [Fact]
    public void The_provider_follows_a_vehicle_to_its_new_body()
    {
        JoltScene scene = NewScene(64f, 2);
        try
        {
            JoltPrim car = AddCar(scene, new Vector3(40f, 128f, Ground + 0.26f));
            car.VehicleType = (int)Vehicle.TYPE_CAR;
            car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            car.Size = new Vector3(3f, 1.5f, 0.5f);   // a new shape: the body is made anew
            for (int i = 0; i < 3; i++)
                scene.Simulate(Heartbeat);
            long before = scene.VehicleOnStepCalls;
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            Assert.True(scene.VehicleOnStepCalls > before + 100, $"the resized vehicle was not stepped in the collision steps: {scene.VehicleOnStepCalls - before} calls");
            Assert.Equal(1, BackendOf(scene).StepForceProviderCount);
            Assert.Equal(0, BackendOf(scene).StepForceProviderFaults);
        }
        finally { scene.Dispose(); }
    }
}
