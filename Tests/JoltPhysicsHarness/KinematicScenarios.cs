/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Kinematic scenarios: a 6 x 6 x 0.5 m non-physical platform, its top 3 m above the ground, with a physical 0.5 m cube or an
// avatar standing at its centre (settled 4 s). The platform then moves 6 m, sideways (+x) or up, at 1 or 3 m/s, and the run
// goes on 2 s more. Two ways to move it, as core does:
//   - keyframed: a position set every 50 ms (KeyframeMotion's timer), with PhysicsActor.MovedByKeyframedMotion on while it
//     plays and off at the first tick after the path ends;
//   - setpos: a position set every 0.2 s (llSetPos costs 0.2 s a call), no hint.
// Every set that falls before a heartbeat's step is made before it, in order, as the timer's thread would. The rider's place
// against the platform's is what a run is judged by (Figures): whether it stays on, how far it slides back, how far it trails
// the platform sideways, how far below and above its resting height it goes.
// The turn scenarios turn the platform about its vertical axis at 30 degrees a second for 3 s (keyframed), the rider 2 m
// from its centre.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class KinematicScenarios
{
    public const float PlatformSide = 6f, PlatformThickness = 0.5f, PlatformHeight = 3f, PathLength = 6f;
    public const float CentreX = 128f, CentreY = 128f;
    public const double SettleSeconds = 4.0, TrailSeconds = 2.0;
    public const double KeyframeInterval = 0.05, SetPosInterval = 0.2;
    public const float TurnDegreesPerSecond = 30f, TurnSeconds = 3f, TurnRadius = 2f;

    private sealed class State
    {
        public Vector3 Start;
        public bool Started, Stopped;
        public double NextSet;
        public int SetIndex, Beat, DoneBeat;
    }

    /// <summary>The figures of a straight-path run (see the file header).</summary>
    public sealed record Figures(string Verdict, bool OnAtEnd, double Slide, double MaxLag, double MaxSink, double MaxAir)
    {
        /// <summary>The most the rider's place against the platform's differs from where it rested at the end of the settle, in
        /// any direction, from the first set to the end of the run.</summary>
        public double MaxFollow { get; init; }

        /// <summary>The same, over the part of the run in which the platform moves at its speed: from half a second after it
        /// starts to the end of the path, against the rider's mean place over that stretch (what remains is the start and
        /// the stop, which a rider on a body that changes speed at once feels as the change). 0 when the figures were read without the speed.</summary>
        public double SteadyFollow { get; init; }

        /// <summary>The same as MaxFollow, over the stop only: from the end of the path to the end of the run (the platform
        /// goes from its speed to rest in one heartbeat). 0 when the figures were read without the speed.</summary>
        public double StopFollow { get; init; }
    }

    private static string NameOf(string rider, bool up, float speed, bool keyframed)
        => $"kin-{rider}-{(up ? "up" : "side")}-{speed:0}-{(keyframed ? "keyframed" : "setpos")}";

    /// <summary>A straight-path run. <paramref name="hint"/> is whether the platform is told keyframed motion moves it: the
    /// registered keyframed scenarios do, the plain ones never do.</summary>
    public static Scenario Motion(string rider, bool up, float speed, bool keyframed, bool hint, double? setInterval = null, int[] setsPerHeartbeat = null)
    {
        State st = null;
        double moveTime = PathLength / speed;
        double interval = setInterval ?? (keyframed ? KeyframeInterval : SetPosInterval);
        Vector3 dir = up ? Vector3.UnitZ : Vector3.UnitX;
        return new Scenario
        {
            Name = NameOf(rider, up, speed, keyframed) + (keyframed && !hint ? "-nohint" : "") + (setInterval.HasValue ? $"-every{setInterval.Value * 1000:0}ms" : "") + (setsPerHeartbeat != null ? "-uneven" : ""),
            Description = $"A {rider} on a {PlatformSide:0} m non-physical platform moved {PathLength:0} m {(up ? "up" : "along +x")} at {speed:0} m/s by "
                          + (keyframed ? "keyframed motion (a set every 50 ms" + (hint ? ", the platform told)." : ", the platform not told).") : "plain position sets (one every 0.2 s)."),
            DefaultDuration = _ => (float)(SettleSeconds + moveTime + TrailSeconds),
            Setup = r =>
            {
                st = new State();
                float top = r.GroundAt(CentreX, CentreY) + PlatformHeight;
                st.Start = new Vector3(CentreX, CentreY, top - PlatformThickness * 0.5f);
                r.AddOtherBox(new Vector3(PlatformSide, PlatformSide, PlatformThickness), st.Start, Quaternion.Identity, false);
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(CentreX, CentreY, top + 0.26f), Quaternion.Identity);
                else
                    r.AddAvatarAt(new Vector3(CentreX, CentreY, top + r.AvatarStandHalf + 0.05f), false);
            },
            Input = r =>
            {
                double until = r.Now + r.Dt;
                if (!st.Started && until > SettleSeconds + 1e-9)
                {
                    st.Started = true;
                    st.NextSet = SettleSeconds;
                    if (keyframed && hint)
                        r.Other.MovedByKeyframedMotion = true;
                }
                if (!st.Started)
                    return;
                if (setsPerHeartbeat != null)
                {
                    // Heartbeats that see a different number of sets each (a region's heartbeat is not as regular as its timer): the
                    // pattern says how many sets each heartbeat from the start finds, and the path is the same as the regular one's.
                    int n = setsPerHeartbeat[st.Beat++ % setsPerHeartbeat.Length];
                    for (int i = 0; i < n && st.NextSet - SettleSeconds <= moveTime + 1e-9; i++)
                    {
                        double done = Math.Min(1.0, (++st.SetIndex * interval) / moveTime);
                        r.Other.Position = st.Start + dir * (PathLength * (float)done);
                        st.NextSet += interval;
                    }
                    if (st.DoneBeat == 0 && st.NextSet - SettleSeconds > moveTime + 1e-9)
                        st.DoneBeat = st.Beat;
                    else if (!st.Stopped && st.DoneBeat != 0)
                    {
                        st.Stopped = true;
                        if (keyframed && hint)
                            r.Other.MovedByKeyframedMotion = false;
                    }
                    return;
                }
                while (st.NextSet < until - 1e-9 && st.NextSet - SettleSeconds <= moveTime + 1e-9)
                {
                    double done = Math.Min(1.0, (st.NextSet - SettleSeconds + interval) / moveTime);   // where the path is at the next tick
                    r.Other.Position = st.Start + dir * (PathLength * (float)done);
                    st.NextSet += interval;
                }
                if (!st.Stopped && st.NextSet - SettleSeconds > moveTime + 1e-9 && st.NextSet < until - 1e-9)
                {
                    st.Stopped = true;
                    if (keyframed && hint)
                        r.Other.MovedByKeyframedMotion = false;   // the first tick that finds the path played out
                }
            },
        };
    }

    /// <summary>Reads a straight-path run's figures: the rider's place against the platform's, from the end of the settle
    /// to the end of the run.</summary>
    public static Figures Measure(RunResult result, bool up, float speed = 0f)
    {
        var moving = new List<Vector3>();
        List<Sample> s = result.Samples;
        int at = s.FindLastIndex(x => x.T <= SettleSeconds + 1e-6);
        Vector3 off0 = s[at].Position - s[at].Other;
        double maxLag = 0, maxSink = 0, maxAir = 0, maxFollow = 0, steady = 0, stopFollow = 0;
        for (int i = at + 1; i < s.Count; i++)
        {
            Vector3 rel = s[i].Position - s[i].Other;
            maxFollow = Math.Max(maxFollow, (rel - off0).Length());
            if (!up)
                maxLag = Math.Max(maxLag, Math.Max(off0.X - rel.X, Math.Abs(rel.Y - off0.Y)));
            double dz = rel.Z - off0.Z;
            maxSink = Math.Max(maxSink, -dz);
            maxAir = Math.Max(maxAir, dz);
            if (speed > 0f && s[i].T >= SettleSeconds + 0.5 && s[i].T <= SettleSeconds + PathLength / speed + 1e-6)
                moving.Add(rel);
            if (speed > 0f && s[i].T > SettleSeconds + PathLength / speed + 1e-6)
                stopFollow = Math.Max(stopFollow, (rel - off0).Length());
        }
        if (moving.Count > 0)
        {
            // against the rider's mean place over the stretch: a rider that slid back as the platform started keeps that offset;
            // what matters is how far it moves about it
            Vector3 mean = Vector3.Zero;
            foreach (Vector3 v in moving) mean += v;
            mean /= moving.Count;
            foreach (Vector3 v in moving) steady = Math.Max(steady, (v - mean).Length());
        }
        Sample end = s[^1];
        Vector3 relEnd = end.Position - end.Other;
        bool on = Math.Abs(relEnd.X) < PlatformSide / 2 - 0.1 && Math.Abs(relEnd.Y) < PlatformSide / 2 - 0.1 && Math.Abs(relEnd.Z - off0.Z) < 0.3;
        double slide = Math.Sqrt(Math.Pow(relEnd.X - off0.X, 2) + Math.Pow(relEnd.Y - off0.Y, 2));
        string verdict = on ? (slide > 0.5 ? "stays on, slid" : "stays on") : (end.Position.Z < end.Other.Z - 0.3 ? "fell below" : "left behind");
        return new Figures(verdict, on, slide, maxLag, maxSink, maxAir) { MaxFollow = maxFollow, SteadyFollow = steady, StopFollow = stopFollow };
    }

    /// <summary>A turning platform: where the rider is, as an angle about the platform's centre and a distance from it, at the end
    /// of the run, against the angle the platform has turned.</summary>
    public sealed record TurnFigures(double PlatformDegrees, double RiderDegrees, double RadiusMetres, double RiseMetres);

    public static Scenario Turn(string rider)
    {
        State st = null;
        return new Scenario
        {
            Name = $"kin-{rider}-turn",
            Description = $"A {rider} {TurnRadius:0} m from the centre of a {PlatformSide:0} m non-physical platform that keyframed motion turns about z at {TurnDegreesPerSecond:0} degrees a second for {TurnSeconds:0} s.",
            DefaultDuration = _ => (float)(SettleSeconds + TurnSeconds + TrailSeconds),
            Setup = r =>
            {
                st = new State();
                float top = r.GroundAt(CentreX, CentreY) + PlatformHeight;
                st.Start = new Vector3(CentreX, CentreY, top - PlatformThickness * 0.5f);
                r.AddOtherBox(new Vector3(PlatformSide, PlatformSide, PlatformThickness), st.Start, Quaternion.Identity, false);
                var at = new Vector3(CentreX + TurnRadius, CentreY, 0f);
                if (rider == "box")
                    r.AddBox(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(at.X, at.Y, top + 0.26f), Quaternion.Identity);
                else
                    r.AddAvatarAt(new Vector3(at.X, at.Y, top + r.AvatarStandHalf + 0.05f), false);
            },
            Input = r =>
            {
                double until = r.Now + r.Dt;
                if (!st.Started && until > SettleSeconds + 1e-9)
                {
                    st.Started = true;
                    st.NextSet = SettleSeconds;
                    r.Other.MovedByKeyframedMotion = true;
                }
                if (!st.Started)
                    return;
                while (st.NextSet < until - 1e-9 && st.NextSet - SettleSeconds <= TurnSeconds + 1e-9)
                {
                    double done = Math.Min(1.0, (st.NextSet - SettleSeconds + KeyframeInterval) / TurnSeconds);
                    r.Other.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(done * TurnSeconds * TurnDegreesPerSecond * Math.PI / 180.0));
                    st.NextSet += KeyframeInterval;
                }
                if (!st.Stopped && st.NextSet - SettleSeconds > TurnSeconds + 1e-9 && st.NextSet < until - 1e-9)
                {
                    st.Stopped = true;
                    r.Other.MovedByKeyframedMotion = false;
                }
            },
        };
    }

    /// <summary>The figures of a turn run.</summary>
    public static TurnFigures MeasureTurn(RunResult result)
    {
        Sample end = result.Samples[^1];
        Vector3 rel = end.Position - end.Other;
        Vector3 settled = result.Samples.FindLast(x => x.T <= SettleSeconds + 1e-6).Position - result.Samples.FindLast(x => x.T <= SettleSeconds + 1e-6).Other;
        double platform = TurnDegreesPerSecond * TurnSeconds;
        double rider = Math.Atan2(rel.Y, rel.X) * 180.0 / Math.PI;
        return new TurnFigures(platform, rider, Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y), rel.Z - settled.Z);
    }

    public static readonly IReadOnlyList<Scenario> All = BuildAll();

    private static List<Scenario> BuildAll()
    {
        var list = new List<Scenario>();
        foreach (string rider in new[] { "box", "avatar" })
            foreach (bool up in new[] { false, true })
                foreach (float speed in new[] { 1f, 3f })
                    foreach (bool keyframed in new[] { true, false })
                        list.Add(Motion(rider, up, speed, keyframed, hint: keyframed));
        list.Add(Turn("box"));
        list.Add(Turn("avatar"));
        return list;
    }
}
