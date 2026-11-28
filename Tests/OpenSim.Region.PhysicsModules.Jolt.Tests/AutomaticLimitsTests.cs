/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using OMVVector3 = OpenMetaverse.Vector3;
using OMVQuaternion = OpenMetaverse.Quaternion;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The automatic [Jolt] MaxBodies, MaxBodyPairs and MaxContactConstraints (each 0, the default). MaxBodies comes from
/// what the region can hold: its object capacity ([Region] MaxPrims, never less than 15000 per 256 x 256 m of area) and
/// its agent capacity ([Region] MaxAgents), plus the terrain, and is never below 65536. MaxBodyPairs and
/// MaxContactConstraints are 65536 and 16384. An explicit setting is used as it is. At 80 % of MaxBodies one
/// warning names the key to raise; at MaxBodies a new body is refused, counted and reported, and the scene goes on.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class AutomaticLimitsTests
{
    private static IniConfigSource Source(params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        var cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return src;
    }

    private static JoltConfig Defaults()
    {
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(new IniConfigSource(), warnings);
        Assert.Empty(warnings);
        return c;
    }

    // ------------------------------------------------------------------ the automatic values

    [Fact]
    public void A_standard_256m_region_keeps_the_earlier_limits()
    {
        // 15000 prims + 100 avatars (one query body each) + the terrain = 15101 bodies at most.
        Assert.Equal(15101, JoltConfig.AutoBodyBound(256, 256, 15000, 100));
        PhysicsBackendSettings s = Defaults().ToBackendSettings(256, 256, 0f, 15000, 100);
        Assert.Equal(65536, s.MaxBodies);              // 1.5 x 15101 is below the floor
        Assert.Equal(65536, s.MaxBodyPairs);
        Assert.Equal(16384, s.MaxContactConstraints);
    }

    [Fact]
    public void A_1024m_region_gets_limits_from_its_capacity()
    {
        // MaxPrims 15000 is below 15000 per 256 m of area: the bound is 16 x 15000 prims + 100 avatars + the terrain.
        Assert.Equal(240101, JoltConfig.AutoBodyBound(1024, 1024, 15000, 100));
        PhysicsBackendSettings s = Defaults().ToBackendSettings(1024, 1024, 0f, 15000, 100);
        Assert.Equal(360448, s.MaxBodies);             // was 1048576
        Assert.Equal(65536, s.MaxBodyPairs);
        Assert.Equal(16384, s.MaxContactConstraints);
    }

    [Fact]
    public void A_raised_object_capacity_raises_the_automatic_limits()
    {
        Assert.Equal(45101, JoltConfig.AutoBodyBound(256, 256, 45000, 100));
        PhysicsBackendSettings s = Defaults().ToBackendSettings(256, 256, 0f, 45000, 100);
        Assert.Equal(68608, s.MaxBodies);
        Assert.Equal(65536, s.MaxBodyPairs);
        Assert.Equal(16384, s.MaxContactConstraints);
    }

    [Theory]
    [InlineData(256u, 256u, 15000, 100)]
    [InlineData(256u, 256u, 1, 1)]
    [InlineData(512u, 512u, 15000, 40)]
    [InlineData(768u, 256u, 60000, 100)]
    [InlineData(1024u, 1024u, 15000, 100)]
    [InlineData(1024u, 1024u, 400000, 500)]
    [InlineData(4096u, 4096u, 15000, 100)]
    public void A_full_region_stays_under_the_warning_and_no_limit_is_below_its_floor(uint sx, uint sy, int prims, int agents)
    {
        int bound = JoltConfig.AutoBodyBound(sx, sy, prims, agents);
        Assert.True(bound >= Math.Max(prims, 15000L * sx * sy / (256 * 256)) + agents + 1);
        PhysicsBackendSettings s = Defaults().ToBackendSettings(sx, sy, 0f, prims, agents);
        Assert.True(s.MaxBodies * 4L > bound * 5L, $"MaxBodies {s.MaxBodies} leaves a full region ({bound}) at or above 80 %");
        Assert.True(s.MaxBodies >= 65536, $"MaxBodies {s.MaxBodies} is below 65536");
        Assert.Equal(65536, s.MaxBodyPairs);
        Assert.Equal(16384, s.MaxContactConstraints);
        Assert.True(s.MaxBodies <= 8_388_607);
    }

    [Fact]
    public void Zero_is_the_automatic_value_of_all_three_keys()
    {
        JoltConfig unset = Defaults();
        Assert.Equal(0, unset.MaxBodies);
        Assert.Equal(0, unset.MaxBodyPairs);
        Assert.Equal(0, unset.MaxContactConstraints);

        var warnings = new List<string>();
        JoltConfig zero = JoltConfig.FromConfig(Source(("MaxBodies", "0"), ("MaxBodyPairs", "0"), ("MaxContactConstraints", "0")), warnings);
        Assert.Empty(warnings);
        Assert.Equal(unset.ToBackendSettings(256, 256, 0f, 15000, 100), zero.ToBackendSettings(256, 256, 0f, 15000, 100));
    }

    // ------------------------------------------------------------------ explicit settings

    [Theory]
    [InlineData(256u, 15000)]
    [InlineData(1024u, 15000)]
    [InlineData(256u, 45000)]
    public void Explicit_settings_are_used_exactly(uint size, int prims)
    {
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(Source(("MaxBodies", "1000"), ("MaxBodyPairs", "2000"), ("MaxContactConstraints", "3000")), warnings);
        Assert.Empty(warnings);
        PhysicsBackendSettings s = c.ToBackendSettings(size, size, 0f, prims, 100);
        Assert.Equal(1000, s.MaxBodies);
        Assert.Equal(2000, s.MaxBodyPairs);
        Assert.Equal(3000, s.MaxContactConstraints);

        // ScaleCapsWithArea scales explicit pair and contact caps by area, as before.
        JoltConfig scaled = JoltConfig.FromConfig(Source(("MaxBodyPairs", "2000"), ("MaxContactConstraints", "3000"), ("ScaleCapsWithArea", "true")), warnings);
        PhysicsBackendSettings ss = scaled.ToBackendSettings(1024, 1024, 0f, prims, 100);
        Assert.Equal(2000 * 16, ss.MaxBodyPairs);
        Assert.Equal(3000 * 16, ss.MaxContactConstraints);
    }

    [Fact]
    public void ScaleCapsWithArea_without_explicit_caps_scales_the_earlier_defaults_as_before()
    {
        JoltConfig c = JoltConfig.FromConfig(Source(("ScaleCapsWithArea", "true")), null);
        PhysicsBackendSettings s = c.ToBackendSettings(1024, 1024, 0f, 15000, 100);
        Assert.Equal(65536 * 16, s.MaxBodyPairs);
        Assert.Equal(16384 * 16, s.MaxContactConstraints);
    }

    // ------------------------------------------------------------------ the 80 % warning

    [Fact]
    public void One_warning_at_80_percent_names_the_key_to_raise()
    {
        var watch = new BodyLimitWatch();
        Assert.Null(watch.Check("Region A", 79, 100));
        string line = watch.Check("Region A", 80, 100);
        Assert.NotNull(line);
        Assert.Contains("Region A", line);
        Assert.Contains("80 of 100", line);
        Assert.Contains("raise [Jolt] MaxBodies", line);
        Assert.Null(watch.Check("Region A", 85, 100));   // once, not every step
        Assert.Null(watch.Check("Region A", 100, 100));
        Assert.Null(watch.Check("Region A", 75, 100));   // still near the limit: not re-armed
        Assert.Null(watch.Check("Region A", 81, 100));
        Assert.Null(watch.Check("Region A", 60, 100));   // well below again (under 70 %): re-armed
        Assert.NotNull(watch.Check("Region A", 90, 100));
    }

    // ------------------------------------------------------------------ the scene

    private const float Heartbeat = 1f / 11f;
    private const float Ground = 25f;
    private uint _id = 100;

    private static JoltScene NewScene(uint size, int objectCapacity, int agentCapacity, params (string key, string value)[] jolt)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", ScratchFolders.Create());
        IConfig j = config.AddConfig("Jolt");
        foreach ((string key, string value) in jolt)
            j.Set(key, value);
        var scene = new JoltScene();
        scene.Initialise(config);
        float[] heights = new float[size * size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", size, size, heights, 20f, Heartbeat, objectCapacity, agentCapacity);
        var mesher = new OpenSim.Region.PhysicsModules.Meshing.Meshmerizer();
        mesher.Initialise(config);
        typeof(JoltScene).GetField("m_mesher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(scene, mesher);
        return scene;
    }

    private PhysicsActor Box(JoltScene scene, int i, bool physical = false, float height = 0.5f)
        => scene.AddPrimShape("box" + i, PrimitiveBaseShape.CreateBox(), new OMVVector3(20f + (i % 40) * 2f, 20f + (i / 40) * 2f, Ground + height),
            new OMVVector3(0.5f, 0.5f, 0.5f), OMVQuaternion.Identity, physical, _id++);

    private static void Step(JoltScene scene, int frames)
    {
        for (int i = 0; i < frames; i++)
            scene.Simulate(Heartbeat);
    }

    [Fact]
    public void The_scene_sizes_its_automatic_limits_from_the_regions_capacity()
    {
        JoltScene scene = NewScene(256, 45000, 100);
        try
        {
            PhysicsCapacityStats s = scene.Backend.GetCapacityStats();
            Assert.Equal(68608, s.MaxBodies);
            Assert.Equal(65536, s.MaxBodyPairs);
            Assert.Equal(16384, s.MaxContactConstraints);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_scene_of_a_standard_256m_region_has_the_earlier_limits()
    {
        JoltScene scene = NewScene(256, 15000, 100);
        try
        {
            PhysicsCapacityStats s = scene.Backend.GetCapacityStats();
            Assert.Equal(65536, s.MaxBodies);
            Assert.Equal(65536, s.MaxBodyPairs);
            Assert.Equal(16384, s.MaxContactConstraints);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_scene_honours_an_explicit_MaxBodies_and_warns_once_at_80_percent()
    {
        JoltScene scene = NewScene(256, 15000, 100, ("MaxBodies", "20"), ("MaxBodyPairs", "64"), ("MaxContactConstraints", "64"));
        try
        {
            Assert.Equal(20, scene.Backend.GetCapacityStats().MaxBodies);
            Step(scene, 1);
            Assert.Equal(1, scene.Backend.GetCapacityStats().LiveBodyCount);   // the terrain
            for (int i = 0; i < 14; i++)
                Box(scene, i);
            Step(scene, 2);
            Assert.Equal(15, scene.Backend.GetCapacityStats().LiveBodyCount);
            Assert.Equal(0, scene.BodyLimitWarnings);                         // 75 %

            Box(scene, 14);
            Step(scene, 2);
            Assert.Equal(16, scene.Backend.GetCapacityStats().LiveBodyCount); // 80 %
            Assert.Equal(1, scene.BodyLimitWarnings);
            Assert.Contains("16 of 20", scene.LastBodyLimitWarning);
            Assert.Contains("raise [Jolt] MaxBodies", scene.LastBodyLimitWarning);

            for (int i = 15; i < 18; i++)
                Box(scene, i);
            Step(scene, 5);
            Assert.Equal(19, scene.Backend.GetCapacityStats().LiveBodyCount);
            Assert.Equal(1, scene.BodyLimitWarnings);                         // still one
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void At_MaxBodies_a_new_prim_is_refused_cleanly_with_one_log_line()
    {
        JoltScene scene = NewScene(256, 15000, 100, ("MaxBodies", "20"), ("MaxBodyPairs", "64"), ("MaxContactConstraints", "64"),
            ("CapacityLogIntervalSeconds", "3600"));
        try
        {
            Step(scene, 1);
            PhysicsActor faller = Box(scene, 0, physical: true, height: 5f);
            for (int i = 1; i < 19; i++)
                Box(scene, i);
            Step(scene, 2);
            Assert.Equal(20, scene.Backend.GetCapacityStats().LiveBodyCount);   // full: terrain + 19 prims

            PhysicsActor refused = Box(scene, 19);
            Assert.NotNull(refused);                                             // the prim exists, with no physics
            Step(scene, 2);
            PhysicsCapacityStats s = scene.Backend.GetCapacityStats();
            Assert.Equal(20, s.LiveBodyCount);
            Assert.Equal(1, s.BodyCreateFailures);
            Assert.Equal(1, scene.CapacityWarnings);
            Assert.Contains("bodies refused (MaxBodies 20 reached x1", scene.LastCapacityWarning);
            Assert.Contains("raise [Jolt] MaxBodies", scene.LastCapacityWarning);

            // The scene goes on: a second refusal inside the quiet interval writes no second line, and the physical
            // prim that was already in the scene still falls and rests on the ground.
            Box(scene, 20);
            Step(scene, 60);
            Assert.Equal(1, scene.CapacityWarnings);
            Assert.Equal(20, scene.Backend.GetCapacityStats().LiveBodyCount);
            Assert.InRange(faller.Position.Z, Ground, Ground + 1f);
            Assert.True(float.IsFinite(faller.Position.X) && float.IsFinite(faller.Position.Y));
        }
        finally { scene.Dispose(); }
    }
}
