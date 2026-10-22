/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What the vehicle controller gives under Second Life's behaviour (the default) for the things the InWorldz solver
/// work touches (the motors' ramp and decay, angular deflection, per-axis motor timescales, the decay cap), to the bit.
/// The figures were taken from the controller before that work: every scenario is a state of the body sampled during a
/// run, and the golden words are the bits of those floats. The linear motor under Second Life's choice is pinned the same
/// way; under the InWorldz choice it is Halcyon's stepwise law, compared with a reference worked out from Halcyon's
/// formulas (HalcyonLinearReference) rather than with figures taken from the controller. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleSolverUnchangedTests
{
    private const double Hz45 = 1.0 / 45;
    private const double Hz11 = 1.0 / 11;

    private static VehicleSettings SecondLife => new VehicleSettings { Behaviour = VehicleBehaviour.SecondLife };
    private static VehicleSettings InWorldz => new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz };

    // A run, sampled: the state after every `every` steps of `steps`.
    private static float[] Sampled(BehaviourRig rig, double h, int steps, int every)
    {
        var all = new List<float>();
        for (int i = 1; i <= steps; i++)
        {
            rig.Step(h);
            if (i % every == 0)
                all.AddRange(rig.State());
        }
        return all.ToArray();
    }

    // An airplane with only its angular motor and one angular friction acting: a motor on the third axis, as a script sets it.
    private static float[] AngularMotor(VehicleSettings s, float motorTs, float decayTs, float frictionZ, double h, int steps)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, frictionZ));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, motorTs);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, decayTs);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        return Sampled(rig, h, steps, Math.Max(1, steps / 10));
    }

    private static float[] AngularMotorThreeAxes(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(4f, 6f, 8f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 3f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0.5f, -0.3f, 0.8f));
        return Sampled(rig, Hz45, 225, 25);
    }

    // A moving airplane whose nose is off its heading, with angular deflection on and nothing else.
    private static float[] Deflection(VehicleSettings s, Vector3 velocity, float timescale, float efficiency, double h, int steps)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, timescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, efficiency);
        rig.Body.LinearVelocity = velocity;
        return Sampled(rig, h, steps, Math.Max(1, steps / 10));
    }

    // The four motor timescale codes sent as vectors, then both motors run: under Second Life the vectors are dropped.
    private static float[] VectorTimescalesSent(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 6f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 6f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, new Vector3(0.2f, 3f, 9f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, new Vector3(1f, 2f, 3f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, new Vector3(0.2f, 3f, 9f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, new Vector3(1f, 2f, 3f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 2f, 0f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0.4f, 0f, 1f));
        return Sampled(rig, Hz45, 225, 25);
    }

    // A decay timescale above 120 s sent as a float, motors on for a minute.
    private static float[] LongDecay(VehicleSettings s)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 500f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 500f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(50f, 50f, 50f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(50f, 50f, 50f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(5f, 0f, 0f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 0.5f));
        return Sampled(rig, 0.1, 600, 60);
    }

    // The linear motor with friction and decay, a car, nothing angular.
    private static float[] LinearMotor(VehicleSettings s, float motorTs, float decayTs, double h, int steps)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(100f, 20f, 100f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, motorTs);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, decayTs);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(10f, 3f, 0f));
        return Sampled(rig, h, steps, Math.Max(1, steps / 10));
    }

    private static readonly Dictionary<string, Func<VehicleSettings, float[]>> Scenarios = new()
    {
        ["AirplaneMotorSlow"] = s => AngularMotor(s, 1f, 5f, 20f, Hz45, 450),
        ["AirplaneMotorQuick"] = s => AngularMotor(s, 0.1f, 0.5f, 0.5f, Hz45, 450),
        ["AirplaneMotorSlowAt11"] = s => AngularMotor(s, 5f, 5f, 20f, Hz11, 110),
        ["CarMotorThreeAxes"] = AngularMotorThreeAxes,
        ["DeflectionForward"] = s => Deflection(s, new Vector3(20f, 6f, 0f), 0.5f, 1f, Hz45, 135),
        ["DeflectionBackward"] = s => Deflection(s, new Vector3(-10f, 3f, 0f), 1f, 0.8f, Hz45, 135),
        ["DeflectionSlowAt11"] = s => Deflection(s, new Vector3(4f, -4f, 2f), 2f, 1f, Hz11, 55),
        ["VectorTimescalesSent"] = VectorTimescalesSent,
        ["LongDecay"] = LongDecay,
    };

    private static readonly Dictionary<string, (float motorTs, float decayTs, double h, int steps)> LinearRuns = new()
    {
        ["LinearMotorSlow"] = (1.5f, 6f, Hz45, 450),
        ["LinearMotorQuick"] = (0.05f, 2f, Hz45, 225),
    };

    private static readonly Dictionary<string, Func<VehicleSettings, float[]>> LinearScenarios = LinearRuns.ToDictionary(
        kv => kv.Key, kv => (Func<VehicleSettings, float[]>)(s => LinearMotor(s, kv.Value.motorTs, kv.Value.decayTs, kv.Value.h, kv.Value.steps)));

    private static uint[] Bits(float[] state) => state.Select(f => (uint)BitConverter.SingleToInt32Bits(f)).ToArray();

    private static string Words(float[] state) => string.Join(", ", Bits(state).Select(b => b == 0 ? "0" : "0x" + b.ToString("X8")));

    public static IEnumerable<object[]> SecondLifeNames() => Scenarios.Keys.Select(k => new object[] { k });
    public static IEnumerable<object[]> LinearNames() => LinearScenarios.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(SecondLifeNames))]
    public void Second_Life_behaviour_gives_what_the_controller_gave_before_the_InWorldz_solver_work_to_the_bit(string scenario)
    {
        float[] state = Scenarios[scenario](SecondLife);
        Assert.True(SecondLifeGolden.TryGetValue(scenario, out uint[] golden), $"no golden for {scenario}: [\"{scenario}\"] = new uint[] {{ {Words(state)} }},");
        // Within float rounding of the figures recorded on x64 (see FloatRounding): the last bits differ on Arm64.
        try { FloatRounding.AssertNear(golden, state, scenario); }
        catch (Xunit.Sdk.XunitException e) { throw new Xunit.Sdk.XunitException($"{e.Message}; now new uint[] {{ {Words(state)} }}"); }
        // A controller handed no settings is the same controller: bit for bit, on this machine.
        Assert.Equal(Bits(state), Bits(Scenarios[scenario](VehicleSettings.Default)));
    }

    // The InWorldz linear motor of the same two runs, against Halcyon's law worked out step by step in doubles: the motor's ramp
    // and decay from the velocity at the start of the step, and the friction (100 s on x, 20 s on y) as a change of velocity of
    // its own from the same velocity. The state sampled is the velocity on x and y.
    [Theory]
    [MemberData(nameof(LinearNames))]
    public void The_InWorldz_linear_motor_follows_the_Halcyon_reference(string scenario)
    {
        (float motorTs, float decayTs, double h, int steps) = LinearRuns[scenario];
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(100f, 20f, 100f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, motorTs);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, decayTs);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(10f, 3f, 0f));
        var want = new HalcyonLinearReference(new double[] { 10, 3, 0 }, new double[] { motorTs, motorTs, motorTs }, new double[] { decayTs, decayTs, decayTs })
        {
            Friction = new double[] { 100, 20, 100 },
        };
        int every = Math.Max(1, steps / 10);
        for (int i = 1; i <= steps; i++)
        {
            rig.Step(h);
            want.Step(h);
            if (i % every != 0)
                continue;
            Assert.True(Math.Abs(want.V[0] - rig.Body.LinearVelocity.X) <= 0.003 + 0.002 * Math.Abs(want.V[0]), $"{scenario} x at step {i}: {rig.Body.LinearVelocity.X:R} against {want.V[0]:R}");
            Assert.True(Math.Abs(want.V[1] - rig.Body.LinearVelocity.Y) <= 0.003 + 0.002 * Math.Abs(want.V[1]), $"{scenario} y at step {i}: {rig.Body.LinearVelocity.Y:R} against {want.V[1]:R}");
        }
    }

    [Theory]
    [MemberData(nameof(LinearNames))]
    public void The_Second_Life_linear_motor_is_what_it_was_to_the_bit(string scenario)
    {
        float[] state = LinearScenarios[scenario](SecondLife);
        Assert.True(SecondLifeLinearGolden.TryGetValue(scenario, out uint[] golden), $"no golden for {scenario}: [\"{scenario}\"] = new uint[] {{ {Words(state)} }},");
        try { FloatRounding.AssertNear(golden, state, scenario); }
        catch (Xunit.Sdk.XunitException e) { throw new Xunit.Sdk.XunitException($"{e.Message}; now new uint[] {{ {Words(state)} }}"); }
    }

    private static readonly Dictionary<string, uint[]> SecondLifeGolden = new()
    {
        ["VectorTimescalesSent"] = new uint[] { 0x3C970198, 0xAE880001, 0x3D3CC1FF, 0x3F7FAF3B, 0x3DF41333, 0xAF5A0000, 0x3E988BFF, 0x40172BCC, 0x3F2C35BF, 0x3C0CE0F3, 0x42C800AC, 0x3F800000, 0x3D815970, 0xB0A00000, 0x3E21AFD0, 0x3F7C4561, 0x3E494CA0, 0xAF3A0000, 0x3EFB9FC9, 0x4070CF2C, 0x3FB9E5EB, 0x3D8A6E4D, 0x42C80A07, 0x3F800000, 0x3DFD296B, 0xB1C00000, 0x3E9E39E7, 0x3F716753, 0x3E7E5967, 0x32CE0C00, 0x3F1EF7E1, 0x408C7F0D, 0x401BC432, 0x3E6C7112, 0x42C83250, 0x3F800000, 0x3E437288, 0xAF800000, 0x3EF44F2A, 0x3F5B9B20, 0x3E916BA9, 0x33720600, 0x3F35C693, 0x408A462C, 0x405F6240, 0x3F0B3485, 0x42C89E68, 0x3F800000, 0x3E83579B, 0x32820000, 0x3F242D80, 0x3F391E6B, 0x3E9E49E1, 0x33474600, 0x3F45DC58, 0x40691815, 0x408C63C6, 0x3F821A1A, 0x42C97B71, 0x3F800000, 0x3EA01F7F, 0x33300000, 0x3F482758, 0x3F0A1328, 0x3EA7928D, 0x323D1800, 0x3F517732, 0x40216A4F, 0x409C3596, 0x3FCE0349, 0x42CAF242, 0x3F800000, 0x3EB48862, 0x33D00000, 0x3F61AA71, 0x3EA0C8EB, 0x3EAE692A, 0x318C3000, 0x3F5A0373, 0x3F9B7FD5, 0x4099FDE2, 0x400F4F99, 0x42CD1992, 0x3F800000, 0x3EBDCDA5, 0x34160000, 0x3F6D4103, 0x3D77FF0A, 0x3EB38B31, 0x3317DE00, 0x3F606DFA, 0x3C49A948, 0x40851781, 0x40333A89, 0x42CFEC62, 0x3F800000, 0x3EBA2DA8, 0x3448C000, 0x3F68B903, 0xBE504084, 0x3EB776A1, 0x33CA91C0, 0x3F655444, 0xBF53E9D4, 0x404388A2, 0x404CA79E, 0x42D3487E, 0x3F800000 },
        ["CarMotorThreeAxes"] = new uint[] { 0x3C877A53, 0xBC2511FF, 0x3CDDB675, 0x3F7FDBB3, 0x3DD64868, 0xBD83A1E7, 0x3E314F54, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3D5FC830, 0xBD0A5F06, 0x3DBAE7C3, 0x3F7E6699, 0x3E26487E, 0xBDD205AE, 0x3E8E173F, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3DD4F69B, 0xBD85C00E, 0x3E35596F, 0x3F79FC18, 0x3E461620, 0xBE017399, 0x3EAE809B, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3E21B334, 0xBDCE8D61, 0x3E8C482E, 0x3F717C65, 0x3E55C44E, 0xBE11847E, 0x3EC19FC4, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3E58D002, 0xBE0D0A82, 0x3EBF87DA, 0x3F647007, 0x3E5BD9E2, 0xBE1CA4ED, 0x3ECBE7B7, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3E86333D, 0xBE3226C6, 0x3EF15C3A, 0x3F52EAED, 0x3E5C57EC, 0xBE249FA4, 0x3ED00E0C, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3E9D0C8B, 0xBE5524ED, 0x3F0FBD2F, 0x3F3D648A, 0x3E59C485, 0xBE2A5002, 0x3ECFD4B5, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3EB0342B, 0xBE750233, 0x3F2420D9, 0x3F249294, 0x3E55B3FC, 0xBE2E0CE1, 0x3ECC73B0, 0, 0, 0, 0x42C80000, 0x3F800000, 0x3EBF46BE, 0xBE888E4D, 0x3F355371, 0x3F094A15, 0x3E511693, 0xBE2FEBC0, 0x3EC6D109, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["AirplaneMotorSlow"] = new uint[] { 0, 0, 0x3E341122, 0x3F7C02CA, 0, 0, 0x3F1541B1, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F007E09, 0x3F5D6AE3, 0, 0, 0x3F46B45F, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F4DF4F9, 0x3F180B93, 0, 0, 0x3F58D6BD, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F7AA7A5, 0x3E5028C3, 0, 0, 0x3F5F68A6, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F790922, 0xBE6D39A8, 0, 0, 0x3F6106C0, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F483833, 0xBF1F8656, 0, 0, 0x3F603330, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3EE44D52, 0xBF652412, 0, 0, 0x3F5DFD30, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3D015B7C, 0xBF7FDF50, 0, 0, 0x3F5AE314, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBEC3BCC9, 0xBF6C8DC9, 0, 0, 0x3F5725BF, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBF391FD3, 0xBF30D1AF, 0, 0, 0x3F52EA5A, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["LongDecay"] = new uint[] { 0, 0, 0x3F585614, 0x3F08DFDF, 0, 0, 0x3EEA1F5C, 0x3F879097, 0x4071BA8C, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F2852DE, 0xBF40E192, 0, 0, 0x3EF4B28E, 0xC009B3AE, 0xC027C66D, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBF2846BB, 0xBF40EC28, 0, 0, 0x3EF4C906, 0x402F4044, 0x3FED1923, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBF563A83, 0x3F0C267F, 0, 0, 0x3EF4491F, 0xC0451355, 0xBF6FD240, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3ED94487, 0x3F67CEB5, 0, 0, 0x3EF3BA24, 0x40485E81, 0xBCF8CFE8, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F7507B8, 0xBE9445EC, 0, 0, 0x3EF32427, 0xC038C2DD, 0x3F75ED9F, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBE14D34E, 0xBF7D4827, 0, 0, 0x3EF2877A, 0x4017AEAF, 0xBFE1A2DF, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBF7FFEEA, 0xBBBCD371, 0, 0, 0x3EF1E3E5, 0xBFD0DE19, 0x4016B1EC, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBE245C46, 0x3F7CAE4B, 0, 0, 0x3EF13936, 0x3F3FEE4C, 0xC02B0EBC, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F72F3E0, 0x3EA15E93, 0, 0, 0x3EF08723, 0x3E373DE6, 0x402B9E32, 0, 0x42C80000, 0x3F800000 },
        ["DeflectionForward"] = new uint[] { 0, 0, 0x3D45915D, 0x3F7FB3B8, 0, 0, 0x3E8D1E1D, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3DA4BEA3, 0x3F7F2B9E, 0, 0, 0x3E3CC851, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3DD0C5F1, 0x3F7EAA98, 0, 0, 0x3DFC8C06, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3DEE2B8C, 0x3F7E4355, 0, 0, 0x3DA8ECAE, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E00E725, 0x3F7DF6AD, 0, 0, 0x3D61FB4B, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E07769B, 0x3F7DC006, 0, 0, 0x3D1727CD, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E0BD924, 0x3F7D99F4, 0, 0, 0x3CCA3649, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E0EC7A1, 0x3F7D7FD0, 0, 0, 0x3C8741A7, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E10BD75, 0x3F7D6E05, 0, 0, 0x3C34F0E7, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E120D09, 0x3F7D61FD, 0, 0, 0x3BF20F3F, 0x41A00000, 0x40C00000, 0, 0x42C80000, 0x3F800000 },
        ["DeflectionBackward"] = new uint[] { 0, 0, 0xBC389846, 0x3F7FFBD7, 0, 0, 0xBD99CF46, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBCB168D9, 0x3F7FF0A2, 0, 0, 0xBD8DEC5F, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBCFFED72, 0x3F7FE004, 0, 0, 0xBD82F49E, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBD242DB0, 0x3F7FCB55, 0, 0, 0xBD71ABB1, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBD45954C, 0x3F7FB3B5, 0, 0, 0xBD5EFE97, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBD646511, 0x3F7F9A0A, 0, 0, 0xBD4DC2F4, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBD80682A, 0x3F7F7F10, 0, 0, 0xBD3DDC42, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBD8D8348, 0x3F7F635D, 0, 0, 0xBD2F3023, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBD9999D0, 0x3F7F476B, 0, 0, 0xBD21A646, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000, 0, 0, 0xBDA4BFF6, 0x3F7F2B9B, 0, 0, 0xBD152844, 0xC1200000, 0x40400000, 0, 0x42C80000, 0x3F800000 },
        ["AirplaneMotorSlowAt11"] = new uint[] { 0, 0, 0x3D407D97, 0x3F7FB798, 0, 0, 0x3E2594B5, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E218E1D, 0x3F7CCB3D, 0, 0, 0x3E88C19D, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3E9D3900, 0x3F73A1A4, 0, 0, 0x3EAC531C, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3EF29B22, 0x3F616FBA, 0, 0, 0x3EC3AB65, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F23984C, 0x3F44E869, 0, 0, 0x3ED29CD5, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F492FE0, 0x3F1E4D86, 0, 0, 0x3EDB9DBB, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F6694ED, 0x3EDE6C74, 0, 0, 0x3EE05329, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F794CE0, 0x3E68BC47, 0, 0, 0x3EE1DFCF, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F7FFD5E, 0x3C12EECE, 0, 0, 0x3EE1125C, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F7A65E8, 0xBE550C0C, 0, 0, 0x3EDE81B6, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["AirplaneMotorQuick"] = new uint[] { 0, 0, 0x3E9CB0DE, 0x3F73B790, 0, 0, 0x3F04CA90, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3EEC59F1, 0x3F631767, 0, 0, 0x3E437042, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F01DB7F, 0x3F5C9EA6, 0, 0, 0x3D4492BF, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F047B9C, 0x3F5B0D9E, 0, 0, 0x3C2009E9, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F04FD58, 0x3F5ABEF3, 0, 0, 0x3AE87F18, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F05143E, 0x3F5AB105, 0, 0, 0x399E0BF3, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F051811, 0x3F5AAEB1, 0, 0, 0x384DFCB9, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F0518B0, 0x3F5AAE51, 0, 0, 0x370263D6, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F0518C5, 0x3F5AAE44, 0, 0, 0x35A19CE7, 0, 0, 0, 0x42C80000, 0x3F800000, 0, 0, 0x3F0518C5, 0x3F5AAE44, 0, 0, 0x34451B69, 0, 0, 0, 0x42C80000, 0x3F800000 },
        ["DeflectionSlowAt11"] = new uint[] { 0, 0xBC08EA9B, 0xBC88EA9B, 0x3F7FF48F, 0, 0xBD13E40F, 0xBD93E40F, 0x40800000, 0xC0800000, 0x40000000, 0x42C9D173, 0x3F800000, 0, 0xBC85DA34, 0xBD05DA34, 0x3F7FD43F, 0, 0xBD0D51AC, 0xBD8D51AC, 0x40800000, 0xC0800000, 0x40000000, 0x42CBA2E6, 0x3F800000, 0, 0xBCC44D0D, 0xBD444D0D, 0x3F7FA1DB, 0, 0xBD070A0C, 0xBD870A0C, 0x40800000, 0xC0800000, 0x40000000, 0x42CD7459, 0x3F800000, 0, 0xBCFFEBE2, 0xBD7FEBE2, 0x3F7F5FE7, 0, 0xBD0109DD, 0xBD8109DD, 0x40800000, 0xC0800000, 0x40000000, 0x42CF45CC, 0x3F800000, 0, 0xBD1C6A23, 0xBD9C6A23, 0x3F7F10A5, 0, 0xBCF69BDE, 0xBD769BDE, 0x40800000, 0xC0800000, 0x40000000, 0x42D1173F, 0x3F800000, 0, 0xBD379198, 0xBDB79198, 0x3F7EB618, 0, 0xBCEBA679, 0xBD6BA679, 0x40800000, 0xC0800000, 0x40000000, 0x42D2E8B2, 0x3F800000, 0, 0xBD517A6E, 0xBDD17A6E, 0x3F7E5211, 0, 0xBCE12DBF, 0xBD612DBF, 0x40800000, 0xC0800000, 0x40000000, 0x42D4BA25, 0x3F800000, 0, 0xBD6A3268, 0xBDEA3268, 0x3F7DE62B, 0, 0xBCD72C24, 0xBD572C24, 0x40800000, 0xC0800000, 0x40000000, 0x42D68B98, 0x3F800000, 0, 0xBD80E36D, 0xBE00E36D, 0x3F7D73D8, 0, 0xBCCD9C5E, 0xBD4D9C5E, 0x40800000, 0xC0800000, 0x40000000, 0x42D85D0B, 0x3F800000, 0, 0xBD8C2255, 0xBE0C2255, 0x3F7CFC5E, 0, 0xBCC4795C, 0xBD44795C, 0x40800000, 0xC0800000, 0x40000000, 0x42DA2E7E, 0x3F800000, 0, 0xBD96DC24, 0xBE16DC24, 0x3F7C80E1, 0, 0xBCBBBE4C, 0xBD3BBE4C, 0x40800000, 0xC0800000, 0x40000000, 0x42DBFFF1, 0x3F800000 },
    };

    private static readonly Dictionary<string, uint[]> SecondLifeLinearGolden = new()
    {
        ["LinearMotorQuick"] = new uint[] { 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411FDFB0, 0x403F6017, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411FDFE2, 0x403F3FEF, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411FD76B, 0x403F0D6D, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411FCCD4, 0x403ECE6A, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411FBFAA, 0x403E803B, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411FAF64, 0x403E1FD3, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411F9B67, 0x403DA9C4, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411F830C, 0x403D1A5B, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411F659D, 0x403C6DB5, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x411F425A, 0x403B9FDD, 0, 0x42C80000, 0x3F800000 },
        ["LinearMotorSlow"] = new uint[] { 0, 0, 0, 0x3F800000, 0, 0, 0, 0x40922826, 0x3FAC2FDB, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x40D7271A, 0x3FF972F2, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x40FA8DD1, 0x400F2652, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x4106F9DB, 0x40181C7E, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x410C8D77, 0x401C3D63, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x410FDA68, 0x401DB17F, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x4111D10A, 0x401D8CA3, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x4112F2DF, 0x401C617A, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x41138948, 0x401A82C9, 0, 0x42C80000, 0x3F800000, 0, 0, 0, 0x3F800000, 0, 0, 0, 0x4113BFE2, 0x4018220D, 0, 0x42C80000, 0x3F800000 },
    };
}
