/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The job pool benchmark: several Jolt scenes in one process sharing the job pools, as the regions of one simulator
// do, one of them heavy (a pile of boxes kept moving, or the test car driving) and the others light (bare ground).
// Each scene runs its own heartbeat on its own thread, in real time at the heartbeat rate (or as fast as it can,
// unpaced), for each combination of [Jolt] ThreadCount, JobPools and JobPoolFairHandoff asked for. It reports what the
// light scenes waited for their pool, what a heartbeat cost the heavy scene, and the physics steps all the scenes ran
// per second.
//
// Timing figures depend on the machine and on whatever else it is running; run it on a quiet machine.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public sealed class PoolBenchOptions
{
    public int[] Pools = { 1 };
    public bool[] Handoff = { false };
    /// <summary>[Jolt] ThreadCount values; 0 leaves the key unset (the module's default).</summary>
    public int[] Threads = { 0 };
    public int HeavyBoxes = 300;
    /// <summary>The heavy scene is the test car driving instead of a pile of boxes.</summary>
    public bool HeavyCar;
    /// <summary>How many heavy scenes, each its own pile (or car).</summary>
    public int HeavyScenes = 1;
    public int LightScenes = 2;
    public double RateHz = 11.0;
    public double PhysicsRateHz = 45.0;
    public double Seconds = 20.0;
    public double WarmupSeconds = 2.0;
    public bool Unpaced;
}

/// <summary>One combination's figures. Waits are per physics step that found its pool taken.</summary>
public sealed class PoolBenchResult
{
    public int Pools;
    public bool Handoff;
    public double Seconds;
    public long LightHeartbeats;
    public long LightWaits;
    public double LightWaitMsTotal;
    public double LightWaitMsMax;
    public long HeavyHeartbeats;
    public double HeavyHeartbeatMsAvg;
    public double HeavyHeartbeatMsMax;
    public double HeavyPoolWaitMsTotal;
    public long PhysicsSteps;
    public long LateHeartbeats;
    public long Heartbeats;
    /// <summary>The job threads the pools were sized from, and each pool's share (the backend's capacity stats).</summary>
    public int Threads;
    public int ThreadsPerPool;
    /// <summary>The heavy scene's (or, with none, the light scene's) active bodies after each measured heartbeat, averaged.</summary>
    public double ActiveBodiesAvg;
    /// <summary>One line per scene (<see cref="SceneHeader"/>).</summary>
    public readonly List<string> SceneLines = new();

    public const string SceneHeader =
        "handoff,pools,threads,scene,pool,heartbeats,heartbeat_avg_ms,heartbeat_max_ms,pool_waits,pool_wait_ms_total,pool_wait_max_ms,pool_wait_ms_per_heartbeat,late_heartbeats";

    public const string Header =
        "handoff,pools,seconds,light_waits,light_wait_avg_ms,light_wait_max_ms,light_wait_ms_per_heartbeat," +
        "heavy_heartbeat_avg_ms,heavy_heartbeat_max_ms,heavy_pool_wait_ms_per_heartbeat,steps_per_s,heartbeats,late_heartbeats," +
        "threads,threads_per_pool,heavy_active_bodies_avg";

    public string Line()
    {
        static string F(double v, string f) => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);
        return string.Join(",",
            Handoff ? "on" : "off", Pools.ToString(CultureInfo.InvariantCulture), F(Seconds, "0.0"),
            LightWaits.ToString(CultureInfo.InvariantCulture),
            F(LightWaits > 0 ? LightWaitMsTotal / LightWaits : 0, "0.000"), F(LightWaitMsMax, "0.000"),
            F(LightHeartbeats > 0 ? LightWaitMsTotal / LightHeartbeats : double.NaN, "0.000"),
            F(HeavyHeartbeatMsAvg, "0.000"), F(HeavyHeartbeatMsMax, "0.000"),
            F(HeavyHeartbeats > 0 ? HeavyPoolWaitMsTotal / HeavyHeartbeats : double.NaN, "0.000"),
            F(PhysicsSteps / Seconds, "0"), Heartbeats.ToString(CultureInfo.InvariantCulture),
            LateHeartbeats.ToString(CultureInfo.InvariantCulture),
            Threads.ToString(CultureInfo.InvariantCulture), ThreadsPerPool.ToString(CultureInfo.InvariantCulture),
            F(ActiveBodiesAvg, "0.0"));
    }
}

public static class PoolBench
{
    private sealed class BenchScene
    {
        public string Name;
        public bool Heavy;
        public JoltScene Scene;
        public List<PhysicsActor> Boxes = new();
        public PhysicsActor Car;
        // Measured after the warm-up, by the scene's own thread.
        public long Heartbeats;
        public double HeartbeatMsTotal;
        public double HeartbeatMsMax;
        public long ActiveBodiesTotal;
        public long Late;
        public Backend.PhysicsCapacityStats Start;
        public Backend.PhysicsCapacityStats End;
        public long StepsAtStart;
        public long StepsAtEnd;
    }

    public static IEnumerable<PoolBenchResult> Run(PoolBenchOptions o, TextWriter output)
    {
        output.WriteLine(PoolBenchResult.Header);
        foreach (bool handoff in o.Handoff)
            foreach (int threads in o.Threads)
                foreach (int pools in o.Pools)
                {
                    PoolBenchResult r = RunOne(o, pools, handoff, threads);
                    output.WriteLine(r.Line());
                    yield return r;
                }
    }

    private static JoltScene NewScene(string name, PoolBenchOptions o, int pools, bool handoff, int threads)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("JobPools", pools.ToString(CultureInfo.InvariantCulture));
        // Set only when asked for, so 0 runs the module's own default.
        if (threads > 0)
            jolt.Set("ThreadCount", threads.ToString(CultureInfo.InvariantCulture));
        // Set only when on, so a build without the key runs the default handoff unchanged.
        if (handoff)
            jolt.Set("JobPoolFairHandoff", "true");
        // Always set: 0 is one step per heartbeat, and the module's own default is 45 Hz.
        jolt.Set("PhysicsStepRate", Math.Max(o.PhysicsRateHz, 0).ToString(CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        scene.InitialiseWithoutScene(name, Course.Size, Course.Size, Course.Heightmap(0f), Course.Water, (float)(1.0 / o.RateHz));
        return scene;
    }

    private static bool HasHeavy(PoolBenchOptions o) => o.HeavyCar || o.HeavyBoxes > 0;

    private static PoolBenchResult RunOne(PoolBenchOptions o, int pools, bool handoff, int threadCount)
    {
        var scenes = new List<BenchScene>();
        try
        {
            // The heavy scene first, so it is given pool 0 and the light scenes are spread over the others.
            if (HasHeavy(o))
                for (int i = 0; i < Math.Max(1, o.HeavyScenes); i++)
                {
                    string name = o.HeavyScenes > 1 ? $"Heavy {i + 1}" : "Heavy";
                    scenes.Add(new BenchScene { Name = name, Heavy = true, Scene = NewScene(name, o, pools, handoff, threadCount) });
                }
            for (int i = 0; i < o.LightScenes; i++)
                scenes.Add(new BenchScene { Name = $"Light {i + 1}", Scene = NewScene($"Light {i + 1}", o, pools, handoff, threadCount) });
            foreach (BenchScene s in scenes.Where(s => s.Heavy))
            {
                if (o.HeavyCar)
                    AddCar(s);
                else
                    AddPile(s, o.HeavyBoxes);
            }

            double period = 1.0 / o.RateHz;
            var clock = Stopwatch.StartNew();
            double measureFrom = o.WarmupSeconds;
            double stopAt = o.WarmupSeconds + o.Seconds;
            var threads = new List<Thread>();
            for (int k = 0; k < scenes.Count; k++)
            {
                BenchScene s = scenes[k];
                // Regions' heartbeats neither start in step nor stay in step: each drifts against the others. Scene k
                // starts k/n of a heartbeat late and paces 1% x k slower in real time (it still steps the nominal dt),
                // so over a run its heartbeat passes through every phase of the heavy scene's several times.
                double phase = period * k / scenes.Count;
                double pace = period * (1.0 + 0.01 * k);
                var th = new Thread(() => Heartbeats(s, o, clock, period, pace, phase, measureFrom, stopAt)) { IsBackground = true, Name = "bench-" + s.Name };
                threads.Add(th);
            }
            foreach (Thread th in threads) th.Start();
            foreach (Thread th in threads)
                if (!th.Join(TimeSpan.FromSeconds(stopAt + 120)))
                    throw new InvalidOperationException("a benchmark scene did not finish; a job pool may have wedged");

            var r = new PoolBenchResult { Pools = pools, Handoff = handoff, Seconds = o.Seconds };
            if (scenes.Count > 0)
            {
                r.Threads = scenes[0].End.JobThreadCount;
                r.ThreadsPerPool = scenes[0].End.JobThreadsPerPool;
                BenchScene counted = scenes[0];   // the heavy scene, or with none the first light one
                r.ActiveBodiesAvg = counted.Heartbeats > 0 ? (double)counted.ActiveBodiesTotal / counted.Heartbeats : double.NaN;
            }
            double heavyMsTotal = 0;
            foreach (BenchScene s in scenes)
            {
                r.Heartbeats += s.Heartbeats;
                r.LateHeartbeats += s.Late;
                r.PhysicsSteps += s.StepsAtEnd - s.StepsAtStart;
                double waitMs = s.End.UpdateGateWaitMsTotal - s.Start.UpdateGateWaitMsTotal;
                long waits = s.End.UpdateGateWaits - s.Start.UpdateGateWaits;
                r.SceneLines.Add(string.Join(",",
                    handoff ? "on" : "off", pools.ToString(CultureInfo.InvariantCulture), s.End.JobThreadCount.ToString(CultureInfo.InvariantCulture),
                    s.Name, s.End.PoolIndex.ToString(CultureInfo.InvariantCulture), s.Heartbeats.ToString(CultureInfo.InvariantCulture),
                    (s.Heartbeats > 0 ? s.HeartbeatMsTotal / s.Heartbeats : 0).ToString("0.000", CultureInfo.InvariantCulture),
                    s.HeartbeatMsMax.ToString("0.000", CultureInfo.InvariantCulture), waits.ToString(CultureInfo.InvariantCulture),
                    waitMs.ToString("0.000", CultureInfo.InvariantCulture), s.End.UpdateGateWaitMsMax.ToString("0.000", CultureInfo.InvariantCulture),
                    (s.Heartbeats > 0 ? waitMs / s.Heartbeats : 0).ToString("0.000", CultureInfo.InvariantCulture),
                    s.Late.ToString(CultureInfo.InvariantCulture)));
                if (s.Heavy)
                {
                    // Several heavy scenes: their heartbeats together.
                    r.HeavyHeartbeats += s.Heartbeats;
                    heavyMsTotal += s.HeartbeatMsTotal;
                    r.HeavyHeartbeatMsAvg = r.HeavyHeartbeats > 0 ? heavyMsTotal / r.HeavyHeartbeats : double.NaN;
                    r.HeavyHeartbeatMsMax = Math.Max(double.IsNaN(r.HeavyHeartbeatMsMax) ? 0 : r.HeavyHeartbeatMsMax, s.HeartbeatMsMax);
                    r.HeavyPoolWaitMsTotal += waitMs;
                }
                else
                {
                    r.LightHeartbeats += s.Heartbeats;
                    r.LightWaits += s.End.UpdateGateWaits - s.Start.UpdateGateWaits;
                    r.LightWaitMsTotal += waitMs;
                    r.LightWaitMsMax = Math.Max(r.LightWaitMsMax, s.End.UpdateGateWaitMsMax);   // reset when measuring began
                }
            }
            if (!HasHeavy(o))
            {
                r.HeavyHeartbeatMsAvg = double.NaN;
                r.HeavyHeartbeatMsMax = double.NaN;
                // With no heavy scene, report the (single) light scene's heartbeat cost in the heavy columns' place.
                BenchScene only = scenes.FirstOrDefault();
                if (only != null && only.Heartbeats > 0)
                {
                    r.HeavyHeartbeatMsAvg = only.HeartbeatMsTotal / only.Heartbeats;
                    r.HeavyHeartbeatMsMax = only.HeartbeatMsMax;
                    r.HeavyHeartbeats = only.Heartbeats;
                }
            }
            return r;
        }
        finally
        {
            foreach (BenchScene s in scenes)
                s.Scene.Dispose();
        }
    }

    // A loose lattice of boxes over the middle of the region; they fall into a pile.
    private static void AddPile(BenchScene s, int boxes)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(boxes / 3.0));
        for (int i = 0; i < boxes; i++)
        {
            int x = i % side, y = (i / side) % side, z = i / (side * side);
            var p = new Vector3(120f + x * 0.85f + (z % 2) * 0.3f, 120f + y * 0.85f + (z % 2) * 0.3f, Course.Ground + 1f + z * 0.9f);
            PhysicsActor pa = s.Scene.AddPrimShape("bench box", PrimitiveBaseShape.CreateBox(), p, new Vector3(0.8f, 0.8f, 0.8f),
                                                   Quaternion.Identity, true, (uint)(2000 + i));
            pa.Density = 1000f;
            s.Boxes.Add(pa);
        }
    }

    // The harness's test car (scenario testcar): VEHICLE_TYPE_CAR with a test-drive script's linear friction timescale
    // <1,1,1000>, motor timescale 1 and motor decay 0.5, on level ground facing east, far enough west that it stays in
    // the region for a run of a few tens of seconds. Its key is held for the whole run (see Heartbeats).
    private static readonly Vector3 CarSize = new(2f, 1f, 0.5f);
    private static readonly Vector3 CarMotor = new(8f, 0f, 0f);

    private static void AddCar(BenchScene s)
    {
        var p = new Vector3(20f, 128f, Course.Ground + CarSize.Z * 0.5f + 0.02f);
        PhysicsActor car = s.Scene.AddPrimShape("bench car", PrimitiveBaseShape.CreateBox(), p, CarSize, Quaternion.Identity, true, 2000u);
        car.Density = 1000f;
        car.VehicleType = (int)Vehicle.TYPE_CAR;
        car.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
        car.VehicleFloatParam((int)Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        car.VehicleFloatParam((int)Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0.5f);
        s.Car = car;
    }

    private static void Heartbeats(BenchScene s, PoolBenchOptions o, Stopwatch clock, double period, double pace, double phase, double measureFrom, double stopAt)
    {
        float dt = (float)period;
        double next = phase;
        bool measuring = false;
        long beat = 0;
        while (true)
        {
            if (!o.Unpaced)
            {
                double wait = next - clock.Elapsed.TotalSeconds;
                if (wait > 0.002)
                    Thread.Sleep(TimeSpan.FromSeconds(wait - 0.001));
                while (clock.Elapsed.TotalSeconds < next)
                    Thread.SpinWait(50);
            }
            double now = clock.Elapsed.TotalSeconds;
            if (now >= stopAt)
                break;
            if (!measuring && now >= measureFrom)
            {
                measuring = true;
                ResetLongestWait(s.Scene);
                s.Start = s.Scene.CapacityStats();
                s.StepsAtStart = StepsTaken(s.Scene);
            }

            // Keep the pile moving: every box hops once a second, a share of them each heartbeat, so the load is steady.
            // A held drive key: the motor re-sent every heartbeat, as a key's repeat (every 0.1 s) about does at 11 Hz.
            if (s.Car != null)
                s.Car.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, CarMotor);
            else if (s.Heavy)
            {
                int beatsPerSecond = Math.Max(1, (int)Math.Round(o.RateHz));
                for (int i = (int)(beat % beatsPerSecond); i < s.Boxes.Count; i += beatsPerSecond)
                    s.Boxes[i].Velocity = new Vector3(0f, 0f, 3f);
            }

            long t0 = Stopwatch.GetTimestamp();
            s.Scene.Simulate(dt);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            beat++;
            if (measuring)
            {
                s.Heartbeats++;
                s.HeartbeatMsTotal += ms;
                if (ms > s.HeartbeatMsMax) s.HeartbeatMsMax = ms;
                s.ActiveBodiesTotal += ActiveBodies(s.Scene);
                if (!o.Unpaced && now - next > period)
                    s.Late++;
            }
            next += pace;
            if (!o.Unpaced && clock.Elapsed.TotalSeconds - next > 4 * period)
                next = clock.Elapsed.TotalSeconds;   // far behind: start again from now, as a region's heartbeat does
        }
        s.End = s.Scene.CapacityStats();
        s.StepsAtEnd = StepsTaken(s.Scene);
    }

    // Backend steps taken: with physics steps on, the scene's substep counter; otherwise one per heartbeat.
    private static long StepsTaken(JoltScene scene)
        => scene.Substepping ? scene.Substeps.Steps : StepCount(scene);

    private static readonly System.Reflection.FieldInfo StepCountField =
        typeof(JoltScene).GetField("_stepCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static long StepCount(JoltScene scene) => StepCountField?.GetValue(scene) is long n ? n : 0;

    private static readonly System.Reflection.FieldInfo ActiveBodyCountField =
        typeof(JoltScene).GetField("_lastActiveBodyCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // The active bodies the scene's last physics step reported.
    private static int ActiveBodies(JoltScene scene) => ActiveBodyCountField?.GetValue(scene) is int n ? n : 0;

    // The backend keeps the longest pool wait since the region started; zero it when measuring begins, so the warm-up's
    // (the pile's first collapse) is left out. Run on the scene's own heartbeat thread, the only one that writes it.
    private static readonly System.Reflection.FieldInfo BackendField =
        typeof(JoltScene).GetField("_backend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static readonly System.Reflection.FieldInfo LongestWaitField =
        typeof(Backend.JoltPhysicsBackend).GetField("_gateWaitTicksMax", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static void ResetLongestWait(JoltScene scene)
    {
        object backend = BackendField?.GetValue(scene);
        if (backend != null && LongestWaitField != null)
            LongestWaitField.SetValue(backend, 0L);
    }
}
