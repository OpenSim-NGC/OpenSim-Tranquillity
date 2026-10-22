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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

// The scene the step-rate force tests run on (StepRateWindSceneTests, StepRateLocalForceSceneTests): a real backend with the
// physics rate and the number of collision steps set. They run it at 32, 45 and 64 Hz with 1 to 4 collision steps (the module
// keeps each solver step to 1/60 s or shorter, so 32 and 45 Hz cannot run with one).

// A wind module made at run time (a class written out for IWindModule would be taken by the host's region-module
// discovery as one more module): the same wind everywhere, counting the questions.
public class ConstantWind : DispatchProxy
{
    public Vector3 Wind;
    public int Reads;

    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name != nameof(IWindModule.WindSpeed))
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        Interlocked.Increment(ref Reads);
        return Wind;
    }

    public static (IWindModule Module, ConstantWind Log) Make(Vector3 wind)
    {
        IWindModule module = Create<IWindModule, ConstantWind>();
        var log = (ConstantWind)(object)module;
        log.Wind = wind;
        return (module, log);
    }
}

internal sealed class StepRateRig : IDisposable
{
    public const float Heartbeat = 1f / 11f;
    public const int Size = 256;
    public const float Ground = 25f;

    private static readonly FieldInfo BackendField = typeof(JoltScene).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance);

    public readonly JoltScene Scene;
    public readonly JoltPhysicsBackend Backend;
    public JoltPrim Prim;
    private readonly long _steps0;

    public StepRateRig(double rate, int collisionSteps, Action<IConfig> more = null)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", rate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("PhysicsStepCollisionSteps", collisionSteps.ToString(CultureInfo.InvariantCulture));
        MaterialPins.Apply(jolt);
        more?.Invoke(jolt);
        Scene = new JoltScene();
        Scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        Scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 0f, Heartbeat);
        Scene.Simulate(Heartbeat);
        Backend = (JoltPhysicsBackend)BackendField.GetValue(Scene);
        _steps0 = Scene.Substeps.Steps;
    }

    public double Time => (Scene.Substeps.Steps - _steps0) * (double)Scene.Substeps.StepSeconds;

    public JoltPrim AddBox(Vector3 size, Vector3 position, bool vehicle)
    {
        Prim = Assert.IsType<JoltPrim>(Scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), position, size, Quaternion.Identity, true, 900u));
        Prim.Density = 1000f;
        if (vehicle)
        {
            Prim.VehicleType = (int)Vehicle.TYPE_CAR;
            Float(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
            Float(Vehicle.BANKING_EFFICIENCY, 0f);
            Float(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
            Float(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
            Float(Vehicle.HOVER_TIMESCALE, 1000f);
            Float(Vehicle.BUOYANCY, 1f);
            Vector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f));
            Vector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f));
            Prim.VehicleFlags((int)ExtendedVehicleFlags.LimitMotorUp, true);
        }
        else
        {
            Prim.Buoyancy = 1f;   // weightless: only what a test applies acts
        }
        return Prim;
    }

    public void Float(Vehicle code, float v) => Prim.VehicleFloatParam((int)code, v);
    public void Vector(Vehicle code, Vector3 v) => Prim.VehicleVectorParam((int)code, v);

    public void RunTo(double seconds)
    {
        while (Time < seconds - 1e-9)
            Scene.Simulate(Heartbeat);
        Assert.True(Backend.StepForceProviderFaults == 0, $"a step provider threw: {Backend.LastStepForceProviderFault}");
    }

    public void Dispose() => Scene.Dispose();
}
