/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The limits on what scripts can make a region's physics do: the cost of a ray cast ([Jolt] RayCastBudgetMs,
/// RayCastSimulatorBudgetMs, RayCastMaxTestedHits, RayCastMaxHits) and pushes on avatars ([Jolt] AvatarPushMaxSpeed,
/// AvatarPushRecovery).
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RayCastAndPushLimitTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float Gravity = 9.80665f;

    private const RayFilterFlags AllTypes =
        RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical | RayFilterFlags.BackFaceCull;

    // Every scene here measures its ray cast budgets on a clock the test drives, one microsecond a reading (a cast costs
    // a few microseconds to a few hundred, as on a quiet machine), so no test depends on how fast the machine runs at
    // the moment: a cast that is descheduled while it holds the region's lock would otherwise be charged that time.
    private static JoltScene NewScene(float physicsRate, params (string key, string value)[] keys)
        => NewScene(physicsRate, new StepClock { Step = 1 }, keys);

    // rayClock: the clock the ray cast budgets are measured with (null: the wall clock).
    private static JoltScene NewScene(float physicsRate, TimeProvider rayClock, params (string key, string value)[] keys)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        // Always set: 0 is one step per heartbeat, and the module's own default is 45 Hz.
        jolt.Set("PhysicsStepRate", physicsRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach ((string key, string value) in keys)
            jolt.Set(key, value);
        var scene = new JoltScene { RayCastClock = rayClock };
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static PhysicsActor Box(JoltScene scene, uint localId, Vector3 position, Vector3 size, bool physical, PrimitiveBaseShape shape = null)
    {
        PhysicsActor pa = scene.AddPrimShape("test box", shape ?? PrimitiveBaseShape.CreateBox(), position, size, Quaternion.Identity, physical, localId);
        if (physical)
            pa.Density = 1000f;
        return pa;
    }

    // A row of small boxes along y 128 from x 20, the kind of pile a long ray crosses.
    private static void Row(JoltScene scene, int boxes, uint firstId = 5000)
    {
        for (int i = 0; i < boxes; i++)
            Box(scene, firstId + (uint)i, new Vector3(20f + i * 0.5f, 128f, Ground + 1f), new Vector3(0.25f, 0.5f, 0.5f), false);
    }

    private static List<ContactResult> Cast(JoltScene scene, Vector3 from, Vector3 dir, float length, int count)
        => (List<ContactResult>)scene.RaycastWorld(from, dir, length, count, AllTypes);

    // ------------------------------------------------------------------ keys

    [Fact]
    public void The_new_keys_default_to_their_safe_values_parse_and_fall_back()
    {
        var d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(5f, d.RayCastBudgetMs);
        Assert.Equal(5f, d.RayCastSimulatorBudgetMs);
        Assert.Equal(1024, d.RayCastMaxTestedHits);
        Assert.Equal(256, d.RayCastMaxHits);
        Assert.Equal(100f, d.AvatarPushMaxSpeed);
        Assert.Equal(5f, d.AvatarPushRecovery);
        PhysicsBackendSettings ds = d.ToBackendSettings(256, 256);
        Assert.Equal(5f, ds.RayCastBudgetMs);
        Assert.Equal(5f, ds.RayCastSimulatorBudgetMs);
        Assert.Equal(1024, ds.RayCastMaxTestedHits);
        Assert.Equal(100f, ds.AvatarPushMaxSpeed);
        Assert.Equal(5f, ds.AvatarPushRecovery);
        Assert.Equal(PhysicsBackendSettings.Default.RayCastBudgetMs, ds.RayCastBudgetMs);
        Assert.Equal(PhysicsBackendSettings.Default.RayCastSimulatorBudgetMs, ds.RayCastSimulatorBudgetMs);
        Assert.Equal(PhysicsBackendSettings.Default.AvatarPushMaxSpeed, ds.AvatarPushMaxSpeed);

        var src = new IniConfigSource();
        IConfig j = src.AddConfig("Jolt");
        j.Set("RayCastBudgetMs", "2.5");
        j.Set("RayCastSimulatorBudgetMs", "7.5");
        j.Set("RayCastMaxTestedHits", "300");
        j.Set("RayCastMaxHits", "32");
        j.Set("AvatarPushMaxSpeed", "0");
        j.Set("AvatarPushRecovery", "2");
        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(src, warnings);
        Assert.Empty(warnings);
        PhysicsBackendSettings cs = c.ToBackendSettings(256, 256);
        Assert.Equal(2.5f, cs.RayCastBudgetMs);
        Assert.Equal(7.5f, cs.RayCastSimulatorBudgetMs);
        Assert.Equal(300, cs.RayCastMaxTestedHits);
        Assert.Equal(32, c.RayCastMaxHits);
        Assert.Equal(0f, cs.AvatarPushMaxSpeed);
        Assert.Equal(2f, cs.AvatarPushRecovery);

        foreach ((string key, string value) in new[]
                 {
                     ("RayCastBudgetMs", "0"), ("RayCastBudgetMs", "NaN"), ("RayCastSimulatorBudgetMs", "0"), ("RayCastSimulatorBudgetMs", "-1"),
                     ("RayCastMaxTestedHits", "0"), ("RayCastMaxHits", "257"),
                     ("RayCastMaxHits", "0"), ("AvatarPushMaxSpeed", "-1"), ("AvatarPushRecovery", "Infinity"),
                 })
        {
            var bad = new IniConfigSource();
            bad.AddConfig("Jolt").Set(key, value);
            var w = new List<string>();
            var b = JoltConfig.FromConfig(bad, w);
            Assert.Single(w);
            Assert.Contains(key, w[0]);
            Assert.Equal(d.ToBackendSettings(256, 256), b.ToBackendSettings(256, 256));
            Assert.Equal(d.RayCastMaxHits, b.RayCastMaxHits);
        }
    }

    // ------------------------------------------------------------------ ray casts

    // Ordinary casts through a scene of terrain, boxes, spheres, physical bodies and an avatar give, hit for hit and in
    // order, what the unbounded RayCastAll gives (the path llCastRay took before).
    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Ordinary_casts_return_what_they_returned_before_in_order(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            var rnd = new Random(20261005);
            uint id = 100;
            for (int i = 0; i < 40; i++)
            {
                var p = new Vector3(40f + (float)rnd.NextDouble() * 176f, 40f + (float)rnd.NextDouble() * 176f, Ground + 0.5f + (float)rnd.NextDouble() * 6f);
                var size = new Vector3(0.3f + (float)rnd.NextDouble() * 3f, 0.3f + (float)rnd.NextDouble() * 3f, 0.3f + (float)rnd.NextDouble() * 3f);
                bool sphere = i % 4 == 0;
                Box(scene, id++, p, size, physical: i % 3 == 0, sphere ? PrimitiveBaseShape.CreateSphere() : null);
            }
            Row(scene, 60);
            scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            for (int k = 0; k < 22; k++)
                scene.Simulate(Heartbeat);

            IPhysicsBackend backend = scene.Backend;
            int compared = 0, withHits = 0, multi = 0;
            for (int i = 0; i < 400; i++)
            {
                var from = new Vector3(30f + (float)rnd.NextDouble() * 196f, 30f + (float)rnd.NextDouble() * 196f, Ground + 0.2f + (float)rnd.NextDouble() * 10f);
                var dir = new Vector3((float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 0.6f - 0.4f);
                if (i % 5 == 0)
                {
                    from = new Vector3(5f + (float)rnd.NextDouble() * 20f, 128f, Ground + 1f);   // along the row: many hits
                    dir = Vector3.UnitX;
                }
                dir.Normalize();
                float length = 1f + (float)rnd.NextDouble() * 120f;
                // Keep the whole ray inside the region, where the clip changes nothing.
                Vector3 end = from + dir * length;
                if (end.X < 0f || end.X > Size || end.Y < 0f || end.Y > Size)
                    continue;
                int count = new[] { 1, 4, 16 }[i % 3];

                scene.Simulate(Heartbeat);   // a fresh heartbeat: the budget never interferes here
                List<ContactResult> now = Cast(scene, from, dir, length, count);
                // A ray that starts inside a convex shape is not solid to it ("llCastRay will not detect a prim if the line starts
                // inside the prim"): the older cast reported that shape at distance 0, so those hits are left out of what it is
                // compared with, asking it for a few more to fill the count.
                var wider = new RayHit[count + 8];
                int all = backend.RayCastAll(new SVector3(from.X, from.Y, from.Z), new SVector3(dir.X, dir.Y, dir.Z), length, QueryFilter.Default, wider);
                RayHit[] before = wider.Take(all).Where(h => h.Distance > 1e-4f).Take(count).ToArray();
                int n = before.Length;

                Assert.Equal(n, now.Count);
                for (int h = 0; h < n; h++)
                {
                    Assert.Equal(before[h].UserData, now[h].ConsumerID);
                    Assert.True(Vector3.Distance(new Vector3(before[h].Point.X, before[h].Point.Y, before[h].Point.Z), now[h].Pos) < 1e-4f, $"ray {i} hit {h}: point");
                    Assert.True(Vector3.Distance(new Vector3(before[h].Normal.X, before[h].Normal.Y, before[h].Normal.Z), now[h].Normal) < 1e-4f, $"ray {i} hit {h}: normal");
                    Assert.True(MathF.Abs(before[h].Distance - now[h].Depth) < 1e-4f, $"ray {i} hit {h}: distance");
                    if (h > 0)
                        Assert.True(now[h].Depth >= now[h - 1].Depth, $"ray {i}: hits out of order");
                }
                compared++;
                if (n > 0) withHits++;
                if (n > 1) multi++;
            }
            Assert.True(compared > 150, $"only {compared} rays compared");
            Assert.True(withHits > 60, $"only {withHits} rays hit anything");
            Assert.True(multi > 20, $"only {multi} rays had more than one hit");
            Assert.Equal(0, scene.CapacityStats().RayCastsRefused);
            Assert.Equal(0, scene.CapacityStats().RayCastsCutShort);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_cast_along_a_row_returns_its_closest_boxes_and_a_ray_is_cut_to_the_region()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            List<ContactResult> hits = Cast(scene, from, Vector3.UnitX, 300f, 16);
            Assert.Equal(16, hits.Count);
            for (int i = 0; i < 16; i++)
                Assert.Equal(5000u + (uint)i, hits[i].ConsumerID);

            // A ray far longer than the region: the same hits, distances from the caller's start.
            List<ContactResult> longRay = Cast(scene, from, Vector3.UnitX, 1e30f, 16);
            Assert.Equal(hits.Select(h => h.ConsumerID), longRay.Select(h => h.ConsumerID));
            for (int i = 0; i < 16; i++)
                Assert.True(MathF.Abs(hits[i].Depth - longRay[i].Depth) < 1e-3f);

            // Starting far outside the region and coming in: clipped to where it enters, depth still from its start.
            List<ContactResult> fromAfar = Cast(scene, new Vector3(-5000f, 128f, Ground + 1f), Vector3.UnitX, 6000f, 1);
            Assert.Single(fromAfar);
            Assert.Equal(5000u, fromAfar[0].ConsumerID);
            Assert.True(MathF.Abs(fromAfar[0].Depth - (5000f + hits[0].Depth + 10f)) < 1e-2f, $"depth {fromAfar[0].Depth}");

            long castsBefore = scene.CapacityStats().RayCasts;
            Assert.Empty(Cast(scene, new Vector3(-100f, 128f, Ground + 1f), -Vector3.UnitX, 1000f, 4));   // never in the region
            Assert.Empty(Cast(scene, from, Vector3.UnitX, float.NaN, 4));
            Assert.Empty(Cast(scene, from, Vector3.UnitX, float.PositiveInfinity, 4));
            Assert.Empty(Cast(scene, from, Vector3.UnitX, -5f, 4));
            Assert.Empty(Cast(scene, new Vector3(float.NaN, 128f, 30f), Vector3.UnitX, 10f, 4));
            Assert.Empty(Cast(scene, from, new Vector3(float.NaN, 0f, 0f), 10f, 4));
            Assert.Equal(castsBefore, scene.CapacityStats().RayCasts);   // none reached the engine

            // More hits than RayCastMaxHits are never returned.
            Assert.Equal(256, Cast(scene, from, Vector3.UnitX, 300f, 100000).Count);
        }
        finally { scene.Dispose(); }
    }

    // Second Life: "RCERR_CAST_TIME_EXCEEDED -3: The raycast failed because the parcel or agent has exceeded the maximum
    // time allowed for raycasting. This resource pool is continually replenished, so waiting a few frames and retrying
    // is likely to succeed." The 4-argument caller (Phlox llCastRay) reports an exception from RaycastWorld as that
    // code; the 5-argument caller gets no hits.
    [Fact]
    public void A_refused_or_cut_short_cast_is_reported_as_cast_time_exceeded_and_the_next_heartbeat_casts_again()
    {
        JoltScene scene = NewScene(0f, ("RayCastBudgetMs", "1"), ("RayCastSimulatorBudgetMs", "1"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            // Spend this heartbeat's 1 ms of script time; the casts after that are refused.
            RayCastTimeExceededException refused = SpendScriptBudget(scene, from);
            Assert.NotNull(refused);
            // Spend the other budget's 1 ms through the 5-argument caller; it then gets no hits.
            Assert.True(SpendSimulatorBudget(scene, from), "the 5-argument casts never ran out of time");
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 16));   // the 5-argument caller: no hits
            Assert.True(scene.CapacityStats().RayCastsRefused >= 2);

            scene.Simulate(Heartbeat);   // a new heartbeat: the time is there again
            Assert.Equal(4, scene.RaycastWorld(from, Vector3.UnitX, 300f, 4).Count);
        }
        finally { scene.Dispose(); }

        // Cut short: a cast that tests more hits than one cast may.
        scene = NewScene(0f, ("RayCastMaxTestedHits", "10"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);
            var e = Assert.Throws<RayCastTimeExceededException>(() => scene.RaycastWorld(from, Vector3.UnitX, 300f, 16));
            Assert.Equal(RayCastStatus.CutShort, e.Status);
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 16));
            Assert.Equal(2, scene.CapacityStats().RayCastsCutShort);
            Assert.Equal(3, scene.RaycastWorld(from, Vector3.UnitX, 11f, 16).Count);   // a ray crossing three boxes: fine
        }
        finally { scene.Dispose(); }
    }

    // The 4-argument caller's casts until one is refused (cast after cast through the row spends the script budget).
    private static RayCastTimeExceededException SpendScriptBudget(JoltScene scene, Vector3 from)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            // The cast that runs out of the time is cut short; the ones after it are refused.
            try { scene.RaycastWorld(from, Vector3.UnitX, 300f, 16); }
            catch (RayCastTimeExceededException e) { if (e.Status == RayCastStatus.Refused) return e; }
        }
        return null;
    }

    // The 5-argument caller's casts until the region refuses one (it gets no status, so read the counter).
    private static bool SpendSimulatorBudget(JoltScene scene, Vector3 from)
    {
        long before = scene.CapacityStats().SimulatorRayCasts.Refused;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Cast(scene, from, Vector3.UnitX, 300f, 16);
            if (scene.CapacityStats().SimulatorRayCasts.Refused > before)
                return true;
        }
        return false;
    }

    // The two budgets are drawn separately: a script flood that spends RayCastBudgetMs leaves the 5-argument entry
    // (rez placement, landing, YEngine's llCastRay) its own time in the same heartbeat.
    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void With_the_script_budget_spent_a_five_argument_cast_in_the_same_heartbeat_still_hits(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate, ("RayCastBudgetMs", "0.1"));
        try
        {
            Row(scene, 400);
            Box(scene, 7000u, new Vector3(200f, 200f, Ground + 5f), new Vector3(4f, 4f, 0.5f), false);   // a platform
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            Assert.NotNull(SpendScriptBudget(scene, from));
            Assert.Throws<RayCastTimeExceededException>(() => scene.RaycastWorld(from, Vector3.UnitX, 300f, 16));   // still spent

            List<ContactResult> hits = Cast(scene, from, Vector3.UnitX, 300f, 16);
            Assert.Equal(16, hits.Count);
            Assert.Equal(5000u, hits[0].ConsumerID);
            // The landing ray after a teleport (ScenePresence.MakeRootAgent: from 300 m down, prims and avatars, 30 hits)
            // finds the platform's top.
            var down = (List<ContactResult>)scene.RaycastWorld(new Vector3(200f, 200f, 300f), -Vector3.UnitZ, 300f, 30,
                RayFilterFlags.BackFaceCull | RayFilterFlags.PrimsNonPhantomAgents);
            Assert.Single(down);
            Assert.Equal(7000u, down[0].ConsumerID);
            Assert.True(MathF.Abs(down[0].Pos.Z - (Ground + 5.25f)) < 0.01f, $"landed at {down[0].Pos.Z}");

            PhysicsCapacityStats s = scene.CapacityStats();
            Assert.True(s.ScriptRayCasts.Refused >= 2);
            Assert.Equal(0, s.SimulatorRayCasts.Refused);
            Assert.Equal(0, s.SimulatorRayCasts.CutShort);
            Assert.Equal(2, s.SimulatorRayCasts.Casts);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void With_the_simulator_budget_spent_a_script_cast_in_the_same_heartbeat_still_hits(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate, ("RayCastSimulatorBudgetMs", "0.1"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            Assert.True(SpendSimulatorBudget(scene, from), "the 5-argument casts never ran out of time");
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 16));   // still spent

            List<ContactResult> hits = scene.RaycastWorld(from, Vector3.UnitX, 300f, 16);   // no exception: not refused
            Assert.Equal(16, hits.Count);
            Assert.Equal(5000u, hits[0].ConsumerID);

            PhysicsCapacityStats s = scene.CapacityStats();
            Assert.True(s.SimulatorRayCasts.Refused >= 2);
            Assert.Equal(0, s.ScriptRayCasts.Refused);
            Assert.Equal(0, s.ScriptRayCasts.CutShort);
            Assert.Equal(1, s.ScriptRayCasts.Casts);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Both_budgets_spent_in_one_heartbeat_are_there_again_in_the_next()
    {
        JoltScene scene = NewScene(0f, ("RayCastBudgetMs", "0.1"), ("RayCastSimulatorBudgetMs", "0.1"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);

            Assert.NotNull(SpendScriptBudget(scene, from));
            Assert.True(SpendSimulatorBudget(scene, from), "the 5-argument casts never ran out of time");
            Assert.Throws<RayCastTimeExceededException>(() => scene.RaycastWorld(from, Vector3.UnitX, 300f, 4));
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 4));

            scene.Simulate(Heartbeat);
            Assert.Equal(4, scene.RaycastWorld(from, Vector3.UnitX, 300f, 4).Count);
            Assert.Equal(4, Cast(scene, from, Vector3.UnitX, 300f, 4).Count);
        }
        finally { scene.Dispose(); }
    }

    // RayCastMaxTestedHits and RayCastMaxHits bound one cast, whichever budget it is charged to.
    [Fact]
    public void The_per_cast_limits_apply_to_both_budgets()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);
            Assert.Equal(256, scene.RaycastWorld(from, Vector3.UnitX, 300f, 100000).Count);
            Assert.Equal(256, Cast(scene, from, Vector3.UnitX, 300f, 100000).Count);
        }
        finally { scene.Dispose(); }

        scene = NewScene(0f, ("RayCastMaxTestedHits", "10"));
        try
        {
            Row(scene, 400);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);
            var e = Assert.Throws<RayCastTimeExceededException>(() => scene.RaycastWorld(from, Vector3.UnitX, 300f, 16));
            Assert.Equal(RayCastStatus.CutShort, e.Status);
            Assert.Empty(Cast(scene, from, Vector3.UnitX, 300f, 16));
            PhysicsCapacityStats s = scene.CapacityStats();
            Assert.Equal(1, s.ScriptRayCasts.CutShort);
            Assert.Equal(1, s.SimulatorRayCasts.CutShort);
        }
        finally { scene.Dispose(); }
    }

    // Nothing in RayFilterFlags marks a script's cast yet, so every 5-argument cast goes to the simulator's budget,
    // whatever its flags.
    [Fact]
    public void Every_five_argument_cast_is_charged_to_the_simulator_budget()
    {
        foreach (RayFilterFlags f in Enum.GetValues<RayFilterFlags>())
            Assert.Equal(RayCastBudget.Simulator, JoltScene.BudgetFor(f));
        Assert.Equal(RayCastBudget.Simulator, JoltScene.BudgetFor((RayFilterFlags)0xFFFF));
    }

    // A clock the test drives, for the ray cast budgets (PhysicsBackendSettings.RayCastClock). It counts microseconds
    // and moves on by Step every time it is read, so what a cast costs is how often the module reads it: the same on
    // every machine and under any load.
    private sealed class StepClock : TimeProvider
    {
        private long _now;
        public long Step;
        public override long TimestampFrequency => 1_000_000;
        public override long GetTimestamp() => Interlocked.Add(ref _now, Step) - Step;   // the reading, then the step
        public long Now => Interlocked.Read(ref _now);
    }

    private const long ClockStep = 250;       // 0.25 ms a reading
    private const long BudgetTicks = 5000;    // RayCastBudgetMs and RayCastSimulatorBudgetMs, 5 ms (the defaults)

    private static long ToTicks(double ms) => (long)Math.Round(ms * 1000.0);

    // What the script budget admits, refuses and charges, on a clock the test drives. A cast is admitted while the
    // heartbeat has spent less than the budget, and charged the clock time from its first reading to its last. A cast
    // in flight is cut short at the first hit that finds the time left gone, so it runs past it by at most one reading
    // and the one at its end. Once the budget is spent every cast is refused without being charged, until the next
    // heartbeat.
    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Casts_are_admitted_until_the_budget_is_spent_and_charged_the_time_they_took(float physicsRate)
    {
        var clock = new StepClock();
        JoltScene scene = NewScene(physicsRate, clock);
        try
        {
            Row(scene, 2000);
            scene.Simulate(Heartbeat);
            var from = new Vector3(10f, 128f, Ground + 1f);
            clock.Step = ClockStep;

            long mostInOneHeartbeat = 0;
            int cutShortSeen = 0;
            for (int heartbeat = 0; heartbeat < 4; heartbeat++)
            {
                scene.Simulate(Heartbeat);
                long used = 0;
                int admitted = 0, refused = 0;
                for (int i = 0; i < 40; i++)
                {
                    long chargedBefore = ToTicks(scene.CapacityStats().ScriptRayCasts.MsTotal);
                    long readBefore = clock.Now;
                    RayCastStatus status = RayCastStatus.Ok;
                    int hits = -1;
                    try { hits = scene.RaycastWorld(from, Vector3.UnitX, 300f, 4).Count; }
                    catch (RayCastTimeExceededException e) { status = e.Status; }
                    long moved = clock.Now - readBefore;
                    long charged = ToTicks(scene.CapacityStats().ScriptRayCasts.MsTotal) - chargedBefore;

                    if (used >= BudgetTicks)
                    {
                        Assert.Equal(RayCastStatus.Refused, status);
                        Assert.Equal(0, moved);     // a refused cast does not even read the clock
                        Assert.Equal(0, charged);
                        refused++;
                        continue;
                    }

                    admitted++;
                    Assert.NotEqual(RayCastStatus.Refused, status);
                    Assert.Equal(moved - ClockStep, charged);   // every reading but the last moved the clock inside the cast
                    long left = BudgetTicks - used;
                    if (status == RayCastStatus.CutShort)
                    {
                        cutShortSeen++;
                        Assert.InRange(charged, left + ClockStep + 1, left + 2 * ClockStep);
                    }
                    else
                    {
                        Assert.Equal(4, hits);
                        Assert.InRange(charged, ClockStep, left + ClockStep);
                    }
                    used += charged;
                }

                Assert.True(admitted >= 2, $"heartbeat {heartbeat}: {admitted} casts admitted");
                Assert.True(refused > 0, $"heartbeat {heartbeat}: the budget was never spent");
                Assert.InRange(used, BudgetTicks, BudgetTicks + 2 * ClockStep);
                mostInOneHeartbeat = Math.Max(mostInOneHeartbeat, used);
            }

            PhysicsCapacityStats s = scene.CapacityStats();
            Assert.Equal(mostInOneHeartbeat, ToTicks(s.RayCastMsMaxHeartbeat));
            Assert.Equal(cutShortSeen, s.ScriptRayCasts.CutShort);
            Assert.Equal(0, s.SimulatorRayCasts.Casts);
        }
        finally { scene.Dispose(); }
    }

    // Script threads casting as fast as they can, long rays through a row of 2000 boxes asking for 256 hits each, while
    // the region's heartbeats run. A cast holds the region's physics lock, and the heartbeat's step waits for it, so the
    // ray cast time in one heartbeat is what delays it: the casts must stop being admitted once the budget is spent,
    // and no heartbeat may be charged more than the budget and the overrun of the one cast in flight when it ran out.
    // On a clock the test drives, so the bound is checked exactly and the machine's load cannot stretch it.
    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_flood_of_casts_from_several_threads_is_held_to_the_budget_in_every_heartbeat(float physicsRate)
    {
        var clock = new StepClock();
        JoltScene scene = NewScene(physicsRate, clock);
        try
        {
            Row(scene, 2000);
            for (int i = 0; i < 100; i++)
                Box(scene, 9000u + (uint)i, new Vector3(60f + (i % 10) * 3f, 60f + (i / 10) * 3f, Ground + 3f), new Vector3(1f, 1f, 1f), true);
            for (int k = 0; k < 11; k++)
                scene.Simulate(Heartbeat);
            clock.Step = ClockStep;

            using var stop = new CancellationTokenSource();
            var threads = new Thread[4];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                        Cast(scene, new Vector3(10f, 128f, Ground + 1f), Vector3.UnitX, 1000f, 256);
                }) { IsBackground = true };
                threads[t].Start();
            }
            int spentHeartbeats = 0;
            try
            {
                for (int heartbeat = 0, missed = 0; heartbeat < 33 && missed <= 33 - 30; heartbeat++)
                {
                    long refusedBefore = scene.CapacityStats().SimulatorRayCasts.Refused;
                    scene.Simulate(Heartbeat);
                    // Wait for the flood to spend this heartbeat's budget (a refusal), not for a set time. The cap is
                    // generous, so a stalled machine cannot reach it; once more heartbeats have missed it than the check
                    // below allows, the rest are not waited for.
                    var sw = Stopwatch.StartNew();
                    while (scene.CapacityStats().SimulatorRayCasts.Refused == refusedBefore && sw.Elapsed < TimeSpan.FromSeconds(60))
                        Thread.Yield();
                    if (scene.CapacityStats().SimulatorRayCasts.Refused > refusedBefore)
                        spentHeartbeats++;
                    else
                        missed++;
                }
            }
            finally
            {
                stop.Cancel();
                foreach (Thread t in threads)
                    t.Join();
            }

            PhysicsCapacityStats s = scene.CapacityStats();
            Assert.True(spentHeartbeats >= 30, $"the flood spent the budget in only {spentHeartbeats} of 33 heartbeats");
            Assert.True(s.SimulatorRayCasts.Casts > s.SimulatorRayCasts.Refused, "no cast was ever admitted");
            Assert.InRange(ToTicks(s.RayCastMsMaxHeartbeat), BudgetTicks, BudgetTicks + 2 * ClockStep);
            Assert.Equal(0, s.ScriptRayCasts.Casts);
        }
        finally { scene.Dispose(); }
    }

    // The harness's raycast-cost scenario: what one cast through the row costs, read from the region's counters.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_harness_measures_what_a_cast_costs(double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("raycast-cost"),
            new HarnessOptions { PhysicsRateHz = physicsRate, RayCastClock = new StepClock { Step = 1 } });
        Summary m = r.Summary;
        Assert.Equal((long)Harness.Harness.RayCastsPerHeartbeat * m.Steps, m.RayCasts);
        Assert.Equal(0, m.RayCastsRefused);
        Assert.Equal(0, m.RayCastsCutShort);
        Assert.True(m.RayCastMicrosPerCast > 0 && m.RayCastMicrosPerCast < 2000, $"{m.RayCastMicrosPerCast} us per cast");
    }

    // ------------------------------------------------------------------ pushes

    // llPushObject on an avatar: its velocity changes by impulse / mass (the push's distance attenuation is applied
    // by the script engine before it reaches the actor). Pushed straight up with 6 m/s it flies as a jump of 6 m/s
    // does: up v^2 / 2g.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_single_push_moves_an_avatar_as_documented(double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("push-avatar"), new HarnessOptions { PhysicsRateHz = physicsRate });
        float v = Harness.Harness.PushAvatarSpeed;
        float peak = v * v / (2f * Gravity);   // 1.835 m
        float slack = (float)(Gravity * Heartbeat * Heartbeat / 8) + 0.002f;
        Assert.Equal(0, r.Summary.NonFinite);
        Assert.InRange(r.Summary.PeakRise, peak - slack, peak + 0.002f);
        Assert.True(r.Samples.Max(s => s.Velocity.Z) <= v + 1e-3f);
    }

    // A push of 1e9 every heartbeat for six seconds, with the cap set to 10 m/s (the default is 100, which this flood would
    // carry out of the region). Each push spends the avatar's push allowance (AvatarPushMaxSpeed, refilled at
    // AvatarPushRecovery, 5 m/s per second), and pushes never take its speed past the cap. Since the allowance refills
    // slower than gravity takes speed away, the most it can rise is 10^2 / (2 (g - 5)) = 10.4 m.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_flood_of_pushes_cannot_take_an_avatar_past_the_cap(double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("push-flood"), new HarnessOptions { PhysicsRateHz = physicsRate, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });
        const float cap = 10f;
        const float recovery = PhysicsBackendSettings.DefaultAvatarPushRecovery;
        Assert.Equal(0, r.Summary.NonFinite);
        // What pushes add: the horizontal speed, the upward speed, and the speed while going up. Falling back, gravity
        // adds its own.
        List<Sample> pushed = r.Samples.Where(s => s.T <= 7.0).ToList();
        float topHorizontal = pushed.Max(s => s.HorizontalSpeed);
        float topUp = pushed.Max(s => s.Velocity.Z);
        float topRising = pushed.Where(s => s.Velocity.Z >= 0f).Max(s => s.Speed);
        Assert.True(topHorizontal <= cap + 1e-3f, $"{topHorizontal:0.000} m/s horizontal while pushed");
        Assert.True(topUp <= cap + 1e-3f, $"{topUp:0.000} m/s up while pushed");
        Assert.True(topRising <= cap + 1e-3f, $"{topRising:0.000} m/s while rising");
        Assert.True(topHorizontal >= cap * 0.9f, $"{topHorizontal:0.000} m/s: the pushes did not move it");
        float bound = cap * cap / (2f * (Gravity - recovery));
        Assert.True(r.Summary.PeakRise <= bound + 0.05f, $"rose {r.Summary.PeakRise:0.000} m, the bound is {bound:0.000}");
        Assert.True(double.IsNaN(r.Summary.LeftRegionT), "pushed out of the region");
    }

    // A push on a parked car (asleep under the rest rule) and on a box that has settled and slept: it wakes the body
    // and moves it, about the 2 m/s the impulse gives its mass, and nothing becomes non-finite.
    [Theory]
    [InlineData("push-car", 0.0)]
    [InlineData("push-car", 45.0)]
    [InlineData("push-box", 0.0)]
    [InlineData("push-box", 45.0)]
    public void A_push_wakes_a_sleeping_body_or_parked_vehicle_and_moves_it(string scenario, double physicsRate)
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { PhysicsRateHz = physicsRate });
        Assert.Equal(0, r.Summary.NonFinite);
        Sample asleep = r.Samples.Last(s => s.T <= 5.0);
        Assert.Equal(0, asleep.Active);
        List<Sample> after = r.Samples.Where(s => s.T > 5.0 && s.T <= 6.0).ToList();
        Assert.Contains(after, s => s.Active > 0);
        float top = after.Max(s => s.HorizontalSpeed);
        Assert.InRange(top, 0.5f, 2.05f);
        Assert.True(r.Samples[^1].Position.X - asleep.Position.X > 0.05f, $"moved {r.Samples[^1].Position.X - asleep.Position.X:0.000} m");
    }

    [Fact]
    public void Pushes_of_any_size_leave_an_avatar_finite_and_AvatarPushMaxSpeed_0_turns_them_off()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            PhysicsActor av = scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            for (int k = 0; k < 11; k++)
                scene.Simulate(Heartbeat);
            foreach (Vector3 push in new[]
                     {
                         new Vector3(float.MaxValue, 0f, 0f), new Vector3(0f, 0f, float.PositiveInfinity), new Vector3(float.NaN, 1f, 1f),
                         new Vector3(-float.MaxValue, float.MaxValue, float.MaxValue), new Vector3(1e-30f, 0f, 0f),
                     })
            {
                av.AddForce(push, true);
                scene.Simulate(Heartbeat);
                Assert.True(av.Position.IsFinite() && av.Velocity.IsFinite(), $"after {push}: {av.Position} {av.Velocity}");
                Assert.True(av.Velocity.Length() <= PhysicsBackendSettings.DefaultAvatarPushMaxSpeed + Gravity * Heartbeat, $"after {push}: {av.Velocity}");
            }
        }
        finally { scene.Dispose(); }

        scene = NewScene(0f, ("AvatarPushMaxSpeed", "0"));
        try
        {
            PhysicsActor av = scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            for (int k = 0; k < 11; k++)
                scene.Simulate(Heartbeat);
            Vector3 at = av.Position;
            for (int k = 0; k < 11; k++)
            {
                av.AddForce(new Vector3(800f, 0f, 800f), true);
                scene.Simulate(Heartbeat);
            }
            Assert.True(Vector3.Distance(at, av.Position) < 0.01f, $"moved from {at} to {av.Position}");
        }
        finally { scene.Dispose(); }
    }
}
