/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The velocity a flying avatar crosses a region border with fades, as the speed of a flying avatar does when it lets go of its
/// forward key: an avatar that crossed alone with no key held stops in about the time one stops after letting go of the key,
/// and one that holds forward keeps flying at its normal speed. Serial with the other native tests: every scene steps a real
/// backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarFlyingArrivalTests
{
    private readonly ITestOutputHelper _out;
    public AvatarFlyingArrivalTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float Altitude = 40f;                // m above the ground: clear of it for the whole run
    private const float CrossingSpeed = 3f;            // m/s
    private const float RequestFly = 4.096f;           // the request core makes for a held forward key
    private const double StoppedBelow = 0.1;           // m/s: at rest
    private const double Slack = 0.5;                  // s more than a key let go needs: the time before a held key reaches the new region

    private static readonly float StandHalf = JoltCharacter.StandHalfFor(Run.AvatarSize);

    private static JoltScene NewScene(float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static JoltCharacter NewFlyer(JoltScene scene, float x)
    {
        PhysicsActor pa = scene.AddAvatar(99u, "Test User", new Vector3(x, 128f, Ground + Altitude + StandHalf), Run.AvatarSize, 0f, true);
        pa.SubscribeEvents(100);
        var av = Assert.IsType<JoltCharacter>(pa);
        av.Flying = true;
        return av;
    }

    // Seconds from heartbeat `from` until the avatar's speed is under StoppedBelow and stays there for a second; NaN if it
    // does not stop in the run. speeds[i] is the speed over heartbeat i (m/s).
    private static double StopTime(List<double> speeds, int from)
    {
        for (int i = from; i + 11 <= speeds.Count; i++)
        {
            bool still = true;
            for (int j = i; j < i + 11 && still; j++)
                still = speeds[j] < StoppedBelow;
            if (still)
                return (i - from) * Heartbeat;
        }
        return double.NaN;
    }

    private static double Speed(Vector3 a, Vector3 b) => (b - a).Length() / Heartbeat;

    // A flying avatar holds forward, then lets the key go: the time it takes to stop.
    private static double StopAfterLettingGoOfForward(float physicsRate, out double flySpeed)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = NewFlyer(scene, 100f);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            av.TargetVelocity = new Vector3(RequestFly, 0f, 0f);
            for (int i = 0; i < 22; i++)
                scene.Simulate(Heartbeat);
            Vector3 a = av.Position;
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            flySpeed = (av.Position.X - a.X) / (11 * Heartbeat);
            av.TargetVelocity = Vector3.Zero;
            var speeds = new List<double>();
            Vector3 prev = av.Position;
            for (int i = 0; i < 110; i++)
            {
                scene.Simulate(Heartbeat);
                speeds.Add(Speed(prev, av.Position));
                prev = av.Position;
            }
            return StopTime(speeds, 0);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_flying_avatar_that_crossed_alone_with_no_key_held_stops_as_soon_as_one_does_after_a_key_is_let_go(float physicsRate)
    {
        double afterKey = StopAfterLettingGoOfForward(physicsRate, out double flySpeed);
        JoltScene scene = NewScene(physicsRate);
        try
        {
            // ScenePresence.MakeRootAgent: the avatar is made flying, then SetMomentum(its velocity); no control input follows
            JoltCharacter av = NewFlyer(scene, 100f);
            av.SetMomentum(new Vector3(CrossingSpeed, 0f, 0f));
            var speeds = new List<double>();
            Vector3 prev = av.Position;
            for (int i = 0; i < 110; i++)
            {
                scene.Simulate(Heartbeat);
                speeds.Add(Speed(prev, av.Position));
                prev = av.Position;
            }
            double stop = StopTime(speeds, 0);
            double travelled = av.Position.X - 100f;
            Say($"ARRIVE flying alone at {CrossingSpeed:0} m/s, no key, rate {physicsRate:0}: stopped after {F(stop)} s ({F(travelled)} m), "
                + $"a flying avatar that lets go of forward (flying at {F(flySpeed)} m/s) stops in {F(afterKey)} s");
            Assert.False(double.IsNaN(afterKey), "the avatar never stopped after letting go of forward");
            Assert.False(double.IsNaN(stop), $"the avatar kept moving: {F(speeds[^1])} m/s after 10 s, {F(travelled)} m from where it arrived");
            Assert.True(stop <= afterKey + Slack, $"it took {F(stop)} s to stop against {F(afterKey)} s after a key is let go");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_flying_avatar_that_holds_forward_keeps_flying_at_its_normal_speed_after_the_crossing(float physicsRate)
    {
        StopAfterLettingGoOfForward(physicsRate, out double flySpeed);
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = NewFlyer(scene, 100f);
            av.SetMomentum(new Vector3((float)flySpeed, 0f, 0f));   // what a flying avatar crosses with
            var speeds = new List<double>();
            Vector3 prev = av.Position;
            for (int i = 0; i < 44; i++)
            {
                if (i == 2)
                    av.TargetVelocity = new Vector3(RequestFly, 0f, 0f);   // the held key reaches the region two heartbeats after the avatar
                scene.Simulate(Heartbeat);
                speeds.Add(Speed(prev, av.Position));
                prev = av.Position;
            }
            // at 45 Hz a heartbeat holds four or five steps, so a single heartbeat's speed is read over four (about a third of a second)
            double steady = speeds.Skip(12).Average(), top = 0;
            for (int i = 2; i + 4 <= speeds.Count; i++)
                top = Math.Max(top, speeds.Skip(i).Take(4).Average());
            Say($"ARRIVE flying, holding forward, rate {physicsRate:0}: steady {F(steady)} m/s, fastest {F(top)} m/s, flying from rest {F(flySpeed)} m/s");
            Assert.InRange(steady, flySpeed - 0.1, flySpeed + 0.1);
            Assert.True(top <= flySpeed + 0.2, $"it flew at {F(top)} m/s against {F(flySpeed)}");
        }
        finally { scene.Dispose(); }
    }
}
