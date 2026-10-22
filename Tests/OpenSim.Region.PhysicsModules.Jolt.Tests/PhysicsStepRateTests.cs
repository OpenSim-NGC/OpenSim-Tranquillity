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
/// [Jolt] PhysicsStepRate and PhysicsStepCollisionSteps, and the accumulator that turns heartbeats into physics
/// steps. Pure: no backend, no shared state, so these run in parallel with everything else.
/// </summary>
public class PhysicsStepRateTests
{
    private const float Heartbeat = 0.0909f;   // OpenSimDefaults.ini [Startup] FrameTime

    private static JoltConfig Parse(List<string> warnings, params (string key, string value)[] kv)
    {
        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        foreach (var (k, v) in kv)
            cfg.Set(k, v);
        return JoltConfig.FromConfig(src, warnings);
    }

    [Fact]
    public void With_no_rate_set_physics_steps_at_45_Hz()
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings);
        Assert.Empty(warnings);
        Assert.Equal(45f, c.PhysicsStepRate);
        Assert.Equal(2, c.PhysicsStepCollisionSteps);
        Assert.Equal(45f, c.EffectivePhysicsStepRate(Heartbeat, out string w, out string note));
        Assert.Null(w);
        Assert.Null(note);
        Assert.False(c.PhysicsStepRateSet);
        Assert.Equal(2, c.ToBackendSettings(256, 256, substepping: true).CollisionSteps);

        // No [Jolt] section at all, and no configuration at all, give the same.
        Assert.Equal(45f, JoltConfig.FromConfig(new IniConfigSource(), warnings).PhysicsStepRate);
        Assert.Equal(45f, JoltConfig.FromConfig(null, warnings).PhysicsStepRate);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("0")]
    [InlineData(" 0 ")]
    [InlineData("0.0")]
    public void Rate_0_is_one_step_per_heartbeat_and_the_solver_keeps_its_collision_steps(string zero)
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, ("PhysicsStepRate", zero));
        Assert.Empty(warnings);
        Assert.Equal(0f, c.PhysicsStepRate);
        Assert.Equal(0f, c.EffectivePhysicsStepRate(Heartbeat, out string w, out string note));
        Assert.Null(w);
        Assert.Equal(6, c.ToBackendSettings(256, 256).CollisionSteps);
        Assert.Equal(c.ToBackendSettings(256, 256), c.ToBackendSettings(256, 256, substepping: false));
    }

    [Fact]
    public void Both_keys_parse_and_collision_steps_apply_only_when_substepping()
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, ("PhysicsStepRate", "45"), ("PhysicsStepCollisionSteps", "3"), ("CollisionSteps", "5"));
        Assert.Empty(warnings);
        Assert.Equal(45f, c.PhysicsStepRate);
        Assert.Equal(45f, c.EffectivePhysicsStepRate(Heartbeat, out string w, out string note));
        Assert.Null(w);
        Assert.Equal(3, c.ToBackendSettings(256, 256, substepping: true).CollisionSteps);
        Assert.Equal(5, c.ToBackendSettings(256, 256, substepping: false).CollisionSteps);
    }

    // The solver never steps slower than 60 Hz, the step Jolt is built around: PhysicsStepCollisionSteps, or more where
    // that would be slower. A larger value set by the operator is kept.
    [Theory]
    [InlineData(90f, 2, 2)]
    [InlineData(60f, 2, 2)]
    [InlineData(45f, 2, 2)]
    [InlineData(30f, 2, 2)]
    [InlineData(22.5f, 2, 3)]
    [InlineData(20f, 2, 3)]
    [InlineData(15f, 2, 4)]
    [InlineData(11f, 2, 6)]
    [InlineData(45f, 1, 2)]
    [InlineData(45f, 4, 4)]
    [InlineData(22.5f, 6, 6)]
    public void The_solver_steps_at_60_Hz_or_faster(float rate, int set, int used)
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, ("PhysicsStepRate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                             ("PhysicsStepCollisionSteps", set.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(warnings);
        Assert.Equal(set, c.PhysicsStepCollisionSteps);
        Assert.Equal(used, c.CollisionStepsAt(rate));
        Assert.Equal(used, c.ToBackendSettings(256, 256, rate).CollisionSteps);
        Assert.Equal(used, c.ToBackendSettings(256, 256, substepping: true).CollisionSteps);
        Assert.True(rate * used >= JoltConfig.MinCollisionStepRate - 1e-3f);
        // One step per heartbeat keeps CollisionSteps.
        Assert.Equal(c.CollisionSteps, c.CollisionStepsAt(0f));
        Assert.Equal(c.CollisionSteps, c.ToBackendSettings(256, 256, 0f).CollisionSteps);
    }

    // An invalid rate gives the default, with one warning that names the value given and the rate used, as an
    // invalid value of every other key does.
    [Theory]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("fast")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public void An_invalid_rate_warns_and_gives_the_default(string value)
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, ("PhysicsStepRate", value));
        Assert.Single(warnings);
        Assert.StartsWith($"[Jolt] PhysicsStepRate = \"{value}\" is invalid", warnings[0]);
        Assert.EndsWith("using the default 45.", warnings[0]);
        Assert.Equal(45f, c.PhysicsStepRate);
        Assert.False(c.PhysicsStepRateSet);   // the default is in use, not a rate the operator set
        Assert.Equal(45f, c.EffectivePhysicsStepRate(Heartbeat, out string w, out string note));
        Assert.Null(w);
        Assert.Null(note);
        Assert.Equal(2, c.PhysicsStepCollisionSteps);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    public void An_invalid_collision_step_count_warns_and_keeps_the_default(string value)
    {
        var warnings = new List<string>();
        JoltConfig c = Parse(warnings, ("PhysicsStepCollisionSteps", value));
        Assert.Single(warnings);
        Assert.Contains("PhysicsStepCollisionSteps", warnings[0]);
        Assert.Contains("using the default 2.", warnings[0]);
        Assert.Equal(45f, c.PhysicsStepRate);
        Assert.Equal(2, c.PhysicsStepCollisionSteps);
    }

    [Theory]
    [InlineData(5f)]     // below an 11 Hz heartbeat
    [InlineData(10.9f)]
    public void A_rate_below_the_heartbeat_is_refused_with_one_warning(float rate)
    {
        JoltConfig c = Parse(new List<string>(), ("PhysicsStepRate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(c.PhysicsStepRateSet);
        Assert.Equal(0f, c.EffectivePhysicsStepRate(Heartbeat, out string w, out string note));
        Assert.NotNull(w);
        Assert.Contains("PhysicsStepRate", w);
        Assert.Contains("FrameTime", w);
        Assert.Null(note);
    }

    // A heartbeat faster than the default rate (FrameTime 1/90 s here). With no rate set: one step per heartbeat and an
    // information line, no warning. Set to the same 45 by the operator, or to an invalid value (which gives the
    // default): the operator's 45 keeps the warning, the invalid value has had its own warning and gets the note.
    [Theory]
    [InlineData(null, false)]
    [InlineData("45", true)]
    [InlineData("fast", false)]
    public void A_heartbeat_faster_than_the_default_warns_only_when_the_operator_set_the_rate(string value, bool warns)
    {
        const float FastHeartbeat = 1f / 90f;
        JoltConfig c = value == null ? Parse(new List<string>()) : Parse(new List<string>(), ("PhysicsStepRate", value));
        Assert.Equal(warns, c.PhysicsStepRateSet);
        Assert.Equal(0f, c.EffectivePhysicsStepRate(FastHeartbeat, out string w, out string note));
        if (warns)
        {
            Assert.Null(note);
            Assert.Equal("[Jolt] PhysicsStepRate = 45 is below the heartbeat's own rate (90 Hz, [Startup] FrameTime); " +
                         "using 0, one physics step per heartbeat.", w);
        }
        else
        {
            Assert.Null(w);
            Assert.Equal("[Jolt] PhysicsStepRate is not set, and its default of 45 Hz is below the heartbeat's own rate " +
                         "(90 Hz, [Startup] FrameTime); running one physics step per heartbeat.", note);
        }
    }

    [Theory]
    [InlineData(11f)]    // the heartbeat's own rate (1 / 0.0909 = 11.001) is accepted
    [InlineData(45f)]
    [InlineData(90f)]
    public void A_rate_at_or_above_the_heartbeat_is_used(float rate)
    {
        JoltConfig c = Parse(new List<string>(), ("PhysicsStepRate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(rate, c.EffectivePhysicsStepRate(Heartbeat, out string w, out string note));
        Assert.Null(w);
    }

    [Theory]
    [InlineData(0.0909, 45f)]
    [InlineData(1.0 / 11.0, 45f)]
    [InlineData(0.0909, 60f)]
    [InlineData(0.05, 45f)]
    [InlineData(0.0222, 45f)]   // a 45 Hz heartbeat: one step each
    public void Over_a_thousand_heartbeats_the_step_count_is_right_to_within_one(double heartbeat, float rate)
    {
        var acc = new SubstepAccumulator(rate);
        int min = int.MaxValue, max = 0;
        for (int i = 0; i < 1000; i++)
        {
            int n = acc.Advance(heartbeat);
            min = Math.Min(min, n);
            max = Math.Max(max, n);
        }
        double expected = 1000 * heartbeat * rate;
        Assert.InRange(acc.Steps, (long)Math.Floor(expected) - 1, (long)Math.Ceiling(expected) + 1);
        Assert.InRange((double)acc.Steps, expected - 1.0, expected + 1.0);
        Assert.True(max - min <= 1, $"steps per heartbeat ranged {min}..{max}");   // never bunched up
        Assert.InRange(acc.Carry, 0.0, 1.0 / rate);
        Assert.Equal(0, acc.CappedFrames);
    }

    [Fact]
    public void Every_step_is_exactly_one_over_the_rate()
    {
        Assert.Equal(1f / 45f, new SubstepAccumulator(45f).StepSeconds);
        Assert.Equal(1f / 60f, new SubstepAccumulator(60f).StepSeconds);
    }

    [Fact]
    public void A_heartbeat_past_the_cap_runs_the_cap_counts_it_and_drops_the_rest()
    {
        var acc = new SubstepAccumulator(45f);
        Assert.Equal(SubstepAccumulator.MaxStepsPerFrame, acc.Advance(1.0));   // 45 steps' worth
        Assert.Equal(1, acc.CappedFrames);
        Assert.Equal(0.0, acc.Carry);                                           // dropped, not carried
        Assert.Equal(4, acc.Advance(0.0909));                                   // back to normal at once
        Assert.Equal(1, acc.CappedFrames);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_heartbeat_with_no_time_runs_no_step(double frame)
    {
        var acc = new SubstepAccumulator(45f);
        Assert.Equal(0, acc.Advance(frame));
        Assert.Equal(0, acc.Steps);
    }
}
