/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How far a fast physical linkset moves a standing avatar, at the step rate a region runs (11 Hz heartbeat, 45 Hz
/// physics, the simulator's own prim and avatar values), against the figure worked out from the masses and the speed.
///
/// The worked answer. A loose object meets an avatar as two bodies that go on together at their common speed
/// (ubODE's avatar contacts do not bounce), the avatar's share held to [Jolt] AvatarPushMaxSpeed (10 m/s):
///   push = min(m u / (m + M), cap), with m the linkset's mass, u its speed and M the avatar's 80.
/// The push then fades on the ground with a 0.25 s time constant (JoltPhysicsBackend.PushFadeOnGroundSeconds), so one
/// push carries the avatar push x 0.25 m. The linkset, still behind the avatar, strikes it again for a few steps as the
/// avatar slows, which adds a little (measured up to 15 % at pushes under the cap); at the cap the carry falls well
/// short of cap x 0.25 (measured 0.47 to 0.70 of it), as the linkset has lost most of its speed to the first strike.
///   100 kg at 19.9 m/s: common speed 11.06, held to 10 m/s: 2.5 m at most, measured 1.17 to 1.86 m.
///   1 kg at 19.9 m/s: common speed 0.246 m/s: 0.06 m, measured 0.063 to 0.065 m.
/// The mass is the one a script reads from llGetObjectMass: a part's density is SceneObjectPart.Density, and
/// Density x 0.01 is kg/m3, so a three-cube linkset of 0.6 m cubes weighs 1 with a density of 154.32 and 100 with 15432.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class LinksetHitOnStandingAvatarTests
{
    private const float Side = 0.6f;
    private static readonly float AvatarMass = JoltCharacter.MassFor(Harness.Run.SimulatorAvatarSize);   // what JoltCharacter reports: mass from shape and size
    private const float PushCap = 10f;   // [Jolt] AvatarPushMaxSpeed, set to 10 in these runs: the cap rule is what they check (the default is 100)
    private const float PushFade = 0.25f;      // JoltPhysicsBackend.PushFadeOnGroundSeconds
    private const uint RootId = 1501;
    private const float X = 170f, Y = 60f;
    private const double At = 1.0;

    private readonly ITestOutputHelper _out;

    public LinksetHitOnStandingAvatarTests(ITestOutputHelper output) => _out = output;

    private readonly record struct Hit(
        Vector3 AvatarStart, Vector3 AvatarEnd, Vector3 LinksetEnd, Vector3 LinksetVelocity, float LinksetMass,
        float FrontMinusAvatarX, float LinksetEndSpeed)
    {
        /// <summary>How far the avatar went along the throw (west), m.</summary>
        public float Carried => AvatarStart.X - AvatarEnd.X;
        public float Sideways => MathF.Abs(AvatarEnd.Y - AvatarStart.Y);
    }

    private static float Worked(float kg, float speed) => MathF.Min(kg * speed / (kg + AvatarMass), PushCap);

    // A physical linkset of three 0.6 m cubes in a row along x, its root in the middle, thrown west at `speed` from 3 m
    // away (its front part 3 m from the avatar's middle) at an avatar standing on level ground, `offsetY` m off its middle.
    // `density` is SceneObjectPart.Density. `floats` is a buoyancy of 1, as a script that throws it with llSetBuoyancy(1)
    // has; off, the linkset falls as it flies.
    private Hit Throw(float density, float speed, float offsetY, bool floats)
    {
        PhysicsActor[] parts = null;
        Vector3 start = default;
        float frontMinus = float.MaxValue, mass = 0f;
        var sc = new Scenario
        {
            Name = "linkset-hit",
            DefaultDuration = _ => 4f,
            Setup = r => r.AddAvatar(X, Y),
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= At - 1e-9)
                {
                    start = r.Actor.Position;
                    var size = new Vector3(Side, Side, Side);
                    Vector3 mid = new Vector3(start.X + 3f + Side * 1.5f, start.Y + offsetY, start.Z);
                    PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid, Quaternion.Identity, true, RootId, null, density);
                    PhysicsActor west = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid - new Vector3(Side, 0f, 0f), Quaternion.Identity, true, RootId + 1, root, density);
                    PhysicsActor east = r.AddPart(PrimitiveBaseShape.CreateBox(), size, mid + new Vector3(Side, 0f, 0f), Quaternion.Identity, true, RootId + 2, root, density);
                    parts = new[] { root, west, east };
                    if (floats)
                        root.Buoyancy = 1f;
                    root.Velocity = new Vector3(-speed, 0f, 0f);
                    r.Stage = 1;
                }
                if (parts != null && mass == 0f)
                    mass = parts.Sum(p => p.Mass);   // read while the bodies exist: once the run ends they are gone
                // Never through: while level with the avatar, the front part's centre stays east of the avatar's.
                if (parts != null && MathF.Abs(parts[1].Position.Z - r.Actor.Position.Z) < 1f
                    && MathF.Abs(parts[1].Position.Y - r.Actor.Position.Y) < Side)
                    frontMinus = MathF.Min(frontMinus, parts[1].Position.X - r.Actor.Position.X);
            },
        };
        RunResult res = Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0, SimulatorDefaults = true, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });
        Vector3 end = res.Samples[^1].Position;
        var hit = new Hit(start, end, parts[0].Position, parts[0].Velocity, mass, frontMinus, parts[0].Velocity.Length());
        _out.WriteLine($"density {density} speed {speed} offset {offsetY} floats {floats}: mass {hit.LinksetMass:0.000} kg, avatar carried {hit.Carried:0.000} m "
            + $"(sideways {hit.Sideways:0.000}), linkset ends ({hit.LinksetEnd.X:0.00}, {hit.LinksetEnd.Y:0.00}) at {hit.LinksetEndSpeed:0.00} m/s, "
            + $"front part never nearer than {hit.FrontMinusAvatarX:0.000} m east of the avatar's middle; worked push {Worked(hit.LinksetMass, speed):0.000} m/s");
        return hit;
    }

    // ------------------------------------------------------------------ the mass a script reads

    [Theory]
    [InlineData(154.32f, 1f)]
    [InlineData(15432f, 100f)]
    public void A_linksets_mass_is_its_density_times_point_zero_one_times_its_volume(float density, float kg)
    {
        Hit hit = Throw(density, 0f, 0f, floats: true);
        Assert.InRange(hit.LinksetMass, kg * 0.999f, kg * 1.001f);
    }

    // ------------------------------------------------------------------ a hundred kilograms at the middle

    [Theory]
    [InlineData(19.9f, true)]
    [InlineData(19.9f, false)]
    [InlineData(20f, false)]
    public void A_100_kg_linkset_at_20_ms_carries_a_standing_avatar_about_a_metre_and_a_half_and_does_not_pass_through(float speed, bool floats)
    {
        Hit hit = Throw(15432f, speed, 0f, floats);
        float push = Worked(hit.LinksetMass, speed);
        Assert.Equal(PushCap, push);   // 100 kg x 19.9 / 180 = 11.06 m/s: the cap holds it to 10
        // At most one push's fade (push x 0.25 s = 2.5 m), and not under 0.4 of it: the linkset behind the avatar keeps
        // striking it, which the single-push figure leaves out. Measured at this rate: the harness 1.17 to 1.86 m over speeds
        // of 19 to 21 m/s (the carry changes with where in a physics step the linkset first touches); a region 1.363 m twice.
        Assert.InRange(hit.Carried, 0.4f * push * PushFade, push * PushFade);
        Assert.True(hit.Sideways < 0.01f, $"the avatar went {hit.Sideways:0.000} m off the throw's line");
        Assert.True(hit.FrontMinusAvatarX > 0f, $"the linkset went through the avatar (front part {hit.FrontMinusAvatarX:0.000} m east of its middle)");
        // The avatar is light now, so it slows the linkset less: measured 0.45 m/s as the run ends, the linkset still sliding to rest.
        Assert.True(hit.LinksetEndSpeed < 1f, $"the linkset is still moving at {hit.LinksetEndSpeed:0.000} m/s");
    }

    // ------------------------------------------------------------------ one kilogram at the middle

    [Theory]
    [InlineData(19.9f, true)]
    [InlineData(19.9f, false)]
    public void A_1_kg_linkset_at_20_ms_stops_at_a_standing_avatar_and_moves_it_a_few_centimetres(float speed, bool floats)
    {
        Hit hit = Throw(154.32f, speed, 0f, floats);
        float push = Worked(hit.LinksetMass, speed);
        float expected = 1f * speed / (1f + AvatarMass);   // 1 x 19.9 / (1 + the avatar's mass)
        Assert.InRange(push, expected * 0.99f, expected * 1.01f);
        // About one push's fade (not over 1.2 of it, not under 0.3). Measured with the avatar's shape-and-size mass: 1.26 to
        // 1.30 m, 0.75 to 0.78 of the fade (6.73 x 0.25 = 1.68 m).
        Assert.InRange(hit.Carried, 0.3f * push * PushFade, 1.2f * push * PushFade);
        Assert.True(hit.Sideways < 0.01f, $"the avatar went {hit.Sideways:0.000} m off the throw's line");
        Assert.True(hit.FrontMinusAvatarX > 0f, $"the linkset went through the avatar (front part {hit.FrontMinusAvatarX:0.000} m east of its middle)");
        Assert.True(hit.LinksetEndSpeed < 0.2f, $"the linkset is still moving at {hit.LinksetEndSpeed:0.000} m/s");   // measured 0.00 and 0.09
    }

    // ------------------------------------------------------------------ a slow linkset

    [Theory]
    [InlineData(15432f, 3f)]
    [InlineData(15432f, 8f)]
    [InlineData(154.32f, 8f)]
    public void A_slower_linkset_moves_a_standing_avatar_about_as_far_as_its_common_speed_carries_it(float density, float speed)
    {
        bool kg100AndSlow = density > 10000f && speed < 5f;
        Hit hit = Throw(density, speed, 0f, floats: true);
        float push = Worked(hit.LinksetMass, speed);
        // Measured 0.85 to 1.15 of one push's fade (push x 0.25 m) for the 8 m/s throws. A 100 kg linkset at 3 m/s keeps
        // striking the light avatar as it slows, so it carries it with the linkset (measured 2.10 m, 2.9 of the fade).
        float most = kg100AndSlow ? 3.2f : 1.25f;
        // The linkset's wood (0.5) against the avatar's body (no restitution set, so 0) now averages to 0.25 where the product
        // was 0, so a heavy linkset sends the avatar off at (1 + 0.25) x its own speed instead of 1 x: the carry is 1.25 times
        // as far. Measured 1.31 times for the 8 m/s throw (2.57 m against 1.96 m).
        most *= 1.25f;
        Assert.InRange(hit.Carried, 0.3f * push * PushFade, most * push * PushFade);
        Assert.True(hit.FrontMinusAvatarX > 0f, $"the linkset went through the avatar (front part {hit.FrontMinusAvatarX:0.000} m east of its middle)");
    }

    // ------------------------------------------------------------------ a glancing blow

    [Fact]
    public void A_1_kg_linkset_that_only_grazes_a_standing_avatar_is_turned_and_barely_moves_it()
    {
        // 0.51 m off the avatar's middle: the cube's edge meets the capsule's side (the capsule's radius is a quarter
        // of a metre, a cube is 0.3 m either side of its middle). The linkset carries on, turned; the avatar is nudged.
        Hit hit = Throw(154.32f, 19.9f, 0.51f, floats: true);
        Assert.True(hit.Carried < 0.35f, $"carried {hit.Carried:0.000} m by a graze");   // measured 0.229 with the light avatar (0.1 limit at 80 kg)
        Assert.True(hit.LinksetEndSpeed > 10f, $"the linkset left at {hit.LinksetEndSpeed:0.00} m/s");
        Assert.True(MathF.Abs(hit.LinksetVelocity.Y) > 1f, $"the linkset was not turned (leaves at {hit.LinksetVelocity.Y:0.00} m/s across the throw)");
    }
}
