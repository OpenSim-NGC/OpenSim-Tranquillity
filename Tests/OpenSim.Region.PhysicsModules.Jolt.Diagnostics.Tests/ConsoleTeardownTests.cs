/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics.Tests;

/// <summary>
/// A test console command reads the scene's backend once, so one typed while the region is torn down on another thread
/// reports (misses, or "no backend") and never throws. Serial with the other native tests, and because it sets the
/// process-wide MainConsole.Instance for the handler's output.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ConsoleTeardownTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;
    private const float Ground = 25f;

    private static readonly System.Reflection.MethodInfo HandleTest =
        typeof(JoltDiagnostics).GetMethod("HandleJoltTestConsole", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt").Set("TestCommands", "true");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    // `jolt hilltest` is a test command that queries the backend once per probe point and needs no logged-in avatar or
    // region scene, so it runs on a bare JoltScene. It reads the backend once, so one typed while the region is torn
    // down on another thread reports misses or "no backend" and never throws.
    [Fact]
    public void Test_console_hilltest_typed_during_a_teardown_does_not_throw()
    {
        Assert.NotNull(HandleTest);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        ICommandConsole saved = MainConsole.Instance;
        MainConsole.Instance = new MockConsole();
        try
        {
            for (int cycle = 0; cycle < 40; cycle++)
            {
                JoltScene s = NewScene();
                object diagnostics = s.Diagnostics;
                Assert.IsType<JoltDiagnostics>(diagnostics);
                typeof(JoltDiagnostics).GetField("_hillSet", flags).SetValue(diagnostics, true);   // as `jolt terrainhill` leaves it
                typeof(JoltDiagnostics).GetField("_hillCx", flags).SetValue(diagnostics, Size / 2f);
                typeof(JoltDiagnostics).GetField("_hillCy", flags).SetValue(diagnostics, Size / 2f);
                typeof(JoltDiagnostics).GetField("_hillBase", flags).SetValue(diagnostics, 20f);
                typeof(JoltDiagnostics).GetField("_hillAmp", flags).SetValue(diagnostics, 40f);
                typeof(JoltDiagnostics).GetField("_hillR", flags).SetValue(diagnostics, 200f);
                s.Simulate(Heartbeat);
                Exception failure = null;
                using var started = new ManualResetEventSlim();
                var console = new Thread(() =>
                {
                    try
                    {
                        started.Set();
                        var limit = System.Diagnostics.Stopwatch.StartNew();
                        while (s.Backend != null && limit.Elapsed < TimeSpan.FromSeconds(20))
                            HandleTest.Invoke(diagnostics, new object[] { "jolt", new[] { "jolt", "hilltest" } });
                        HandleTest.Invoke(diagnostics, new object[] { "jolt", new[] { "jolt", "hilltest" } });   // and once after the backend is gone
                    }
                    catch (System.Reflection.TargetInvocationException e) { failure = e.InnerException; }
                    catch (Exception e) { failure = e; }
                });
                console.Start();
                started.Wait();
                Thread.Sleep(cycle % 5);
                s.Dispose();
                Assert.True(console.Join(TimeSpan.FromSeconds(30)), "the test console command did not finish after teardown");
                Assert.True(failure == null, $"cycle {cycle}: {failure}");
            }
        }
        finally { MainConsole.Instance = saved; }
    }
}
