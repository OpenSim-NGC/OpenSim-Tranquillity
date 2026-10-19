/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The scene's time dilation. The Second Life wiki page for llGetRegionTimeDilation says it "Returns a float that is the
/// current time dilation": 0.0 is full dilation and 1.0 is none, "a method the server uses to cope with simulator lag".
/// The Jolt scene reports how much of the time it was asked to simulate arrived in real time: the simulated seconds of the
/// last second's heartbeats over the real seconds they took, at most 1. ubODE reports the same through
/// PhysicsScene.TimeDilation; before this the Jolt scene left the base value, always 1, so a region that fell behind never
/// said so. The heartbeat clock is the test's, so no test waits. Serial: a scene steps a real backend on the shared pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class TimeDilationTests
{
    private const float Heartbeat = 0.0909f;

    private sealed class Clock
    {
        public long Ticks = 1000;
        public void Advance(double seconds) => Ticks += (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
    }

    private static JoltScene NewScene(Clock clock)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", "0");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[256 * 256];
        Array.Fill(heights, 25f);
        scene.InitialiseWithoutScene("Test Region", 256, 256, heights, 20f, Heartbeat);
        scene.HeartbeatClock = () => clock.Ticks;
        return scene;
    }

    // `count` heartbeats that each arrive `realSeconds` after the one before.
    private static void Beat(JoltScene s, Clock c, int count, double realSeconds)
    {
        for (int i = 0; i < count; i++)
        {
            c.Advance(realSeconds);
            s.Simulate(Heartbeat);
        }
    }

    [Fact]
    public void A_region_keeping_its_heartbeat_reports_no_dilation()
    {
        var c = new Clock();
        JoltScene s = NewScene(c);
        try
        {
            Beat(s, c, 40, Heartbeat);
            Assert.Equal(1f, s.TimeDilation);
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void A_region_whose_heartbeats_take_twice_as_long_reports_about_half()
    {
        var c = new Clock();
        JoltScene s = NewScene(c);
        try
        {
            Beat(s, c, 40, Heartbeat * 2);
            Assert.InRange(s.TimeDilation, 0.49f, 0.51f);
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void Dilation_comes_back_to_one_once_the_heartbeat_does()
    {
        var c = new Clock();
        JoltScene s = NewScene(c);
        try
        {
            Beat(s, c, 40, Heartbeat * 4);
            Assert.True(s.TimeDilation < 0.3f);
            Beat(s, c, 40, Heartbeat);
            Assert.Equal(1f, s.TimeDilation);
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void A_heartbeat_that_arrives_early_is_not_more_than_no_dilation()
    {
        var c = new Clock();
        JoltScene s = NewScene(c);
        try
        {
            Beat(s, c, 40, Heartbeat * 0.5);
            Assert.Equal(1f, s.TimeDilation);
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void One_late_heartbeat_among_steady_ones_dips_the_value_a_little_and_never_below_zero()
    {
        var c = new Clock();
        JoltScene s = NewScene(c);
        try
        {
            Beat(s, c, 20, Heartbeat);
            Beat(s, c, 1, 5.0);   // a five-second stall
            float after = s.TimeDilation;
            Assert.InRange(after, 0f, 0.5f);
            Beat(s, c, 11, Heartbeat);   // the window has moved past it
            Assert.Equal(1f, s.TimeDilation);
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void The_first_heartbeat_has_nothing_to_compare_with_and_reports_one()
    {
        var c = new Clock();
        JoltScene s = NewScene(c);
        try
        {
            Assert.Equal(1f, s.TimeDilation);
            Beat(s, c, 1, 3.0);
            Assert.Equal(1f, s.TimeDilation);
        }
        finally { s.Dispose(); }
    }
}
