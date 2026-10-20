/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class Program
{
    private const string Usage =
@"JoltPhysicsHarness: run Jolt physics scenarios with no region, at chosen heartbeat rates.

  --list                     list the scenarios and exit
  --scenario NAME[,NAME..]   scenarios to run, or 'all' (default: all)
  --rate HZ[,HZ..]           heartbeat rates, or 'all' = 11,22.5,45,90 (default: 11)
  --physics-rate HZ[,HZ..]   physics steps per second inside each heartbeat ([Jolt] PhysicsStepRate);
                             0 = one step per heartbeat, set as PhysicsStepRate = 0 (the module's
                             own default is 45) (default: 0, or JOLT_HARNESS_PHYSICS_RATE)
  --slope DEG[,DEG..]        ramp angles for the scenarios that use one (default: each scenario's own list)
  --duration S               seconds to simulate (default: the scenario's)
  --hold S                   seconds the drive key is held (default: the scenario's)
  --camera-noise DEG         mouselook scenarios: the camera shakes, each update adding a random yaw and pitch of this
                             standard deviation (degrees); --camera-rate HZ updates a second (30); --camera-seed N
  --keyrepeat S              how often a held key re-sends the motor (default: 0.1)
  --feed heartbeat|region    how a held key reaches a car: 'heartbeat' (default) lands each key event half a
                             heartbeat before a step, the car resting on the ground at the start; 'region' feeds
                             it as a seated driver's control events reach a script in a region (the car dropped
                             from where a rez places it, events at their own times between heartbeats)
  --keydelay S               region feed: seconds from physics on to the first key event (default: 0)
  --startspeed V             heartbeat feed: a car's forward speed (m/s) just before its key goes down,
                             which then goes down one heartbeat later
  --terrain FILE             scenarios that take one (sled-drop): the ground from a raw float32 file, 256 x 256, x fastest
  --trace-dir DIR            scenarios that trace their collision steps (sled-drop): write the trace there
  --jolt KEY=VALUE           a [Jolt] config key, as in the region's ini (repeatable)
  --vparam NAME=V|X,Y,Z      a vehicle param applied after the scenario's own, by its LSL name,
                             e.g. LINEAR_FRICTION_TIMESCALE=1,1,1000 (repeatable)
  --vflag NAME|-NAME         a vehicle flag set (or, with -, removed) after the scenario's own, e.g.
                             HOVER_UP_ONLY (repeatable)
  --crash-speed F[,F..]      crash scenarios: arrival speed as a share of the scenario's (default 1)
  --crash-offset M[,M..]     crash scenarios: the car's sideways offset (m, default 0)
  --crash-angle DEG[,DEG..]  crash scenarios: the car turned about the vertical (crash-drop: rolled) (default 0)
                             Lists run every combination (a sweep).
  --ball M[,M..]             shot scenarios (tunnel-*): the ball's diameter (m, default 0.2)
  --shot-speed V[,V..]       shot scenarios: the speed the ball is shot at (m/s, default 25). Lists sweep, as above.
  --start-offset DX,DY       move the scenario's own body (car, box or prim) DX and DY metres from its start;
                             nothing else moves (default: 0,0)
  Every run is built as the simulator builds it. Each of these four switches gives back the earlier harness's way:
  --no-sim-defaults          prims and avatars get the harness's own values (friction 0.6, restitution 0; box 1.9 m)
                             in place of what the simulator hands the engine (a prim's wood material: friction 0.6,
                             restitution 0.5; the default avatar box, 2.1 m tall, subscribed to collisions)
  --no-sim-prims, --no-sim-avatars   the two halves of --no-sim-defaults
  --no-region-mesher         the scene has no mesher, in place of a Meshmerizer of a load context of its own
  --no-warm-up-step          the setup runs before the first step (the region-load window), in place of after one
  --no-shipped-config        no [Jolt] keys but the run's own, in place of RegionServerDefaults.ini's [Jolt] section
  --legacy-defaults          all four of the above
  --sim-defaults             accepted, and does nothing more than the default
  --vehicle-restore ROUTE    vehicle scenarios: when the setup is done, hand the vehicle back from its saved record
                             as the simulator does by ROUTE: rez, region-start, copy, crossing, detach or
                             physics-off-on (see VehicleRestore.cs)
  --vehicle-script-again     after --vehicle-restore, the scenario's script makes its vehicle calls again (the same
                             vehicle set up by its script, on a body with the route's history)
  --out DIR                  write <scenario>-s<slope>-r<rate>.csv per run and summary.csv to DIR, and
                             native.txt, the joltc build loaded (for --check-baseline); a scenario that traces
                             its parts (tower-10) also writes <scenario>-s<slope>-r<rate>-parts.csv

  --pool-bench               run the job pool benchmark instead of the scenarios: a heavy scene and light scenes
                             sharing the job pools, each heartbeat on its own thread (see PoolBench.cs). It uses the
                             first --rate (default 11) and --physics-rate (default 45 here), and with --out writes
                             pool-bench.csv
  --pools N[,N..]            pool bench: [Jolt] JobPools values (default 1)
  --handoff off|on[,..]      pool bench: [Jolt] JobPoolFairHandoff values (default off)
  --threads N[,N..]          pool bench: [Jolt] ThreadCount values; 0 = the module's default (default 0)
  --heavy-boxes N            pool bench: boxes in the heavy scene, kept moving; 0 = no heavy scene (default 300)
  --heavy-car                pool bench: the heavy scene is the test car driving, its key held, instead of boxes
  --heavy-scenes N           pool bench: heavy scenes, each its own (default 1)
  --light N                  pool bench: light scenes, bare ground (default 2)
  --seconds S                pool bench: seconds measured per combination, after a 2 s warm-up (default 20)
  --unpaced                  pool bench: heartbeats back to back instead of in real time

  --check-baseline OUTROOT --baselines DIR
                             the regression check: compare each run folder under OUTROOT (one --out each) with
                             the rows recorded in DIR for the joltc build the runs loaded (see BaselineCheck.cs).
                             Exit 0 passed, 1 failed, 3 skipped: no baseline for this native
  --record-baseline OUTROOT --baselines DIR [--leave-out RUN[,RUN..]]
                             record each run's summary.csv as the baseline of the build the runs loaded

The summary table always goes to standard output. Nothing is written anywhere else.";

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--stress")
                return Stress.Run(args[1..], Console.Out);
            if (args.Length > 0 && args[0] is "--check-baseline" or "--record-baseline")
                return BaselineCheck.Run(args, Console.Out);
            return Run(args, Console.Out);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    public static int Run(string[] args, TextWriter output)
    {
        var o = new HarnessOptions();
        List<Scenario> scenarios = Harness.Scenarios.ToList();
        double[] rates = { 11.0 };
        double[] physicsRates = { o.PhysicsRateHz };
        float[] slopes = null;
        string outDir = null;
        float[] crashSpeeds = { 1f }, crashOffsets = { 0f }, crashAngles = { 0f };
        float[] shotBalls = { o.ShotBall }, shotSpeeds = { o.ShotSpeed };
        bool poolBench = false, physicsRateGiven = false;
        var bench = new PoolBenchOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--help": case "-h": case "/?":
                    output.WriteLine(Usage);
                    return 0;
                case "--list":
                    foreach (Scenario s in Harness.Scenarios)
                        output.WriteLine($"{s.Name,-14} {(s.UsesSlope ? "slopes " + string.Join("/", s.Slopes.Select(f => f.ToString(CultureInfo.InvariantCulture))) : "level"),-18} {s.Description}");
                    return 0;
                case "--scenario":
                    string names = Next();
                    scenarios = names == "all" ? Harness.Scenarios.ToList() : names.Split(',').Select(Harness.Find).ToList();
                    break;
                case "--rate":
                    string r = Next();
                    rates = r == "all" ? Harness.Rates : r.Split(',').Select(x => PositiveDouble(x, a)).ToArray();
                    break;
                case "--physics-rate":
                    physicsRates = Next().Split(',').Select(x => NonNegativeDouble(x, a)).ToArray();
                    physicsRateGiven = true;
                    break;
                case "--pool-bench": poolBench = true; break;
                case "--pools": bench.Pools = Next().Split(',').Select(x => (int)PositiveDouble(x, a)).ToArray(); break;
                case "--handoff":
                    bench.Handoff = Next().Split(',').Select(x => x switch
                    {
                        "off" => false,
                        "on" => true,
                        _ => throw new ArgumentException($"--handoff '{x}': expected off or on"),
                    }).ToArray();
                    break;
                case "--threads": bench.Threads = Next().Split(',').Select(x => (int)NonNegativeDouble(x, a)).ToArray(); break;
                case "--heavy-boxes": bench.HeavyBoxes = (int)NonNegativeDouble(Next(), a); break;
                case "--heavy-car": bench.HeavyCar = true; break;
                case "--heavy-scenes": bench.HeavyScenes = (int)PositiveDouble(Next(), a); break;
                case "--light": bench.LightScenes = (int)NonNegativeDouble(Next(), a); break;
                case "--seconds": bench.Seconds = PositiveDouble(Next(), a); break;
                case "--unpaced": bench.Unpaced = true; break;
                case "--slope":
                    slopes = Next().Split(',').Select(x => (float)NonNegativeDouble(x, a)).ToArray();
                    if (slopes.Any(s => s >= 60f)) throw new ArgumentException("--slope must be under 60 degrees");
                    break;
                case "--duration": o.Duration = (float)PositiveDouble(Next(), a); break;
                case "--hold": o.Hold = (float)NonNegativeDouble(Next(), a); break;
                case "--camera-noise": o.CameraNoiseDegrees = (float)NonNegativeDouble(Next(), a); break;
                case "--camera-rate": o.CameraRateHz = PositiveDouble(Next(), a); break;
                case "--camera-seed": o.CameraSeed = (int)NonNegativeDouble(Next(), a); break;
                case "--keyrepeat": o.KeyRepeat = (float)PositiveDouble(Next(), a); break;
                case "--keydelay": o.KeyDelay = (float)NonNegativeDouble(Next(), a); break;
                case "--startspeed": o.StartSpeed = (float)NonNegativeDouble(Next(), a); break;
                case "--feed":
                    string feed = Next();
                    o.Feed = feed switch
                    {
                        "heartbeat" => InputFeed.Heartbeat,
                        "region" => InputFeed.Region,
                        _ => throw new ArgumentException($"--feed '{feed}': expected heartbeat or region"),
                    };
                    break;
                case "--terrain": o.TerrainFile = Next(); break;
                case "--trace-dir": o.TraceDir = Next(); break;
                case "--jolt":
                    string kv = Next();
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"--jolt '{kv}': expected KEY=VALUE");
                    o.Jolt[kv[..eq].Trim()] = kv[(eq + 1)..].Trim();
                    break;
                case "--vparam": o.VehicleParams.Add(VehicleParamSetting.Parse(Next())); break;
                case "--vflag": o.VehicleFlags.Add(VehicleFlagSetting.Parse(Next())); break;
                case "--crash-speed": crashSpeeds = Next().Split(',').Select(x => (float)PositiveDouble(x, a)).ToArray(); break;
                case "--crash-offset": crashOffsets = Next().Split(',').Select(x => (float)SignedDouble(x, a)).ToArray(); break;
                case "--crash-angle": crashAngles = Next().Split(',').Select(x => (float)SignedDouble(x, a)).ToArray(); break;
                case "--ball": shotBalls = Next().Split(',').Select(x => (float)PositiveDouble(x, a)).ToArray(); break;
                case "--shot-speed": shotSpeeds = Next().Split(',').Select(x => (float)PositiveDouble(x, a)).ToArray(); break;
                case "--start-offset":
                    float[] d = Next().Split(',').Select(x => (float)SignedDouble(x, a)).ToArray();
                    if (d.Length != 2) throw new ArgumentException("--start-offset needs two numbers: DX,DY");
                    (o.StartOffsetX, o.StartOffsetY) = (d[0], d[1]);
                    break;
                case "--sim-defaults": o.SimulatorDefaults = true; break;
                case "--no-sim-defaults": o.SimulatorDefaults = false; break;
                case "--no-sim-prims": o.SimulatorPrims = false; break;
                case "--no-sim-avatars": o.SimulatorAvatars = false; break;
                case "--no-region-mesher": o.RegionMesher = false; break;
                case "--no-warm-up-step": o.WarmUpStep = false; break;
                case "--no-shipped-config": o.ShippedConfig = false; break;
                case "--legacy-defaults": o.SimulatorDefaults = o.RegionMesher = o.WarmUpStep = o.ShippedConfig = false; break;
                case "--vehicle-script-again": o.VehicleScriptAgain = true; break;
                case "--vehicle-restore":
                    string route = Next();
                    o.VehicleRoute = route switch
                    {
                        "rez" => VehicleRoute.Rez,
                        "region-start" => VehicleRoute.RegionStart,
                        "copy" => VehicleRoute.Copy,
                        "crossing" => VehicleRoute.Crossing,
                        "detach" => VehicleRoute.Detach,
                        "physics-off-on" => VehicleRoute.PhysicsOffOn,
                        _ => throw new ArgumentException($"--vehicle-restore '{route}': expected rez, region-start, copy, crossing, detach or physics-off-on"),
                    };
                    break;
                case "--out": outDir = Next(); break;
                default: throw new ArgumentException($"unknown argument '{a}'");
            }
        }

        if (outDir != null)
            Directory.CreateDirectory(outDir);

        if (poolBench)
        {
            bench.RateHz = rates[0];
            if (physicsRateGiven)
                bench.PhysicsRateHz = physicsRates[0];
            var lines = new StringBuilder().Append(PoolBenchResult.Header).Append('\n');
            var sceneLines = new StringBuilder().Append(PoolBenchResult.SceneHeader).Append('\n');
            foreach (PoolBenchResult res in PoolBench.Run(bench, output))
            {
                lines.Append(res.Line()).Append('\n');
                foreach (string l in res.SceneLines)
                    sceneLines.Append(l).Append('\n');
            }
            if (outDir != null)
            {
                File.WriteAllText(Path.Combine(outDir, "pool-bench.csv"), lines.ToString());
                File.WriteAllText(Path.Combine(outDir, "pool-bench-scenes.csv"), sceneLines.ToString());
            }
            return 0;
        }

        var summary = new StringBuilder();
        summary.Append(RunResult.SummaryHeader).Append('\n');
        output.WriteLine(RunResult.SummaryHeader);
        foreach (Scenario sc in scenarios)
        {
            float[] runSlopes = sc.UsesSlope ? (slopes ?? sc.Slopes) : new[] { 0f };
            float[] runBalls = sc.UsesShot ? shotBalls : new[] { o.ShotBall };
            float[] runShotSpeeds = sc.UsesShot ? shotSpeeds : new[] { o.ShotSpeed };
            foreach (float slope in runSlopes)
                foreach (double rate in rates)
                foreach (double physicsRate in physicsRates)
                foreach (float crashSpeed in crashSpeeds)
                foreach (float crashOffset in crashOffsets)
                foreach (float crashAngle in crashAngles)
                foreach (float shotBall in runBalls)
                foreach (float shotSpeed in runShotSpeeds)
                {
                    var opts = Copy(o, rate, slope);
                    opts.PhysicsRateHz = physicsRate;
                    opts.CrashSpeed = crashSpeed;
                    opts.CrashOffset = crashOffset;
                    opts.CrashAngle = crashAngle;
                    opts.ShotBall = shotBall;
                    opts.ShotSpeed = shotSpeed;
                    RunResult res = Harness.Run(sc, opts);
                    string line = res.SummaryLine();
                    output.WriteLine(line);
                    foreach (CollisionWatch w in res.Watches)
                        output.WriteLine($"  events {w}");   // console only: summary.csv keeps its columns
                    foreach (string part in res.PartLines())
                        output.WriteLine(part);
                    summary.Append(line).Append('\n');
                    if (outDir != null)
                    {
                        File.WriteAllText(Path.Combine(outDir, res.Name + ".csv"), res.ToCsv());
                        string parts = res.PartsToCsv();
                        if (parts != null)
                            File.WriteAllText(Path.Combine(outDir, res.Name + "-parts.csv"), parts);
                    }
                }
        }
        if (outDir != null)
        {
            File.WriteAllText(Path.Combine(outDir, "summary.csv"), summary.ToString());
            // The runs above loaded the native; this returns that load, and names the build for the regression check.
            BaselineCheck.WriteNativeFile(outDir, Backend.JoltNative.EnsureLoaded(allowUnrecorded: true));
        }
        return 0;
    }

    private static HarnessOptions Copy(HarnessOptions o, double rate, float slope)
    {
        var c = new HarnessOptions { RateHz = rate, SlopeDeg = slope, Duration = o.Duration, Hold = o.Hold, KeyRepeat = o.KeyRepeat, Feed = o.Feed, KeyDelay = o.KeyDelay, StartSpeed = o.StartSpeed, StartOffsetX = o.StartOffsetX, StartOffsetY = o.StartOffsetY, SimulatorPrims = o.SimulatorPrims, SimulatorAvatars = o.SimulatorAvatars, RegionMesher = o.RegionMesher, WarmUpStep = o.WarmUpStep, ShippedConfig = o.ShippedConfig, VehicleRoute = o.VehicleRoute, VehicleScriptAgain = o.VehicleScriptAgain, TerrainFile = o.TerrainFile, TraceDir = o.TraceDir };
        foreach (KeyValuePair<string, string> kv in o.Jolt) c.Jolt[kv.Key] = kv.Value;
        c.VehicleParams.AddRange(o.VehicleParams);
        c.VehicleFlags.AddRange(o.VehicleFlags);
        return c;
    }

    private static double PositiveDouble(string s, string arg)
    {
        double v = NonNegativeDouble(s, arg);
        if (v <= 0) throw new ArgumentException($"{arg} must be above 0");
        return v;
    }

    private static double SignedDouble(string s, string arg)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new ArgumentException($"{arg}: '{s}' is not a number");
        return v;
    }

    private static double NonNegativeDouble(string s, string arg)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < 0)
            throw new ArgumentException($"{arg}: '{s}' is not a number of 0 or more");
        return v;
    }
}
