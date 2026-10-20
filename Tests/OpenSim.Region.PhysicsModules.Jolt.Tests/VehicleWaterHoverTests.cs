/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using OpenSim.Region.PhysicsModules.SharedBase;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A boat set down on water stays at the surface. A 4 x 1.5 x 0.5 m box is put on water 20 m up (the land under it is lower),
/// made a boat or a sailboat, and left for 10 s; its height is read at the end and must be within 5 cm of the height the
/// preset hovers at.
/// Under InWorldz behaviour the hover holds the hull there with no gravity (InWorldz switches gravity off while hover acts),
/// so the sailboat, whose preset has no buoyancy, does not sink to where a spring would carry its weight (g T^2 = 2.45 m for
/// its 0.5 s timescale). Under SecondLife behaviour the documented spring and buoyancy stand: the documented boat has full
/// buoyancy and holds; the sailboat preset gets buoyancy 1 under that behaviour, so it holds too. Serial with the other
/// native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VehicleWaterHoverTests
{
    private readonly ITestOutputHelper _out;
    public VehicleWaterHoverTests(ITestOutputHelper output) { _out = output; }

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Water = 20f, Land = 15f;
    private const float Tolerance = 0.05f;
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JoltScene NewScene(string behaviour, float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("VehicleBehaviour", behaviour);
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Land);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, Water, Heartbeat);
        scene.Simulate(Heartbeat);
        return scene;
    }

    // The box's height after `seconds` of heartbeats, and the lowest it was on the way.
    private (float end, float lowest) Float(string behaviour, float physicsRate, SharedVehicle type, bool reactToWind, float seconds)
    {
        JoltScene scene = NewScene(behaviour, physicsRate);
        try
        {
            var box = Assert.IsType<JoltPrim>(scene.AddPrimShape("boat", PrimitiveBaseShape.CreateBox(),
                new Vector3(100f, 128f, Water), new Vector3(4f, 1.5f, 0.5f), Quaternion.Identity, true, 701u));
            double now = 0;
            box.VehicleType = (int)type;
            box.VehicleControl.Clock = () => Epoch.AddTicks((long)(now * TimeSpan.TicksPerSecond));
            if (reactToWind)
                box.VehicleFlags((int)VehicleFlag.REACT_TO_WIND, false);
            float lowest = box.Position.Z;
            for (int i = 0; i < (int)Math.Ceiling(seconds / Heartbeat); i++)
            {
                now += Heartbeat;
                scene.Simulate(Heartbeat);
                lowest = MathF.Min(lowest, box.Position.Z);
            }
            return (box.Position.Z, lowest);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    // type, behaviour, the height its preset hovers at, with the wind flag as a script sets it
    [InlineData(SharedVehicle.TYPE_SAILBOAT, "InWorldz", Water + 0.0001f, true)]
    [InlineData(SharedVehicle.TYPE_SAILBOAT, "InWorldz", Water + 0.0001f, false)]
    [InlineData(SharedVehicle.TYPE_BOAT, "InWorldz", Water + 0.5f, false)]
    [InlineData(SharedVehicle.TYPE_BOAT, "SecondLife", Water, false)]
    [InlineData(SharedVehicle.TYPE_SAILBOAT, "SecondLife", Water + 0.0001f, false)]
    public void A_boat_set_down_on_water_stays_at_its_hover_height(SharedVehicle type, string behaviour, float expected, bool wind)
    {
        foreach (float rate in new[] { 0f, 45f })
        {
            (float end, float lowest) = Float(behaviour, rate, type, wind, 10f);
            _out.WriteLine($"WATER-HOVER {type} under {behaviour}, rate {rate:0}, wind flag {wind}: after 10 s at {end:0.000} (expected {expected:0.000}), lowest {lowest:0.000}");
            Assert.InRange(end, expected - Tolerance, expected + Tolerance);
            Assert.True(lowest > MathF.Min(Water, expected) - 0.25f, $"it sank to {lowest:0.000} on the way");
        }
    }
}
