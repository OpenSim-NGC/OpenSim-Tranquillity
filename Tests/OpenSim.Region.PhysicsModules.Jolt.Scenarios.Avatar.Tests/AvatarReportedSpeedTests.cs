/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The velocity an avatar reports is the speed it moves at. Jolt's character also moves an avatar out of anything it
/// overlaps, all at once in one update: an avatar put inside a prim (ScenePresence.StandUp places one with no clearance
/// check), or one a body has gone into. Being set out is not moving, and is not reported as speed.
///
/// Every avatar update is read through the backend's CharacterStepped trace, so the speeds are per physics step: at
/// [Jolt] PhysicsStepRate 45 a heartbeat runs four or five steps and reports the last. A heartbeat's own average cannot be
/// the measure there: an avatar walking off a ledge, or struck in the middle of a heartbeat, really moves faster at the
/// heartbeat's end than its average over it.
/// Each case runs at an 11 Hz heartbeat with one physics step per heartbeat and with PhysicsStepRate 45.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarReportedSpeedTests
{
    private static readonly float AvatarMass = JoltCharacter.MassFor(Harness.Run.AvatarSize);   // what JoltCharacter reports: mass from shape and size
    private const float PushCap = 10f;   // [Jolt] AvatarPushMaxSpeed, set to 10 in these runs: the cap rule is what they check (the default is 100)
    private const float Gravity = 9.80665f;

    // A scenario that also records each avatar update of the run's backend. The backend calls the trace on the step
    // thread, which in the harness is the test's own (Simulate runs the step inline).
    private static Scenario Traced(Scenario sc, List<CharacterStepTrace> steps) => new()
    {
        Name = sc.Name,
        Description = sc.Description,
        Slopes = sc.Slopes,
        UsesSlope = sc.UsesSlope,
        DefaultDuration = sc.DefaultDuration,
        DefaultHold = sc.DefaultHold,
        World = sc.World,
        Setup = r =>
        {
            ((JoltPhysicsBackend)((JoltScene)r.PhysicsScene).Backend).CharacterStepped += t => steps.Add(t);
            sc.Setup(r);
        },
        Input = sc.Input,
        Steady = sc.Steady,
        SteadyFromPath = sc.SteadyFromPath,
        Gap = sc.Gap,
        PassedThrough = sc.PassedThrough,
        UsesShot = sc.UsesShot,
    };

    private static (RunResult Result, List<CharacterStepTrace> Steps) Run(string scenario, double physicsHz)
    {
        var steps = new List<CharacterStepTrace>();
        RunResult res = Harness.Harness.Run(Traced(Harness.Harness.Find(scenario), steps),
            new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });
        return (res, steps);
    }

    private static float Level(SVector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // An update that set the avatar out: the first after the caller put it where it is, in which it moved further than
    // its own velocity would take it. An avatar put somewhere clear moves as it is asked in that update.
    private static bool SetOut(CharacterStepTrace t, float tolerance = 0.01f)
        => t.Placed && t.Displacement.Length() > t.Asked.Length() * t.Seconds + tolerance;

    // ------------------------------------------------------------------ 1. standing up inside a prim

    // ScenePresence.StandUp puts the avatar at the middle of a fixed 2 m cube; Jolt sets it out to the nearest side in its
    // first update. That update reports no speed. Set out with its feet about 0.1 m above the ground, it then drops to it:
    // no later update reports more than such a drop gives (1.4 m/s; 2 allowed), nor does any heartbeat.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_set_out_of_a_prim_it_stood_up_in_reports_no_speed_for_that_update(double physicsHz)
    {
        (RunResult res, List<CharacterStepTrace> steps) = Run("avatar-stand-up-inside", physicsHz);

        CharacterStepTrace first = steps[0];
        Assert.True(first.Placed, $"{res.Name}: the first update is not marked as placed");
        Assert.True(first.Displacement.Length() > 1f, $"{res.Name}: the avatar was set out only {first.Displacement.Length():0.000} m");
        Assert.True(first.Reported.Length() == 0f, $"{res.Name}: the update that set it out reported {first.Reported.Length():0.00} m/s");

        const float Drop = 2f;
        foreach (Sample s in res.Samples.Where(s => s.T > AvatarHitScenarios.At + 1e-9))
            Assert.True(s.Speed < Drop, $"{res.Name}: the heartbeat at {s.T:0.000} s reported {s.Speed:0.00} m/s");
        for (int i = 1; i < steps.Count; i++)
            Assert.True(steps[i].Reported.Length() < Drop, $"{res.Name}: update {i} reported {steps[i].Reported.Length():0.00} m/s after it was set out");
    }

    // ------------------------------------------------------------------ 2. a heavy linkset catching a flying avatar up

    // The 100 kg three-part linkset thrown at 20 m/s at a flying avatar. The strike carries the avatar at the push cap; its
    // push fades, and at 45 Hz the slowed linkset catches it up and goes a little into it before it may push it, so Jolt
    // set it out on the next step: 0.31 m in 1/45 s, reported as 14.05 m/s. The avatar is moved only by the linkset, never
    // faster than the push cap, and reports no more.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_flying_avatar_carried_by_a_heavy_linkset_reports_no_more_than_the_push_cap(double physicsHz)
    {
        (RunResult res, List<CharacterStepTrace> steps) = Run("avatar-fly-linkset-20ms", physicsHz);

        float most = steps.Max(t => t.Reported.Length());
        Assert.True(most <= PushCap * 1.02f, $"{res.Name}: an update reported {most:0.00} m/s, over the {PushCap} m/s push cap");
        float mostSample = res.Samples.Max(s => s.Speed);
        Assert.True(mostSample <= PushCap * 1.02f, $"{res.Name}: a heartbeat reported {mostSample:0.00} m/s, over the {PushCap} m/s push cap");
        Assert.True(res.Samples[^1].Position.X < AvatarHitScenarios.X - 3f, $"{res.Name}: the avatar was carried only {AvatarHitScenarios.X - res.Samples[^1].Position.X:0.00} m");
    }

    // The 100 kg box thrown at 5 m/s at a flying avatar: they meet at their common speed by mass, 100 x 5 / (100 + the avatar's mass),
    // and the avatar reports no more than that (the meeting of a box and a flying avatar is unchanged).
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_flying_avatar_struck_by_a_heavy_box_reports_their_common_speed(double physicsHz)
    {
        (RunResult res, List<CharacterStepTrace> steps) = Run("avatar-fly-hit-100kg", physicsHz);

        float Common = 100f * 5f / (100f + AvatarMass);
        float most = steps.Max(t => t.Reported.Length());
        Assert.InRange(most, Common * 0.9f, Common * 1.02f);
    }

    // ------------------------------------------------------------------ 3. every avatar scenario

    public static IEnumerable<object[]> AvatarScenariosAndRates()
    {
        foreach (Scenario s in Harness.Harness.Scenarios)
            if (s.Name.StartsWith("avatar", StringComparison.Ordinal) || s.Name is "push-avatar" or "push-flood")
                foreach (double p in new[] { 0.0, 45.0 })
                    yield return new object[] { s.Name, p };
    }

    // In every update of every avatar scenario, the speed reported is never more than 5 % above the distance the update
    // really moved the avatar over its time. Up and down it may also carry the half step of gravity it gains after the
    // move (StepCharacter's "late" half, so a falling avatar reports the speed it ends the step with). An update that set
    // the avatar out of something it was put inside reports no speed.
    [Theory]
    [MemberData(nameof(AvatarScenariosAndRates))]
    public void In_every_avatar_scenario_no_update_reports_more_speed_than_it_moved(string scenario, double physicsHz)
    {
        (RunResult res, List<CharacterStepTrace> steps) = Run(scenario, physicsHz);
        Assert.NotEmpty(steps);
        for (int i = 0; i < steps.Count; i++)
        {
            CharacterStepTrace t = steps[i];
            if (SetOut(t))
            {
                Assert.True(t.Reported.Length() == 0f, $"{res.Name}: update {i} set the avatar out {t.Displacement.Length():0.000} m and reported {t.Reported.Length():0.00} m/s");
                continue;
            }
            float level = Level(t.Displacement) / t.Seconds, levelReported = Level(t.Reported);
            Assert.True(levelReported <= level * 1.05f + 1e-3f,
                $"{res.Name}: update {i} reported {levelReported:0.000} m/s across, moved {level:0.000}");
            float up = MathF.Abs(t.Displacement.Z) / t.Seconds, upReported = MathF.Abs(t.Reported.Z);
            Assert.True(upReported <= up * 1.05f + Gravity * t.Seconds * 0.5f + 1e-3f,
                $"{res.Name}: update {i} reported {upReported:0.000} m/s up or down, moved {up:0.000}");
        }
    }
}
