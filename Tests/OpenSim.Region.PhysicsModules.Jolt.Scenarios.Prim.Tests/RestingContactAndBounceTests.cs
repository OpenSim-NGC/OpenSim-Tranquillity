/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Resting contacts and bounces, as Second Life documents them (wiki.secondlife.com):
/// - collision_end is raised when a task stops colliding with another task, and land_collision_end when it stops
///   colliding with land: a box that rests on something and falls asleep has not stopped touching it, so no end event
///   comes until it is moved off. A volume detector raises collision_start and collision_end only, so likewise.
/// - A collision with a physical object or avatar that rests on an object does not keep triggering beyond a few
///   times unless something moves (the collision event's page): once the box is asleep no more collision events come. The wiki
///   says nothing of land_collision at rest; ubODE keeps sending a sleeping prim's contacts, so land_collision goes on.
/// - Restitution: a higher value is bouncier (Physics Material Settings test); a box leaves at its restitution x
///   its arrival speed, so it rises to restitution^2 x the drop (less its damping).
/// The resting scenarios run with an 11 Hz heartbeat at one physics step per heartbeat and at 22.5, 45 and 90 Hz.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RestingContactAndBounceTests
{
    private const float G = 9.80665f;
    private const float Lift = ContactScenarios.LiftAt;

    private static RunResult Run(string scenario, double physicsRate, params (string Key, string Value)[] jolt)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate };
        foreach ((string key, string value) in jolt)
            o.Jolt[key] = value;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    private static HarnessPart Part(RunResult r, string name) => r.Parts.Single(p => p.Name == name);

    private static int CountIn(List<double> times, double from, double to) => times.Count(t => t > from - 1e-9 && t < to - 1e-9);

    // The heartbeat after the one in which the body was first seen asleep: from here the engine reports none of its contacts.
    private const double Heartbeat = 1.0 / 11.0;

    // ------------------------------------------------------------------ resting on an object

    [Theory]
    [InlineData(0.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_box_asleep_on_a_platform_keeps_touching_it_until_it_is_thrown_off(double physicsRate)
    {
        RunResult r = Run("rest-platform", physicsRate);
        HarnessPart box = Part(r, "box"), platform = Part(r, "platform");
        Assert.True(box.SleptAt < Lift - 1.0, $"{r.Name}: the box fell asleep at {box.SleptAt:0.00} s");

        // Both sides: touching from the landing, no end while it rests and sleeps, one end when it is thrown off, and the
        // platform is not touched again (the box lands on the ground).
        foreach ((CollisionWatch w, uint other) in new[] { (platform.Watch, box.LocalId), (box.Watch, platform.LocalId) })
        {
            List<double> starts = w.TimesOf("start", other), ends = w.TimesOf("end", other);
            Assert.True(starts.Count > 0 && starts[0] < box.SleptAt, $"{r.Name} {w}: no start before the box slept");
            Assert.Equal(0, CountIn(ends, starts[^1], Lift));
            Assert.Equal(1, CountIn(ends, Lift, 10.0));
            Assert.Equal(0, CountIn(starts, Lift, 10.0));
            Assert.True(ends[^1] < Lift + 0.5, $"{r.Name} {w}: ended at {ends[^1]:0.00} s, thrown at {Lift} s");
        }

        // A few collision events while it settles, none once it is asleep and still.
        List<double> updates = box.Watch.TimesOf("update");
        Assert.True(CountIn(updates, 0.0, box.SleptAt) >= 2, $"{r.Name}: {CountIn(updates, 0.0, box.SleptAt)} updates before sleep");
        Assert.Equal(0, CountIn(updates, box.SleptAt + 2 * Heartbeat, Lift));
    }

    // ------------------------------------------------------------------ resting on the ground

    [Theory]
    [InlineData(0.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_box_asleep_on_the_ground_keeps_its_land_contact_until_it_is_thrown_up(double physicsRate)
    {
        RunResult r = Run("rest-ground", physicsRate);
        HarnessPart box = Part(r, "box");
        Assert.True(box.SleptAt < Lift - 1.0, $"{r.Name}: the box fell asleep at {box.SleptAt:0.00} s");
        CollisionWatch w = box.Watch;

        List<double> starts = w.TimesOf("land start"), ends = w.TimesOf("land end");
        Assert.True(starts.Count > 0 && starts[0] < box.SleptAt, $"{r.Name}: {w}");
        double settled = starts.Where(t => t < Lift).Max();
        Assert.True(settled < box.SleptAt, $"{r.Name}: last land start before the throw at {settled:0.00} s, asleep at {box.SleptAt:0.00} s");
        Assert.Equal(0, CountIn(ends, settled, Lift));
        // Thrown up at 4 m/s it is off the ground for about 0.8 s: one end, then the land again. It comes back at about 4 m/s and
        // wood on land combines to 0.275 (the shipped averaging), so it rebounds at 1.1 m/s and rises 6 cm: that lifts it clear of
        // the land at 0, 45 and 90 Hz (a second end and start, 1.02 s after the throw) but not at 22.5 Hz, where its next
        // sample is already back at 1 cm. Every start after the throw follows an end, so the counts are equal, one or two.
        Assert.Equal(1, CountIn(ends, Lift, Lift + 0.5));
        int landings = CountIn(starts, Lift, Lift + 1.2);
        Assert.InRange(landings, 1, 2);
        Assert.Equal(landings, CountIn(ends, Lift, Lift + 1.2));

        // land_collision goes on while it is asleep (ubODE): one update every heartbeat it is asleep before the throw.
        int asleep = (int)Math.Floor((Lift - box.SleptAt) / Heartbeat) - 1;
        Assert.True(CountIn(w.TimesOf("update"), box.SleptAt + Heartbeat, Lift) >= asleep - 1,
            $"{r.Name}: {CountIn(w.TimesOf("update"), box.SleptAt + Heartbeat, Lift)} land updates while asleep over about {asleep} heartbeats");
    }

    // ------------------------------------------------------------------ inside a volume detector

    [Theory]
    [InlineData(0.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_box_asleep_inside_a_volume_detector_stays_detected_until_it_is_thrown_out(double physicsRate)
    {
        RunResult r = Run("rest-vd", physicsRate);
        HarnessPart box = Part(r, "box"), detector = Part(r, "detector");
        // A box bounces off the platform it is dropped on (a prim's restitution is 0.5, as in the simulator), which delays its
        // sleep against a box with no bounce: at one physics step per heartbeat it sleeps at 4.18 s against 3.82 s, and at the
        // other rates 0.36 s later too. The lift is at 5 s, so asleep half a second before it is the margin (it was a second
        // before the bounce).
        Assert.True(box.SleptAt < Lift - 0.5, $"{r.Name}: the box fell asleep at {box.SleptAt:0.00} s");

        CollisionWatch w = detector.Watch;
        Assert.Equal(1, w.StartsOf(box.LocalId));
        Assert.True(w.TimesOf("start", box.LocalId)[0] < box.SleptAt, $"{r.Name}: {w}");
        Assert.Equal(0, CountIn(w.TimesOf("end", box.LocalId), 0.0, Lift));
        Assert.Equal(1, w.EndsOf(box.LocalId));
        Assert.True(w.TimesOf("end", box.LocalId)[0] < Lift + 0.5, $"{r.Name}: {w}");
        Assert.DoesNotContain(detector.LocalId, box.Watch.Touched);   // what passes through a detector is not told
    }

    // ------------------------------------------------------------------ resting things still rest

    [Theory]
    [InlineData(0.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_tower_of_ten_boxes_stands_and_falls_asleep(double physicsRate)
    {
        // At the module's default of 20 velocity steps ([Jolt] VelocityIterations). At Jolt's own 10 ten stacked boxes
        // sway and never fall asleep at 11, 22.5 or 45 Hz: Jolt puts the stack to sleep only when every box in it is
        // still, and the top one sways a few centimetres. At 22.5 Hz the solver takes 3 steps of 1/67.5 s
        // (JoltConfig.MinCollisionStepRate); with 2 of 1/45 s the tower did not fall asleep at 20.
        AssertTowerStandsAndSleeps(Run("tower-10", physicsRate, LegacyStepSleepSettings(physicsRate)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_tower_of_ten_boxes_stands_and_falls_asleep_at_the_shipped_solver_settings(double physicsRate)
    {
        // The solver settings RegionServerDefaults.ini sets in [Jolt], which every simulator reads before its own ini.
        AssertTowerStandsAndSleeps(Run("tower-10", physicsRate, ShippedSolverSettings().Concat(LegacyStepSleepSettings(physicsRate)).ToArray()));
    }

    // At one physics step per heartbeat (physicsRate 0) the stack sways for the whole run at 0.04 to 0.06 m/s on the top boxes,
    // above Jolt's own sleep threshold of 0.03 m/s, so whether it falls asleep there depends on float rounding. On Windows
    // (x64 and Arm64) it settles and sleeps at 3.1 s, moving 8 mm. On Linux (x64 and Arm64) and macOS it sways by centimetres:
    // with the engine's threshold it never sleeps; with 0.06 and above it sleeps at 3.1 s mid-sway, 3.3 cm from where it
    // stood, which fails the 3 cm test below; with 0.05 it sleeps at 4.45 s, 2.4 cm out. Those runs set 0.05 m/s. At the other
    // rates the sway is under 0.03 and the engine's own threshold is used.
    private static (string Key, string Value)[] LegacyStepSleepSettings(double physicsRate)
        => physicsRate == 0.0 ? new[] { ("SleepVelocityThreshold", "0.05") } : Array.Empty<(string, string)>();

    private static (string Key, string Value)[] ShippedSolverSettings([CallerFilePath] string here = "")
    {
        string ini = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Source", "OpenSim.Server.RegionServer", "AppData",
            "RegionServerDefaults.ini");
        IConfig jolt = new IniConfigSource(ini).Configs["Jolt"];
        Assert.NotNull(jolt);
        var keys = new[] { "VelocityIterations", "PositionIterations" };
        foreach (string key in keys)
            Assert.False(string.IsNullOrEmpty(jolt.Get(key)), $"RegionServerDefaults.ini [Jolt] sets no {key}");
        return keys.Select(k => (k, jolt.Get(k))).ToArray();
    }

    private static void AssertTowerStandsAndSleeps(RunResult r)
    {
        foreach (HarnessPart p in r.Parts)
        {
            Assert.True(p.SleptAt < 4.5, $"{r.Name} {p.Name}: asleep at {p.SleptAt:0.00} s");
            Assert.True(p.AsleepAtEnd, $"{r.Name} {p.Name}: awake at the end");
            Assert.True(Vector3.Distance(p.Position, p.EndPosition) < 0.03f,
                $"{r.Name} {p.Name}: moved from {p.Position} to {p.EndPosition}");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(22.5)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    public void A_box_with_no_restitution_lands_without_bouncing_or_jitter_and_sleeps(double physicsRate)
    {
        RunResult r = Run("rest-no-bounce", physicsRate);
        HarnessPart box = Part(r, "box");
        float rest = r.Samples[^1].Position.Z;
        int landed = -1;   // the heartbeat in which it struck the ground: it was falling, and now it is not
        for (int i = 1; i < r.Samples.Count && landed < 0; i++)
            if (r.Samples[i - 1].Velocity.Z < -1f && r.Samples[i].Velocity.Z > -1f)
                landed = i;
        Assert.True(landed > 0, $"{r.Name}: never came down");
        // From the heartbeat it reaches the ground it never rises more than 1 cm above where it comes to rest (the
        // engine pushes a box found inside the ground back out), and from 1 s on it does not move by a millimetre.
        Assert.All(r.Samples.Skip(landed), s => Assert.True(s.Position.Z < rest + 0.01f, $"{r.Name} t {s.T:0.000}: z {s.Position.Z:0.0000}, rests at {rest:0.0000}"));
        double settledAt = r.Samples[landed].T + 1.0;
        Assert.All(r.Samples.Where(s => s.T >= settledAt), s => Assert.True(MathF.Abs(s.Position.Z - rest) < 0.001f && s.Speed < 0.01f,
            $"{r.Name} t {s.T:0.000}: z {s.Position.Z:0.0000} speed {s.Speed:0.0000}"));
        Assert.True(box.SleptAt < 4.5 && box.AsleepAtEnd, $"{r.Name}: asleep at {box.SleptAt:0.00}, at the end {box.AsleepAtEnd}");
    }

    // ------------------------------------------------------------------ bounce height

    // Jolt finds a contact up to its speculative contact distance (2 cm) above the surface. A bounce that should rise less
    // than that can be found above its own top, and no restitution then gives the right height (see BounceFromTheSurface).
    private const double SpeculativeDistance = 0.02;

    public static IEnumerable<object[]> BounceCells()
    {
        foreach (double rate in new[] { 0.0, 22.5, 45.0, 90.0 })
            foreach (float drop in new[] { 0.5f, 2f, 10f })
                for (int e = 1; e <= 9; e++)
                    yield return new object[] { rate, drop, e / 10f };
    }

    public static IEnumerable<object[]> BounceCellsAboveTheSpeculativeDistance()
        => BounceCells().Where(c => ExpectedRise((float)c[1], (float)c[2]) >= SpeculativeDistance);

    public static IEnumerable<object[]> BounceCellsBelowTheSpeculativeDistance()
        => BounceCells().Where(c => ExpectedRise((float)c[1], (float)c[2]) < SpeculativeDistance);

    private static double ExpectedRise(float drop, float restitution)
        => RiseHeight(restitution * FallSpeed(drop, JoltConfig.DefaultPrimLinearDamping), JoltConfig.DefaultPrimLinearDamping);

    // The height a body rises to when it leaves at speed v under g with damping c (dv/dt = -g - c v).
    private static double RiseHeight(double v, double c)
    {
        double h = 1e-4, z = 0;
        while (v > 0) { v -= (G + c * v) * h; z += v * h; }
        return z;
    }

    // The speed a body reaches falling `height` from rest under g with damping c.
    private static double FallSpeed(double height, double c)
    {
        double h = 1e-4, v = 0, d = 0;
        while (d < height) { v += (G - c * v) * h; d += v * h; }
        return v;
    }

    /// <summary>
    /// A 1 m box of restitution `restitution` dropped `drop` metres (its base above the top) onto a fixed plate of
    /// the same restitution, so the pair's is that value under the shipped averaging. The heartbeat runs at the physics step rate, one
    /// step per heartbeat, so every step the engine takes is seen (11 Hz: one step per heartbeat, as in a region). Returns
    /// the highest point its base reaches above the plate after the first bounce: the highest stepped position, or from a
    /// sample in flight, where its rise from there ends (its damped rise, less the v x step / 2 the stepping loses on the way).
    /// </summary>
    private static double Rebound(double physicsRate, float drop, float restitution)
    {
        const float plateTop = 60f;
        var trace = new List<(Vector3 P, Vector3 V)>();
        var sc = new Scenario
        {
            Name = "bounce",
            DefaultDuration = _ => MathF.Sqrt(2f * drop / G) + 2.5f,
            Setup = r =>
            {
                r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, plateTop - 1f), Quaternion.Identity, false).Restitution = restitution;
                r.AddBox(Vector3.One, new Vector3(128f, 128f, plateTop + drop + 0.5f), Quaternion.Identity).Restitution = restitution;
            },
            Input = r => trace.Add((r.Actor.Position, r.Actor.Velocity)),
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = physicsRate > 0 ? physicsRate : 11.0, PhysicsRateHz = physicsRate });

        var config = new JoltConfig();
        double step = physicsRate > 0 ? 1.0 / (physicsRate * config.CollisionStepsAt((float)physicsRate)) : 1.0 / (11.0 * config.CollisionSteps);
        int bounce = -1;
        for (int i = 1; i < trace.Count; i++)
            if (trace[i - 1].V.Z < -0.5f && trace[i].V.Z > -0.5f) { bounce = i; break; }
        Assert.True(bounce > 0, "no impact");
        double apex = double.NegativeInfinity;
        for (int i = bounce; i < trace.Count && trace[i].V.Z >= -0.5f; i++)
        {
            double z = trace[i].P.Z - 0.5 - plateTop, v = trace[i].V.Z;
            apex = Math.Max(apex, z);
            if (v > 0 && z > 0.001)
                apex = Math.Max(apex, z + RiseHeight(v, JoltConfig.DefaultPrimLinearDamping) - v * step / 2);
        }
        return apex;
    }

    [Theory]
    [MemberData(nameof(BounceCellsAboveTheSpeculativeDistance))]
    public void A_dropped_box_rebounds_to_the_height_its_restitution_gives(double physicsRate, float drop, float restitution)
    {
        double expected = ExpectedRise(drop, restitution);
        double got = Rebound(physicsRate, drop, restitution);
        Assert.True(Math.Abs(got - expected) <= expected * 0.05,
            $"rate {physicsRate} drop {drop} restitution {restitution}: rebound {got:0.0000} m, expected {expected:0.0000} m ({(got - expected) / expected * 100:+0.0;-0.0} %)");
    }

    // The engine limit: a bounce lower than Jolt's speculative contact distance comes out anywhere between no bounce and
    // that distance above its own height, by where the step falls. Held to that, and no more.
    [Theory]
    [MemberData(nameof(BounceCellsBelowTheSpeculativeDistance))]
    public void A_rebound_lower_than_the_speculative_contact_distance_stays_within_it(double physicsRate, float drop, float restitution)
    {
        double expected = ExpectedRise(drop, restitution);
        double got = Rebound(physicsRate, drop, restitution);
        Assert.InRange(got, -0.001, expected + SpeculativeDistance);
    }
}
