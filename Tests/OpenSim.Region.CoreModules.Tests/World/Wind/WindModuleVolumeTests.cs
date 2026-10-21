/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The wind every caller reads: WindModule.WindSpeed (integer position) and WindModule.WindSpeedAt (float position) are
/// the wind model's wind plus the weather volumes' wind. With weather off, or no volume at the point, both return exactly
/// what the wind model returns; both take no lock and allocate nothing.
/// </summary>
// MainConsole and the logger factory are not touched here, and each test owns its scene and module; the assembly runs
// its tests one at a time anyway (AssemblyInfo.cs).
public class WindModuleVolumeTests : OpenSimTestCase
{
    private static readonly UUID Owner = UUID.Random();

    private static TestScene NewScene()
    {
        TestScene scene = new SceneHelpers().SetupScene("Wind volume test region", UUID.Random(), 1000, 1000, 256, 256, new IniConfigSource());
        scene.RegionInfo.RegionSettings.WaterHeight = 0f;
        return scene;
    }

    private static WindModule ModuleOn(TestScene scene, string plugin, bool weather)
    {
        IniConfigSource config = new IniConfigSource();
        config.AddConfig("Wind").Set("wind_plugin", plugin);
        IConfig weatherConfig = config.AddConfig("Weather");
        weatherConfig.Set("enabled", weather ? "true" : "false");
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);
        return module;
    }

    private static IWindModelPlugin PluginOf(WindModule module)
        => (IWindModelPlugin)typeof(WindModule).GetField("m_activeWindPlugin", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module);

    private static void SetPlugin(WindModule module, IWindModelPlugin plugin)
        => typeof(WindModule).GetField("m_activeWindPlugin", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(module, plugin);

    private static WindVolumeSpec Sphere(Vector3 center, float radius, Vector3 velocity, float falloff = 1f)
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Sphere, Center = center, Radius = radius, FalloffStart = falloff,
            LifetimeSeconds = 3000f, Velocity = velocity,
        };

    /// <summary>A wind model whose wind is the same everywhere.</summary>
    private sealed class ConstantPlugin : IWindModelPlugin
    {
        // A wind model found by the plugin discovery in this assembly is built with its parameterless constructor and
        // never selected, so every fake has one.
        public Vector3 Wind;
        public ConstantPlugin() { }
        public ConstantPlugin(Vector3 wind) => Wind = wind;
        public string Version => "1";
        public string Name => "Constant";
        public string Description => "test";
        public void Initialise() { }
        public void Dispose() { }
        public void WindConfig(Scene scene, IConfig windConfig) { }
        public bool WindUpdate(uint frame) => false;
        public Vector3 WindSpeed(float x, float y, float z) => Wind;
        public Vector2[] WindLLClientArray() => new Vector2[256];
        public Dictionary<string, string> WindParams() => new();
        public void WindParamSet(string param, float value) { }
        public float WindParamGet(string param) => 0f;
    }

    public static IEnumerable<object[]> PluginNames()
    {
        yield return new object[] { "SimpleRandomWind" };
        yield return new object[] { "ConfigurableWind" };
        yield return new object[] { "ZephyrWind" };
    }

    private static IEnumerable<Vector3> Grid()
    {
        float[] xs = { -3.5f, -0.25f, 0f, 0.5f, 7.75f, 15.99f, 16f, 40.3f, 100f, 128.5f, 255f, 255.9f, 256f, 300.5f };
        float[] zs = { -5f, 0f, 0.5f, 21.5f, 60f, 300f };
        foreach (float x in xs)
            foreach (float y in xs)
                foreach (float z in zs)
                    yield return new Vector3(x, y, z);
    }

    // ---- weather off, or no volume: exactly the wind model's wind ----

    [Theory]
    [MemberData(nameof(PluginNames))]
    public void WithWeatherOffBothPathsEqualThePluginAlone(string plugin)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin, weather: false);
        IWindModelPlugin model = PluginOf(module);
        Assert.NotNull(model);

        int points = 0;
        foreach (Vector3 p in Grid())
        {
            Assert.Equal(model.WindSpeed(p), module.WindSpeedAt(p));

            int x = (int)p.X, y = (int)p.Y, z = (int)p.Z;
            Assert.Equal(model.WindSpeed((float)x, y, z), module.WindSpeed(x, y, z));
            points++;
        }
        Assert.True(points > 1000);
    }

    [Theory]
    [MemberData(nameof(PluginNames))]
    public void WithWeatherOnAndNoVolumeBothPathsEqualThePluginAlone(string plugin)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin, weather: true);
        IWindModelPlugin model = PluginOf(module);
        Assert.True(module.WindVolumes.Enabled);
        Assert.Equal(0, module.WindVolumes.Count);

        foreach (Vector3 p in Grid())
        {
            Assert.Equal(model.WindSpeed(p), module.WindSpeedAt(p));
            int x = (int)p.X, y = (int)p.Y, z = (int)p.Z;
            Assert.Equal(model.WindSpeed((float)x, y, z), module.WindSpeed(x, y, z));
        }
    }

    [Theory]
    [MemberData(nameof(PluginNames))]
    public void APointOutsideEveryVolumeGetsThePluginsWindAlone(string plugin)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin, weather: true);
        IWindModelPlugin model = PluginOf(module);
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(200, 200, 50), 5f, new Vector3(9, 0, 4))) > 0);

        Vector3 far = new Vector3(20.5f, 30.5f, 50f);
        Assert.Equal(model.WindSpeed(far), module.WindSpeedAt(far));
        Assert.Equal(model.WindSpeed(20f, 30f, 50f), module.WindSpeed(20, 30, 50));
    }

    // ---- volumes add ----

    [Fact]
    public void AVolumeAddsItsWindToThePluginsWindOnBothPathsAndItsZPassesThrough()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, new ConstantPlugin(new Vector3(1f, 2f, 0f)));
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100, 100, 50), 10f, new Vector3(5f, 0f, 3f))) > 0);

        Assert.Equal(new Vector3(6f, 2f, 3f), module.WindSpeed(100, 100, 50));
        Assert.Equal(new Vector3(6f, 2f, 3f), module.WindSpeedAt(new Vector3(100f, 100f, 50f)));
        // Outside the radius: the model alone, with no vertical part.
        Assert.Equal(new Vector3(1f, 2f, 0f), module.WindSpeedAt(new Vector3(100f, 100f, 70f)));
    }

    [Fact]
    public void TwoVolumesAddUpAndTheBaseIsNotPartOfTheVolumeClamp()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, new ConstantPlugin(new Vector3(40f, 0f, 0f)));
        Vector3 here = new Vector3(100, 100, 50);
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(here, 10f, new Vector3(30f, 0f, 0f))) > 0);
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(here, 10f, new Vector3(30f, 0f, 0f))) > 0);

        // The volumes' sum (60) is clamped to max_speed (50); the model's 40 is added on top.
        Assert.Equal(new Vector3(90f, 0f, 0f), module.WindSpeedAt(here));
    }

    [Fact]
    public void ARegionWithNoWindModelStillFeelsAVolume()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, null);
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100, 100, 50), 10f, new Vector3(2f, 3f, 4f))) > 0);

        Assert.Equal(new Vector3(2f, 3f, 4f), module.WindSpeed(100, 100, 50));
        Assert.Equal(new Vector3(2f, 3f, 4f), module.WindSpeedAt(new Vector3(100f, 100f, 50f)));
        Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(1f, 1f, 1f)));
    }

    [Theory]
    [MemberData(nameof(PluginNames))]
    public void WindSpeedAtAWholeNumberPositionEqualsWindSpeedThere(string plugin)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin, weather: true);
        // A falloff band crosses the points tested, so the volume's weight is between 0 and 1 there.
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100, 100, 50), 12f, new Vector3(8f, -3f, 2f), 0.2f)) > 0);

        int compared = 0;
        for (int x = 86; x <= 114; x += 3)
            for (int y = 90; y <= 112; y += 2)
                for (int z = 44; z <= 56; z += 3)
                {
                    Assert.Equal(module.WindSpeed(x, y, z), module.WindSpeedAt(new Vector3(x, y, z)));
                    compared++;
                }
        Assert.True(compared > 100);
    }

    [Fact]
    public void TheFloatPathFollowsAVolumesEdgeSmoothlyWhereTheIntegerPathSteps()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, new ConstantPlugin(Vector3.Zero));
        // Radius 5 with the falloff starting at 0.5: the wind falls from full to nothing between 2.5 m and 5 m.
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100, 100, 50), 5f, new Vector3(10f, 0f, 0f), 0.5f)) > 0);

        float previous = float.MaxValue;
        var distinctFloat = new HashSet<float>();
        for (float x = 102.5f; x <= 105f; x += 0.1f)
        {
            float wind = module.WindSpeedAt(new Vector3(x, 100f, 50f)).X;
            Assert.True(wind <= previous + 1e-4f, $"not monotone at {x}: {wind} after {previous}");
            previous = wind;
            distinctFloat.Add(MathF.Round(wind, 3));
        }

        var distinctInt = new HashSet<float>();
        for (float x = 102.5f; x <= 105f; x += 0.1f)
            distinctInt.Add(MathF.Round(module.WindSpeed((int)x, 100, 50).X, 3));

        Assert.True(distinctFloat.Count > 20, $"float path gave {distinctFloat.Count} values");
        Assert.True(distinctInt.Count <= 3, $"integer path gave {distinctInt.Count} values");
    }

    // ---- bad input ----

    [Theory]
    [MemberData(nameof(PluginNames))]
    public void ANonFinitePositionGivesZeroAndEveryResultIsFinite(string plugin)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin, weather: true);
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100, 100, 50), 10f, new Vector3(5f, 0f, 3f))) > 0);

        float[] bad = { float.NaN, float.PositiveInfinity, float.NegativeInfinity };
        foreach (float b in bad)
        {
            Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(b, 100f, 50f)));
            Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(100f, b, 50f)));
            Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(100f, 100f, b)));
        }

        foreach (Vector3 p in new[] { new Vector3(1e30f, 0f, 0f), new Vector3(-1e30f, -1e30f, 1e30f), new Vector3(float.MaxValue, float.MaxValue, float.MaxValue) })
        {
            Vector3 w = module.WindSpeedAt(p);
            Assert.True(float.IsFinite(w.X) && float.IsFinite(w.Y) && float.IsFinite(w.Z), $"{p} gave {w}");
        }
    }

    [Fact]
    public void ANonFiniteWindFromAModelGivesZeroOnBothPaths()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, new ConstantPlugin(new Vector3(float.NaN, 0f, 0f)));

        Assert.Equal(Vector3.Zero, module.WindSpeed(10, 10, 10));
        Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(10f, 10f, 10f)));
    }

    // ---- no lock, no allocation ----

    [Theory]
    [MemberData(nameof(PluginNames))]
    public void SamplingAllocatesNothing(string plugin)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin, weather: true);
        for (int i = 0; i < 3; i++)
            Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100 + i, 100, 50), 15f, new Vector3(4f, 1f, 2f), 0.3f)) > 0);

        // Warm up so that JIT work is not counted.
        float sink = 0f;
        for (int i = 0; i < 2000; i++)
        {
            sink += module.WindSpeedAt(new Vector3(100.5f + (i % 7), 99.5f, 50.5f)).X;
            sink += module.WindSpeed(100 + (i % 7), 100, 50).X;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200_000; i++)
        {
            sink += module.WindSpeedAt(new Vector3(100.5f + (i % 13), 99.5f, 50.5f)).X;
            sink += module.WindSpeed(100 + (i % 13), 100, 50).Y;
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.False(float.IsNaN(sink));
    }

    [Fact]
    public void SamplingDoesNotWaitForAWriterHoldingTheStoresLock()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, new ConstantPlugin(new Vector3(1f, 0f, 0f)));
        Assert.True(module.WindVolumes.Create(UUID.Random(), Owner, Sphere(new Vector3(100, 100, 50), 10f, new Vector3(5f, 0f, 0f))) > 0);

        object writeLock = typeof(WindVolumeStore).GetField("m_writeLock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module.WindVolumes);
        Vector3 seen = default, seenInt = default;
        lock (writeLock)
        {
            Task reader = Task.Run(() =>
            {
                seen = module.WindSpeedAt(new Vector3(100f, 100f, 50f));
                seenInt = module.WindSpeed(100, 100, 50);
            });
            Assert.True(reader.Wait(TimeSpan.FromSeconds(10)), "a sample waited for the store's write lock");
        }
        Assert.Equal(new Vector3(6f, 0f, 0f), seen);
        Assert.Equal(new Vector3(6f, 0f, 0f), seenInt);
    }

    [Fact]
    public void SamplingWhileAnotherThreadCreatesAndRemovesVolumesNeverSeesAHalfMadeResult()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);
        SetPlugin(module, new ConstantPlugin(new Vector3(1f, 2f, 0f)));
        Vector3 centre = new Vector3(100, 100, 50);

        using var stop = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            var handles = new Queue<int>();
            while (!stop.IsCancellationRequested)
            {
                int h = module.WindVolumes.Create(UUID.Random(), Owner, Sphere(centre, 10f, new Vector3(10f, 0f, 0f), 0.5f));
                if (h > 0) handles.Enqueue(h);
                if (handles.Count > 8)
                    module.WindVolumes.Remove(handles.Dequeue(), UUID.Zero);
                if (handles.Count % 5 == 0)
                    module.WindVolumes.Clear();
            }
        });

        long samples = 0;
        var clock = Stopwatch.StartNew();
        string failure = null;
        while (clock.ElapsedMilliseconds < 400 && failure == null)
        {
            for (int i = 0; i < 500; i++)
            {
                Vector3 w = module.WindSpeedAt(new Vector3(centre.X + (i % 9) - 4f, centre.Y, centre.Z));
                samples++;
                // The model gives (1, 2, 0); a volume adds only along X, at most 50 (the store's cap) in total.
                if (w.Y != 2f || w.Z != 0f || !(w.X >= 1f - 1e-4f && w.X <= 51f + 1e-4f) || !float.IsFinite(w.X))
                {
                    failure = $"torn result {w}";
                    break;
                }
            }
        }
        stop.Cancel();
        Assert.True(writer.Wait(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.True(samples > 1000, $"only {samples} samples");
    }

    // ---- an IWindModule that only knows the integer form ----

    private sealed class IntegerOnlyModule : IWindModule
    {
        public string Name => "IntegerOnly";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene) { }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
        public Vector3 WindSpeed(int x, int y, int z) => new Vector3(x, y, z);
        public void WindSet(int type, Vector3 loc, Vector3 speed) { }
        public void WindParamSet(string plugin, string param, float value) { }
        public float WindParamGet(string plugin, string param) => 0f;
        public string WindActiveModelPluginName => "";
    }

    [Fact]
    public void AModuleWithOnlyTheIntegerFormGetsTheFloatFormThroughTheDefaultThatFloorsThePosition()
    {
        IWindModule module = new IntegerOnlyModule();

        Assert.Equal(new Vector3(3f, 7f, -1f), module.WindSpeedAt(new Vector3(3.9f, 7.1f, -0.5f)));
        Assert.Equal(new Vector3(3f, 4f, 5f), module.WindSpeedAt(new Vector3(3f, 4f, 5f)));
        Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(float.NaN, 0f, 0f)));
        Assert.Equal(Vector3.Zero, module.WindSpeedAt(new Vector3(0f, float.PositiveInfinity, 0f)));
        Vector3 far = module.WindSpeedAt(new Vector3(1e30f, -1e30f, 0f));
        Assert.True(float.IsFinite(far.X) && float.IsFinite(far.Y));
    }

    [Fact]
    public void APluginWithOnlyTheFloatFormGetsThePositionFormThatFloors()
    {
        IWindModelPlugin plugin = new RecordingPlugin();

        Vector3 w = plugin.WindSpeed(new Vector3(3.9f, 7.1f, -0.5f));
        Assert.Equal(new Vector3(3f, 7f, -1f), w);
    }

    private sealed class RecordingPlugin : IWindModelPlugin
    {
        public RecordingPlugin() { }
        public string Version => "1";
        public string Name => "Recording";
        public string Description => "test";
        public void Initialise() { }
        public void Dispose() { }
        public void WindConfig(Scene scene, IConfig windConfig) { }
        public bool WindUpdate(uint frame) => false;
        public Vector3 WindSpeed(float x, float y, float z) => new Vector3(x, y, z);
        public Vector2[] WindLLClientArray() => new Vector2[256];
        public Dictionary<string, string> WindParams() => new();
        public void WindParamSet(string param, float value) { }
        public float WindParamGet(string param) => 0f;
    }

    // ---- an object's volumes end with it ----

    private static (TestScene Scene, WindModule Module) WeatherScene(bool weather = true)
    {
        TestScene scene = NewScene();
        return (scene, ModuleOn(scene, "ConfigurableWind", weather));
    }

    private static int MakeVolume(WindModule module, UUID creator)
        => module.WindVolumes.Create(creator, Owner, Sphere(new Vector3(100, 100, 50), 10f, new Vector3(5f, 0f, 0f)));

    [Fact]
    public void DeletingAnObjectRemovesItsVolumesAndLeavesOthers()
    {
        var (scene, module) = WeatherScene();
        SceneObjectGroup doomed = SceneHelpers.AddSceneObject(scene, "doomed", Owner);
        SceneObjectGroup kept = SceneHelpers.AddSceneObject(scene, "kept", Owner);
        Assert.True(MakeVolume(module, doomed.UUID) > 0);
        Assert.True(MakeVolume(module, doomed.UUID) > 0);
        int keptHandle = MakeVolume(module, kept.UUID);
        Assert.True(keptHandle > 0);
        Assert.Equal(3, module.WindVolumes.Count);

        scene.DeleteSceneObject(doomed, false);

        Assert.Empty(module.WindVolumes.List(doomed.UUID));
        Assert.Equal(new[] { keptHandle }, module.WindVolumes.List(UUID.Zero).Select(v => v.Id).ToArray());
        // The wind at the volumes' common centre is now the one surviving volume's alone, on top of the model's.
        Assert.Equal(PluginOf(module).WindSpeed(100f, 100f, 50f) + new Vector3(5f, 0f, 0f), module.WindSpeed(100, 100, 50));
    }


    // Take to inventory and return go through the scene's asynchronous deleter, which is cranked by hand here.
    [Theory]
    [InlineData(DeRezAction.Take)]
    [InlineData(DeRezAction.Delete)]
    public void TakingOrDeletingFromTheViewerRemovesTheObjectsVolumes(DeRezAction action)
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        TestScene scene = new SceneHelpers().SetupScene("Wind take region", UUID.Random(), 1000, 1000, 256, 256, config);
        SceneHelpers.SetupSceneModules(scene, config, new BasicInventoryAccessModule());
        UserAccount ua = UserAccountHelpers.CreateUserWithInventory(scene, TestHelpers.ParseTail(0x21));
        TestClient client = (TestClient)SceneHelpers.AddScenePresence(scene, ua.PrincipalID).ControllingClient;
        scene.SceneObjectGroupDeleter.Enabled = false;
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);

        SceneObjectGroup taken = SceneHelpers.AddSceneObject(scene, "taken", ua.PrincipalID);
        SceneObjectGroup other = SceneHelpers.AddSceneObject(scene, "other", ua.PrincipalID);
        Assert.True(MakeVolume(module, taken.UUID) > 0);
        int otherHandle = MakeVolume(module, other.UUID);

        scene.DeRezObjects(client, new List<uint> { taken.LocalId }, UUID.Zero, action, UUID.Zero);
        scene.SceneObjectGroupDeleter.InventoryDeQueueAndDelete();

        Assert.Empty(module.WindVolumes.List(taken.UUID));
        Assert.Equal(new[] { otherHandle }, module.WindVolumes.List(UUID.Zero).Select(v => v.Id).ToArray());
    }

    [Fact]
    public void AnAutoReturnWithNoAgentRemovesTheObjectsVolumes()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        TestScene scene = new SceneHelpers().SetupScene("Wind return region", UUID.Random(), 1000, 1000, 256, 256, config);
        SceneHelpers.SetupSceneModules(scene, config, new BasicInventoryAccessModule());
        UserAccount ua = UserAccountHelpers.CreateUserWithInventory(scene, TestHelpers.ParseTail(0x22));
        scene.SceneObjectGroupDeleter.Enabled = false;
        WindModule module = ModuleOn(scene, "ConfigurableWind", weather: true);

        SceneObjectGroup returned = SceneHelpers.AddSceneObject(scene, "returned", ua.PrincipalID);
        Assert.True(MakeVolume(module, returned.UUID) > 0);

        scene.DeRezObjects(null, new List<uint> { returned.LocalId }, UUID.Zero, DeRezAction.Return, UUID.Zero);
        scene.SceneObjectGroupDeleter.InventoryDeQueueAndDelete();

        Assert.Equal(0, module.WindVolumes.Count);
    }

    [Fact]
    public void AVolumeMadeFromAChildPrimGoesWhenItsLinksetGoes()
    {
        var (scene, module) = WeatherScene();
        SceneObjectGroup linkset = SceneHelpers.AddSceneObject(scene, 3, Owner, "link", 0x400);
        SceneObjectPart child = linkset.Parts.First(p => p != linkset.RootPart);

        // The script functions key a volume by SceneObjectPart.ParentGroup.UUID, the same for every prim of the linkset.
        Assert.Equal(linkset.UUID, child.ParentGroup.UUID);
        Assert.NotEqual(child.UUID, child.ParentGroup.UUID);
        Assert.True(MakeVolume(module, child.ParentGroup.UUID) > 0);

        scene.DeleteSceneObject(linkset, false);

        Assert.Equal(0, module.WindVolumes.Count);
    }

    [Fact]
    public void ADeleteThatKeepsTheScriptsStillRemovesTheVolumes()
    {
        // An attachment taken off, and a crossing that hands the object to the next region, delete without removing scripts.
        var (scene, module) = WeatherScene();
        SceneObjectGroup attachment = SceneHelpers.AddSceneObject(scene, "worn", Owner);
        Assert.True(MakeVolume(module, attachment.UUID) > 0);

        scene.DeleteSceneObject(attachment, true, false);

        Assert.Equal(0, module.WindVolumes.Count);
    }

    [Fact]
    public void TheRemovalEventWithNoVolumesOrWithWeatherOffChangesNothing()
    {
        var (scene, module) = WeatherScene(weather: false);
        SceneObjectGroup obj = SceneHelpers.AddSceneObject(scene, "plain", Owner);

        scene.DeleteSceneObject(obj, false);

        Assert.Equal(0, module.WindVolumes.Count);

        var (scene2, module2) = WeatherScene();
        SceneObjectGroup obj2 = SceneHelpers.AddSceneObject(scene2, "plain", Owner);
        scene2.DeleteSceneObject(obj2, false);
        Assert.Equal(0, module2.WindVolumes.Count);
    }

    [Fact]
    public void AfterTheRegionIsRemovedTheModuleNoLongerListensForRemovals()
    {
        var (scene, module) = WeatherScene();
        SceneObjectGroup obj = SceneHelpers.AddSceneObject(scene, "stays", Owner);
        Assert.True(MakeVolume(module, obj.UUID) > 0);

        module.RemoveRegion(scene);
        scene.DeleteSceneObject(obj, false);

        Assert.Equal(1, module.WindVolumes.Count);
    }

    [Fact]
    public void RemovingAScriptDoesNotRemoveTheObjectsVolumes()
    {
        // A volume belongs to the object, like a particle system: only the object leaving, the lifetime or the console ends it.
        var (scene, module) = WeatherScene();
        SceneObjectGroup obj = SceneHelpers.AddSceneObject(scene, "scripted", Owner);
        Assert.True(MakeVolume(module, obj.UUID) > 0);

        scene.EventManager.TriggerRemoveScript(obj.RootPart.LocalId, UUID.Random());
        scene.EventManager.TriggerScriptReset(obj.RootPart.LocalId, UUID.Random());

        Assert.Equal(1, module.WindVolumes.Count);
    }
}
