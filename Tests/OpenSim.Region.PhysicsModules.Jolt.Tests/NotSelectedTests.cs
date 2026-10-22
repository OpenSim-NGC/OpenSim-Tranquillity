/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A region whose [Startup] physics is another engine gets nothing from the Jolt module: the native is not loaded or
/// hashed, no backend, job pool, thread or timer exists, nothing is logged, no console command is registered, no
/// [Jolt] key is read, and the backend, vehicle and binding assemblies are not even loaded. A missing native, an
/// unrecorded one or a platform with none makes no difference then; with Jolt selected the same cases still stop
/// startup with their one clear error. A Jolt region beside such a module steps exactly as it does alone.
///
/// Each test drives the module the plugin registration hands the host, through the calls the region module
/// controller makes (Initialise, AddRegion, RegionLoaded, RemoveRegion, Close). The scene passed is null: a module
/// that does nothing never reads it, and one that does throws.
///
/// Serial: it swaps LoggerProvider.LoggerFactory, MainConsole.Instance and JoltScene.NativeLoader, all
/// process-wide, and its Jolt region steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class NotSelectedTests
{
    // The engines NGC ships besides Jolt (OpenSimDefaults.ini [Startup] physics), and no physics key at all.
    private static readonly string[] EngineNames = { "ubODE", "BulletSim", "basicphysics", "POS", null };
    public static readonly TheoryData<string> OtherEngines = new(EngineNames);

    // Every [Jolt] key a region might carry, each set to a value the module would act on, so a module that read any
    // of them would change what it does.
    private static readonly (string Key, string Value)[] JoltKeys =
    {
        ("TestCommands", "true"), ("AllowUnrecordedNative", "true"), ("PhysicsStepRate", "45"), ("ThreadCount", "2"),
        ("JobPools", "2"), ("JobPoolFairHandoff", "true"), ("CapacityLogIntervalSeconds", "1"), ("MaxBodies", "1024"),
    };

    private static IConfigSource Config(string engine, bool joltSection = true, string joltValue = null)
    {
        var src = new IniConfigSource();
        IConfig startup = src.AddConfig("Startup");
        if (engine != null)
            startup.Set("physics", engine);
        startup.Set("meshing", engine == "Jolt" ? "Meshmerizer" : "ubODEMeshmerizer");
        startup.Set("JoltAutoDropTest", "true");
        if (joltSection)
        {
            IConfig jolt = src.AddConfig("Jolt");
            foreach (var (key, value) in JoltKeys)
                jolt.Set(key, joltValue ?? value);
        }
        return src;
    }

    private static Type RegisteredType(Assembly joltAssembly = null)
    {
        joltAssembly ??= typeof(JoltScene).Assembly;
        var registry = new PluginRegistry();
        var provider = (IPluginRegistryProvider)Activator.CreateInstance(joltAssembly.GetType("OpenSim.Region.PhysicsModules.Jolt.PluginRegistration"));
        provider.RegisterPlugins(registry);
        return Assert.Single(registry.GetPluginTypes("/OpenSim/RegionModules"));
    }

    private static INonSharedRegionModule RegisteredModule() => (INonSharedRegionModule)Activator.CreateInstance(RegisteredType());

    // A region's whole life, in the order RegionModulesControllerPlugin drives a non-shared module.
    private static void RunRegionLife(INonSharedRegionModule module, IConfigSource config)
    {
        Assert.Equal("Jolt", module.Name);
        Assert.Null(module.ReplaceableInterface);
        module.Initialise(config);
        module.AddRegion(null);
        module.RegionLoaded(null);
        module.RemoveRegion(null);
        module.Close();
    }

    // ------------------------------------------------------------------ what the module can be seen doing

    private sealed class Line
    {
        public string Category;
        public LogLevel Level;
        public string Message;
        public override string ToString() => $"{Level} {Category}: {Message}";
    }

    private sealed class CaptureFactory : ILoggerFactory
    {
        public readonly List<Line> Lines = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CaptureLogger : ILogger
        {
            private readonly CaptureFactory _f;
            private readonly string _category;
            public CaptureLogger(CaptureFactory f, string category) { _f = f; _category = category; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_f.Lines)
                    _f.Lines.Add(new Line { Category = _category, Level = logLevel, Message = formatter(state, exception) });
            }
        }
    }

    private sealed class TestConsole : ICommandConsole
    {
        public readonly Commands Real = new Commands();
#pragma warning disable 0067
        public event OnOutputDelegate OnOutput;
#pragma warning restore 0067
        public ICommands Commands => Real;
        public string DefaultPrompt { get; set; }
        public IScene ConsoleScene { get; set; }
        public void Prompt() { }
        public void RunCommand(string cmd) { }
        public string ReadLine(string p, bool isCommand, bool e) => "";
        public void WriteLine(string s) { }
        public void ReadConfig(IConfigSource configSource) { }
        public void SetCntrCHandler(OnCntrCCelegate handler) { }
        public void Output(string format) { }
        public void Output(string format, params object[] components) { }
        public string Prompt(string p) => "";
        public string Prompt(string p, string def) => "";
        public string Prompt(string p, List<char> excludedCharacters) => "";
        public string Prompt(string p, string def, List<char> excludedCharacters, bool echo = true) => "";
        public string Prompt(string prompt, string defaultresponse, List<string> options) => "";
    }

    /// <summary>Captures every log line and console command, and counts native loads, for as long as it lives.</summary>
    private sealed class Watch : IDisposable
    {
        public readonly CaptureFactory Log = new();
        public readonly TestConsole Console = new();
        public int NativeCalls;

        private readonly ILoggerFactory _savedFactory = LoggerProvider.LoggerFactory;
        private readonly ICommandConsole _savedConsole = MainConsole.Instance;
        private readonly Func<bool, JoltNativeInfo> _savedLoader = JoltScene.NativeLoader;

        public Watch(Func<bool, JoltNativeInfo> loader = null)
        {
            Func<bool, JoltNativeInfo> inner = loader ?? (_ => throw new InvalidOperationException("the native loader was called"));
            JoltScene.NativeLoader = allow => { Interlocked.Increment(ref NativeCalls); return inner(allow); };
            LoggerProvider.LoggerFactory = Log;
            MainConsole.Instance = Console;
        }

        public List<Line> Lines { get { lock (Log.Lines) return Log.Lines.ToList(); } }

        // Lines from the Jolt assemblies, or about Jolt from anywhere but the plugin registry the test itself fills.
        public List<Line> JoltLines => Lines.Where(l => l.Category != typeof(PluginRegistry).FullName &&
            (l.Category.Contains("Jolt", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("jolt", StringComparison.OrdinalIgnoreCase))).ToList();

        public List<string> JoltCommands => Console.Real.GetHelp(new[] { "help" })
            .Concat(Console.Real.GetHelp(new[] { "help", "Physics" }))
            .Where(h => h.Contains("jolt", StringComparison.OrdinalIgnoreCase)).ToList();

        public void Dispose()
        {
            JoltScene.NativeLoader = _savedLoader;
            LoggerProvider.LoggerFactory = _savedFactory;
            MainConsole.Instance = _savedConsole;
        }
    }

    private static void AssertNoLines(IEnumerable<Line> lines)
    {
        var list = lines.ToList();
        Assert.True(list.Count == 0, "logged: " + string.Join(" | ", list));
    }

    // ------------------------------------------------------------------ what the host makes

    [Fact]
    public void The_hosts_discovery_finds_one_Jolt_region_module()
    {
        // The region module controller's own discovery, over this test's output folder: it takes the registered
        // modules and every other class that implements a region module interface, and makes one of each per region.
        // A second Jolt module would be a second Jolt scene in every region on Jolt.
        string dir = Path.GetDirectoryName(typeof(JoltScene).Assembly.Location);
        using var discovery = new DotNetCorePluginsDiscovery(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        discovery.Initialize(dir);
        for (int call = 0; call < 2; call++)   // the first call reflects; later calls also take the registrations
        {
            var jolt = discovery.GetExtensionNodes("/OpenSim/RegionModules", typeof(IRegionModuleBase))
                .Where(n => n.Type.Assembly.GetName().Name.StartsWith("OpenSim.Region.PhysicsModules.Jolt", StringComparison.Ordinal))
                .ToList();
            var node = Assert.Single(jolt);
            Assert.Equal("OpenSim.Region.PhysicsModules.Jolt.JoltModule", node.TypeName);
            Assert.True(typeof(INonSharedRegionModule).IsAssignableFrom(node.Type));
        }
    }

    // ------------------------------------------------------------------ the native: loaded only for Jolt

    [Theory]
    [MemberData(nameof(OtherEngines))]
    public void A1_another_engine_never_loads_or_hashes_the_native(string engine)
    {
        using var w = new Watch();
        RunRegionLife(RegisteredModule(), Config(engine));
        Assert.Equal(0, w.NativeCalls);
    }

    [Fact]
    public void A1_with_Jolt_selected_the_same_module_does_load_the_native()
    {
        // The other half of the test above, which sees no native call with another engine: the loader the test
        // watches is the one the module calls.
        using var w = new Watch(JoltNative.EnsureLoaded);
        RegisteredModule().Initialise(Config("Jolt", joltSection: false));
        Assert.Equal(1, w.NativeCalls);
    }

    // ------------------------------------------------------------------ A2 and A6: in a load context of its own

    /// <summary>
    /// A load context holding only the Jolt module assembly. The backend, vehicle and binding assemblies are loaded
    /// into it on request and recorded; everything else comes from the test's own context.
    /// </summary>
    private sealed class JoltContext : AssemblyLoadContext
    {
        private static readonly string[] Own =
        {
            "OpenSim.Region.PhysicsModules.Jolt", "OpenSim.Region.PhysicsModules.Jolt.Backend",
            "OpenSim.Region.PhysicsModules.Jolt.Vehicles", "JoltPhysicsSharp",
        };

        private readonly string _dir = Path.GetDirectoryName(typeof(JoltScene).Assembly.Location);
        public readonly List<string> Requested = new();

        public JoltContext() : base("jolt-not-selected", isCollectible: true) { }

        public Assembly Jolt => LoadFromAssemblyName(new AssemblyName(Own[0]));

        protected override Assembly Load(AssemblyName name)
        {
            if (!Own.Contains(name.Name))
                return null;
            if (name.Name != Own[0])
                lock (Requested) Requested.Add(name.Name);
            return LoadFromAssemblyPath(Path.Combine(_dir, name.Name + ".dll"));
        }

        public string[] Loaded => Assemblies.Select(a => a.GetName().Name).OrderBy(n => n).ToArray();
    }

    private static object StaticField(Assembly a, string type, string field)
        => a.GetType(type, true).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    [Theory]
    [MemberData(nameof(OtherEngines))]
    public void A2_another_engine_starts_no_backend_job_pool_thread_or_timer(string engine)
    {
        var ctx = new JoltContext();
        try
        {
            using (new Watch())
                RunRegionLife((INonSharedRegionModule)Activator.CreateInstance(RegisteredType(ctx.Jolt)), Config(engine));

            // Every job pool and its worker threads belong to the backend (JoltPhysicsBackend.s_pools), and the native
            // is loaded by it (JoltNative.s_loaded): neither exists unless the backend assembly was loaded and used.
            Assembly backend = ctx.Assemblies.FirstOrDefault(a => a.GetName().Name == "OpenSim.Region.PhysicsModules.Jolt.Backend");
            if (backend != null)
            {
                Assert.Null(StaticField(backend, "OpenSim.Region.PhysicsModules.Jolt.Backend.JoltPhysicsBackend", "s_pools"));
                Assert.Null(StaticField(backend, "OpenSim.Region.PhysicsModules.Jolt.Backend.JoltNative", "s_loaded"));
            }
            // No region was initialised, so no step and no metrics interval (the module's only periodic work).
            var regions = (System.Collections.ICollection)StaticField(ctx.Jolt, "OpenSim.Region.PhysicsModules.Jolt.JoltMetrics", "s_regions");
            Assert.Empty(regions);
            // The module assembly itself starts no thread or timer anywhere: its only threads are the backend's pools.
            foreach (Type t in ctx.Jolt.GetTypes())
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    Assert.False(typeof(Thread).IsAssignableFrom(f.FieldType) || f.FieldType.Name.Contains("Timer"),
                        $"{t.Name}.{f.Name} is a {f.FieldType.Name}");
        }
        finally { ctx.Unload(); }
    }

    [Theory]
    [MemberData(nameof(OtherEngines))]
    public void A6_another_engine_loads_only_the_module_assembly(string engine)
    {
        var ctx = new JoltContext();
        try
        {
            using (new Watch())
                RunRegionLife((INonSharedRegionModule)Activator.CreateInstance(RegisteredType(ctx.Jolt)), Config(engine));
            Assert.Empty(ctx.Requested);
            Assert.Equal(new[] { "OpenSim.Region.PhysicsModules.Jolt" }, ctx.Loaded);
        }
        finally { ctx.Unload(); }
    }

    [Fact]
    public void A6_the_context_sees_the_backend_load_when_the_scene_is_made()
    {
        // The other half of A6: making the Jolt scene itself does pull the backend in, so the test above would see it.
        var ctx = new JoltContext();
        try
        {
            Activator.CreateInstance(ctx.Jolt.GetType("OpenSim.Region.PhysicsModules.Jolt.JoltScene", true));
            Assert.Contains("OpenSim.Region.PhysicsModules.Jolt.Backend", ctx.Requested);
        }
        finally { ctx.Unload(); }
    }

    // ------------------------------------------------------------------ A3: the log

    [Theory]
    [MemberData(nameof(OtherEngines))]
    public void A3_another_engine_logs_nothing_above_debug(string engine)
    {
        using var w = new Watch();
        RunRegionLife(RegisteredModule(), Config(engine));
        AssertNoLines(w.JoltLines.Where(l => l.Level > LogLevel.Debug));
    }

    [Fact]
    public void A3_the_watch_sees_the_modules_lines_when_Jolt_is_selected()
    {
        // The other half of A3: a module that logs through its usual logger is seen.
        using var w = new Watch(JoltNative.EnsureLoaded);
        RegisteredModule().Initialise(Config("Jolt", joltSection: false));
        Assert.Contains(w.JoltLines, l => l.Level == LogLevel.Information && l.Message.Contains("enabled (physics = Jolt)"));
    }

    // ------------------------------------------------------------------ A4: console commands

    [Theory]
    [MemberData(nameof(OtherEngines))]
    public void A4_another_engine_registers_no_jolt_command_even_with_the_test_commands_on(string engine)
    {
        // `jolt parity` registered once per process, behind a static flag; clear it so this region could register it.
        typeof(JoltScene).GetField("s_parityRegistered", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        using var w = new Watch();
        RunRegionLife(RegisteredModule(), Config(engine));
        Assert.Empty(w.JoltCommands);
        Assert.False(w.Console.Real.HasCommand("jolt parity"));
        Assert.False(w.Console.Real.HasCommand("jolt capacity"));
    }

    [Fact]
    public void A4_a_scene_of_another_engine_is_not_kept_by_the_module()
    {
        // `jolt parity` used to keep the last region it saw in a static, whatever its engine.
        FieldInfo kept = typeof(JoltScene).GetField("s_parityScene", BindingFlags.NonPublic | BindingFlags.Static);
        object before = kept?.GetValue(null);
        using var w = new Watch();
        var module = RegisteredModule();
        module.Initialise(Config("ubODE"));
        var scene = (OpenSim.Region.Framework.Scenes.Scene)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OpenSim.Region.Framework.Scenes.Scene));
        module.AddRegion(scene);
        module.RegionLoaded(scene);
        module.RemoveRegion(scene);
        if (kept != null)
            Assert.Same(before, kept.GetValue(null));
    }

    // ------------------------------------------------------------------ A5: [Jolt] settings

    /// <summary>A [Jolt] section that records every key read from it.</summary>
    public class RecordingConfig : DispatchProxy
    {
        public readonly List<string> Reads = new();
        public IConfigSource Source;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_Name": return "Jolt";
                case "set_Name": return null;
                case "get_ConfigSource": return Source;
                case "get_Alias": return null;
            }
            if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_"))
                return null;
            lock (Reads) Reads.Add(method.Name + (args.Length > 0 ? " " + args[0] : ""));
            if (method.ReturnType == typeof(void))
                return null;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    [Theory]
    [MemberData(nameof(OtherEngines))]
    public void A5_another_engine_reads_no_jolt_key(string engine)
    {
        IConfigSource src = Config(engine, joltSection: false);
        var jolt = DispatchProxy.Create<IConfig, RecordingConfig>();
        var recorder = (RecordingConfig)(object)jolt;
        recorder.Source = src;
        src.Configs.Add(jolt);
        Assert.Same(jolt, src.Configs["Jolt"]);

        using var w = new Watch();
        RunRegionLife(RegisteredModule(), src);
        Assert.Empty(recorder.Reads);
    }

    [Theory]
    [InlineData(null, false)]    // no [Jolt] section
    [InlineData("", true)]       // every key empty
    [InlineData("maybe", true)]  // every key invalid
    public void A5_another_engine_says_nothing_of_a_missing_or_invalid_jolt_section(string value, bool section)
    {
        foreach (string engine in EngineNames)
        {
            using var w = new Watch();
            RunRegionLife(RegisteredModule(), Config(engine, section, value));
            AssertNoLines(w.JoltLines);
        }
    }

    [Fact]
    public void A5_with_Jolt_selected_an_invalid_key_is_still_warned_about()
    {
        using var w = new Watch(JoltNative.EnsureLoaded);
        IConfigSource src = Config("Jolt", joltSection: false);
        src.AddConfig("Jolt").Set("PhysicsStepRate", "fast");
        RegisteredModule().Initialise(src);
        Assert.Contains(w.JoltLines, l => l.Level == LogLevel.Warning && l.Message.Contains("PhysicsStepRate"));
    }

    // ------------------------------------------------------------------ A7: a native the module cannot use

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), "jolt-not-selected-" + Guid.NewGuid().ToString("N"));

    // Each case stands in for the module's native loader: the real check against a folder with no file, a folder whose
    // file has the wrong hash, and a platform the module has no native for.
    public static readonly TheoryData<string> Unusable = new() { "missing", "hash", "linux-musl-x64", "freebsd-x64" };

    private static Func<bool, JoltNativeInfo> UnusableLoader(string kind, string dir)
    {
        string rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        switch (kind)
        {
            case "missing":
                return allow => JoltNative.Check(dir, rid, allow);
            case "hash":
                string file = JoltNative.PathFor(dir, rid);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "not a recorded build");
                return allow => JoltNative.Check(dir, rid, allow);
            default:
                return allow => JoltNative.Check(JoltNative.DefaultBaseDirectory(), kind, allow);
        }
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public void A7_another_engine_is_untouched_by_a_native_the_module_cannot_use(string kind)
    {
        string dir = NewTempDir();
        try
        {
            Func<bool, JoltNativeInfo> loader = UnusableLoader(kind, dir);
            Assert.Throws<JoltNativeException>(() => loader(false));   // the case is real

            foreach (string engine in EngineNames)
            {
                using var w = new Watch(loader);
                INonSharedRegionModule module = RegisteredModule();
                RunRegionLife(module, Config(engine));   // no exception
                Assert.Equal(0, w.NativeCalls);
                AssertNoLines(w.JoltLines.Where(l => l.Level > LogLevel.Debug));
                // The module made no scene, so the region's life did no Jolt work at all (no backend, no buffers).
                Assert.Null(module.GetType().GetField("m_scene", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(module));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public void A7_with_Jolt_selected_the_clear_error_stays(string kind)
    {
        string dir = NewTempDir();
        try
        {
            Func<bool, JoltNativeInfo> loader = UnusableLoader(kind, dir);
            using var w = new Watch(loader);
            IConfigSource src = Config("Jolt", joltSection: false);
            var e = Assert.Throws<JoltNativeException>(() => RegisteredModule().Initialise(src));
            Assert.Equal(1, w.NativeCalls);
            Assert.Contains(w.JoltLines, l => l.Level == LogLevel.Error && l.Message.Contains(e.Message));
            Assert.StartsWith("Jolt physics", e.Message);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ A8: a Jolt region beside another engine's

    private const float Heartbeat = 0.0909f;
    private const int Size = 256;
    private const float Ground = 25f;

    private static JoltScene NewJoltRegion()
    {
        var scene = new JoltScene();
        scene.Initialise(Config("Jolt", joltSection: false));
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Jolt Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    // A box dropped 5 m in a Jolt region: its height after each heartbeat. Between heartbeats it runs the given work.
    private static float[] DropBox(Action<int> between)
    {
        JoltScene s = NewJoltRegion();
        try
        {
            PhysicsActor box = s.AddPrimShape("test box", PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, Ground + 5f),
                new Vector3(1f, 1f, 1f), Quaternion.Identity, true, 1000);
            box.Density = 1000f;
            var z = new float[60];
            for (int i = 0; i < z.Length; i++)
            {
                between(i);
                s.Simulate(Heartbeat);
                z[i] = box.Position.Z;
            }
            return z;
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void A8_a_Jolt_region_steps_the_same_beside_a_region_of_another_engine()
    {
        // A first region spends the lines the module writes once per process (the native, the job pools).
        DropBox(_ => { });

        float[] alone;
        List<Line> aloneLines;
        using (var w = new Watch(JoltNative.EnsureLoaded))
        {
            alone = DropBox(_ => { });
            aloneLines = w.JoltLines;
        }
        Assert.InRange(alone[^1], Ground + 0.45f, Ground + 0.55f);   // it fell and came to rest on the ground

        float[] beside;
        List<Line> besideLines;
        using (var w = new Watch(JoltNative.EnsureLoaded))
        {
            INonSharedRegionModule other = null;
            beside = DropBox(i =>
            {
                // Another engine's region comes up, runs and goes down while the Jolt region steps.
                if (i == 5) { other = RegisteredModule(); other.Initialise(Config("ubODE")); other.AddRegion(null); }
                if (i == 10) other.RegionLoaded(null);
                if (i == 40) { other.RemoveRegion(null); other.Close(); }
            });
            besideLines = w.JoltLines;
            // The other region got nothing: the native was loaded for the Jolt region only, and no command exists.
            Assert.Equal(1, w.NativeCalls);
            Assert.Empty(w.JoltCommands);
        }

        Assert.Equal(alone, beside);
        // The scene's lines at the same levels: nothing came from the other region (the numbers in them may differ).
        // JoltMetrics is left out: its process summary is written on a 30 s clock, whichever region steps then.
        static IEnumerable<(LogLevel, string)> SceneLines(List<Line> lines)
            => lines.Where(l => l.Category != "OpenSim.Region.PhysicsModules.Jolt.JoltMetrics").Select(l => (l.Level, l.Category)).Order();
        Assert.True(SceneLines(aloneLines).SequenceEqual(SceneLines(besideLines)),
            "alone: " + string.Join(" | ", aloneLines) + " || beside: " + string.Join(" | ", besideLines));
    }
}
