/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical deck that crossed into a region with a standing rider is kept from crossing out again for three seconds
/// (<c>PhysicsActor.WaitsBeforeCrossingBack</c>; Halcyon: SceneObjectGroup.TestForAndBeginCrossing, which puts the
/// object back inside the region and leaves the avatars standing on it alone). The test plays the calls core makes
/// when the deck is told to go back during that wait: the engine takes the body out at the border, core puts the object
/// back half a metre inside and calls CrossingFailure, and the object's script keeps pushing it. The rider is a carried
/// avatar (AllowOutsideRegion), as core makes one for an arrival. Each step is recorded as a line "WAIT ..." in the test
/// output and in a file named by the JOLT_WAIT_RECORD_DIR environment variable, when it is set.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RecrossWaitRiderTests
{
    private readonly ITestOutputHelper _out;
    public RecrossWaitRiderTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float DeckSide = 4f, DeckThickness = 0.5f;
    private const float DeckZ = Ground + 8f;
    private const double WaitSeconds = 3.0;                 // RideOnLimits.RecrossWaitMs
    private const double CommandAt = 0.3;                   // the deck's script answers its first report
    private const uint DeckId = 800u, RiderId = 801u;

    private static readonly float StandHalf = JoltCharacter.StandHalfFor(Run.AvatarSize);

    private static JoltScene NewScene(string basicForces, float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("BasicForcesBehaviour", basicForces);
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        scene.Simulate(Heartbeat);
        return scene;
    }

    public sealed record Row(double T, Vector3 Deck, Vector3 DeckVel, bool DeckAway, Vector3 Rider, Vector3 RiderVel, bool OnDeck, string Event);

    public sealed class Outcome
    {
        public readonly List<Row> Rows = new();
        /// <summary>First time the rider is off the deck, NaN when it never is.</summary>
        public double LeftAt = double.NaN;
        /// <summary>How many times core had to put the deck back inside during the wait.</summary>
        public int PutBack;
        /// <summary>The time the deck crossed out (the real crossing after the wait), NaN when it did not.</summary>
        public double CrossedAt = double.NaN;
        public bool RiderOnDeckAtCrossing;
        public double HeldFrom = double.NaN, HeldUntil = double.NaN;
        public double MaxDeckX = double.MinValue, MinDeckX = double.MaxValue;
        public double MaxRiderSpeed, MinRiderZ = double.MaxValue;
    }

    // The rider is on the deck when it is over the deck's footprint and its feet are within a body's height above its top.
    private static bool IsOn(Vector3 deck, Vector3 rider)
    {
        float top = deck.Z + DeckThickness * 0.5f;
        return Math.Abs(rider.X - deck.X) <= DeckSide * 0.5f + 0.3f && Math.Abs(rider.Y - deck.Y) <= DeckSide * 0.5f + 0.3f
            && rider.Z - top >= StandHalf - 0.4f && rider.Z - top <= StandHalf + 0.6f;
    }

    /// <summary>
    /// The deck is in the second region 6 m from its west border with the rider standing on it, the wait just begun. Its
    /// script calls llMoveToTarget back west at <see cref="CommandAt"/>. Core's answer to the deck reaching the border is
    /// played as core plays it: during the wait the object is put back (CrossAsyncCore's hold branch, then
    /// CrossAsyncCompleted); after it the deck crosses, with the rider taken out of the engine first.
    /// </summary>
    private Outcome Play(string basicForces, float physicsRate, double seconds, float DeckStartX = 1.5f, float RiderAhead = 0.8f)
    {
        JoltScene scene = NewScene(basicForces, physicsRate);
        var o = new Outcome();
        try
        {
            var deck = Assert.IsType<JoltPrim>(scene.AddPrimShape("deck", PrimitiveBaseShape.CreateBox(),
                new Vector3(DeckStartX, 128f, DeckZ), new Vector3(DeckSide, DeckSide, DeckThickness), Quaternion.Identity, true, DeckId));
            deck.Buoyancy = 1f;
            deck.Density = 100f;
            deck.LockAngularMotion(0x0E);
            float seat = DeckZ + DeckThickness * 0.5f + StandHalf + 0.02f;
            PhysicsActor pa = scene.AddAvatar(RiderId, "Test User", new Vector3(DeckStartX + RiderAhead, 128f, seat), Run.AvatarSize, 0f, false, true);
            pa.SubscribeEvents(100);
            var rider = Assert.IsType<JoltCharacter>(pa);
            for (int i = 0; i < 4; i++)
                scene.Simulate(Heartbeat);

            bool commanded = false, crossed = false, kept = false;
            bool waitBranch = deck.WaitsBeforeCrossingBack;
            if (waitBranch)
            {
                deck.KeepInsideBorder(true);                     // Scene.IncomingCreateObject, at the arrival
                kept = true;
            }
            int beats = (int)Math.Ceiling(seconds / Heartbeat);
            for (int k = 0; k < beats && !crossed; k++)
            {
                double t = k * (double)Heartbeat;
                string ev = "";
                if (!commanded && t >= CommandAt)
                {
                    deck.PIDTarget = new Vector3(-20f, 128f, DeckZ);
                    deck.PIDTau = 0.5f;
                    deck.PIDActive = true;
                    commanded = true;
                    ev = "command";
                }
                if (kept && t >= WaitSeconds)
                {
                    deck.KeepInsideBorder(false);                // Scene.ReleaseExpiredCrossingHolds, when the wait is over
                    kept = false;
                    ev += (ev.Length > 0 ? "+" : "") + "wait-over";
                }
                scene.Simulate(Heartbeat);

                if (deck.HasLeftRegion)
                {
                    if (waitBranch && t < WaitSeconds)
                    {
                        // CrossAsyncCore, hold branch: CrossingStart (the body is out already), then CrossAsyncCompleted
                        deck.CrossingStart();
                        deck.Position = new Vector3(Math.Clamp(deck.Position.X, 0.5f, Size - 0.5f), Math.Clamp(deck.Position.Y, 0.5f, Size - 0.5f), deck.Position.Z);
                        deck.Velocity = Vector3.Zero;                // SceneObjectPart.Stop
                        deck.CrossingFailure();
                        o.PutBack++;
                        ev += (ev.Length > 0 ? "+" : "") + "put-back";
                    }
                    else
                    {
                        // the crossing goes ahead: the avatars that stood on the deck at its last report are frozen and taken out
                        // of the engine, then the deck. The step that took the deck across the border is not that report.
                        o.RiderOnDeckAtCrossing = o.Rows.Count > 0 && o.Rows[^1].OnDeck;
                        o.CrossedAt = t;
                        crossed = true;
                        ev += (ev.Length > 0 ? "+" : "") + "crossed";
                    }
                }

                bool on = IsOn(deck.Position, rider.Position);
                if (!on && double.IsNaN(o.LeftAt) && !crossed)
                    o.LeftAt = t;
                if (on == false && double.IsNaN(o.LeftAt) == false && ev.Length == 0)
                    ev = "off-deck";
                o.MaxRiderSpeed = Math.Max(o.MaxRiderSpeed, rider.Velocity.Length());
                o.MinRiderZ = Math.Min(o.MinRiderZ, rider.Position.Z);
                o.MaxDeckX = Math.Max(o.MaxDeckX, deck.Position.X);
                o.MinDeckX = Math.Min(o.MinDeckX, deck.Position.X);
                o.Rows.Add(new Row(t, deck.Position, deck.Velocity, deck.HasLeftRegion, rider.Position, rider.Velocity, on, ev));
            }
            return o;
        }
        finally { scene.Dispose(); }
    }

    private void Report(string label, Outcome o)
    {
        var sb = new StringBuilder();
        sb.AppendLine("t,deck_x,deck_y,deck_z,deck_vx,deck_vy,deck_vz,deck_away,rider_x,rider_y,rider_z,rider_vx,rider_vy,rider_vz,on_deck,event");
        foreach (Row r in o.Rows)
        {
            string line = $"WAIT {label} t {F(r.T)} deck {F(r.Deck.X)},{F(r.Deck.Z)} v {F(r.DeckVel.X)},{F(r.DeckVel.Z)} away {r.DeckAway} rider {F(r.Rider.X)},{F(r.Rider.Z)} v {F(r.RiderVel.X)},{F(r.RiderVel.Z)} on {r.OnDeck} {r.Event}";
            Say(line);
            sb.Append(F(r.T)).Append(',').Append(F(r.Deck.X)).Append(',').Append(F(r.Deck.Y)).Append(',').Append(F(r.Deck.Z)).Append(',')
              .Append(F(r.DeckVel.X)).Append(',').Append(F(r.DeckVel.Y)).Append(',').Append(F(r.DeckVel.Z)).Append(',').Append(r.DeckAway).Append(',')
              .Append(F(r.Rider.X)).Append(',').Append(F(r.Rider.Y)).Append(',').Append(F(r.Rider.Z)).Append(',')
              .Append(F(r.RiderVel.X)).Append(',').Append(F(r.RiderVel.Y)).Append(',').Append(F(r.RiderVel.Z)).Append(',').Append(r.OnDeck).Append(',').AppendLine(r.Event);
        }
        Say($"WAIT {label}: rider left the deck at {(double.IsNaN(o.LeftAt) ? "never" : F(o.LeftAt) + " s")}, deck put back {o.PutBack} times, crossed at {(double.IsNaN(o.CrossedAt) ? "never" : F(o.CrossedAt) + " s")}, rider on deck at the crossing {o.RiderOnDeckAtCrossing}, deck x from {F(o.MinDeckX)} to {F(o.MaxDeckX)}, fastest rider {F(o.MaxRiderSpeed)} m/s, lowest rider z {F(o.MinRiderZ)}");
        string dir = Environment.GetEnvironmentVariable("JOLT_WAIT_RECORD_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"wait-{label}.csv"), sb.ToString());
        }
    }

    // Where the arrival put the deck (x) and the rider against its middle: at the border's edge of the deck, in the middle, ahead.
    public static IEnumerable<object[]> InWorldzCases()
    {
        foreach (float rate in new[] { 0f, 45f })
            foreach ((float deckX, float ahead) in new[] { (0.3f, -1.5f), (0.3f, 0f), (0.3f, 1.8f), (1.5f, -1.5f), (1.5f, 0.8f), (1.5f, 1.8f) })
                yield return new object[] { rate, deckX, ahead };
    }

    [Theory]
    [MemberData(nameof(InWorldzCases))]
    public void A_rider_stays_on_a_deck_that_is_told_to_go_back_during_the_wait_and_when_it_crosses_after_it(float physicsRate, float deckX, float ahead)
    {
        Outcome o = Play("InWorldz", physicsRate, 6.0, deckX, ahead);
        Report($"A-{(physicsRate == 0f ? "11hz" : "45hz")}-x{deckX}-a{ahead}", o);

        Assert.True(double.IsNaN(o.LeftAt), $"the rider left the deck at {F(o.LeftAt)} s (lowest z {F(o.MinRiderZ)})");
        Assert.True(o.PutBack == 0, $"core had to put the deck back {o.PutBack} times: its body left the engine at the border");
        Assert.False(double.IsNaN(o.CrossedAt), "the deck never crossed after the wait");
        Assert.True(o.CrossedAt >= WaitSeconds - 1e-9, $"the deck crossed out at {F(o.CrossedAt)} s, inside the wait");
        Assert.True(o.CrossedAt <= WaitSeconds + 1.0, $"the deck crossed out only at {F(o.CrossedAt)} s");
        Assert.True(o.RiderOnDeckAtCrossing, "the rider was not on the deck when it crossed");
        Assert.True(o.MinDeckX >= 0.5f - 0.01f || o.CrossedAt - WaitSeconds < 1.0, "the deck was out of the region during the wait");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void The_wait_keeps_the_deck_inside_the_region_for_three_seconds(float physicsRate)
    {
        Outcome o = Play("InWorldz", physicsRate, 6.0, 1.5f, 0.8f);
        double minXInWait = o.Rows.Where(r => r.T < WaitSeconds - 0.2).Min(r => (double)r.Deck.X);
        Assert.Equal(0, o.PutBack);
        Assert.True(minXInWait >= 0.5 - 0.01, $"the deck was at x {F(minXInWait)} during the wait");
        Assert.True(o.Rows.Where(r => r.T < WaitSeconds - 0.2).All(r => !r.DeckAway), "the deck's body left the engine during the wait");
    }

    [Theory]
    [InlineData(0f, 1.5f, 0.8f)]
    [InlineData(45f, 1.5f, 0.8f)]
    [InlineData(0f, 0.3f, -1.5f)]
    [InlineData(45f, 0.3f, -1.5f)]
    public void Under_SecondLife_the_deck_crosses_at_once_with_its_rider(float physicsRate, float deckX, float ahead)
    {
        Outcome o = Play("SecondLife", physicsRate, 6.0, deckX, ahead);
        Report($"S-{(physicsRate == 0f ? "11hz" : "45hz")}-x{deckX}-a{ahead}", o);

        Assert.Equal(0, o.PutBack);
        Assert.False(double.IsNaN(o.CrossedAt), "the deck never crossed");
        Assert.True(o.CrossedAt < 1.5, $"the deck crossed at {F(o.CrossedAt)} s");
        Assert.True(o.RiderOnDeckAtCrossing, "the rider was not on the deck when it crossed");
        Assert.True(double.IsNaN(o.LeftAt), $"the rider left the deck at {F(o.LeftAt)} s");
    }

    [Theory]
    [InlineData("InWorldz", 0f)]
    [InlineData("InWorldz", 45f)]
    [InlineData("SecondLife", 0f)]
    [InlineData("SecondLife", 45f)]
    public void Record_the_deck_and_the_rider_through_the_wait(string basicForces, float physicsRate)
    {
        Outcome o = Play(basicForces, physicsRate, 4.0);
        Report($"{basicForces}-{(physicsRate == 0f ? "11hz" : "45hz")}", o);
    }
}
