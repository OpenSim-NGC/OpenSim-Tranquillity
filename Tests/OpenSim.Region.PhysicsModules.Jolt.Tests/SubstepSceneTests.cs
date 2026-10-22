/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using SVector3 = System.Numerics.Vector3;
using Vehicle = OpenSim.Region.PhysicsModules.Jolt.Vehicles.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] PhysicsStepRate in a real scene: the rate is taken or refused against the heartbeat, a heartbeat runs the
/// accumulator's steps, collisions are reported once per heartbeat with nothing lost and no false end, a script
/// impulse is the same at any rate, and a scene stepping at a rate tears down cleanly. Serial with the other native
/// tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SubstepSceneTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;
    private const float Ground = 25f;

    private static JoltScene NewScene(float physicsRate, float heartbeat = Heartbeat)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        // Always set: 0 is one step per heartbeat, and the module's own default is 45 Hz.
        jolt.Set("PhysicsStepRate", physicsRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, heartbeat);
        return scene;
    }

    // A scene whose [Jolt] PhysicsStepRate is the given text, or not set at all when it is null.
    private static JoltScene NewSceneWithRateText(string rate, float heartbeat = Heartbeat)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        if (rate != null)
            jolt.Set("PhysicsStepRate", rate);
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, heartbeat);
        return scene;
    }

    // The lines the module logs while `body` runs (LoggerProvider.LoggerFactory is process-wide; this class is serial).
    private static List<(LogLevel Level, string Message)> Logged(Action body)
    {
        var capture = new CaptureFactory();
        ILoggerFactory saved = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try { body(); }
        finally { LoggerProvider.LoggerFactory = saved; }
        lock (capture.Lines)
            return capture.Lines.ToList();
    }

    private sealed class CaptureFactory : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
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
                    _f.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static PhysicsActor AddBox(JoltScene scene, uint localId, Vector3 position, bool physical)
    {
        PhysicsActor pa = scene.AddPrimShape("test box", PrimitiveBaseShape.CreateBox(), position, new Vector3(1f, 1f, 1f), Quaternion.Identity, physical, localId);
        pa.Density = 1000f;
        return pa;
    }

    [Fact]
    public void A_rate_below_the_heartbeat_leaves_one_step_per_heartbeat()
    {
        JoltScene s = NewScene(5f);
        try { Assert.False(s.Substepping); }
        finally { s.Dispose(); }

        JoltScene t = NewScene(45f);
        try
        {
            Assert.True(t.Substepping);
            Assert.Equal(1f / 45f, t.Substeps.StepSeconds);
        }
        finally { t.Dispose(); }
    }

    [Fact]
    public void A_scene_with_no_rate_set_steps_at_45_Hz()
    {
        JoltScene s = NewSceneWithRateText(null);
        try
        {
            Assert.True(s.Substepping);
            Assert.Equal(1f / 45f, s.Substeps.StepSeconds);
            AddBox(s, 1000, new Vector3(128f, 128f, Ground + 3f), physical: true);
            for (int i = 0; i < 100; i++)
                s.Simulate(Heartbeat);
            double expected = 100 * Heartbeat * 45.0;
            Assert.InRange((double)s.Substeps.Steps, expected - 1.0, expected + 1.0);
        }
        finally { s.Dispose(); }
    }

    // 0 gives one step per heartbeat.
    [Theory]
    [InlineData("0")]
    public void A_scene_with_rate_0_runs_one_step_per_heartbeat(string rate)
    {
        JoltScene s = NewSceneWithRateText(rate);
        try
        {
            Assert.False(s.Substepping);
            Assert.Null(s.Substeps);
        }
        finally { s.Dispose(); }
    }

    // An invalid value gives the default rate, with one warning naming the value and the rate used.
    [Theory]
    [InlineData("fast")]
    [InlineData("-1")]
    [InlineData("1001")]
    public void A_scene_with_an_invalid_rate_steps_at_the_default_rate(string rate)
    {
        JoltScene s = null;
        var lines = Logged(() => s = NewSceneWithRateText(rate));
        try
        {
            Assert.True(s.Substepping);
            Assert.Equal(1f / 45f, s.Substeps.StepSeconds);
            var warning = Assert.Single(lines, l => l.Level == LogLevel.Warning && l.Message.Contains("PhysicsStepRate"));
            Assert.Contains($"PhysicsStepRate = \"{rate}\" is invalid", warning.Message);
            Assert.Contains("using the default 45.", warning.Message);
        }
        finally { s?.Dispose(); }
    }

    // A heartbeat faster than the default rate: with no rate set, one step per heartbeat and one information line, no
    // warning; with the rate set by the operator, the warning as before.
    [Theory]
    [InlineData(null)]
    [InlineData("45")]
    public void A_heartbeat_faster_than_the_rate_warns_only_about_a_rate_the_operator_set(string rate)
    {
        JoltScene s = null;
        var lines = Logged(() => s = NewSceneWithRateText(rate, heartbeat: 1f / 90f));
        try
        {
            Assert.False(s.Substepping);
            var about = lines.Where(l => l.Message.Contains("PhysicsStepRate")).ToList();
            var line = Assert.Single(about);
            if (rate == null)
            {
                Assert.Equal(LogLevel.Information, line.Level);
                Assert.Contains("PhysicsStepRate is not set, and its default of 45 Hz is below the heartbeat's own rate (90 Hz", line.Message);
            }
            else
            {
                Assert.Equal(LogLevel.Warning, line.Level);
                Assert.Contains("PhysicsStepRate = 45 is below the heartbeat's own rate (90 Hz", line.Message);
            }
        }
        finally { s?.Dispose(); }
    }

    [Fact]
    public void A_thousand_heartbeats_run_the_right_number_of_steps()
    {
        JoltScene s = NewScene(45f);
        try
        {
            AddBox(s, 1000, new Vector3(128f, 128f, Ground + 3f), physical: true);
            for (int i = 0; i < 1000; i++)
                s.Simulate(Heartbeat);
            double expected = 1000 * Heartbeat * 45.0;
            Assert.InRange((double)s.Substeps.Steps, expected - 1.0, expected + 1.0);
            Assert.Equal(0, s.Substeps.CappedFrames);
        }
        finally { s.Dispose(); }
    }

    // ---------------------------------------------------------------- collisions

    private sealed class Recorder
    {
        public readonly List<uint[]> Updates = new();   // one entry per update delivered, the colliders it lists
        public Recorder(PhysicsActor pa)
        {
            pa.OnCollisionUpdate += e => Updates.Add(((CollisionEventUpdate)e).m_objCollisionList.Keys.OrderBy(k => k).ToArray());
        }
    }

    private static ContactReport Report(uint a, uint b, ContactPhase phase)
        => new ContactReport { ChildUserDataA = a, ChildUserDataB = b, UserDataA = a, UserDataB = b, Phase = phase, Normal = new SVector3(0f, 0f, 1f) };

    // OpenSim's SceneObjectPart turns consecutive updates into events: a collider that appears is collision_start,
    // one still listed is collision, and an empty update after a listed one is collision_end.
    private static List<string> Events(List<uint[]> updates)
    {
        var events = new List<string>();
        uint[] last = Array.Empty<uint>();
        foreach (uint[] u in updates)
        {
            foreach (uint id in u)
                events.Add(last.Contains(id) ? $"collision {id}" : $"start {id}");
            foreach (uint id in last)
                if (!u.Contains(id))
                    events.Add($"end {id}");
            last = u;
        }
        return events;
    }

    [Fact]
    public void A_contact_that_begins_and_ends_inside_one_heartbeat_reports_start_then_end()
    {
        JoltScene s = NewScene(45f);
        try
        {
            PhysicsActor prim = AddBox(s, 1000, new Vector3(128f, 128f, Ground + 10f), physical: false);
            prim.SubscribeEvents(50);
            var rec = new Recorder(prim);

            // Heartbeat 1: four steps; the box touches the terrain (0) in step 2 and leaves in step 3.
            s.DispatchContacts(new[] { Report(1000, 0, ContactPhase.Begin), Report(1000, 0, ContactPhase.End) }, 2, false, mergeSubsteps: true);
            Assert.Equal(1f, prim.CollisionScore);
            s.DispatchContacts(Array.Empty<ContactReport>(), 0, false, mergeSubsteps: true);   // heartbeat 2: nothing
            s.DispatchContacts(Array.Empty<ContactReport>(), 0, false, mergeSubsteps: true);   // heartbeat 3: nothing

            Assert.Equal(new[] { "start 0", "end 0" }, Events(rec.Updates));
            Assert.Equal(2, rec.Updates.Count);   // and no update at all once it has ended
            Assert.Equal(0f, prim.CollisionScore);
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void A_contact_that_spans_heartbeats_reports_start_ongoing_and_end_once_each_in_order()
    {
        JoltScene s = NewScene(45f);
        try
        {
            PhysicsActor prim = AddBox(s, 1000, new Vector3(128f, 128f, Ground + 10f), physical: false);
            prim.SubscribeEvents(50);
            var rec = new Recorder(prim);

            // Begins in the last step of heartbeat 1, persists through every step of heartbeat 2, ends in the second
            // step of heartbeat 3.
            s.DispatchContacts(new[] { Report(1000, 0, ContactPhase.Begin) }, 1, false, mergeSubsteps: true);
            var persist = new[] { Report(1000, 0, ContactPhase.Persist), Report(0, 1000, ContactPhase.Persist), Report(1000, 0, ContactPhase.Persist), Report(1000, 0, ContactPhase.Persist) };
            s.DispatchContacts(persist, persist.Length, false, mergeSubsteps: true);
            Assert.Equal(1f, prim.CollisionScore);   // one touching pair, however many steps reported it
            s.DispatchContacts(new[] { Report(1000, 0, ContactPhase.Persist), Report(1000, 0, ContactPhase.End) }, 2, false, mergeSubsteps: true);
            s.DispatchContacts(Array.Empty<ContactReport>(), 0, false, mergeSubsteps: true);

            Assert.Equal(new[] { "start 0", "collision 0", "collision 0", "end 0" }, Events(rec.Updates));
        }
        finally { s.Dispose(); }
    }

    [Fact]
    public void With_one_step_per_heartbeat_every_report_still_counts()
    {
        JoltScene s = NewScene(0f);
        try
        {
            PhysicsActor prim = AddBox(s, 1000, new Vector3(128f, 128f, Ground + 10f), physical: false);
            prim.SubscribeEvents(50);
            var persist = new[] { Report(1000, 0, ContactPhase.Persist), Report(1000, 0, ContactPhase.Persist) };
            s.DispatchContacts(persist, persist.Length, false, mergeSubsteps: false);
            Assert.Equal(2f, prim.CollisionScore);   // the single-step path is unchanged
        }
        finally { s.Dispose(); }
    }

    // A real box dropped on the terrain gives the same event sequence with one step per heartbeat and at 45 Hz.
    [Fact]
    public void A_dropped_box_gives_the_same_collision_events_at_any_rate()
    {
        List<string> Drop(float rate)
        {
            JoltScene s = NewScene(rate);
            try
            {
                PhysicsActor box = AddBox(s, 1000, new Vector3(128f, 128f, Ground + 3f), physical: true);
                box.SubscribeEvents(50);
                var rec = new Recorder(box);
                for (int i = 0; i < 66; i++)   // 6 s
                    s.Simulate(Heartbeat);
                return Events(rec.Updates).Distinct().ToList();
            }
            finally { s.Dispose(); }
        }
        List<string> single = Drop(0f), stepped = Drop(45f);
        Assert.Equal("start 0", single[0]);
        Assert.Equal(single, stepped);
    }

    // ---------------------------------------------------------------- forces

    // The velocity an impulse (llApplyImpulse reaches the prim as AddForce without pushforce) or a push force
    // (AddForce with pushforce) adds to a box in free fall, measured against the same box without it.
    private static float AddedVelocity(float rate, bool push)
    {
        float Run(bool apply)
        {
            JoltScene s = NewScene(rate);
            try
            {
                PhysicsActor box = AddBox(s, 1000, new Vector3(128f, 128f, Ground + 150f), physical: true);
                s.Simulate(Heartbeat);
                s.Simulate(Heartbeat);
                if (apply)
                    box.AddForce(new Vector3(0f, 0f, 10f * box.Mass), push);
                s.Simulate(Heartbeat);
                s.Simulate(Heartbeat);
                return box.Velocity.Z;
            }
            finally { s.Dispose(); }
        }
        return Run(true) - Run(false);
    }

    [Fact]
    public void A_script_impulse_adds_the_same_velocity_at_any_rate()
    {
        float single = AddedVelocity(0f, push: false), stepped = AddedVelocity(45f, push: false);
        Assert.InRange(single, 9.8f, 10.2f);                 // impulse / mass = 10 m/s
        Assert.InRange(stepped, single * 0.99f, single * 1.01f);
    }

    [Fact]
    public void A_push_adds_the_same_velocity_at_any_rate()
    {
        float single = AddedVelocity(0f, push: true), stepped = AddedVelocity(45f, push: true);
        Assert.InRange(single, 10f * Heartbeat * 0.98f, 10f * Heartbeat * 1.02f);   // force x heartbeat / mass
        Assert.InRange(stepped, single * 0.99f, single * 1.01f);
    }

    // ---------------------------------------------------------------- teardown

    private static void Populate(JoltScene s)
    {
        PhysicsActor car = AddBox(s, 1000, new Vector3(100f, 100f, Ground + 0.6f), physical: true);
        car.VehicleType = (int)Vehicle.TYPE_CAR;
        car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        AddBox(s, 1001, new Vector3(120f, 120f, Ground + 4f), physical: true);
        s.AddAvatar(1002, "Test User", new Vector3(140f, 140f, Ground + 2f), new Vector3(0.45f, 0.6f, 1.9f), 0f, false);
    }

    [Fact]
    public void Scenes_stepping_at_a_rate_are_created_and_torn_down_repeatedly()
    {
        for (int cycle = 0; cycle < 10; cycle++)
        {
            JoltScene s = NewScene(45f);
            Populate(s);
            for (int i = 0; i < 11; i++)
                s.Simulate(Heartbeat);
            Assert.True(s.Substeps.Steps >= 44);
            s.Dispose();
            Assert.Equal(1f, s.Simulate(Heartbeat));   // a heartbeat after teardown does nothing
        }
    }

    [Fact]
    public void Teardown_during_a_heartbeat_at_a_rate_is_safe()
    {
        for (int cycle = 0; cycle < 5; cycle++)
        {
            JoltScene s = NewScene(90f);   // eight or nine steps a heartbeat: teardown lands inside the loop
            Populate(s);
            s.Simulate(Heartbeat);
            Exception failure = null;
            using var started = new ManualResetEventSlim();
            var heartbeat = new Thread(() =>
            {
                try
                {
                    started.Set();
                    for (int i = 0; i < 200; i++)
                        s.Simulate(Heartbeat);
                }
                catch (Exception e) { failure = e; }
            });
            heartbeat.Start();
            started.Wait();
            Thread.Sleep(20 + cycle * 7);
            s.Dispose();
            Assert.True(heartbeat.Join(TimeSpan.FromSeconds(30)), "the heartbeat did not finish after teardown");
            Assert.Null(failure);
        }
    }

    // One step a heartbeat: the heartbeat keeps running until it has seen the scene's backend gone, so the teardown's
    // null lands somewhere inside a heartbeat. Once the backend is disposed its steps return at once, so heartbeats are
    // short and the vehicle controllers between Simulate's backend read and the step take a good share of each one; a
    // second read of the field there would see it null in some of these cycles.
    [Fact]
    public void Teardown_during_a_heartbeat_with_one_step_a_heartbeat_is_safe()
    {
        const int Cycles = 40;
        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            JoltScene s = NewScene(0f);
            Assert.False(s.Substepping);
            Populate(s);
            for (uint id = 0; id < 30; id++)
            {
                PhysicsActor car = AddBox(s, 2000 + id, new Vector3(20f + id * 6f, 60f, Ground + 0.6f), physical: true);
                car.VehicleType = (int)Vehicle.TYPE_CAR;
                car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(4f, 0f, 0f));
            }
            s.Simulate(Heartbeat);
            Exception failure = null;
            using var started = new ManualResetEventSlim();
            var heartbeat = new Thread(() =>
            {
                try
                {
                    started.Set();
                    var limit = System.Diagnostics.Stopwatch.StartNew();
                    while (s.Backend != null && limit.Elapsed < TimeSpan.FromSeconds(20))
                        s.Simulate(Heartbeat);
                    s.Simulate(Heartbeat);   // and one after it is gone
                }
                catch (Exception e) { failure = e; }
            });
            heartbeat.Start();
            started.Wait();
            Thread.Sleep(cycle % 5);
            s.Dispose();
            Assert.True(heartbeat.Join(TimeSpan.FromSeconds(30)), "the heartbeat did not finish after teardown");
            Assert.True(failure == null, $"cycle {cycle}: {failure}");
        }
    }
}
