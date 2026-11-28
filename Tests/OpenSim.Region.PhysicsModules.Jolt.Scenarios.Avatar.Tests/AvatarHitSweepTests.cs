/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A box thrown level at 10 m/s at a standing avatar, at several avatar heights and box masses, at an 11 Hz heartbeat with
/// [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. The two meet with no bounce, so the avatar goes no
/// faster than their common speed, kg x 10 / (avatar mass + kg), however the box then falls and slides along it (the box
/// falls while it is held against the avatar, and for some heights lands with its side against the avatar's rounded
/// foot: a contact whose normal is not level, and where the avatar is moving away from the box). Core plays collision sounds
/// and works out impact damage from ContactPoint.RelativeSpeed, which is the speed of the strike: 10 m/s.
/// The default push limit ([Jolt] AvatarPushMaxSpeed 100) is in force. Serial with the other native tests: every run steps a
/// real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarHitSweepTests
{
    private const float Speed = 10f;

    public static IEnumerable<object[]> Cases()
    {
        foreach (double rate in new[] { 45.0, 0.0 })
            foreach (float height in new[] { 1.9f, 2.0f, 2.1f, 2.3f })
                foreach (float kg in new[] { 0.5f, 1f, 10f })
                    yield return new object[] { rate, height, kg };
    }

    private static RunResult Throw(double physicsRate, float height, float kg)
        => Harness.Harness.Run(AvatarHitScenarios.Thrown($"avatar-hit-{kg}kg-{height}m", kg, Speed),
            new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, AvatarHeight = height });

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_box_thrown_at_a_standing_avatar_moves_it_no_faster_than_their_momentum_allows(double physicsRate, float height, float kg)
    {
        RunResult r = Throw(physicsRate, height, kg);
        float mass = JoltCharacter.MassFor(new Vector3(0.45f, 0.6f, height));
        float allowed = kg * Speed / (mass + kg);
        float peak = r.Samples.Where(s => s.T > AvatarHitScenarios.At).Max(s => s.HorizontalSpeed);
        Assert.True(peak <= allowed * 1.1f, $"{r.Name}: the avatar went at up to {peak:0.00} m/s; their common speed is {allowed:0.00}");
        Assert.True(peak >= allowed * 0.5f, $"{r.Name}: the avatar was hardly moved ({peak:0.00} m/s; their common speed is {allowed:0.00})");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_box_thrown_at_a_standing_avatar_tells_core_the_speed_it_closed_at(double physicsRate, float height, float kg)
    {
        RunResult r = Throw(physicsRate, height, kg);
        HarnessPart box = r.Parts.Single(p => p.Name == "box");
        CollisionWatch avatar = r.Watches.Single(w => w.Name == "avatar");
        Assert.InRange(-box.Watch.Strikes[Harness.Run.ActorLocalId], Speed * 0.9f, Speed * 1.1f);
        Assert.InRange(-avatar.Strikes[box.LocalId], Speed * 0.9f, Speed * 1.1f);
    }
}
