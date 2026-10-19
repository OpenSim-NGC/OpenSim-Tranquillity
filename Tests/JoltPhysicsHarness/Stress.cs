/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The multi-scene stress run: several Jolt scenes in one process, each stepped from its own thread through the module's
// job pool gate, as the regions of one simulator are, while other threads cast rays into them, resize their avatars,
// and create and tear down further scenes. It is meant to show whether a joltc build is safe for several regions in one
// process: a native abort ends the process, so run it as a child process and read its exit code.
//
// Each busy scene is a pile of boxes kept hopping; its inputs depend only on its heartbeat count, so the hash of its
// boxes' positions and velocities after a fixed heartbeat is the same in every run on one native, whatever the other
// scenes and threads do. A different hash means the scene computed something else.
//
// The memory run creates and tears down a scene many times beside one scene that stays up, and reports the process's
// private bytes.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public sealed class StressOptions
{
    public int Pools = 1;
    public int Threads;
    public int Busy = 1;
    public int Light = 2;
    public int Boxes = 300;
    public int RayThreads;
    public int ResizeThreads;
    public int ChurnThreads;
    public int CheckBeat = 400;
    public double Seconds = 60;
    public int MemoryCycles;
    public readonly Dictionary<string, string> Jolt = new();
}

public static class Stress
{
    public const int ExitWedged = 20;
    public const int ExitThreadFailed = 21;

    private const string Usage =
@"JoltPhysicsHarness --stress: several scenes stepped at once from their own threads, with other threads at work on them.

  --pools N            [Jolt] JobPools (default 1)
  --threads N          [Jolt] ThreadCount; 0 = the module's default (default 0)
  --busy N             busy scenes, each a pile of boxes kept hopping (default 1)
  --light N            light scenes, an avatar on bare ground (default 2)
  --boxes N            boxes in each busy scene (default 300)
  --rays N             threads casting rays into the scenes (default 0)
  --resize N           threads resizing the scenes' avatars (default 0)
  --churn N            threads each creating a scene, stepping it 10 times and tearing it down, over and over (default 0)
  --check-beat N       heartbeat after which each busy scene's boxes are hashed (default 400)
  --seconds S          how long the scenes step (default 60)
  --memory-cycles N    instead: create and tear down a scene N times beside one that stays up, and report memory
  --jolt KEY=VALUE     a [Jolt] config key (repeatable), e.g. AllowUnrecordedNative=true

Exit code 0 when every thread finished; 20 when a scene stopped advancing for 60 s; 21 when a thread threw.";

    private sealed class StressScene
    {
        public string Name;
        public JoltScene Scene;
        public bool Busy;
        public readonly List<PhysicsActor> Boxes = new();
        public PhysicsActor Avatar;
        public long Beats;          // Interlocked: read by the watchdog
        public string Hash;
        public int NonFinite;
    }

    public static int Run(string[] args, TextWriter output)
    {
        var o = new StressOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            int Int() => int.Parse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--help": output.WriteLine(Usage); return 0;
                case "--pools": o.Pools = Int(); break;
                case "--threads": o.Threads = Int(); break;
                case "--busy": o.Busy = Int(); break;
                case "--light": o.Light = Int(); break;
                case "--boxes": o.Boxes = Int(); break;
                case "--rays": o.RayThreads = Int(); break;
                case "--resize": o.ResizeThreads = Int(); break;
                case "--churn": o.ChurnThreads = Int(); break;
                case "--check-beat": o.CheckBeat = Int(); break;
                case "--seconds": o.Seconds = double.Parse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture); break;
                case "--memory-cycles": o.MemoryCycles = Int(); break;
                case "--jolt":
                    string kv = Next();
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"--jolt '{kv}': expected KEY=VALUE");
                    o.Jolt[kv[..eq].Trim()] = kv[(eq + 1)..].Trim();
                    break;
                default: throw new ArgumentException($"unknown argument '{a}'\n\n{Usage}");
            }
        }
        return o.MemoryCycles > 0 ? Memory(o, output) : Run(o, output);
    }

    private static JoltScene NewScene(string name, StressOptions o)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("JobPools", o.Pools.ToString(CultureInfo.InvariantCulture));
        if (o.Threads > 0)
            jolt.Set("ThreadCount", o.Threads.ToString(CultureInfo.InvariantCulture));
        foreach (KeyValuePair<string, string> kv in o.Jolt)
            jolt.Set(kv.Key, kv.Value);
        var scene = new JoltScene();
        scene.Initialise(config);
        scene.InitialiseWithoutScene(name, Course.Size, Course.Size, Course.Heightmap(0f), Course.Water, 1f / 11f);
        return scene;
    }

    private static readonly Vector3 AvatarSize = new(0.45f, 0.6f, 1.9f);
    private static readonly Vector3 AvatarSizeTall = new(0.45f, 0.6f, 2.3f);

    // An avatar standing on the ground well away from any pile, so resizing it never touches the boxes.
    private static PhysicsActor AddAvatar(JoltScene scene, uint id)
        => scene.AddAvatar(id, "Test User", new Vector3(30f, 30f, Course.Ground + 1.2f), AvatarSize, 0f, false);

    private static void AddPile(StressScene s, int boxes)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(boxes / 3.0));
        for (int i = 0; i < boxes; i++)
        {
            int x = i % side, y = (i / side) % side, z = i / (side * side);
            var p = new Vector3(120f + x * 0.85f + (z % 2) * 0.3f, 120f + y * 0.85f + (z % 2) * 0.3f, Course.Ground + 1f + z * 0.9f);
            PhysicsActor pa = s.Scene.AddPrimShape("stress box", PrimitiveBaseShape.CreateBox(), p, new Vector3(0.8f, 0.8f, 0.8f),
                                                   Quaternion.Identity, true, (uint)(2000 + i));
            pa.Density = 1000f;
            s.Boxes.Add(pa);
        }
    }

    // One heartbeat: every box hops once a second (a share of them each heartbeat), then the scene steps 1/11 s.
    private static void Beat(StressScene s, long beat)
    {
        for (int i = (int)(beat % 11); i < s.Boxes.Count; i += 11)
            s.Boxes[i].Velocity = new Vector3(0f, 0f, 3f);
        s.Scene.Simulate(1f / 11f);
    }

    // SHA-256 over every box's position and velocity bits, in creation order.
    private static string HashBoxes(StressScene s, out int nonFinite)
    {
        nonFinite = 0;
        var bytes = new List<byte>(s.Boxes.Count * 24);
        foreach (PhysicsActor b in s.Boxes)
        {
            Vector3 p = b.Position, v = b.Velocity;
            foreach (float f in new[] { p.X, p.Y, p.Z, v.X, v.Y, v.Z })
            {
                if (!float.IsFinite(f)) nonFinite++;
                bytes.AddRange(BitConverter.GetBytes(f));
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))[..16];
    }

    private static int Run(StressOptions o, TextWriter output)
    {
        var scenes = new List<StressScene>();
        for (int i = 0; i < o.Busy; i++)
            scenes.Add(new StressScene { Name = $"Busy {i + 1}", Busy = true });
        for (int i = 0; i < o.Light; i++)
            scenes.Add(new StressScene { Name = $"Light {i + 1}" });
        uint avatarId = 9000;
        foreach (StressScene s in scenes)
        {
            s.Scene = NewScene(s.Name, o);
            s.Avatar = AddAvatar(s.Scene, avatarId++);
            if (s.Busy)
                AddPile(s, o.Boxes);
        }
        var stats = scenes[0].Scene.CapacityStats();
        output.WriteLine($"stress: pools={stats.JobPools} threads={stats.JobThreadCount} perPool={stats.JobThreadsPerPool} busy={o.Busy} light={o.Light} " +
                         $"boxes={o.Boxes} rays={o.RayThreads} resize={o.ResizeThreads} churn={o.ChurnThreads} seconds={o.Seconds} checkBeat={o.CheckBeat}");
        foreach (StressScene s in scenes)
            output.WriteLine($"  {s.Name}: pool {s.Scene.CapacityStats().PoolIndex}");
        output.Flush();

        var stop = new CancellationTokenSource();
        Exception failure = null;
        void Guard(string name, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
                output.WriteLine($"THREAD FAILED {name}: {e}");
                output.Flush();
                stop.Cancel();
            }
        }

        long rays = 0, rayHits = 0, resizes = 0, churnCycles = 0;
        var threads = new List<Thread>();
        foreach (StressScene s in scenes)
            threads.Add(new Thread(() => Guard(s.Name, () =>
            {
                long beat = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (s.Busy)
                        Beat(s, beat);
                    else
                        s.Scene.Simulate(1f / 11f);
                    beat++;
                    Interlocked.Exchange(ref s.Beats, beat);
                    if (s.Busy && beat == o.CheckBeat)
                    {
                        s.Hash = HashBoxes(s, out s.NonFinite);
                        output.WriteLine($"  {s.Name}: hash after beat {beat} {s.Hash} nonfinite {s.NonFinite}");
                        output.Flush();
                    }
                }
            })) { IsBackground = true, Name = "step " + s.Name });

        for (int t = 0; t < o.RayThreads; t++)
        {
            int k = t;
            threads.Add(new Thread(() => Guard("rays " + k, () =>
            {
                int n = k;
                while (!stop.IsCancellationRequested)
                {
                    StressScene s = scenes[n++ % scenes.Count];
                    // Along the ground through the pile, and straight down onto it, as llCastRay makes them.
                    var along = s.Scene.RaycastWorld(new Vector3(10f, 125f, Course.Ground + 1f), Vector3.UnitX, 240f, 16,
                        RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical);
                    var down = s.Scene.RaycastWorld(new Vector3(125f, 125f, Course.Ground + 30f), -Vector3.UnitZ, 40f, 16,
                        RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical);
                    Interlocked.Add(ref rayHits, (along is System.Collections.ICollection a ? a.Count : 0) + (down is System.Collections.ICollection d ? d.Count : 0));
                    Interlocked.Add(ref rays, 2);
                }
            })) { IsBackground = true, Name = "rays " + t });
        }

        for (int t = 0; t < o.ResizeThreads; t++)
        {
            int k = t;
            threads.Add(new Thread(() => Guard("resize " + k, () =>
            {
                int n = k;
                while (!stop.IsCancellationRequested)
                {
                    StressScene s = scenes[n % scenes.Count];
                    s.Avatar.Size = (n / scenes.Count) % 2 == 0 ? AvatarSizeTall : AvatarSize;
                    n++;
                    Interlocked.Increment(ref resizes);
                    Thread.Yield();
                }
            })) { IsBackground = true, Name = "resize " + t });
        }

        for (int t = 0; t < o.ChurnThreads; t++)
        {
            int k = t;
            threads.Add(new Thread(() => Guard("churn " + k, () =>
            {
                int cycle = 0;
                while (!stop.IsCancellationRequested)
                {
                    var c = new StressScene { Name = $"Churn {k}-{cycle}", Busy = true };
                    c.Scene = NewScene(c.Name, o);
                    try
                    {
                        AddAvatar(c.Scene, 9500u + (uint)k);
                        AddPile(c, 50);
                        for (int b = 0; b < 10; b++)
                            Beat(c, b);
                    }
                    finally
                    {
                        c.Scene.Dispose();
                    }
                    cycle++;
                    Interlocked.Increment(ref churnCycles);
                }
            })) { IsBackground = true, Name = "churn " + t });
        }

        var clock = Stopwatch.StartNew();
        foreach (Thread th in threads) th.Start();

        // Watchdog: every scene must keep advancing.
        var lastBeats = scenes.Select(s => 0L).ToArray();
        var lastMoved = scenes.Select(s => 0.0).ToArray();
        double nextReport = 10;
        while (clock.Elapsed.TotalSeconds < o.Seconds && !stop.IsCancellationRequested)
        {
            stop.Token.WaitHandle.WaitOne(250);
            double now = clock.Elapsed.TotalSeconds;
            for (int i = 0; i < scenes.Count; i++)
            {
                long b = Interlocked.Read(ref scenes[i].Beats);
                if (b != lastBeats[i]) { lastBeats[i] = b; lastMoved[i] = now; }
                else if (now - lastMoved[i] > 60)
                {
                    output.WriteLine($"WEDGED: {scenes[i].Name} has not advanced for 60 s (beat {b}) at {now:0} s");
                    output.Flush();
                    return ExitWedged;
                }
            }
            if (now >= nextReport)
            {
                output.WriteLine($"  t={now:0}s beats {string.Join(" ", scenes.Select(s => Interlocked.Read(ref s.Beats)))} rays {Interlocked.Read(ref rays)} " +
                                 $"resizes {Interlocked.Read(ref resizes)} churn {Interlocked.Read(ref churnCycles)}");
                output.Flush();
                nextReport += 10;
            }
        }
        stop.Cancel();
        foreach (Thread th in threads)
            if (!th.Join(TimeSpan.FromSeconds(120)))
            {
                output.WriteLine($"WEDGED: thread '{th.Name}' did not finish within 120 s of the stop");
                output.Flush();
                return ExitWedged;
            }
        if (failure != null)
            return ExitThreadFailed;

        double secs = clock.Elapsed.TotalSeconds;
        foreach (StressScene s in scenes)
        {
            int nf = 0;
            if (s.Busy) HashBoxes(s, out nf);
            output.WriteLine($"RESULT scene={s.Name} beats={s.Beats} beats_per_s={s.Beats / secs:0.0}" +
                             (s.Busy ? $" hash@{o.CheckBeat}={s.Hash ?? "not-reached"} nonfinite_end={nf}" : ""));
        }
        output.WriteLine($"RESULT rays={rays} ray_hits={rayHits} resizes={resizes} churn_cycles={churnCycles} seconds={secs:0.0}");
        foreach (StressScene s in scenes)
            s.Scene.Dispose();
        output.WriteLine("STRESS OK");
        output.Flush();
        return 0;
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64;
    }

    // Create and tear down a scene (an avatar, 100 boxes, 20 heartbeats) N times beside one scene that stays up, so Jolt
    // itself stays initialised and only the per-scene native objects come and go, as when one region of several restarts.
    private static int Memory(StressOptions o, TextWriter output)
    {
        var anchor = new StressScene { Name = "Anchor" };
        anchor.Scene = NewScene(anchor.Name, o);
        AddAvatar(anchor.Scene, 9000);
        anchor.Scene.Simulate(1f / 11f);
        long baseline = PrivateBytes();
        output.WriteLine($"memory: cycles={o.MemoryCycles} baseline_private_mb={baseline / 1048576.0:0.0}");
        long afterWarm = 0, peakAlive = 0;
        for (int cycle = 1; cycle <= o.MemoryCycles; cycle++)
        {
            var c = new StressScene { Name = $"Cycle {cycle}", Busy = true };
            c.Scene = NewScene(c.Name, o);
            AddAvatar(c.Scene, 9001);
            AddPile(c, 100);
            for (int b = 0; b < 20; b++)
                Beat(c, b);
            if (cycle == 1)
            {
                peakAlive = PrivateBytes();
                output.WriteLine($"  one more scene alive: private_mb={peakAlive / 1048576.0:0.0} (+{(peakAlive - baseline) / 1048576.0:0.0})");
            }
            c.Scene.Dispose();
            if (cycle == 10)
                afterWarm = PrivateBytes();
            if (cycle % 10 == 0)
                output.WriteLine($"  cycle {cycle}: private_mb={PrivateBytes() / 1048576.0:0.0}");
        }
        long end = PrivateBytes();
        double perCycleKb = o.MemoryCycles > 10 ? (end - afterWarm) / 1024.0 / (o.MemoryCycles - 10) : double.NaN;
        output.WriteLine($"RESULT memory baseline_mb={baseline / 1048576.0:0.0} scene_alive_mb={(peakAlive - baseline) / 1048576.0:0.0} after10_mb={afterWarm / 1048576.0:0.0} " +
                         $"end_mb={end / 1048576.0:0.0} growth_after10_kb_per_cycle={perCycleKb:0.0}");
        anchor.Scene.Dispose();
        output.WriteLine("STRESS OK");
        return 0;
    }
}
