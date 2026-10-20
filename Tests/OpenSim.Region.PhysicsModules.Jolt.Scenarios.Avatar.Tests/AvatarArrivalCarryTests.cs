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
/// The velocity an avatar crosses a region border with is momentum, not a walk command: handed to the new region's avatar
/// (SetMomentum), it carries the avatar on, fades once the avatar is on the ground, and gives way to whatever the avatar's
/// controls ask for. An avatar that crossed alone with no movement key held stops in about the time an avatar stops after
/// it lands from a jump with its key let go; one that holds a movement key walks on at its normal speed.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarArrivalCarryTests
{
    private readonly ITestOutputHelper _out;
    public AvatarArrivalCarryTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float CrossingSpeed = 3f;            // m/s, a standing avatar's world velocity on a platform that moves at 3 m/s
    private const float RequestWalk = 4.096f;          // the walk request core makes for a held forward key (3.2 m/s in the region)
    private const double StoppedBelow = 0.1;           // m/s: at rest
    private const double Slack = 0.5;                  // s more than a landing needs: the time before a held key reaches the new region

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

    private static JoltCharacter NewAvatar(JoltScene scene, float x)
    {
        PhysicsActor pa = scene.AddAvatar(99u, "Test User", new Vector3(x, 128f, Ground + StandHalf + 0.01f), Run.AvatarSize, 0f, false);
        pa.SubscribeEvents(100);
        return Assert.IsType<JoltCharacter>(pa);
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

    private static double Speed(Vector3 a, Vector3 b) => new Vector2(b.X - a.X, b.Y - a.Y).Length() / Heartbeat;

    // An avatar walking on a walk request lands from a jump and its key is let go as it lands: the time it takes to stop.
    private static double StopAfterLandingFromAJump(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = NewAvatar(scene, 100f);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            av.TargetVelocity = new Vector3(RequestWalk, 0f, 0f);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            av.AvatarJump(0f);
            bool airborne = false, landed = false;
            int landedAt = 0;
            var speeds = new List<double>();
            Vector3 prev = av.Position;
            for (int i = 0; i < 110; i++)
            {
                scene.Simulate(Heartbeat);
                if (!av.IsSupported)
                    airborne = true;
                if (airborne && av.IsSupported && !landed)
                {
                    landed = true;
                    landedAt = speeds.Count;
                    av.TargetVelocity = Vector3.Zero;
                }
                speeds.Add(Speed(prev, av.Position));
                prev = av.Position;
            }
            Assert.True(landed, "the avatar never landed");
            return StopTime(speeds, landedAt);
        }
        finally { scene.Dispose(); }
    }

    private static double WalkSpeedFromRest(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = NewAvatar(scene, 100f);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            av.TargetVelocity = new Vector3(RequestWalk, 0f, 0f);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);
            Vector3 a = av.Position;
            for (int i = 0; i < 22; i++)
                scene.Simulate(Heartbeat);
            return (av.Position.X - a.X) / (22 * Heartbeat);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_that_crossed_alone_with_no_key_held_stops_as_soon_as_one_does_after_a_jump(float physicsRate)
    {
        double afterJump = StopAfterLandingFromAJump(physicsRate);
        JoltScene scene = NewScene(physicsRate);
        try
        {
            // ScenePresence.MakeRootAgent: the avatar is made, then SetMomentum(its velocity); no control input follows
            JoltCharacter av = NewAvatar(scene, 100f);
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
            Say($"ARRIVE alone at {CrossingSpeed:0} m/s, no key, rate {physicsRate:0}: stopped after {F(stop)} s ({F(travelled)} m), a landing from a jump stops in {F(afterJump)} s");
            Assert.False(double.IsNaN(afterJump), "the avatar never stopped after landing from a jump");
            Assert.False(double.IsNaN(stop), $"the avatar kept moving: {F(speeds[^1])} m/s after 10 s, {F(travelled)} m from where it arrived");
            Assert.True(stop <= afterJump + Slack, $"it took {F(stop)} s to stop against {F(afterJump)} s after a jump");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_that_holds_forward_keeps_walking_at_its_normal_speed_after_the_crossing(float physicsRate)
    {
        double walkSpeed = WalkSpeedFromRest(physicsRate);
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = NewAvatar(scene, 100f);
            av.SetMomentum(new Vector3((float)walkSpeed, 0f, 0f));   // what a walking avatar crosses with
            var speeds = new List<double>();
            Vector3 prev = av.Position;
            for (int i = 0; i < 44; i++)
            {
                if (i == 2)
                    av.TargetVelocity = new Vector3(RequestWalk, 0f, 0f);   // the held key reaches the region two heartbeats after the avatar
                scene.Simulate(Heartbeat);
                speeds.Add(Speed(prev, av.Position));
                prev = av.Position;
            }
            // at 45 Hz a heartbeat holds four or five steps, so a single heartbeat's speed is read over four (about a third of a second)
            double steady = speeds.Skip(12).Average(), top = 0;
            for (int i = 2; i + 4 <= speeds.Count; i++)
                top = Math.Max(top, speeds.Skip(i).Take(4).Average());
            Say($"ARRIVE holding forward, rate {physicsRate:0}: steady {F(steady)} m/s, fastest {F(top)} m/s, a walk from rest {F(walkSpeed)} m/s");
            Assert.InRange(steady, walkSpeed - 0.1, walkSpeed + 0.1);
            Assert.True(top <= walkSpeed + 0.2, $"it ran at {F(top)} m/s against a walk of {F(walkSpeed)}");
        }
        finally { scene.Dispose(); }
    }
}
