/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Each Jolt region writes one INFO line at start-up with the [Jolt] settings it runs with (the vehicle behaviour and presets
/// in force, the avatar push keys, the physical complexity limit, the two rider-carrying settings and the physics step rate), so an operator can check a deployment from the region log.
/// Serial: it swaps LoggerProvider.LoggerFactory, which is process-wide, and its regions step a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class StartupSettingsLineTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;

    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly Capture _c;
            public Logger(Capture c) { _c = c; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_c.Lines)
                    _c.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // Starts a region with the given [Jolt] keys and returns the "settings:" lines the module logged for it.
    private static List<(LogLevel Level, string Message)> SettingsLines(string regionName, params (string Key, string Value)[] keys)
    {
        var src = new IniConfigSource();
        IConfig startup = src.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = src.AddConfig("Jolt");
        foreach ((string key, string value) in keys)
            jolt.Set(key, value);

        var capture = new Capture();
        ILoggerFactory saved = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try
        {
            var scene = new JoltScene();
            scene.Initialise(src);
            var heights = new float[Size * Size];
            Array.Fill(heights, 25f);
            scene.InitialiseWithoutScene(regionName, Size, Size, heights, 20f, Heartbeat);
            scene.Dispose();
        }
        finally { LoggerProvider.LoggerFactory = saved; }
        lock (capture.Lines)
            return capture.Lines.Where(l => l.Message.Contains("settings:", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void A_region_logs_one_line_with_the_default_settings()
    {
        var lines = SettingsLines("Example Region");
        var line = Assert.Single(lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("region 'Example Region'", line.Message);
        Assert.Contains("VehicleBehaviour=InWorldz", line.Message);
        Assert.Contains("VehiclePresets=Legacy", line.Message);
        Assert.Contains("AvatarPushMaxSpeed=" + JoltConfigDefault("AvatarPushMaxSpeed"), line.Message);
        Assert.Contains("AvatarObjectPushPerMomentum=" + JoltConfigDefault("AvatarObjectPushPerMomentum"), line.Message);
        Assert.Contains("AvatarObjectPushMaxForce=" + JoltConfigDefault("AvatarObjectPushMaxForce"), line.Message);
        Assert.Contains("MaxPhysicalComplexity=0,", line.Message);
        Assert.Contains("KeyframedMotionCarriesRiders=true,", line.Message);
        Assert.Contains("MovedPrimsCarryRiders=false,", line.Message);
        Assert.Contains("physics step rate=45 Hz", line.Message);
    }

    [Fact]
    public void The_line_shows_the_values_the_configuration_gave()
    {
        var lines = SettingsLines("Example Region",
            ("VehicleBehaviour", "InWorldz"), ("AvatarPushMaxSpeed", "100"), ("AvatarPushRecovery", "7.5"),
            ("AvatarObjectPushPerMomentum", "123"), ("AvatarObjectPushMaxForce", "2000"), ("PhysicsStepRate", "0"),
            ("MaxPhysicalComplexity", "256"), ("KeyframedMotionCarriesRiders", "false"), ("MovedPrimsCarryRiders", "true"));
        string m = Assert.Single(lines).Message;
        // InWorldz behaviour with no VehiclePresets key takes the legacy presets: the effective value is what the line shows.
        Assert.Contains("VehicleBehaviour=InWorldz", m);
        Assert.Contains("VehiclePresets=Legacy", m);
        Assert.Contains("AvatarPushMaxSpeed=100,", m);
        Assert.Contains("AvatarPushRecovery=7.5,", m);
        Assert.Contains("AvatarObjectPushPerMomentum=123,", m);
        Assert.Contains("AvatarObjectPushMaxForce=2000,", m);
        Assert.Contains("MaxPhysicalComplexity=256,", m);
        Assert.Contains("KeyframedMotionCarriesRiders=false,", m);
        Assert.Contains("MovedPrimsCarryRiders=true,", m);
        Assert.Contains("physics step rate=one step per heartbeat", m);
    }

    [Fact]
    public void An_explicit_presets_key_is_shown_over_the_behaviours_own()
    {
        string m = Assert.Single(SettingsLines("Example Region", ("VehicleBehaviour", "InWorldz"), ("VehiclePresets", "Documented"))).Message;
        Assert.Contains("VehicleBehaviour=InWorldz, VehiclePresets=Documented", m);
    }

    private static string JoltConfigDefault(string key)
    {
        var c = new JoltConfig();
        float v = key switch
        {
            "AvatarPushMaxSpeed" => c.AvatarPushMaxSpeed,
            "AvatarObjectPushPerMomentum" => c.AvatarObjectPushPerMomentum,
            _ => c.AvatarObjectPushMaxForce,
        };
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
