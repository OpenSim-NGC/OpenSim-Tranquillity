/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The `jolt` console read-outs read the scene's backend once, so one typed while the region is torn down on another
/// thread reports (misses, or "no backend") and never throws. Serial with the other native tests, and because it sets
/// the process-wide MainConsole.Instance for the handler's output.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ConsoleTeardownTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;
    private const float Ground = 25f;

    private static readonly System.Reflection.MethodInfo Handle =
        typeof(JoltScene).GetMethod("HandleJoltConsole", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    // Each read-out that queries the backend. sensortest and raytest need a logged-in ScenePresence, which a scene
    // with no region has none of, so they stop before their queries; avatarstatus queries for the physics avatar.
    private static readonly string[][] Commands =
    {
        new[] { "jolt", "terraintest" },
        new[] { "jolt", "probe", "100", "100" },
        new[] { "jolt", "heights", "100", "100" },
        new[] { "jolt", "avatarstatus" },
        new[] { "jolt", "sensortest" },
        new[] { "jolt", "raytest" },
        new[] { "jolt", "capacity" },
    };

    [Fact]
    public void Console_read_outs_typed_during_a_teardown_do_not_throw()
    {
        Assert.NotNull(Handle);
        ICommandConsole saved = MainConsole.Instance;
        MainConsole.Instance = new MockConsole();
        try
        {
            for (int cycle = 0; cycle < 40; cycle++)
            {
                JoltScene s = NewScene();
                s.AddAvatar(1002, "Test User", new Vector3(140f, 140f, Ground + 2f), new Vector3(0.45f, 0.6f, 1.9f), 0f, false);
                s.Simulate(Heartbeat);
                Exception failure = null;
                using var started = new ManualResetEventSlim();
                var console = new Thread(() =>
                {
                    try
                    {
                        started.Set();
                        var limit = System.Diagnostics.Stopwatch.StartNew();
                        int i = 0;
                        while (s.Backend != null && limit.Elapsed < TimeSpan.FromSeconds(20))
                            Handle.Invoke(s, new object[] { "jolt", Commands[i++ % Commands.Length] });
                        foreach (string[] c in Commands)   // and each once after the backend is gone
                            Handle.Invoke(s, new object[] { "jolt", c });
                    }
                    catch (System.Reflection.TargetInvocationException e) { failure = e.InnerException; }
                    catch (Exception e) { failure = e; }
                });
                console.Start();
                started.Wait();
                Thread.Sleep(cycle % 5);
                s.Dispose();
                Assert.True(console.Join(TimeSpan.FromSeconds(30)), "the console commands did not finish after teardown");
                Assert.True(failure == null, $"cycle {cycle}: {failure}");
            }
        }
        finally { MainConsole.Instance = saved; }
    }
}
