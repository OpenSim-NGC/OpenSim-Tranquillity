/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A non-physical mesh prim whose triangles are a single sheet (a wall or a floor with no thickness) is met from
/// its front face only unless [Jolt] TwoSidedMeshCollision is on. The wall here is one quad whose front faces -y.
/// A physical box fired at it and an avatar walked into it, from the front and from behind, at 45 Hz physics steps
/// and at one step per heartbeat (11 Hz). Each figure is a line "TWO-SIDED ..." in the test output.
/// Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class TwoSidedMeshTests
{
    private readonly ITestOutputHelper _out;
    public TwoSidedMeshTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float WallHeight = 3f;

    // One quad in the plane y = 0, 4 m wide and 3 m tall, centred on the prim. Wound so that its front faces -y.
    // The unused vertex pads the quad's bounds so the asset's quantised domain is not empty along y.
    private static (Vector3[] verts, int[] tris) Wall() => (
        new[]
        {
            new Vector3(-2f, 0f, -1.5f), new Vector3(2f, 0f, -1.5f), new Vector3(2f, 0f, 1.5f), new Vector3(-2f, 0f, 1.5f),
            new Vector3(-2f, -0.01f, -1.5f), new Vector3(-2f, 0.01f, -1.5f),
        },
        new[] { 0, 1, 2, 0, 2, 3 });

    private static JoltScene WallScene(bool stepEveryBeat, bool twoSided, out string kind)
    {
        var settings = new List<(string, string)> { ("PhysicsStepRate", stepEveryBeat ? "0" : "45") };
        if (twoSided)
            settings.Add(("TwoSidedMeshCollision", "true"));
        JoltScene scene = NewScene(Scratch(), settings.ToArray());
        AddMesh(scene, Asset(Wall(), Array.Empty<Box>(), true), WallHeight, false, (byte)PhysShapeType.prim, out kind);
        Step(scene, 1f);
        return scene;
    }

    // A 0.5 m physical box 3 m from the wall on the given side, fired at it at this speed; how far past the wall's
    // plane it ended after 4 s, measured along the direction of travel (negative = it stayed on its own side).
    private static float Fire(JoltScene scene, bool fromBehind, float speed)
    {
        float dir = fromBehind ? -1f : 1f;
        PhysicsActor box = scene.AddPrimShape("probe", PrimitiveBaseShape.CreateBox(),
            new Vector3(CentreX, CentreY - dir * 3f, Ground + 1.5f), new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
        Step(scene, Heartbeat);
        box.Velocity = new Vector3(0f, dir * speed, 0f);
        Step(scene, 4f);
        return (box.Position.Y - CentreY) * dir;
    }

    // An avatar set 4 m from the wall on the given side and walked into it at 3 m/s for 6 s; how far past the wall's
    // plane it ended along the direction of travel.
    private static float Walk(JoltScene scene, bool fromBehind)
    {
        float dir = fromBehind ? -1f : 1f;
        PhysicsActor av = scene.AddAvatar(_id++, "Test User", new Vector3(CentreX, CentreY - dir * 4f, Ground + Run.AvatarSize.Z * 0.5f), Run.AvatarSize, 0f, false);
        Step(scene, 1f);
        av.TargetVelocity = new Vector3(0f, dir * 3f, 0f);
        Step(scene, 6f);
        return (av.Position.Y - CentreY) * dir;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool everyBeat in new[] { false, true })
            foreach (float speed in new[] { 8f, 50f })
                yield return new object[] { everyBeat, speed };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Off__a_box_is_stopped_by_the_front_face_and_passes_the_back(bool everyBeat, float speed)
    {
        JoltScene front = WallScene(everyBeat, false, out string kind);
        float f;
        try { f = Fire(front, false, speed); } finally { front.Dispose(); }
        JoltScene back = WallScene(everyBeat, false, out _);
        float b;
        try { b = Fire(back, true, speed); } finally { back.Dispose(); }
        Say($"TWO-SIDED setting=off steps={(everyBeat ? "11Hz" : "45Hz")} box_speed={speed} cooked={kind} front_past={F(f)} back_past={F(b)}");
        Assert.True(f < 0f, $"front: {f}");
        Assert.True(b > 0f, $"back: {b}");   // today's behaviour: the back face is not met
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void On__a_box_is_stopped_from_either_side(bool everyBeat, float speed)
    {
        JoltScene front = WallScene(everyBeat, true, out string kind);
        float f;
        try { f = Fire(front, false, speed); } finally { front.Dispose(); }
        JoltScene back = WallScene(everyBeat, true, out _);
        float b;
        try { b = Fire(back, true, speed); } finally { back.Dispose(); }
        Say($"TWO-SIDED setting=on steps={(everyBeat ? "11Hz" : "45Hz")} box_speed={speed} cooked={kind} front_past={F(f)} back_past={F(b)}");
        Assert.True(f < 0f, $"front: {f}");
        Assert.True(b < 0f, $"back: {b}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Off__an_avatar_is_stopped_from_either_side_already(bool everyBeat)
    {
        JoltScene front = WallScene(everyBeat, false, out _);
        float f;
        try { f = Walk(front, false); } finally { front.Dispose(); }
        JoltScene back = WallScene(everyBeat, false, out _);
        float b;
        try { b = Walk(back, true); } finally { back.Dispose(); }
        Say($"TWO-SIDED setting=off steps={(everyBeat ? "11Hz" : "45Hz")} avatar front_past={F(f)} back_past={F(b)}");
        // An avatar's character contact collides with back faces whatever the setting says, so this does not change.
        Assert.True(f < 0f, $"front: {f}");
        Assert.True(b < 0f, $"back: {b}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void On__an_avatar_is_stopped_from_either_side(bool everyBeat)
    {
        JoltScene front = WallScene(everyBeat, true, out _);
        float f;
        try { f = Walk(front, false); } finally { front.Dispose(); }
        JoltScene back = WallScene(everyBeat, true, out _);
        float b;
        try { b = Walk(back, true); } finally { back.Dispose(); }
        Say($"TWO-SIDED setting=on steps={(everyBeat ? "11Hz" : "45Hz")} avatar front_past={F(f)} back_past={F(b)}");
        Assert.True(f < 0f, $"front: {f}");
        Assert.True(b < 0f, $"back: {b}");
    }

    [Fact]
    public void The_key_defaults_to_off_and_turns_the_setting_on()
    {
        var warnings = new List<string>();
        var none = JoltConfig.FromConfig(new Nini.Config.IniConfigSource(), warnings);
        Assert.False(none.TwoSidedMeshCollision);
        Assert.False(none.ToBackendSettings(256, 256).TwoSidedMeshes);

        var src = new Nini.Config.IniConfigSource();
        src.AddConfig("Jolt").Set("TwoSidedMeshCollision", "true");
        var on = JoltConfig.FromConfig(src, warnings);
        Assert.True(on.TwoSidedMeshCollision);
        Assert.True(on.ToBackendSettings(256, 256).TwoSidedMeshes);
        Assert.Empty(warnings);
    }

    // The front face is unchanged by the setting: a box dropped on a one-sided floor from above rests at the same height.
    [Fact]
    public void A_box_dropped_on_the_front_face_rests_at_the_same_height_with_the_setting_on_or_off()
    {
        float Rest(bool twoSided)
        {
            var settings = new List<(string, string)>();
            if (twoSided) settings.Add(("TwoSidedMeshCollision", "true"));
            JoltScene scene = NewScene(Scratch(), settings.ToArray());
            try
            {
                // The wall turned to lie flat: a floor 4 m by 3 m whose front faces up.
                var floor = (new[]
                {
                    new Vector3(-2f, -1.5f, 0f), new Vector3(2f, -1.5f, 0f), new Vector3(2f, 1.5f, 0f), new Vector3(-2f, 1.5f, 0f),
                    new Vector3(-2f, -1.5f, -0.01f), new Vector3(-2f, -1.5f, 0.01f),
                }, new[] { 0, 1, 2, 0, 2, 3 });
                scene.AddPrimShape("floor", MeshPbs(Asset(floor, Array.Empty<Box>(), true)), new Vector3(CentreX, CentreY, Ground + 1f),
                                   Vector3.One, Quaternion.Identity, false, false, (byte)PhysShapeType.prim, _id++);
                Step(scene, 1f);
                PhysicsActor box = scene.AddPrimShape("probe", PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY, Ground + 3f),
                    new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);
                Step(scene, 4f);
                return box.Position.Z - Ground;
            }
            finally { scene.Dispose(); }
        }
        float off = Rest(false), on = Rest(true);
        Say($"TWO-SIDED floor rest_z off={F(off)} on={F(on)}");
        Assert.InRange(off, 1.2f, 1.3f);   // the floor at 1 m and half the box
        Assert.Equal(off, on, 2);
    }

    // A ray shot at the wall from behind, with and without the back-face-cull flag: it never meets the back of a
    // one-sided wall, and with the setting on it meets the reversed copy either way.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void A_ray_from_behind_meets_the_wall_only_with_the_setting_on(bool twoSided, int expectedHits)
    {
        JoltScene scene = WallScene(false, twoSided, out _);
        try
        {
            var from = new Vector3(CentreX, CentreY + 3f, Ground + 1.5f);
            var culling = (List<ContactResult>)scene.RaycastWorld(from, -Vector3.UnitY, 6f, 1, RayFilterFlags.BackFaceCull | RayFilterFlags.PrimsNonPhantomAgents);
            var all = (List<ContactResult>)scene.RaycastWorld(from, -Vector3.UnitY, 6f, 1, RayFilterFlags.PrimsNonPhantomAgents);
            Say($"TWO-SIDED setting={(twoSided ? "on" : "off")} ray from behind: with back-face cull {culling.Count} hits, without {all.Count}");
            Assert.Equal(expectedHits, culling.Count);
            Assert.Equal(expectedHits, all.Count);
        }
        finally { scene.Dispose(); }
    }
}
