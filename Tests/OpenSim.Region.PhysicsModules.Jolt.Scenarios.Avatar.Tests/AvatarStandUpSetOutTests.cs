/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An avatar standing up where ScenePresence.StandUp puts it, with no check for what is there: beside a fixed 2 m cube,
/// just above its top, at its middle, at the middle of a light (1 kg) or a heavy (100 kg) physical 1.2 m cube on the
/// ground, and just above a physical 2 m x 2 m board (the cases of FlyingSeatedAndFastLinksetAvatarTests). Jolt's
/// character sets an avatar out of what it overlaps all at once. Here, per physics step through the backend's
/// CharacterStepped trace:
/// - the avatar is set out in the update it was put there, also out of an object the simulation moves;
/// - it is left clear of what it was set out of: that object is not what it stands on afterwards, and nothing moves it
///   across once it is out;
/// - it never moves up, nor reports moving up, faster than 0.1 m/s;
/// - the update that set it out reports no speed, and every later update reports the speed it moved at.
/// Each case runs at an 11 Hz heartbeat with one physics step per heartbeat and with [Jolt] PhysicsStepRate 45. Each
/// test writes every avatar update, with where the avatar and the object are, to the test output.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarStandUpSetOutTests
{
    private const float X = 170f, Y = 60f;
    private const double At = 1.0;
    private const float Gravity = 9.80665f;
    private const float Radius = 0.3f;            // about the avatar's capsule radius, as the stand-up test allows
    private const float MostUp = 0.1f;            // m/s

    private readonly ITestOutputHelper _out;

    public AvatarStandUpSetOutTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> StandCases()
    {
        foreach (double hz in new[] { 45.0, 0.0 })
            foreach (string where in new[] { "next", "top", "inside", "inside-light", "inside-heavy", "on-physical" })
                yield return new object[] { hz, where };
    }

    public static IEnumerable<object[]> InsideCases()
    {
        foreach (double hz in new[] { 45.0, 0.0 })
            foreach (string where in new[] { "inside", "inside-light", "inside-heavy" })
                yield return new object[] { hz, where };
    }

    // One avatar update: the backend's trace, the avatar's state after it, whether what it stands on is the object, and
    // the object's state (as it is when the avatar's update ends, before the simulation's update).
    private readonly record struct Update(CharacterStepTrace Step, CharacterState Avatar, bool OnObject, BodyState Object);

    private sealed record StandUp(string Name, List<Update> Updates, Vector3 Placed, Vector3 Size);

    private static float Level(SVector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static string V(SVector3 v) => $"({v.X:0.0000}, {v.Y:0.0000}, {v.Z:0.0000})";

    private StandUp Stand(double physicsHz, string where)
    {
        Vector3 size = where.StartsWith("inside-") ? new Vector3(1.2f, 1.2f, 1.2f) : where == "on-physical" ? new Vector3(2f, 2f, 0.5f) : new Vector3(2f, 2f, 2f);
        float kg = where switch { "inside-light" => 1f, "inside-heavy" => 100f, "on-physical" => 50f, _ => 0f };
        PhysicsActor thing = null, avatar = null;
        JoltPhysicsBackend backend = null;
        Vector3 placed = Vector3.Zero;
        var updates = new List<Update>();
        var sc = new Scenario
        {
            Name = "stand-up-" + where,
            DefaultDuration = _ => 3f,
            Setup = r =>
            {
                backend = (JoltPhysicsBackend)((JoltScene)r.PhysicsScene).Backend;
                float ground = r.GroundAt(X, Y);
                thing = r.AddPart(PrimitiveBaseShape.CreateBox(), size, new Vector3(X, Y, ground + size.Z * 0.5f + 0.01f), Quaternion.Identity, kg > 0f, 1500);
                if (kg > 0f)
                    thing.Density = kg / (size.X * size.Y * size.Z) / 0.01f;
                r.Actor = thing;   // until the avatar arrives
                // On the step thread, which in the harness is the test's own (Simulate runs the step inline).
                backend.CharacterStepped += t =>
                {
                    backend.TryGetCharacterState(((JoltCharacter)avatar).CharacterHandle, out CharacterState cs);
                    BodyId body = ((JoltPrim)thing).BodyHandle;
                    backend.TryGetBodyState(body, out BodyState bs);
                    updates.Add(new Update(t, cs, cs.GroundBody.Value == body.Value, bs));
                };
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    float ground = r.GroundAt(X, Y), half = r.AvatarStandHalf, top = ground + size.Z + 0.01f;
                    placed = where switch
                    {
                        "next" => new Vector3(X - size.X * 0.5f - 0.3f, Y, ground + half + 0.01f),
                        "top" => new Vector3(X, Y, top + half + 0.1f),
                        "on-physical" => new Vector3(X + 0.65f, Y, top + half + 0.1f),
                        _ => new Vector3(X, Y, ground + MathF.Min(1f, half + 0.01f)),
                    };
                    avatar = r.AddAvatarAt(placed, false);
                    r.Stage = 1;
                }
            },
        };
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        var run = new StandUp(res.Name, updates, placed, size);
        for (int i = 0; i < updates.Count && i < 40; i++)
        {
            Update u = updates[i];
            _out.WriteLine($"{res.Name} update {i}: dt {u.Step.Seconds:0.0000} s, placed {u.Step.Placed}, at {V(u.Avatar.Position)}, moved {V(u.Step.Displacement)}, " +
                           $"asked {V(u.Step.Asked)}, reported {V(u.Step.Reported)}, supported {u.Avatar.IsSupported}, sliding {u.Avatar.IsSliding}, " +
                           $"on {(u.OnObject ? "the object" : u.Avatar.GroundIsTerrain ? "terrain" : u.Avatar.GroundBody.IsValid ? "another body" : "nothing")}, " +
                           $"ground normal {V(u.Avatar.GroundNormal)}; object at {V(u.Object.Position)}, " +
                           $"turned ({u.Object.Orientation.X:0.0000}, {u.Object.Orientation.Y:0.0000}, {u.Object.Orientation.Z:0.0000}, {u.Object.Orientation.W:0.0000}), " +
                           $"velocity {V(u.Object.LinearVelocity)}, spin {V(u.Object.AngularVelocity)}");
        }
        Assert.NotEmpty(updates);
        return run;
    }

    // An update that set the avatar out: the first after the caller put it where it is, in which it moved further than
    // its own velocity would take it (AvatarReportedSpeedTests.SetOut).
    private static bool SetOut(CharacterStepTrace t) => t.Placed && t.Displacement.Length() > t.Asked.Length() * t.Seconds + 0.01f;

    [Theory]
    [MemberData(nameof(StandCases))]
    public void After_standing_up_the_avatar_never_moves_up_faster_than_a_tenth_of_a_metre_a_second(double physicsHz, string where)
    {
        StandUp s = Stand(physicsHz, where);
        for (int i = 0; i < s.Updates.Count; i++)
        {
            CharacterStepTrace t = s.Updates[i].Step;
            float up = t.Displacement.Z / t.Seconds;
            Assert.True(up < MostUp, $"{s.Name}: update {i} moved the avatar up at {up:0.000} m/s");
            Assert.True(t.Reported.Z < MostUp, $"{s.Name}: update {i} reported {t.Reported.Z:0.000} m/s up");
        }
    }

    [Theory]
    [MemberData(nameof(InsideCases))]
    public void An_avatar_standing_up_inside_an_object_is_set_out_in_that_update_and_left_clear_of_it(double physicsHz, string where)
    {
        StandUp s = Stand(physicsHz, where);
        Update first = s.Updates[0];
        Assert.True(first.Step.Placed, $"{s.Name}: the first update is not marked as placed");
        float outFrom = MathF.Max(MathF.Abs(first.Avatar.Position.X - first.Object.Position.X), MathF.Abs(first.Avatar.Position.Y - first.Object.Position.Y));
        Assert.True(outFrom >= s.Size.X * 0.5f + Radius - 0.1f, $"{s.Name}: the update it was put there left it {outFrom:0.000} m from the object's middle");
        for (int i = 1; i < s.Updates.Count; i++)
        {
            Update u = s.Updates[i];
            Assert.False(u.OnObject, $"{s.Name}: after update {i} the avatar stands on the object it was set out of");
            Assert.True(Level(u.Step.Displacement) < 0.01f, $"{s.Name}: update {i} moved the avatar {Level(u.Step.Displacement):0.000} m across");
        }
    }

    [Theory]
    [MemberData(nameof(StandCases))]
    public void After_standing_up_each_update_reports_the_speed_the_avatar_moved_at(double physicsHz, string where)
    {
        StandUp s = Stand(physicsHz, where);
        for (int i = 0; i < s.Updates.Count; i++)
        {
            CharacterStepTrace t = s.Updates[i].Step;
            if (SetOut(t))
            {
                Assert.True(t.Reported.Length() == 0f, $"{s.Name}: update {i} set the avatar out {t.Displacement.Length():0.000} m and reported {t.Reported.Length():0.00} m/s");
                continue;
            }
            SVector3 moved = t.Displacement / t.Seconds;
            Assert.True(MathF.Abs(Level(t.Reported) - Level(moved)) <= Level(moved) * 0.05f + 1e-3f,
                $"{s.Name}: update {i} reported {Level(t.Reported):0.000} m/s across, moved {Level(moved):0.000}");
            // Up and down it also carries the half step of gravity it gains after the move while in the air.
            float extra = t.Reported.Z - moved.Z;
            Assert.True(extra <= MathF.Abs(moved.Z) * 0.05f + 1e-3f && extra >= -Gravity * t.Seconds * 0.5f - MathF.Abs(moved.Z) * 0.05f - 1e-3f,
                $"{s.Name}: update {i} reported {t.Reported.Z:0.000} m/s up, moved {moved.Z:0.000}");
        }
    }
}
