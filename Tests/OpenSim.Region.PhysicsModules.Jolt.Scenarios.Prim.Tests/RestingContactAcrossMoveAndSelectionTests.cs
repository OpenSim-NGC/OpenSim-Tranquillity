/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Where a sleeping body's held contacts meet a move and a selection. A box rests asleep on a fixed platform, so the
/// engine reports none of their contacts and the dispatch holds them (collision_end is "Triggered when task stops
/// colliding with another task", wiki.secondlife.com/wiki/Collision_end). Then:
/// - the platform is moved away by setting its position, as llSetPos or the build tool does: the box is no longer held
///   up, so it wakes and falls, and the platform's script gets one collision_end for it;
/// - the box is selected in the build tool and held there: it has not stopped touching the platform, so neither side
///   gets an end while it is selected, nor when it is let go and settles back.
/// At an 11 Hz heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RestingContactAcrossMoveAndSelectionTests
{
    private static readonly Vector3 BoxSize = new(0.5f, 0.5f, 0.5f);
    // The box falls 0.5 m onto the platform and bounces (wood on wood combines to 0.5 under the shipped averaging: it rises 12 cm,
    // then 3 cm), so it sleeps about 3.7 s in, not 3.1 s as with a quarter of that bounce. The tests act after that.
    private const double ActAt = 5.0;

    private sealed class Result
    {
        public HarnessPart Box, Platform;
        public double SleptAt = double.NaN, ActedAt = double.NaN, ReleasedAt = double.NaN;
        public readonly List<(double T, Vector3 Position, bool Asleep)> Trace = new();
    }

    private static Result Run(double physicsHz, float seconds, Action<Run, Result> act)
    {
        var res = new Result();
        var sc = new Scenario
        {
            Name = "resting-contact-move-select",
            DefaultDuration = _ => seconds,
            Setup = r =>
            {
                float top = r.GroundAt(128f, 128f) + 3f;
                res.Platform = PhantomScenarios.AddPart(r, "platform", new Vector3(3f, 3f, 0.5f), new Vector3(128f, 128f, top - 0.25f),
                                                        false, false, false, true);
                res.Box = PhantomScenarios.AddPart(r, "box", BoxSize, new Vector3(128f, 128f, top + 0.5f + BoxSize.Z * 0.5f),
                                                   true, false, false, true);
                r.Actor = res.Box.Actor;
                r.ActorSize = BoxSize;
            },
            Input = r =>
            {
                var box = (JoltPrim)res.Box.Actor;
                bool asleep = box.PhysicalAndAsleep;
                res.Trace.Add((r.Now, box.Position, asleep));
                if (asleep && double.IsNaN(res.SleptAt) && r.Now > 0.5)
                    res.SleptAt = r.Now;
                act(r, res);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        return res;
    }

    private static int CountIn(List<double> times, double from, double to) => times.Count(t => t > from - 1e-9 && t < to - 1e-9);

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_sleeping_box_wakes_and_falls_when_its_platform_is_moved_away(double physicsHz)
    {
        Result res = Run(physicsHz, 8f, (r, s) =>
        {
            if (double.IsNaN(s.ActedAt) && r.Now >= ActAt)
            {
                s.Platform.Actor.Position += new Vector3(10f, 0f, 0f);   // llSetPos on the platform
                s.ActedAt = r.Now;
            }
        });

        Assert.True(res.SleptAt < ActAt - 0.5, $"the box was not asleep before the platform moved (slept at {res.SleptAt:0.00})");
        float restZ = res.Trace.Last(p => p.T < ActAt).Position.Z;

        // It wakes and falls: one second later it is well below where it rested (free fall from rest covers 4.9 m in 1 s;
        // the ground is 3 m down, so it has landed there).
        (double T, Vector3 Position, bool Asleep) later = res.Trace.First(p => p.T >= res.ActedAt + 1.0 - 1e-9);
        Assert.True(later.Position.Z < restZ - 2.5f, $"the box did not fall: z {later.Position.Z:0.00} a second after, rested at {restZ:0.00}");
        Assert.False(res.Trace.First(p => p.T >= res.ActedAt + 2.0 / 11.0 - 1e-9).Asleep, "the box was still asleep after the platform moved");

        // The platform: touched from the landing, no end while the box rests, exactly one end once it moves away, and no
        // new start. The box likewise for the platform.
        foreach ((CollisionWatch w, uint other) in new[] { (res.Platform.Watch, res.Box.LocalId), (res.Box.Watch, res.Platform.LocalId) })
        {
            List<double> starts = w.TimesOf("start", other), ends = w.TimesOf("end", other);
            Assert.True(starts.Count > 0 && starts[0] < res.SleptAt, $"{w.Name}: no start before the box slept");
            Assert.Equal(0, CountIn(ends, starts[^1], res.ActedAt));
            Assert.Equal(1, CountIn(ends, res.ActedAt, 8.0));
            Assert.Equal(0, CountIn(starts, res.ActedAt, 8.0));
            Assert.True(ends[^1] < res.ActedAt + 0.5, $"{w.Name}: ended at {ends[^1]:0.00} s, the platform moved at {res.ActedAt:0.00} s");
        }
    }

    [Theory]
    [InlineData(45.0, true)]
    [InlineData(0.0, true)]
    [InlineData(45.0, false)]
    [InlineData(0.0, false)]
    public void A_selected_box_keeps_touching_its_platform_while_selected_and_after(double physicsHz, bool selectAsleep)
    {
        // Selected once asleep, or as it lands (awake, still touching and settling).
        const float Seconds = 9f, HoldFor = 3f;
        Result res = Run(physicsHz, Seconds, (r, s) =>
        {
            var box = (JoltPrim)s.Box.Actor;
            if (double.IsNaN(s.ActedAt))
            {
                bool now = selectAsleep ? r.Now >= ActAt : s.Box.Watch.TimesOf("start", s.Platform.LocalId).Count > 0;
                if (now)
                {
                    s.Box.Actor.Selected = true;
                    s.ActedAt = r.Now;
                }
            }
            else if (double.IsNaN(s.ReleasedAt) && r.Now >= s.ActedAt + HoldFor)
            {
                s.Box.Actor.Selected = false;
                s.ReleasedAt = r.Now;
            }
        });

        Assert.False(double.IsNaN(res.ReleasedAt), "the run never let the box go");
        if (selectAsleep)
            Assert.True(res.SleptAt < res.ActedAt, $"the box was not asleep when selected (slept at {res.SleptAt:0.00})");

        // Held where it was while selected, and still on the platform at the end.
        Vector3 heldAt = res.Trace.First(p => p.T >= res.ActedAt - 1e-9).Position;
        foreach ((double T, Vector3 Position, bool _) p in res.Trace.Where(p => p.T > res.ActedAt && p.T < res.ReleasedAt))
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.01f, $"moved while selected at {p.T:0.00}: {p.Position} (held at {heldAt})");
        Vector3 end = res.Trace[^1].Position;
        // Held where a rebound had lifted it, if it was selected as it landed (the box bounces to 6 cm), so the end is judged
        // against the platform's top: the platform's centre, half its 0.5 m thickness, half the box.
        float restZ = res.Platform.Actor.Position.Z + 0.25f + BoxSize.Z * 0.5f;
        Assert.True(MathF.Abs(end.Z - restZ) < 0.05f, $"not back on the platform at the end: {end}, it rests at {restZ}");

        // Touching from the landing to the end, no end after it is selected. One start when it is selected as it lands. Selected
        // asleep, it has landed two or three times first: it arrives at 3.1 m/s, rebounds at 1.6 m/s (12 cm) and again at 0.8 m/s
        // (3 cm), the third arrival being under the engine's 1 m/s bounce cutoff.
        foreach ((CollisionWatch w, uint other) in new[] { (res.Platform.Watch, res.Box.LocalId), (res.Box.Watch, res.Platform.LocalId) })
        {
            // Selected asleep: two or three starts (the 3 cm rebound ends the contact at some step rates and not others), each
            // start after the first following one end, so it is touching at the end.
            // Selected as it lands (held in the first rebound), one or two.
            Assert.InRange(w.StartsOf(other), selectAsleep ? 2 : 1, selectAsleep ? 3 : 2);
            Assert.True(w.TimesOf("start", other)[0] <= res.ActedAt + 1e-9, $"{w.Name}: started after the selection");
            Assert.Equal(w.StartsOf(other) - 1, w.EndsOf(other));   // each rebound before the selection ended the contact once; none after
        }
    }
}
