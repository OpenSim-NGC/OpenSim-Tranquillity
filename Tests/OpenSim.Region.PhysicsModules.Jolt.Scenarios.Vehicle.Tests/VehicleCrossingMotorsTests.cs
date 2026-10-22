/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A vehicle with a held throttle keeps driving through a region border (the InWorldz simulator carries the motors' running state
// over a crossing; VehicleDynamics's Dynamics is serialised and merged). Core starts the crossing, has the engine write the
// motors' running state into the vehicle record it sends (PhysicsActor.CaptureVehicleRunningState), and the neighbour builds the
// vehicle from that record (PhysicsActor.SetVehicle). Every other way a saved vehicle is handed back leaves its motors off until
// a script sets them (VehicleRestoreRouteTests). The record's XML carries the state in extra elements that an older reader skips.
//
// Serial with the other native tests: the crossing runs on a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class VehicleCrossingMotorsTests
{
    private readonly ITestOutputHelper _out;
    public VehicleCrossingMotorsTests(ITestOutputHelper output) => _out = output;

    // Under InWorldz the motor is Halcyon's, which starts from 0.04 m/s and takes the first tenth of a second to beat the
    // ground's friction, so the vehicle is read 40 heartbeats in.
    private const int ReadAt = 40;

    private static VehicleRestoreRouteTests.Outcome Cross(VehicleRoute route, string behaviour)
        => VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_CAR, route, motor: new Vector3(8f, 0f, 0f), behaviour: behaviour, readAt: ReadAt);

    // The vehicle's motor is set at the start; the record is handed back 0.04 s later and the vehicle read 0.8 s after that.
    [Theory]
    [InlineData("InWorldz")]
    [InlineData("SecondLife")]
    public void A_vehicle_carried_over_a_border_goes_on_driving(string behaviour)
    {
        var crossed = Cross(VehicleRoute.Crossing, behaviour);
        var copied = Cross(VehicleRoute.Copy, behaviour);
        _out.WriteLine($"{behaviour}: crossed {crossed.EndVelocity} (moved {crossed.EndedAt - crossed.StartedAt}), copied {copied.EndVelocity}");
        // The record's frame is turned a little, so the speed along the ground is read, not one axis of it.
        float crossedSpeed = new Vector3(crossed.EndVelocity.X, crossed.EndVelocity.Y, 0f).Length();
        float copiedSpeed = new Vector3(copied.EndVelocity.X, copied.EndVelocity.Y, 0f).Length();
        Assert.True(crossedSpeed > 0.1f, $"the crossed vehicle drives on ({crossedSpeed})");
        Assert.True(copiedSpeed < crossedSpeed / 10f, $"a vehicle handed back by any other way waits for its script ({copiedSpeed})");
        Assert.True(crossed.Saved.m_linearMotorRunAge.HasValue);
        Assert.Equal(new Vector3(8f, 0f, 0f), crossed.Saved.m_linearMotorRunDirection);
        Assert.False(crossed.Saved.m_angularMotorRunAge.HasValue, "the angular motor was never set");
    }

    [Fact]
    public void A_vehicle_with_no_motor_set_carries_no_running_state()
    {
        var o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_CAR, VehicleRoute.Crossing);
        Assert.False(o.Saved.m_linearMotorRunAge.HasValue);
        Assert.False(o.Saved.m_angularMotorRunAge.HasValue);
    }

    [Fact]
    public void The_running_state_survives_the_object_XML_and_an_older_reader_skips_it()
    {
        var vehicle = new SOPVehicle();
        vehicle.ProcessTypeChange(SharedVehicle.TYPE_CAR);
        vehicle.vd.m_linearMotorRunAge = 1.25f;
        vehicle.vd.m_linearMotorRunDirection = new Vector3(8f, 0f, 0f);
        vehicle.vd.m_linearMotorRunTarget = new Vector3(3.5f, 0f, 0f);
        vehicle.vd.m_angularMotorRunAge = -0.05f;
        vehicle.vd.m_angularMotorRunDirection = new Vector3(0f, 0f, 1f);
        vehicle.vd.m_angularMotorRunTarget = new Vector3(0f, 0f, 0.4f);

        SOPVehicle back = SOPVehicle.FromXml2(vehicle.ToXml2());
        Assert.NotNull(back);
        Assert.Equal(1.25f, back.vd.m_linearMotorRunAge);
        Assert.Equal(new Vector3(8f, 0f, 0f), back.vd.m_linearMotorRunDirection);
        Assert.Equal(new Vector3(3.5f, 0f, 0f), back.vd.m_linearMotorRunTarget);
        Assert.Equal(-0.05f, back.vd.m_angularMotorRunAge);
        Assert.Equal(new Vector3(0f, 0f, 1f), back.vd.m_angularMotorRunDirection);
        Assert.Equal(new Vector3(0f, 0f, 0.4f), back.vd.m_angularMotorRunTarget);

        // Taken up once: after the engine has the record, the running state is gone from it.
        back.ClearRunningState();
        Assert.Null(back.vd.m_linearMotorRunAge);
        Assert.Null(back.vd.m_angularMotorRunTarget);
        Assert.DoesNotContain("LMRAGE", back.ToXml2());

        // A record with none writes none: a record saved by any other way reads as it always did.
        Assert.DoesNotContain("MRAGE", new SOPVehicle().ToXml2());
    }
}
