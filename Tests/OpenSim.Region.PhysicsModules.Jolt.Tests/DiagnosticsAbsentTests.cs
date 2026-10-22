/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] TestCommands = true asks the module for the console test commands, which are in the opt-in diagnostics
/// assembly. This project does not reference that assembly, so none is beside the module here: the module starts
/// clean, registers only the operator commands and logs one clear line. With the key off it does not look for the
/// assembly and logs nothing. The assembly present is tested in OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests.
///
/// Serial: it swaps LoggerProvider.LoggerFactory and MainConsole.Instance and the loader's once-per-process log
/// guard, all process-wide, and its scenes step a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class DiagnosticsAbsentTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;

    private sealed class CaptureFactory : ILoggerFactory
    {
        public readonly List<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CaptureLogger : ILogger
        {
            private readonly CaptureFactory _f;
            public CaptureLogger(CaptureFactory f) { _f = f; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_f.Lines)
                    _f.Lines.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    /// <summary>Captures the log for as long as it lives, forgets which diagnostics lines were logged, and points the module at a folder.</summary>
    private sealed class Watch : IDisposable
    {
        public readonly CaptureFactory Log = new();
        private readonly ILoggerFactory _savedFactory = LoggerProvider.LoggerFactory;
        private readonly Func<string> _savedFolder = JoltScene.DiagnosticsFolder;
        private readonly ICommandConsole _savedConsole = MainConsole.Instance;

        public Watch(string folder = null)
        {
            LoggerProvider.LoggerFactory = Log;
            MainConsole.Instance = new MockConsole();
            JoltDiagnosticsLoader.ForgetLogged();
            if (folder != null)
                JoltScene.DiagnosticsFolder = () => folder;
        }

        public List<string> Diagnostics()
        {
            lock (Log.Lines)
                return Log.Lines.Where(l => l.Contains("OpenSim.Region.PhysicsModules.Jolt.Diagnostics")).ToList();
        }

        public void Dispose()
        {
            JoltScene.DiagnosticsFolder = _savedFolder;
            LoggerProvider.LoggerFactory = _savedFactory;
            MainConsole.Instance = _savedConsole;
            JoltDiagnosticsLoader.ForgetLogged();
        }
    }

    private static JoltScene NewScene(bool testCommands, string name = "Test Region")
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt").Set("TestCommands", testCommands ? "true" : "false");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, 25f);
        scene.InitialiseWithoutScene(name, Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static List<string> JoltCommandHelp(Commands commands)
        => commands.GetHelp(new[] { "help", "Physics" }).Where(l => l.StartsWith("jolt", StringComparison.Ordinal)).ToList();

    [Fact]
    public void With_the_key_on_and_no_assembly_the_module_starts_registers_the_operator_commands_and_logs_one_line()
    {
        string empty = Path.Combine(Path.GetTempPath(), "jolt-no-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        using var watch = new Watch(empty);

        JoltScene scene = NewScene(testCommands: true);
        try
        {
            Assert.Null(scene.Diagnostics);
            var commands = new Commands();
            scene.AttachConsole(commands);

            var help = JoltCommandHelp(commands);
            Assert.Single(help);   // the one `jolt` command with the operator subcommands, and no test command of its own
            Assert.StartsWith(JoltScene.ReadOnlyUsage + " - ", help[0], StringComparison.Ordinal);

            var lines = watch.Diagnostics();
            Assert.Single(lines);
            Assert.StartsWith("Warning: ", lines[0]);
            Assert.Contains("TestCommands is true", lines[0]);
            Assert.Contains("not in the folder of the Jolt module", lines[0]);
            Assert.Contains("TestCommands = false", lines[0]);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void With_the_key_on_and_the_module_folder_as_it_is_here_the_assembly_is_not_found()
    {
        // This test project does not reference the diagnostics assembly, so the real folder of the module has none.
        using var watch = new Watch();

        JoltScene scene = NewScene(testCommands: true);
        try
        {
            Assert.Null(scene.Diagnostics);
            Assert.Single(watch.Diagnostics());
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_missing_assembly_line_is_logged_once_however_many_regions_start()
    {
        string empty = Path.Combine(Path.GetTempPath(), "jolt-no-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        using var watch = new Watch(empty);

        JoltScene a = NewScene(testCommands: true, "Region A");
        JoltScene b = NewScene(testCommands: true, "Region B");
        try
        {
            Assert.Single(watch.Diagnostics());
        }
        finally { a.Dispose(); b.Dispose(); }
    }

    [Fact]
    public void With_the_key_off_the_assembly_is_not_looked_for_and_nothing_is_logged()
    {
        bool looked = false;
        using var watch = new Watch();
        JoltScene.DiagnosticsFolder = () => { looked = true; return null; };

        JoltScene scene = NewScene(testCommands: false);
        try
        {
            Assert.False(looked, "the module asked where the diagnostics assembly is with TestCommands off");
            Assert.Null(scene.Diagnostics);
            var commands = new Commands();
            scene.AttachConsole(commands);
            Assert.Single(JoltCommandHelp(commands));
            Assert.Empty(watch.Diagnostics());
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_unusable_assembly_file_is_logged_and_the_module_runs_on_without_the_test_commands()
    {
        string folder = Path.Combine(Path.GetTempPath(), "jolt-bad-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, JoltDiagnosticsLoader.AssemblyFileName + ".dll"), "this is not an assembly");
        using var watch = new Watch(folder);

        JoltScene scene = NewScene(testCommands: true);
        try
        {
            Assert.Null(scene.Diagnostics);
            var lines = watch.Diagnostics();
            Assert.Single(lines);
            Assert.Contains("could not be used", lines[0]);
            scene.Simulate(Heartbeat);   // the region still steps
        }
        finally { scene.Dispose(); }
    }
}
