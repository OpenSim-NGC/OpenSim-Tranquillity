/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Wind.Plugins;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// ZephyrWind publishes each update as one immutable snapshot and samplers read it without a lock: a sampler is never
/// held up by an update (including the terrain scan after a terrain change), a WindSet made during an update is not
/// lost, a terrain change recomputes the terrain statistics, and the wind that comes out is the wind the plugin gave
/// before the snapshot change, cell by cell, at region sizes 256 and 1024.
/// </summary>
// Every test builds its own scene and plugin, reads no clock (the day fraction is supplied) and shares no process-wide
// state, so the class runs in parallel with the others.
public class ZephyrWindSnapshotTests : OpenSimTestCase
{
    private const float Water = 20f;
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    // ---- scenario shared by the fixtures and the tests ----

    private static TestScene NewScene(uint size)
    {
        TestScene scene = new SceneHelpers().SetupScene("Zephyr snapshot test region", UUID.Random(), 1000, 1000, size, size, new IniConfigSource());
        scene.RegionInfo.RegionSettings.WaterHeight = Water;
        return scene;
    }

    /// <summary>
    /// Terrain from integer arithmetic only, so every platform gets the same heights: ground from 12 m to about 83 m,
    /// part of it under the 20 m waterline, with steps at the cell borders so cells differ in range and skew.
    /// </summary>
    private static void HillyTerrain(Scene scene)
    {
        var map = scene.Heightmap;
        for (int x = 0; x < map.Width; x++)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int steps = (x * x * 3 + y * y * 5 + x * y * 7) % 64;
                int block = ((x / 16) + (y / 16)) % 5;
                map[x, y] = 12f + steps * 0.75f + block * 6f;
            }
        }
    }

    private static IConfig Settings()
    {
        IConfig windConfig = new IniConfigSource().AddConfig("Wind");
        windConfig.Set("avg_strength", "7");
        windConfig.Set("avg_direction", "40");
        windConfig.Set("var_strength", "3");
        windConfig.Set("var_direction", "25");
        windConfig.Set("rate_change_aloft", "2");
        windConfig.Set("rate_change_flutter", "5");
        return windConfig;
    }

    /// <summary>
    /// The plugin as the fixtures were made: hilly terrain, fixed settings, an update at one time of day, wind set in
    /// four cells (a fixed wind aloft, a fixed current under the water, a fixed wind aloft with ground turbulence, the
    /// default), and a second update at another time of day so the fixed cells are carried through an update.
    /// </summary>
    private static (ZephyrWind Plugin, TestScene Scene) FixtureScenario(uint size)
    {
        TestScene scene = NewScene(size);
        HillyTerrain(scene);
        float day = 0.3f;
        var z = new ZephyrWind { DayFractionSource = () => day };
        z.WindConfig(scene, Settings());

        Assert.True(z.WindUpdate(0));

        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(3 * 16 + 2, 4 * 16 + 2, 100f), new Vector3(3f, -2f, 0f));
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(5 * 16 + 2, 5 * 16 + 2, 5f), new Vector3(-1.5f, 0.5f, 0f));
        z.WindSet((int)(WindConstants.WindSpeedFixed | WindConstants.WindSpeedTurbulence), new Vector3(7 * 16 + 2, 2 * 16 + 2, 100f), new Vector3(0f, 6f, 0f));
        z.WindSet((int)WindConstants.WindSpeedDefault, new Vector3(9 * 16 + 2, 9 * 16 + 2, 100f), Vector3.Zero);

        day = 0.55f;
        Assert.True(z.WindUpdate(1));
        return (z, scene);
    }

    /// <summary>
    /// Everything a sampler can see, in a fixed order: for each cell (row by row) the current under the water, the
    /// surface wind and the wind aloft at the cell's south-west corner and the surface wind inside the cell, then the
    /// 16 x 16 array for the viewer.
    /// </summary>
    private static float[] Observe(ZephyrWind z)
    {
        var values = new List<float>();
        void Add(Vector3 v) { values.Add(v.X); values.Add(v.Y); }

        for (int cy = 0; cy < z.CellsY; cy++)
        {
            for (int cx = 0; cx < z.CellsX; cx++)
            {
                float x = cx * ZephyrWind.CellSize;
                float y = cy * ZephyrWind.CellSize;
                Add(z.WindSpeed(x, y, 0f));                 // under the water
                Add(z.WindSpeed(x, y, Water + 0.5f));       // surface
                Add(z.WindSpeed(x, y, 10000f));             // aloft
                Add(z.WindSpeed(x + 5.5f, y + 9.25f, Water + 0.5f));
            }
        }

        foreach (Vector2 v in z.WindLLClientArray())
        {
            values.Add(v.X);
            values.Add(v.Y);
        }

        return values.ToArray();
    }

    // The fixtures (Fixtures/zephyr-256.bin and zephyr-1024.bin) hold what Observe returned for FixtureScenario with the
    // plugin as it was before the snapshot change: the values as little-endian 32-bit floats, in Observe's order.
    private static float[] ReadFixture(string name)
    {
        Assembly assembly = typeof(ZephyrWindSnapshotTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource);
        using var reader = new BinaryReader(stream);
        var values = new float[stream.Length / sizeof(float)];
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadSingle();
        return values;
    }

    // ---- the output has not changed ----


    [Theory]
    [InlineData(256u, "zephyr-256.bin", 16)]
    [InlineData(1024u, "zephyr-1024.bin", 64)]
    public void EveryCellMatchesTheFixtureMadeBeforeTheSnapshotChange(uint size, string fixture, int cells)
    {
        var (z, _) = FixtureScenario(size);
        Assert.Equal(cells, z.CellsX);
        Assert.Equal(cells, z.CellsY);

        float[] expected = ReadFixture(fixture);
        float[] actual = Observe(z);

        // 4 samples of 2 values per cell, then the viewer's 256 entries of 2 values
        Assert.Equal(cells * cells * 4 * 2 + 16 * 16 * 2, expected.Length);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            // The sines and cosines come from the platform's maths library; a last-digit difference between
            // platforms is not a change of output, a different wind is.
            if (Math.Abs(expected[i] - actual[i]) > 1e-5f)
                Assert.Fail($"value {i} of {expected.Length}: expected {expected[i]:R}, got {actual[i]:R}");
        }
    }

    [Fact]
    public void TheFixtureScenarioIsNotFlat()
    {
        // Guards the fixtures against being made from a scenario where every cell is the same.
        var (z, _) = FixtureScenario(256);
        float[] values = Observe(z);
        Assert.True(values.Distinct().Count() > 200, "the fixture scenario should give many different values");
    }

    // ---- samplers are not held up by a writer ----

    /// <summary>
    /// A heightmap that holds the first read of a height until it is released, so a test can keep an update inside
    /// its terrain scan (the longest part of an update, which runs while the update holds the writers' lock).
    /// </summary>
    private sealed class StallingTerrain : ITerrainChannel
    {
        private readonly ITerrainChannel m_inner;
        private int m_entered;
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);

        public StallingTerrain(ITerrainChannel inner) => m_inner = inner;

        public int Width => m_inner.Width;
        public int Height => m_inner.Height;
        public int Altitude => m_inner.Altitude;

        public float this[int x, int y]
        {
            get
            {
                if (Interlocked.Exchange(ref m_entered, 1) == 0)
                {
                    Entered.Set();
                    Release.Wait(Cap);
                }
                return m_inner[x, y];
            }
            set => m_inner[x, y] = value;
        }

        public float GetHeight(float x, float y) => throw new NotSupportedException();
        public float GetHeightAtXYZ(float x, float y, float z) => throw new NotSupportedException();
        public TerrainData GetTerrainData() => throw new NotSupportedException();
        public float[] GetFloatsSerialised() => throw new NotSupportedException();
        public double[,] GetDoubles() => throw new NotSupportedException();
        public bool Tainted(int x, int y) => throw new NotSupportedException();
        public ITerrainChannel MakeCopy() => throw new NotSupportedException();
        public string SaveToXmlString() => throw new NotSupportedException();
        public void LoadFromXmlString(string data) => throw new NotSupportedException();
        public void Merge(ITerrainChannel newTerrain, Vector3 displacement, float radianRotation, Vector2 rotationDisplacement) => throw new NotSupportedException();
        public void MergeWithBounding(ITerrainChannel newTerrain, Vector3 displacement, float rotationDegrees, Vector2 boundingOrigin, Vector2 boundingSize) => throw new NotSupportedException();
        public int MaxHeight() => throw new NotSupportedException();
    }

    private sealed class Rig
    {
        public ZephyrWind Plugin;
        public TestScene Scene;
        public float Day = 0.3f;
        public StallingTerrain Stall;
        public Task<bool> Update;

        public Rig(uint size = 256)
        {
            Scene = NewScene(size);
            HillyTerrain(Scene);
            Plugin = new ZephyrWind { DayFractionSource = () => Day };
            Plugin.WindConfig(Scene, Settings());
            Assert.True(Plugin.WindUpdate(0));
        }

        /// <summary>Starts an update at another time of day and holds it inside its terrain scan.</summary>
        public void StartStalledUpdate()
        {
            Day = 0.8f;
            Stall = new StallingTerrain(Scene.Heightmap);
            Scene.Heightmap = Stall;     // a replaced heightmap is noticed by the next update, which then rescans it
            Update = Task.Run(() => Plugin.WindUpdate(1));
            Assert.True(Stall.Entered.Wait(Cap), "the update should have reached the terrain scan");
        }

        public void FinishUpdate()
        {
            Stall.Release.Set();
            Assert.True(Update.Wait(Cap), "the update should finish once released");
            Assert.True(Update.Result);
        }
    }

    [Fact]
    public void SamplersFinishWhileAnUpdateIsStuckInTheTerrainScan()
    {
        var rig = new Rig();
        float[] before = Observe(rig.Plugin);

        rig.StartStalledUpdate();
        try
        {
            // Everything a sampler calls, from other threads, while the update holds the writers' lock.
            Task<float[]> reader = Task.Run(() => Observe(rig.Plugin));
            Assert.True(reader.Wait(Cap), "a sampler must not wait for the update");
            Assert.Equal(before, reader.Result);        // it sees the last published wind, not a half-made one
        }
        finally
        {
            rig.FinishUpdate();
        }

        float[] after = Observe(rig.Plugin);
        Assert.NotEqual(before, after);                 // the update did publish its wind at the other time of day
    }

    [Fact]
    public void AWindSetMadeDuringAnUpdateIsNotLost()
    {
        var rig = new Rig();
        var cellOrigin = new Vector3(2 * 16 + 1, 2 * 16 + 1, 500f);

        rig.StartStalledUpdate();
        Task set;
        try
        {
            set = Task.Run(() => rig.Plugin.WindSet((int)WindConstants.WindSpeedFixed, cellOrigin, new Vector3(9f, 1f, 0f)));
            // The set takes its turn behind the update: still waiting after a while, not finished and not dropped.
            Assert.False(set.Wait(TimeSpan.FromMilliseconds(300)), "the set should wait for the update that is running");
        }
        finally
        {
            rig.FinishUpdate();
        }

        Assert.True(set.Wait(Cap), "the set should finish after the update");

        // Right after, the fixed wind is in the cell's wind aloft ...
        Vector3 aloft = rig.Plugin.WindSpeed(2 * 16, 2 * 16, 10000f);
        Assert.True(Vector3.Distance(new Vector3(9f, 1f, 0f), aloft) < 0.001f, $"got {aloft}");

        // ... and the next update keeps it.
        rig.Day = 0.1f;
        Assert.True(rig.Plugin.WindUpdate(2));
        aloft = rig.Plugin.WindSpeed(2 * 16, 2 * 16, 10000f);
        Assert.True(Vector3.Distance(new Vector3(9f, 1f, 0f), aloft) < 0.001f, $"got {aloft}");
    }

    [Fact]
    public void AWindSetBeforeAnUpdateIsKeptByThatUpdate()
    {
        var rig = new Rig();
        rig.Plugin.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(2 * 16 + 1, 2 * 16 + 1, 500f), new Vector3(9f, 1f, 0f));
        rig.Day = 0.9f;
        Assert.True(rig.Plugin.WindUpdate(1));
        Vector3 aloft = rig.Plugin.WindSpeed(2 * 16, 2 * 16, 10000f);
        Assert.True(Vector3.Distance(new Vector3(9f, 1f, 0f), aloft) < 0.001f, $"got {aloft}");
    }

    [Fact]
    public void ManySamplersAndWritersTogetherNeverSeeAnythingBroken()
    {
        var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var failures = new List<string>();

        Task[] readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                i++;
                Vector3 v = rig.Plugin.WindSpeed((i * 7) % 256, (i * 13) % 256, (i % 3) switch { 0 => 0f, 1 => 30f, _ => 900f });
                Vector2[] viewer = rig.Plugin.WindLLClientArray();
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || viewer.Length != 256 || viewer.Any(e => !float.IsFinite(e.X) || !float.IsFinite(e.Y)))
                {
                    lock (failures) failures.Add($"reader {r}: {v}");
                    return;
                }
            }
        })).ToArray();

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                rig.Day = (i % 10) / 10f;
                rig.Plugin.WindSet((int)WindConstants.WindSpeedFixed, new Vector3((i % 16) * 16 + 1, (i % 5) * 16 + 1, i % 2 == 0 ? 500f : 1f), new Vector3(i, -i, 0f));
                rig.Plugin.WindUpdate((uint)i);
            }
        });

        Assert.True(writer.Wait(Cap), "the writer should finish");
        stop.Cancel();
        Assert.True(Task.WaitAll(readers, Cap), "the samplers should finish");
        Assert.Empty(failures);
    }

    // ---- terrain statistics ----

    [Fact]
    public void ATerrainChangeRecomputesTheTerrainStatistics()
    {
        // Water at 20 m, ground 0 m under it. One bump of 10 m over the water in the cell at (32..47, 32..47): the
        // surface layer ends at 20 + 10 + 15 = 45 m, so a sample at 100 m is wind aloft.
        TestScene scene = NewScene(256);
        var map = scene.Heightmap;
        for (int x = 0; x < map.Width; x++)
            for (int y = 0; y < map.Height; y++)
                map[x, y] = 0f;
        map[35, 36] = 30f;

        var z = new ZephyrWind { DayFractionSource = () => 0.3f };
        z.WindConfig(scene, Settings());
        z.WindUpdate(0);

        Vector3 aloftBefore = z.WindSpeed(40, 40, 10000f);
        Vector3 groundBefore = z.WindSpeed(40, 40, Water + 0.5f);
        Assert.Equal(aloftBefore, z.WindSpeed(40, 40, 100f));
        Assert.NotEqual(aloftBefore, groundBefore);     // the bump turns the surface wind

        // Raise the bump to 220 m and taint the terrain: the layer now ends at 20 + 200 + 200 m, so 100 m is surface wind.
        map[35, 36] = 220f;
        scene.EventManager.TriggerTerrainTainted();
        z.WindUpdate(1);

        Vector3 groundAfter = z.WindSpeed(40, 40, Water + 0.5f);
        Assert.Equal(groundAfter, z.WindSpeed(40, 40, 100f));
        Assert.NotEqual(z.WindSpeed(40, 40, 10000f), groundAfter);
    }

    [Fact]
    public void WithoutATerrainChangeTheStatisticsAreNotRecomputed()
    {
        // The terrain is read by an update only when it changed: a second update with no taint reads no heights.
        TestScene scene = NewScene(256);
        var z = new ZephyrWind { DayFractionSource = () => 0.3f };
        z.WindConfig(scene, Settings());
        z.WindUpdate(0);

        var counting = new CountingTerrain(scene.Heightmap);
        scene.Heightmap = counting;
        z.WindUpdate(1);                    // a replaced heightmap is looked at once
        int afterReplace = counting.Reads;
        Assert.True(afterReplace > 0);

        z.WindUpdate(2);
        z.WindUpdate(3);
        Assert.Equal(afterReplace, counting.Reads);

        scene.EventManager.TriggerTerrainTainted();
        z.WindUpdate(4);
        Assert.True(counting.Reads > afterReplace);
    }

    private sealed class CountingTerrain : ITerrainChannel
    {
        private readonly ITerrainChannel m_inner;
        private int m_reads;
        public int Reads => Volatile.Read(ref m_reads);

        public CountingTerrain(ITerrainChannel inner) => m_inner = inner;

        public int Width => m_inner.Width;
        public int Height => m_inner.Height;
        public int Altitude => m_inner.Altitude;

        public float this[int x, int y]
        {
            get
            {
                Interlocked.Increment(ref m_reads);
                return m_inner[x, y];
            }
            set => m_inner[x, y] = value;
        }

        public float GetHeight(float x, float y) => throw new NotSupportedException();
        public float GetHeightAtXYZ(float x, float y, float z) => throw new NotSupportedException();
        public TerrainData GetTerrainData() => throw new NotSupportedException();
        public float[] GetFloatsSerialised() => throw new NotSupportedException();
        public double[,] GetDoubles() => throw new NotSupportedException();
        public bool Tainted(int x, int y) => throw new NotSupportedException();
        public ITerrainChannel MakeCopy() => throw new NotSupportedException();
        public string SaveToXmlString() => throw new NotSupportedException();
        public void LoadFromXmlString(string data) => throw new NotSupportedException();
        public void Merge(ITerrainChannel newTerrain, Vector3 displacement, float radianRotation, Vector2 rotationDisplacement) => throw new NotSupportedException();
        public void MergeWithBounding(ITerrainChannel newTerrain, Vector3 displacement, float rotationDegrees, Vector2 boundingOrigin, Vector2 boundingSize) => throw new NotSupportedException();
        public int MaxHeight() => throw new NotSupportedException();
    }
}
