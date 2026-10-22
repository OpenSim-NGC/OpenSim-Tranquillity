/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Where an arriving avatar's physics body is put. ScenePresence.AddToPhysicalScene makes the body at login
/// (MakeRootAgent), at a teleport within the region (TeleportWithMomentum), on arrival from another region or across a
/// border (MakeRootAgent), when standing up from a seat or a vehicle (StandUp), after a failed crossing
/// (CrossToNewRegionFail) and when ScenePresence resets a non-finite position (HandleAgentUpdate). Each call hands the
/// position ScenePresence chose, then sets the momentum. The body goes where it is asked; the only correction is a lift
/// onto the terrain for a position below it. Each test makes the calls one of those paths makes, in its order.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarArrivalTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float PlatformTop = Ground + 3f;
    private const float Tolerance = 0.05f;

    private static readonly float StandHalf = JoltCharacter.StandHalfFor(Run.AvatarSize);

    private static JoltScene NewScene(float physicsRate, float[] heights = null)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        // Always set: 0 is one step per heartbeat, and the module's own default is 45 Hz.
        jolt.Set("PhysicsStepRate", physicsRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        if (heights == null)
        {
            heights = new float[Size * Size];
            Array.Fill(heights, Ground);
        }
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    // A fixed 4 x 4 x 0.5 m platform centred on (x, y), its top at PlatformTop, as a non-physical prim. Returns its
    // body, which an avatar standing on it reports as its ground body.
    private static uint Platform(JoltScene scene, float x = 128f, float y = 128f, uint localId = 500)
        => Body(scene.AddPrimShape("platform", PrimitiveBaseShape.CreateBox(), new Vector3(x, y, PlatformTop - 0.25f),
                                   new Vector3(4f, 4f, 0.5f), Quaternion.Identity, false, localId));

    private static uint Body(PhysicsActor prim) => Assert.IsType<JoltPrim>(prim).BodyHandle.Value;

    private static JoltCharacter Arrive(JoltScene scene, Vector3 at, bool flying, Vector3 momentum, uint localId = 99u)
    {
        // ScenePresence.AddToPhysicalScene, then the SetMomentum its caller makes.
        PhysicsActor pa = scene.AddAvatar(localId, "Test User", at, Run.AvatarSize, 0f, flying);
        pa.SetMomentum(momentum);
        return Assert.IsType<JoltCharacter>(pa);
    }

    private static void Step(JoltScene scene, int heartbeats)
    {
        for (int i = 0; i < heartbeats; i++)
            scene.Simulate(Heartbeat);
    }

    // Steps until the avatar is supported and at rest, or 10 s of heartbeats have passed.
    private static void StepUntilLanded(JoltScene scene, JoltCharacter av)
    {
        for (int i = 0; i < 110; i++)
        {
            scene.Simulate(Heartbeat);
            if (av.IsSupported && MathF.Abs(av.Velocity.Z) < 0.01f)
                return;
        }
    }

    private static void AssertStandsOnPlatform(JoltCharacter av, uint platform)
    {
        Assert.True(av.IsSupported, $"not supported at {av.Position}");
        Assert.True(av.GroundBody.IsValid && av.GroundBody.Value == platform, $"not standing on the platform at {av.Position}");
        Assert.InRange(av.Position.Z, PlatformTop + StandHalf - Tolerance, PlatformTop + StandHalf + Tolerance);
    }

    // ------------------------------------------------------------------ the rule

    [Fact]
    public void The_asked_height_is_kept_unless_it_is_below_the_terrain_and_is_never_lowered()
    {
        const float half = 0.95f;
        float seat = Ground + half;
        Assert.Equal(seat + 3f, JoltScene.ArrivalCentreZ(seat + 3f, Ground, half, 0f));            // on a platform
        Assert.Equal(seat + 200f, JoltScene.ArrivalCentreZ(seat + 200f, Ground, half, 0f));        // in the air
        Assert.Equal(seat, JoltScene.ArrivalCentreZ(seat, Ground, half, 0f));                      // standing on the terrain
        Assert.Equal(seat + 0.01f, JoltScene.ArrivalCentreZ(seat - 0.001f, Ground, half, 0f));     // a hair below: lifted
        Assert.Equal(seat + 0.01f, JoltScene.ArrivalCentreZ(Ground - 10f, Ground, half, 0f));      // underground: lifted
        Assert.Equal(seat + 0.2f + 0.01f, JoltScene.ArrivalCentreZ(Ground, Ground, half, 0.2f));   // feet offset counted
        Assert.Equal(seat + 0.01f, JoltScene.ArrivalCentreZ(float.NaN, Ground, half, 0f));         // no usable height: on the terrain
        Assert.Equal(7f, JoltScene.ArrivalCentreZ(7f, float.NaN, half, 0f));                       // no terrain: as asked

        var rnd = new Random(20261006);
        for (int i = 0; i < 2000; i++)
        {
            float asked = -50f + (float)rnd.NextDouble() * 400f;
            float terrain = (float)rnd.NextDouble() * 100f;
            float got = JoltScene.ArrivalCentreZ(asked, terrain, half, 0f);
            Assert.True(got >= asked, $"lowered {asked} to {got}");
            Assert.True(got >= terrain + half, $"left {asked} below the terrain {terrain}");
            Assert.True(got == asked || got == terrain + half + 0.01f, $"{asked} over {terrain} -> {got}");
        }
    }

    // ------------------------------------------------------------------ login (MakeRootAgent)

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Login_on_a_platform_stays_on_the_platform(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            uint platform = Platform(scene);
            // MakeRootAgent's landing ray found the platform: the centre is its top plus half the avatar's height.
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, PlatformTop + StandHalf), false, Vector3.Zero);
            Assert.Equal(PlatformTop + StandHalf, av.Position.Z);
            Step(scene, 55);   // 5 s
            AssertStandsOnPlatform(av, platform);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Login_on_open_ground_stands_where_it_stood_before(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            // MakeRootAgent: the ground height + 0.01 + half the avatar's height.
            float asked = Ground + 0.01f + Run.AvatarSize.Z * 0.5f;
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, asked), false, Vector3.Zero);
            Assert.Equal(Ground + StandHalf + 0.01f, av.Position.Z);   // the old terrain seat, to the bit
            Step(scene, 55);
            Assert.True(av.IsSupported);
            Assert.InRange(av.Position.Z, Ground + StandHalf - Tolerance, Ground + StandHalf + Tolerance);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ teleport within the region (TeleportWithMomentum)

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Teleport_onto_a_platform_stays_on_the_platform(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            uint platform = Platform(scene);
            JoltCharacter before = Arrive(scene, new Vector3(60f, 60f, Ground + StandHalf + 0.01f), false, Vector3.Zero);
            Step(scene, 11);
            // TeleportWithMomentum: RemoveFromPhysicalScene, AbsolutePosition = pos, AddToPhysicalScene, SetMomentum.
            scene.RemoveAvatar(before);
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, PlatformTop + StandHalf + 0.1f), false, Vector3.Zero);
            StepUntilLanded(scene, av);
            Step(scene, 33);
            AssertStandsOnPlatform(av, platform);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Teleport_to_mid_air_not_flying_falls_and_lands_on_what_is_below(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            uint platform = Platform(scene);
            float asked = PlatformTop + StandHalf + 10f;
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, asked), false, Vector3.Zero);
            Assert.Equal(asked, av.Position.Z);
            Step(scene, 2);
            Assert.True(av.Position.Z < asked - 0.01f, $"did not fall: {av.Position.Z}");
            StepUntilLanded(scene, av);
            AssertStandsOnPlatform(av, platform);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Teleport_to_mid_air_over_open_ground_lands_on_the_terrain(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + 30f), false, Vector3.Zero);
            StepUntilLanded(scene, av);
            Assert.True(av.IsSupported);
            Assert.InRange(av.Position.Z, Ground + StandHalf - Tolerance, Ground + StandHalf + Tolerance);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ arrival from another region, border crossing

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Arriving_flying_in_mid_air_stays_put_and_keeps_flying(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            var at = new Vector3(128f, 128f, Ground + 40f);
            JoltCharacter av = Arrive(scene, at, true, Vector3.Zero);
            Assert.Equal(at, av.Position);
            Assert.True(av.Flying);
            Step(scene, 33);   // 3 s
            Assert.True(av.Flying);
            Assert.True(Vector3.Distance(at, av.Position) < Tolerance, $"moved from {at} to {av.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Crossing_a_border_keeps_the_position_flying_state_and_velocity(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            // A crossing (TeleportFlags.Default): MakeRootAgent keeps Velocity and calls SetMomentum after the add.
            var vel = new Vector3(3f, 0f, 0f);
            var at = new Vector3(0.5f, 128f, Ground + 20f);
            JoltCharacter av = Arrive(scene, at, true, vel);
            Assert.Equal(at, av.Position);
            Assert.Equal(vel, av.Velocity);
            Assert.True(av.Flying);
            Step(scene, 11);   // 1 s
            Assert.True(av.Flying);
            // The arrival velocity carries the avatar on a little and fades to what its controls ask for (nothing here), as a
            // flight does when a key is let go; it is not held as a flying request.
            Assert.InRange(av.Position.X, at.X + 0.1f, at.X + 0.6f);
            Assert.True(MathF.Abs(av.Position.Z - at.Z) < Tolerance, $"height changed from {at.Z} to {av.Position.Z}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Walking_across_a_border_onto_a_platform_stays_on_it(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            uint platform = Platform(scene, 2f, 128f);
            JoltCharacter av = Arrive(scene, new Vector3(0.5f, 128f, PlatformTop + StandHalf), false, new Vector3(1f, 0f, 0f));
            Step(scene, 11);
            av.TargetVelocity = Vector3.Zero;   // the walk key let go
            StepUntilLanded(scene, av);
            Step(scene, 11);
            AssertStandsOnPlatform(av, platform);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ below the terrain

    [Theory]
    [InlineData(0f, false)]
    [InlineData(45f, false)]
    [InlineData(0f, true)]
    [InlineData(45f, true)]
    public void Arriving_below_the_terrain_is_lifted_onto_it(float physicsRate, bool flying)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground - 5f), flying, Vector3.Zero);
            Assert.Equal(Ground + StandHalf + 0.01f, av.Position.Z);
            Assert.Equal(flying, av.Flying);
            Step(scene, 22);
            Assert.InRange(av.Position.Z, Ground + StandHalf - Tolerance, Ground + StandHalf + Tolerance);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ standing up (StandUp)

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Standing_up_from_a_seat_on_a_platform_stays_on_the_platform(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            uint platform = Platform(scene);
            // A 0.5 m cube seat on the platform at (127, 128). StandUp (no stand offset): the seated position plus
            // <0.65, 0, height/2 + 0.1> turned to the seat's facing, which lands beside the seat, over the platform.
            var seat = new Vector3(127f, 128f, PlatformTop + 0.25f);
            uint seatBody = Body(scene.AddPrimShape("seat", PrimitiveBaseShape.CreateBox(), seat, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, false, 501));
            var sitAt = seat + new Vector3(0f, 0f, 0.25f + 0.5f);
            var standAt = sitAt + new Vector3(0.65f, 0f, Run.AvatarSize.Z * 0.5f + 0.1f);
            JoltCharacter av = Arrive(scene, standAt, false, Vector3.Zero);
            Assert.Equal(standAt.Z, av.Position.Z);
            StepUntilLanded(scene, av);
            Step(scene, 22);
            Assert.True(av.IsSupported);
            Assert.True(av.GroundBody.IsValid && (av.GroundBody.Value == platform || av.GroundBody.Value == seatBody), $"not on the platform or the seat at {av.Position}");
            Assert.True(av.Position.Z >= PlatformTop + StandHalf - Tolerance, $"below the platform: {av.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Standing_up_from_a_car_parked_on_a_ramp_stands_on_the_car_or_the_ramp_never_under_them(float physicsRate)
    {
        // A 15 degree ramp rising north from y 100; a parked car (a fixed 2 x 1 x 0.5 m box, nose north) rests on it.
        float tan = MathF.Tan(15f * MathF.PI / 180f);
        var heights = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                heights[y * Size + x] = Ground + MathF.Max(0f, y - 100f) * tan;
        JoltScene scene = NewScene(physicsRate, heights);
        try
        {
            float RampAt(float y) => Ground + MathF.Max(0f, y - 100f) * tan;
            Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f)
                             * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -15f * MathF.PI / 180f);
            var car = new Vector3(128f, 120f, RampAt(120f) + 0.25f / MathF.Cos(15f * MathF.PI / 180f));
            scene.AddPrimShape("car", PrimitiveBaseShape.CreateBox(), car, new Vector3(2f, 1f, 0.5f), pitch, false, 502);
            // StandUp: the seated position (on the car's top) plus 0.65 m forward and height/2 + 0.1 up.
            var sitAt = car + new Vector3(0f, 0f, 0.25f + 0.5f);
            var standAt = sitAt + new Vector3(0f, 0.65f, Run.AvatarSize.Z * 0.5f + 0.1f);
            JoltCharacter av = Arrive(scene, standAt, false, Vector3.Zero);
            Assert.Equal(standAt.Z, av.Position.Z);
            StepUntilLanded(scene, av);
            Step(scene, 22);
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True(av.Position.IsFinite());
            Assert.True(av.Position.Z >= RampAt(av.Position.Y) + StandHalf - Tolerance, $"under the ramp: {av.Position}");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ re-adds in place (CrossToNewRegionFail, HandleAgentUpdate)

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_re_add_in_place_on_a_platform_stays_on_the_platform(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            uint platform = Platform(scene);
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, PlatformTop + StandHalf), false, Vector3.Zero);
            Step(scene, 22);
            AssertStandsOnPlatform(av, platform);
            // A failed crossing or a non-finite reset: RemoveFromPhysicalScene, then AddToPhysicalScene where it is.
            Vector3 at = av.Position;
            scene.RemoveAvatar(av);
            av = Arrive(scene, at, false, Vector3.Zero);
            Assert.Equal(at, av.Position);
            Step(scene, 22);
            AssertStandsOnPlatform(av, platform);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ the harness scenarios

    // avatar-platform: arrives standing on the platform (top 28 m) and stays; avatar-platform-drop: arrives 3 m above it,
    // not flying, and lands on it. Both end standing on the top, 28 + the stand height.
    [Theory]
    [InlineData("avatar-platform", 11.0)]
    [InlineData("avatar-platform", 45.0)]
    [InlineData("avatar-platform-drop", 11.0)]
    [InlineData("avatar-platform-drop", 45.0)]
    public void The_platform_scenarios_end_standing_on_the_platform(string scenario, double rate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { RateHz = rate });
        float top = Ground + Harness.Harness.PlatformHeight;
        float standHalf = JoltCharacter.StandHalfFor(Run.SimulatorAvatarSize);   // the harness's avatar is the simulator's box
        Sample last = r.Samples[^1];
        Assert.InRange(last.Position.Z, top + standHalf - 0.01f, top + standHalf + 0.01f);
        Sample first = r.Samples.First(x => x.T >= 3.0);
        Assert.InRange(MathF.Abs(last.Position.Z - first.Position.Z), 0f, 0.01f);
        Assert.Equal(0, r.Summary.NonFinite);
        if (scenario == "avatar-platform")
            Assert.InRange(r.Summary.ZRange, 0f, 0.01f);
        else
            Assert.True(r.Samples[0].Position.Z > top + standHalf + 2f, $"started at {r.Samples[0].Position.Z}");
    }
}
