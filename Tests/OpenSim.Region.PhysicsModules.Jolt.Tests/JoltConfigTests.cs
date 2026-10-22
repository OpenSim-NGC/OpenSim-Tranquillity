/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Runtime.CompilerServices;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Nini.Config;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The [Jolt] section: an empty config reproduces the built-in constants and
/// AddRegion settings exactly, every key parses, invalid values fall back with a warning, and area scaling of the
/// pair/contact caps is opt-in.
/// </summary>
public class JoltConfigTests
{
    private static IConfigSource Source(params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        var cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return src;
    }

    /// <summary>What AddRegion builds with no [Jolt] settings: Default, MaxBodies by the area rule, CollisionSteps 6,
    /// VelocityIterations 20.</summary>
    private static PhysicsBackendSettings Today(uint sx, uint sy)
    {
        var s = PhysicsBackendSettings.Default;
        s.MaxBodies = (int)(65536L * Math.Max((long)sx * sy, 256L * 256L) / (256L * 256L));
        s.CollisionSteps = 6;
        s.VelocityIterations = 20;
        s.SpeculativeContactDistance = 0.02f;   // Jolt's own value, set explicitly
        s.SleepVelocityThreshold = 0.03f;       // likewise
        return s;
    }

    [Theory]
    [InlineData(256u, 256u, 65536)]
    [InlineData(1024u, 1024u, 1048576)]
    public void Empty_config_reproduces_todays_settings(uint sx, uint sy, int expectedMaxBodies)
    {
        foreach (var src in new[] { new IniConfigSource(), Source() })   // no section, and an empty section
        {
            var warnings = new List<string>();
            var c = JoltConfig.FromConfig(src, warnings);
            Assert.Empty(warnings);
            var s = c.ToBackendSettings(sx, sy);
            Assert.Equal(Today(sx, sy), s);
            Assert.Equal(expectedMaxBodies, s.MaxBodies);
            Assert.Equal(new Vector3(0f, 0f, -9.80665f), s.Gravity);
            Assert.Equal(4.0f, c.AvatarJumpSpeed);
            Assert.Equal(CharacterDesc.Default.JumpSpeed, c.AvatarJumpSpeed);
            Assert.Equal(65536, c.BodyUpdateBufferMax);
            Assert.Equal(1024, c.CharacterUpdateBufferMax);
            Assert.Equal(0, c.ContactBufferMax);
            Assert.Equal(10f, c.CapacityLogIntervalSeconds);
        }
    }

    [Fact]
    public void Every_key_parses()
    {
        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(Source(
            ("Gravity", "-3.5"), ("CollisionSteps", "4"), ("PositionIterations", "3"), ("VelocityIterations", "12"),
            ("ThreadCount", "5"), ("DeterministicMode", "true"), ("MaxBodies", "1000"), ("MaxBodyPairs", "2000"),
            ("MaxContactConstraints", "3000"), ("ScaleCapsWithArea", "true"), ("BodyUpdateBufferMax", "4096"),
            ("CharacterUpdateBufferMax", "512"), ("ContactBufferMax", "9000"), ("AvatarJumpSpeed", "5.25"),
            ("CapacityLogIntervalSeconds", "30")), warnings);
        Assert.Empty(warnings);

        Assert.Equal(-3.5f, c.Gravity);
        Assert.Equal(4, c.CollisionSteps);
        Assert.Equal(3, c.PositionIterations);
        Assert.Equal(12, c.VelocityIterations);
        Assert.Equal(5, c.ThreadCount);
        Assert.True(c.DeterministicMode);
        Assert.Equal(1000, c.MaxBodies);
        Assert.Equal(2000, c.MaxBodyPairs);
        Assert.Equal(3000, c.MaxContactConstraints);
        Assert.True(c.ScaleCapsWithArea);
        Assert.Equal(4096, c.BodyUpdateBufferMax);
        Assert.Equal(512, c.CharacterUpdateBufferMax);
        Assert.Equal(9000, c.ContactBufferMax);
        Assert.Equal(5.25f, c.AvatarJumpSpeed);
        Assert.Equal(30f, c.CapacityLogIntervalSeconds);
        Assert.Equal(1, c.RequestedThreadCount);   // DeterministicMode forces one worker

        var s = c.ToBackendSettings(256, 256);
        Assert.Equal(new Vector3(0f, 0f, -3.5f), s.Gravity);
        Assert.Equal(1000, s.MaxBodies);            // explicit MaxBodies is taken as-is
        Assert.Equal(4, s.CollisionSteps);
        Assert.Equal(3, s.PositionIterations);
        Assert.Equal(12, s.VelocityIterations);
        Assert.Equal(5, s.ThreadCount);
        Assert.True(s.DeterministicMode);
    }

    [Fact]
    public void The_sleep_velocity_threshold_is_the_engines_own_unless_set()
    {
        var warnings = new List<string>();
        Assert.Equal(0.03f, JoltConfig.FromConfig(new IniConfigSource(), warnings).ToBackendSettings(256, 256).SleepVelocityThreshold);
        Assert.Equal(0.1f, JoltConfig.FromConfig(Source(("SleepVelocityThreshold", "0.1")), warnings).ToBackendSettings(256, 256).SleepVelocityThreshold);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("Gravity", "NaN")]
    [InlineData("Gravity", "Infinity")]
    [InlineData("Gravity", "downwards")]
    [InlineData("CollisionSteps", "0")]
    [InlineData("CollisionSteps", "1.5")]
    [InlineData("PositionIterations", "-1")]
    [InlineData("VelocityIterations", "0")]
    [InlineData("ThreadCount", "-2")]
    [InlineData("DeterministicMode", "yes please")]
    [InlineData("MaxBodies", "-1")]
    [InlineData("MaxBodies", "99999999999")]
    [InlineData("MaxBodyPairs", "0")]
    [InlineData("MaxContactConstraints", "")]
    [InlineData("ScaleCapsWithArea", "2")]
    [InlineData("BodyUpdateBufferMax", "0")]
    [InlineData("CharacterUpdateBufferMax", "-5")]
    [InlineData("ContactBufferMax", "-1")]
    [InlineData("SleepVelocityThreshold", "0")]
    [InlineData("SleepVelocityThreshold", "fast")]
    [InlineData("AvatarJumpSpeed", "NaN")]
    [InlineData("AvatarJumpSpeed", "-1")]
    [InlineData("CapacityLogIntervalSeconds", "0")]
    public void Invalid_values_fall_back_with_a_warning(string key, string value)
    {
        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(Source((key, value)), warnings);
        Assert.Single(warnings);
        Assert.Contains(key, warnings[0]);
        var d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(d.ToBackendSettings(256, 256), c.ToBackendSettings(256, 256));
        Assert.Equal(d.AvatarJumpSpeed, c.AvatarJumpSpeed);
        Assert.Equal(d.BodyUpdateBufferMax, c.BodyUpdateBufferMax);
        Assert.Equal(d.CharacterUpdateBufferMax, c.CharacterUpdateBufferMax);
        Assert.Equal(d.ContactBufferMax, c.ContactBufferMax);
        Assert.Equal(d.CapacityLogIntervalSeconds, c.CapacityLogIntervalSeconds);
    }

    // The defaults turn ScenePresence's request (4.096 m/s walking or running, 16.384 flying) into Second Life's
    // documented 3.20, 5.13 and 16.00 m/s, and a downward flight into its 22.87 m/s
    // (https://wiki.secondlife.com/wiki/Default_Avatar_Movement_Speeds).
    [Fact]
    public void Avatar_speed_factors_default_to_second_lifes_speeds_and_parse()
    {
        var d = JoltConfig.FromConfig(Source(), null);
        Assert.Equal(3.20f, d.AvatarWalkSpeedFactor * 4.096f, 4);
        Assert.Equal(5.13f, d.AvatarRunSpeedFactor * 4.096f, 4);
        Assert.Equal(16.00f, d.AvatarFlySpeedFactor * 16.384f, 4);
        Assert.Equal(22.87f, d.AvatarFlyDownSpeedFactor * 16.384f, 4);

        var warnings = new List<string>();
        var c = JoltConfig.FromConfig(Source(("AvatarWalkSpeedFactor", "1"), ("AvatarRunSpeedFactor", "1.3"), ("AvatarFlySpeedFactor", "0.5"),
            ("AvatarFlyDownSpeedFactor", "2")), warnings);
        Assert.Empty(warnings);
        Assert.Equal(1f, c.AvatarWalkSpeedFactor);
        Assert.Equal(1.3f, c.AvatarRunSpeedFactor);
        Assert.Equal(0.5f, c.AvatarFlySpeedFactor);
        Assert.Equal(2f, c.AvatarFlyDownSpeedFactor);
    }

    [Theory]
    [InlineData("AvatarWalkSpeedFactor")]
    [InlineData("AvatarRunSpeedFactor")]
    [InlineData("AvatarFlySpeedFactor")]
    [InlineData("AvatarFlyDownSpeedFactor")]
    public void An_invalid_avatar_speed_factor_falls_back_to_the_default_with_a_warning(string key)
    {
        var d = JoltConfig.FromConfig(Source(), null);
        foreach (string bad in new[] { "fast", "", "0", "-1", "NaN", "Infinity", "11" })
        {
            var warnings = new List<string>();
            var c = JoltConfig.FromConfig(Source((key, bad)), warnings);
            Assert.Single(warnings);
            Assert.Contains(key, warnings[0]);
            Assert.Contains("using the default", warnings[0]);
            Assert.Equal(d.AvatarWalkSpeedFactor, c.AvatarWalkSpeedFactor);
            Assert.Equal(d.AvatarRunSpeedFactor, c.AvatarRunSpeedFactor);
            Assert.Equal(d.AvatarFlySpeedFactor, c.AvatarFlySpeedFactor);
            Assert.Equal(d.AvatarFlyDownSpeedFactor, c.AvatarFlyDownSpeedFactor);
        }
    }

    [Fact]
    public void VehicleGroundGravityFactor_defaults_to_whole_gravity_parses_and_falls_back()
    {
        Assert.Equal(1f, JoltConfig.FromConfig(new IniConfigSource(), null).VehicleGroundGravityFactor);

        var warnings = new List<string>();
        Assert.Equal(0.2f, JoltConfig.FromConfig(Source(("VehicleGroundGravityFactor", "0.2")), warnings).VehicleGroundGravityFactor);
        Assert.Empty(warnings);

        foreach (string bad in new[] { "-0.1", "1.5", "NaN", "a fifth" })
        {
            warnings.Clear();
            Assert.Equal(1f, JoltConfig.FromConfig(Source(("VehicleGroundGravityFactor", bad)), warnings).VehicleGroundGravityFactor);
            Assert.Single(warnings);
        }
    }

    [Fact]
    public void VehiclePresets_defaults_to_the_documented_set_parses_and_falls_back()
    {
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(Vehicles.VehiclePresetSet.Documented, d.VehiclePresets);
        // Where the key is not set the presets follow the behaviour, and the behaviour defaults to InWorldz: its legacy values.
        Assert.Equal(Vehicles.VehicleBehaviour.InWorldz, d.VehicleBehaviour);
        Assert.Equal(Vehicles.VehiclePresetSet.Legacy, d.ToVehicleSettings().Presets);
        Assert.Equal(Vehicles.VehiclePresetSet.Documented,
            JoltConfig.FromConfig(Source(("VehicleBehaviour", "SecondLife")), null).ToVehicleSettings().Presets);

        var warnings = new List<string>();
        foreach ((string text, Vehicles.VehiclePresetSet set) in new[] { ("legacy", Vehicles.VehiclePresetSet.Legacy), ("Legacy", Vehicles.VehiclePresetSet.Legacy), ("documented", Vehicles.VehiclePresetSet.Documented) })
        {
            JoltConfig c = JoltConfig.FromConfig(Source(("VehiclePresets", text)), warnings);
            Assert.Equal(set, c.VehiclePresets);
            Assert.Equal(set, c.ToVehicleSettings().Presets);
        }
        Assert.Empty(warnings);

        foreach (string bad in new[] { "1", "-1", "halcyon", "" })
        {
            warnings.Clear();
            Assert.Equal(Vehicles.VehiclePresetSet.Documented, JoltConfig.FromConfig(Source(("VehiclePresets", bad)), warnings).VehiclePresets);
            Assert.Single(warnings);
        }
    }

    [Fact]
    public void JobPools_defaults_parses_and_falls_back()
    {
        var d = JoltConfig.FromConfig(new IniConfigSource(), null);
        Assert.Equal(1, d.JobPools);
        Assert.Equal(1, d.ToBackendSettings(256, 256).JobPools);
        Assert.Equal(1, PhysicsBackendSettings.Default.JobPools);
        Assert.Equal(1, JoltPhysicsBackend.ResolveJobPools(0));   // an unset struct means one pool

        var w = new List<string>();
        var c = JoltConfig.FromConfig(Source(("JobPools", "7")), w);
        Assert.Empty(w);
        Assert.Equal(7, c.JobPools);
        Assert.Equal(7, c.ToBackendSettings(1024, 1024).JobPools);
        Assert.Equal(64, JoltConfig.FromConfig(Source(("JobPools", "64")), w).JobPools);
        Assert.Empty(w);

        foreach (var bad in new[] { "0", "65", "-1", "four", "" })
        {
            w.Clear();
            var b = JoltConfig.FromConfig(Source(("JobPools", bad)), w);
            Assert.Single(w);
            Assert.Contains("JobPools", w[0]);
            Assert.Equal(1, b.JobPools);
        }

        // MaxConcurrentUpdates is not a [Jolt] key and is not read.
        w.Clear();
        Assert.Equal(1, JoltConfig.FromConfig(Source(("MaxConcurrentUpdates", "4")), w).JobPools);
        Assert.Empty(w);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ThreadCount_is_split_across_the_pools(int pools)
    {
        // ThreadCount 0 = automatic: each pool's share of ProcessorCount - 1, at most 4, at least 1.
        int perPool = Math.Clamp(Math.Max(1, Environment.ProcessorCount - 1) / pools, 1, 4);
        var zero = JoltConfig.FromConfig(Source(("ThreadCount", "0"), ("JobPools", pools.ToString())), null);
        Assert.Equal(perPool * pools, zero.RequestedThreadCount);
        Assert.Equal(perPool, zero.RequestedThreadsPerPool);

        // An explicit ThreadCount = ProcessorCount.
        int cpus = Math.Min(Environment.ProcessorCount, 256);
        var all = JoltConfig.FromConfig(Source(("ThreadCount", cpus.ToString()), ("JobPools", pools.ToString())), null);
        Assert.Equal(cpus, all.RequestedThreadCount);
        Assert.Equal(Math.Max(1, cpus / pools), all.RequestedThreadsPerPool);
        Assert.True(all.RequestedThreadsPerPool * pools <= cpus || cpus < pools);

        // One pool holds every thread.
        if (pools == 1)
            Assert.Equal(all.RequestedThreadCount, all.RequestedThreadsPerPool);

        Assert.Equal(12 / pools, JoltPhysicsBackend.ResolveThreadsPerPool(12, pools));
        Assert.Equal(pools == 2 ? 3 : pools == 3 ? 2 : 7, JoltPhysicsBackend.ResolveThreadsPerPool(7, pools));
    }

    // The automatic count for a given processor count and JobPools: threads per pool, then the total.
    [Theory]
    [InlineData(2, 1, 1)] [InlineData(2, 2, 1)] [InlineData(2, 3, 1)] [InlineData(2, 4, 1)]
    [InlineData(4, 1, 3)] [InlineData(4, 2, 1)] [InlineData(4, 3, 1)] [InlineData(4, 4, 1)]
    [InlineData(8, 1, 4)] [InlineData(8, 2, 3)] [InlineData(8, 3, 2)] [InlineData(8, 4, 1)]
    [InlineData(20, 1, 4)] [InlineData(20, 2, 4)] [InlineData(20, 3, 4)] [InlineData(20, 4, 4)]
    [InlineData(64, 1, 4)] [InlineData(64, 2, 4)] [InlineData(64, 3, 4)] [InlineData(64, 4, 4)]
    public void Automatic_ThreadCount_gives_each_pool_its_share_of_the_cores_at_most_4(int cores, int pools, int perPool)
    {
        Assert.Equal(perPool, JoltPhysicsBackend.ResolveAutoThreadsPerPool(pools, cores));
        int total = JoltPhysicsBackend.ResolveThreadCount(0, false, pools, cores);
        Assert.Equal(perPool * pools, total);
        Assert.Equal(perPool, JoltPhysicsBackend.ResolveThreadsPerPool(total, pools));   // the pools are built from the total
        Assert.Equal(JobThreadSource.Automatic, JoltPhysicsBackend.ResolveThreadSource(0, false));
    }

    // A positive ThreadCount keeps its meaning: the total for all pools, split evenly, whatever the processor count.
    [Theory]
    [InlineData(2, 1)] [InlineData(4, 2)] [InlineData(8, 3)] [InlineData(20, 4)] [InlineData(64, 1)] [InlineData(64, 4)]
    public void An_explicit_ThreadCount_is_used_as_given(int cores, int pools)
    {
        foreach (int set in new[] { 1, 3, 7, 12, 19, 32 })
        {
            Assert.Equal(set, JoltPhysicsBackend.ResolveThreadCount(set, false, pools, cores));
            Assert.Equal(Math.Max(1, set / pools), JoltPhysicsBackend.ResolveThreadsPerPool(set, pools));
        }
        Assert.Equal(JobThreadSource.Set, JoltPhysicsBackend.ResolveThreadSource(19, false));
        Assert.Equal(1, JoltPhysicsBackend.ResolveThreadCount(0, true, pools, cores));      // DeterministicMode: one
        Assert.Equal(1, JoltPhysicsBackend.ResolveThreadCount(19, true, pools, cores));
        Assert.Equal(JobThreadSource.Deterministic, JoltPhysicsBackend.ResolveThreadSource(19, true));
    }

    [Fact]
    public void The_pool_lines_say_whether_the_count_is_automatic_or_set()
    {
        var s = new PhysicsCapacityStats { JobPools = 2, JobThreadsPerPool = 4, JobThreadCount = 8, JobThreadSource = JobThreadSource.Automatic };
        Assert.Equal("2 pools x 4 worker threads (automatic: each pool's share of the processors less one, at most 4 per pool); " +
                     "one physics update at a time per pool (process-wide).", CapacityReport.JobPoolsStartup(in s));
        Assert.Contains("JobPools=2 threadsPerPool=4 (ThreadCount 8; process-wide); automatic:", CapacityReport.Render("Test Region", s, 1, 0, 1, 0, 1, 0));
        s = new PhysicsCapacityStats { JobPools = 1, JobThreadsPerPool = 19, JobThreadCount = 19, JobThreadSource = JobThreadSource.Set };
        Assert.Equal("1 pool x 19 worker threads (set by [Jolt] ThreadCount = 19, all pools together); " +
                     "one physics update at a time per pool (process-wide).", CapacityReport.JobPoolsStartup(in s));
        Assert.Contains("JobPools=1 threadsPerPool=19 (ThreadCount 19; process-wide); set by [Jolt] ThreadCount = 19", CapacityReport.Render("Test Region", s, 1, 0, 1, 0, 1, 0));
        s = new PhysicsCapacityStats { JobPools = 1, JobThreadsPerPool = 1, JobThreadCount = 1, JobThreadSource = JobThreadSource.Deterministic };
        Assert.StartsWith("1 pool x 1 worker thread (one thread, [Jolt] DeterministicMode)", CapacityReport.JobPoolsStartup(in s));
    }

    [Fact]
    public void ThreadCount_below_JobPools_still_gives_every_pool_a_thread()
    {
        Assert.Equal(1, JoltPhysicsBackend.ResolveThreadsPerPool(2, 3));
        Assert.Equal(1, JoltPhysicsBackend.ResolveThreadsPerPool(1, 64));
        var c = JoltConfig.FromConfig(Source(("ThreadCount", "2"), ("JobPools", "5")), null);
        Assert.Equal(1, c.RequestedThreadsPerPool);
        var det = JoltConfig.FromConfig(Source(("DeterministicMode", "true"), ("JobPools", "4")), null);
        Assert.Equal(1, det.RequestedThreadsPerPool);
    }

    [Fact]
    public void ScaleCapsWithArea_scales_pairs_and_contacts_by_16_for_1024()
    {
        var c = JoltConfig.FromConfig(Source(("ScaleCapsWithArea", "true")), null);
        var big = c.ToBackendSettings(1024, 1024);
        Assert.Equal(65536 * 16, big.MaxBodyPairs);
        Assert.Equal(16384 * 16, big.MaxContactConstraints);
        Assert.Equal(1048576, big.MaxBodies);

        var small = c.ToBackendSettings(256, 256);   // a standard region is unchanged
        Assert.Equal(65536, small.MaxBodyPairs);
        Assert.Equal(16384, small.MaxContactConstraints);

        var off = JoltConfig.FromConfig(new IniConfigSource(), null).ToBackendSettings(1024, 1024);
        Assert.Equal(65536, off.MaxBodyPairs);        // opt-in: off by default
        Assert.Equal(16384, off.MaxContactConstraints);
    }

    // ------------------------------------------------------------------ the module reads it

    private static string Scene([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
            "Source", "OpenSim.Region.PhysicsModules.Jolt", "JoltScene.cs"));

    [Fact]
    public void AddRegion_builds_its_settings_from_JoltConfig()
    {
        var s = Scene();
        var at = s.IndexOf("public void AddRegion(Scene scene)", StringComparison.Ordinal);
        var end = s.IndexOf("public void RemoveRegion(", at, StringComparison.Ordinal);
        var body = s[at..end];
        Assert.Contains(".ToBackendSettings(", body);
        Assert.DoesNotContain("PhysicsBackendSettings.Default", body);
        Assert.DoesNotContain("settings.CollisionSteps = 6", body);
        Assert.Contains("JoltConfig.FromConfig(", s);
    }

    [Fact]
    public void AvatarJumpSpeed_reaches_the_CharacterDesc()
    {
        var c = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ScenePath())!, "JoltCharacter.cs"));
        var at = c.IndexOf("private void CreateCharacterInternal()", StringComparison.Ordinal);
        var body = c[at..c.IndexOf("_backend.CreateCharacter(desc)", at, StringComparison.Ordinal)];
        Assert.Contains("desc.JumpSpeed = _module.AvatarJumpSpeed", body);
    }

    private static string ScenePath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
            "Source", "OpenSim.Region.PhysicsModules.Jolt", "JoltScene.cs"));
}
