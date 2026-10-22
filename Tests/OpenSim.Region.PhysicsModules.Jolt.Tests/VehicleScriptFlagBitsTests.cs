/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The InWorldz extension flags as a script sends them (the numbers of the InWorldz simulator's VehicleFlags.cs:48-60:
/// REACT_TO_CURRENTS 0x10000, REACT_TO_WIND 0x20000, LIMIT_MOTOR_DOWN 0x40000, TORQUE_WORLD_Z 0x80000, MOUSEPOINT_STEER
/// 0x100000, MOUSEPOINT_BANK 0x200000) must arrive in the controller as themselves, from a script call
/// (SceneObjectPart.SetVehicleFlags -> PhysicsActor.VehicleFlags -> ProcessVehicleFlags) and from a saved record
/// (SOPVehicle's flags -> PhysicsActor.SetVehicle -> RestoreFlags). Pure, parallel.
/// </summary>
public class VehicleScriptFlagBitsTests
{
    public static IEnumerable<object[]> Extensions()
    {
        yield return new object[] { 0x10000, ExtendedVehicleFlags.ReactToCurrents };
        yield return new object[] { 0x20000, ExtendedVehicleFlags.ReactToWind };
        yield return new object[] { 0x40000, ExtendedVehicleFlags.LimitMotorDown };
        yield return new object[] { 0x80000, ExtendedVehicleFlags.TorqueWorldZ };
        yield return new object[] { 0x100000, ExtendedVehicleFlags.MousePointSteer };
        yield return new object[] { 0x200000, ExtendedVehicleFlags.MousePointBank };
    }

    private static VehicleController Plain()
    {
        var v = new VehicleController(null!);
        v.ProcessTypeChange(Vehicle.TYPE_NONE);
        return v;
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public void A_script_call_sets_the_flag_it_names_and_no_other(int scriptBit, ExtendedVehicleFlags expected)
    {
        VehicleController v = Plain();
        v.ProcessVehicleFlags(scriptBit, remove: false);
        Assert.Equal(expected, v.Flags);
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public void A_script_call_removes_the_flag_it_names_and_no_other(int scriptBit, ExtendedVehicleFlags flag)
    {
        VehicleController v = Plain();
        v.ProcessVehicleFlags(-1, remove: false);
        v.ProcessVehicleFlags(scriptBit, remove: true);
        Assert.Equal(AllNamed & ~flag, v.Flags & AllNamed);
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public void A_saved_record_restores_the_flag_it_holds_and_no_other(int scriptBit, ExtendedVehicleFlags expected)
    {
        // The record keeps the script's number, as SceneObjectPart.SetVehicleFlags stores it.
        var record = new SOPVehicle();
        record.ProcessVehicleFlags(scriptBit, remove: false);
        Assert.Equal(scriptBit, (int)record.vd.m_flags);

        VehicleController v = Plain();
        v.RestoreFlags((int)record.vd.m_flags);
        Assert.Equal(expected, v.Flags);
    }

    [Fact]
    public void Several_extension_flags_and_the_standard_ones_together_each_arrive_as_themselves()
    {
        VehicleController v = Plain();
        v.ProcessVehicleFlags(0x1 | 0x40 | 0x40000 | 0x200000, remove: false);
        Assert.Equal(ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitMotorUp
            | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.MousePointBank, v.Flags);
    }

    [Fact]
    public void Minus_one_sets_every_flag_and_removes_every_flag()
    {
        VehicleController v = Plain();
        v.ProcessVehicleFlags(-1, remove: false);
        Assert.Equal(AllNamed, v.Flags & AllNamed);
        v.ProcessVehicleFlags(-1, remove: true);
        Assert.Equal(ExtendedVehicleFlags.None, v.Flags);
    }

    [Fact]
    public void Limit_motor_down_from_a_script_is_not_the_mouse_steer_bit()
    {
        // The flag's behaviour (the motor does not push down), reached from the script's number: with the bug the
        // number landed on MousePointSteer and the motor pushed down.
        VehicleController v = Plain();
        v.ProcessVehicleFlags(0x40000, remove: false);
        Assert.True((v.Flags & ExtendedVehicleFlags.LimitMotorDown) != 0);
        Assert.True((v.Flags & ExtendedVehicleFlags.MousePointSteer) == 0);
    }

    private const ExtendedVehicleFlags AllNamed = (ExtendedVehicleFlags)((1 << 22) - 1);
}
