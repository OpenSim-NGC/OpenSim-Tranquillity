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
/// Choosing a vehicle type sets that type's documented flags and clears every other flag, including one a script set
/// before llSetVehicleType. Each type page's llSetVehicleFlags list (https://wiki.secondlife.com/wiki/VEHICLE_TYPE_SLED,
/// _CAR, _BOAT, _AIRPLANE, _BALLOON) is the whole set. Pure, parallel.
/// </summary>
public class VehicleTypeFlagTests
{
    private const ExtendedVehicleFlags AllFlags = (ExtendedVehicleFlags)((1 << 22) - 1);

    public static IEnumerable<object[]> TypeFlags()
    {
        yield return new object[] { Vehicle.TYPE_SLED, ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly
            | ExtendedVehicleFlags.LimitMotorUp };
        yield return new object[] { Vehicle.TYPE_CAR, ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly
            | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp };
        yield return new object[] { Vehicle.TYPE_BOAT, ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverWaterOnly
            | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp };
        yield return new object[] { Vehicle.TYPE_AIRPLANE, ExtendedVehicleFlags.LimitRollOnly };
        yield return new object[] { Vehicle.TYPE_BALLOON, ExtendedVehicleFlags.None };
        yield return new object[] { Vehicle.TYPE_NONE, ExtendedVehicleFlags.None };
    }

    [Theory]
    [MemberData(nameof(TypeFlags))]
    public void A_type_sets_its_documented_flags_and_clears_every_other(Vehicle type, ExtendedVehicleFlags expected)
    {
        foreach (Vehicle before in new[] { Vehicle.TYPE_NONE, Vehicle.TYPE_CAR, Vehicle.TYPE_BOAT, type })
        {
            var v = new VehicleController(null!);
            v.ProcessTypeChange(before);
            v.ProcessVehicleFlags((int)AllFlags, false);   // a script set every flag first
            v.ProcessTypeChange(type);
            Assert.Equal(expected, v.Flags);
        }
    }

    /// <summary>A body that keeps what the controller sets and moves by its velocity, with the engine's share of
    /// gravity applied over each step (no contacts).</summary>
    private sealed class MovingBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        public float GravityFactor;
        public void SetGravityFactor(float factor) => GravityFactor = factor;
        public bool HasCollision => false;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // An airplane (documented flags: LIMIT_ROLL_ONLY only), buoyant, with an upward linear motor of 3 m/s for 2 s:
    // its vertical speed. With flagFirst the script sets LIMIT_MOTOR_UP and then picks the type again; otherwise it
    // sets the flag after the type, where it stays.
    private static float RiseSpeed(double rate, bool flagFirst)
    {
        var body = new MovingBody();
        var v = new VehicleController(body);
        double now = 0;
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        v.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        v.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
        if (flagFirst)
        {
            v.ProcessVehicleFlags((int)ExtendedVehicleFlags.LimitMotorUp, false);
            v.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
        }
        else
        {
            v.ProcessVehicleFlags((int)ExtendedVehicleFlags.LimitMotorUp, false);
        }
        v.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        v.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        v.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
        v.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
        v.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 3f));
        double h = 1.0 / rate;
        for (int i = 0; i < (int)Math.Round(2.0 * rate); i++)
        {
            v.Step((float)h);
            body.LinearVelocity += body.Gravity * body.GravityFactor * (float)h;
            body.Position += body.LinearVelocity * (float)h;
            body.AngularVelocity = Vector3.Zero;
            now += h;
        }
        return body.LinearVelocity.Z;
    }

    [Fact]
    public void A_flag_set_before_the_type_no_longer_acts_at_11_and_45_Hz()
    {
        float at11 = RiseSpeed(11.0, flagFirst: true);
        float at45 = RiseSpeed(45.0, flagFirst: true);
        // The motor lifts it: 3 m/s through its 2 s timescale and the airplane's 5 s vertical friction.
        Assert.True(at11 > 1f, $"11 Hz: rising at {at11:0.000} m/s");
        Assert.True(Math.Abs(at11 - at45) < 0.01f * at11, $"11 Hz {at11:0.0000}, 45 Hz {at45:0.0000}");

        // The same flag set after the type stays and holds the motor down.
        Assert.True(Math.Abs(RiseSpeed(11.0, flagFirst: false)) < 0.01f);
        Assert.True(Math.Abs(RiseSpeed(45.0, flagFirst: false)) < 0.01f);
    }
}
