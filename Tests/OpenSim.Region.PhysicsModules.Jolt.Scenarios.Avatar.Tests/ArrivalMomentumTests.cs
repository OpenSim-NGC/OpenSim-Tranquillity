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
/// An avatar that crossed a region border standing on a platform is made again in the next region over the platform,
/// which is held at rest while its riders arrive and then moves on by keyframed motion. Core hands the new avatar a
/// momentum (SetMomentum) right after it is made. The avatar's velocity in the first region is its world velocity,
/// which already holds the platform's own, so core hands zero for a carried avatar: the engine carries a standing
/// avatar with the platform it stands on. These tests make the same calls in the same order, at both step modes, and
/// measure the avatar against the deck: with zero it moves with the deck (also when the deck stops, and while it
/// walks on the deck); with the deck's own velocity handed over as well it is carried a few centimetres ahead and then
/// stays on the deck, the handed velocity being momentum that fades on the ground.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ArrivalMomentumTests
{
    private readonly ITestOutputHelper _out;
    public ArrivalMomentumTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float PlatformTop = Ground + 3f;
    private const float Thickness = 0.5f;
    private const float DeckSpeed = 3f;                  // metres a second, the speed a platform had in a crossing that was observed
    private const float VelocityTolerance = 0.2f;        // m/s: how far the avatar's speed may differ from the deck's
    private const uint AvatarId = 99u;
    private const uint PlatformId = 500u;

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

    // An 8 x 8 x 0.5 m non-physical platform whose top is at PlatformTop, centred on (x, 128).
    private static JoltPrim Platform(JoltScene scene, float x)
        => Assert.IsType<JoltPrim>(scene.AddPrimShape("platform", PrimitiveBaseShape.CreateBox(),
            new Vector3(x, 128f, PlatformTop - Thickness * 0.5f), new Vector3(8f, 8f, Thickness), Quaternion.Identity, false, PlatformId));

    // ScenePresence.MakeRootAgent for an avatar that crossed: AddToPhysicalScene (AddAvatar with the carried flag,
    // SubscribeEvents), then SetMomentum with what core hands over.
    private static JoltCharacter Arrive(JoltScene scene, Vector3 at, Vector3 momentum)
    {
        PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", at, Run.AvatarSize, 0f, false, true);
        pa.SubscribeEvents(100);
        pa.SetMomentum(momentum);
        return Assert.IsType<JoltCharacter>(pa);
    }

    private sealed class Outcome
    {
        public double MaxDrift;            // furthest the avatar was from its place on the deck, along x, after the first second
        public double FinalDrift;
        public double LastSecondSpeedGap;  // how far the avatar's speed was from the deck's, averaged over the last second, m/s
        public double RelativeSpeed;       // the avatar's speed along the deck's direction relative to the deck, same second, m/s
        public double LowestUnderSeat;
        public bool OnDeckAtEnd;
    }

    /// <summary>
    /// The platform is held at rest, the avatar is made over it with <paramref name="momentum"/>, a second passes, then
    /// the platform moves at DeckSpeed for <paramref name="moveBeats"/> heartbeats, stops, and rests for
    /// <paramref name="restBeats"/> more. <paramref name="walk"/> is a walking request the avatar's owner makes once
    /// the deck moves (its controls are frozen until then).
    /// </summary>
    private Outcome Run3(float physicsRate, Vector3 momentum, int moveBeats, int restBeats, Vector3 walk)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, 100f);
            Vector3 start = plat.Position;
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = Arrive(scene, new Vector3(100f, 128f, seat + 0.01f), momentum);
            for (int i = 0; i < 11; i++)
                scene.Simulate(Heartbeat);               // held for a second while the others arrive
            Vector3 place = av.Position - plat.Position;
            plat.MovedByKeyframedMotion = true;
            if (walk != Vector3.Zero)
                av.TargetVelocity = walk;

            var o = new Outcome();
            double gapSum = 0, relSum = 0;
            int gapCount = 0;
            int total = moveBeats + restBeats;
            Vector3 prevAv = av.Position, prevPlat = plat.Position;
            for (int h = 1; h <= total; h++)
            {
                float travelled = Math.Min(h, moveBeats) * DeckSpeed * Heartbeat;
                plat.Position = start + new Vector3(travelled, 0f, 0f);
                scene.Simulate(Heartbeat);
                o.LowestUnderSeat = Math.Max(o.LowestUnderSeat, seat - av.Position.Z);
                double drift = (av.Position - plat.Position - place).X;
                if (walk == Vector3.Zero && h >= 11)
                    o.MaxDrift = Math.Max(o.MaxDrift, Math.Abs(drift));
                o.FinalDrift = drift;
                if (h > total - 11)
                {
                    double avSpeed = (av.Position.X - prevAv.X) / Heartbeat;
                    double deckSpeed = (plat.Position.X - prevPlat.X) / Heartbeat;
                    gapSum += Math.Abs(avSpeed - deckSpeed);
                    relSum += avSpeed - deckSpeed;
                    gapCount++;
                }
                prevAv = av.Position;
                prevPlat = plat.Position;
            }
            o.LastSecondSpeedGap = gapCount == 0 ? 0 : gapSum / gapCount;
            o.RelativeSpeed = gapCount == 0 ? 0 : relSum / gapCount;
            o.OnDeckAtEnd = av.IsSupported && av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value;
            return o;
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_rider_handed_no_momentum_moves_with_the_deck_when_the_deck_moves_on(float physicsRate)
    {
        Outcome o = Run3(physicsRate, Vector3.Zero, moveBeats: 55, restBeats: 0, Vector3.Zero);
        Say($"ARRIVE zero momentum, rate {physicsRate:0}: drift max {F(o.MaxDrift)} m, speed gap {F(o.LastSecondSpeedGap)} m/s, on deck {o.OnDeckAtEnd}");
        Assert.True(o.OnDeckAtEnd, "the avatar is not on the deck at the end");
        Assert.True(o.LastSecondSpeedGap <= VelocityTolerance, $"the avatar's speed was {o.LastSecondSpeedGap:0.000} m/s off the deck's");
        Assert.True(o.MaxDrift <= 0.2, $"the avatar was {o.MaxDrift:0.000} m off its place on the deck");
        Assert.True(o.LowestUnderSeat <= 0.1, $"the avatar dropped {o.LowestUnderSeat:0.000} m");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_rider_handed_the_decks_own_velocity_as_well_is_carried_only_a_moment_ahead_of_it(float physicsRate)
    {
        // what core handed over before it counted the platform's motion once: the world velocity the avatar had in the first
        // region. It is momentum and fades on the deck, so it moves the avatar a few centimetres, not off the platform
        Outcome o = Run3(physicsRate, new Vector3(DeckSpeed, 0f, 0f), moveBeats: 55, restBeats: 0, Vector3.Zero);
        Say($"ARRIVE deck's velocity as momentum, rate {physicsRate:0}: drift max {F(o.MaxDrift)} m, speed gap {F(o.LastSecondSpeedGap)} m/s, on deck {o.OnDeckAtEnd}");
        Assert.True(o.OnDeckAtEnd, "the avatar is not on the deck at the end");
        Assert.True(o.MaxDrift <= 0.5, $"the avatar was {o.MaxDrift:0.000} m off its place on the deck");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_rider_stops_with_a_deck_that_stops_right_after_the_border(float physicsRate)
    {
        // the deck moves for a second, then stops and rests for two
        Outcome o = Run3(physicsRate, Vector3.Zero, moveBeats: 11, restBeats: 22, Vector3.Zero);
        Say($"ARRIVE deck stops, rate {physicsRate:0}: final drift {F(o.FinalDrift)} m, speed gap {F(o.LastSecondSpeedGap)} m/s, on deck {o.OnDeckAtEnd}");
        Assert.True(o.OnDeckAtEnd, "the avatar is not on the deck at the end");
        Assert.True(o.LastSecondSpeedGap <= VelocityTolerance, $"the avatar still moved {o.LastSecondSpeedGap:0.000} m/s against the resting deck");
        Assert.True(o.MaxDrift <= 0.2, $"the avatar was {o.MaxDrift:0.000} m off its place on the deck");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_rider_that_walks_on_the_deck_from_the_moment_it_moves_is_not_launched_and_does_not_fall_through(float physicsRate)
    {
        Vector3 walk = new(1.5f, 0f, 0f);
        // the same walk on a deck that does not move gives the avatar's own walking speed on a deck (the module scales it)
        Outcome still = Run3(physicsRate, Vector3.Zero, moveBeats: 0, restBeats: 22, walk);
        Outcome o = Run3(physicsRate, Vector3.Zero, moveBeats: 22, restBeats: 0, walk);
        Say($"ARRIVE walking, rate {physicsRate:0}: speed on the deck {F(o.RelativeSpeed)} m/s against {F(still.RelativeSpeed)} m/s on a deck at rest, dropped {F(o.LowestUnderSeat)} m, on deck {o.OnDeckAtEnd}");
        Assert.True(o.OnDeckAtEnd, "the avatar is not on the deck at the end");
        Assert.True(Math.Abs(o.RelativeSpeed - still.RelativeSpeed) <= VelocityTolerance,
            $"the avatar walked {o.RelativeSpeed:0.000} m/s on the moving deck against {still.RelativeSpeed:0.000} m/s on the resting one");
        Assert.True(o.LowestUnderSeat <= 0.1, $"the avatar dropped {o.LowestUnderSeat:0.000} m");
    }
}
