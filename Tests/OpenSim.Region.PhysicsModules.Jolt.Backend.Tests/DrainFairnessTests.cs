/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The step drain is fair and loses nothing: settle (JustDeactivated) states
/// go first and carry over when they do not fit, active bodies and characters rotate through a too-small buffer
/// instead of starving the same tail every frame, and the contact impulse estimate is only paid for when someone
/// is listening.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class DrainFairnessTests
{
    private readonly ITestOutputHelper _out;
    public DrainFairnessTests(ITestOutputHelper output) { _out = output; }

    private const float Dt = 1f / 11f;

    [Fact]
    public void Hundred_falling_spheres_all_appear_within_four_steps_of_a_30_entry_buffer()
    {
        using var t = new JoltTestBackend();
        var sphere = t.B.CreateSphereShape(0.25f);
        for (var i = 0; i < 100; i++)   // 1 m apart, high up: no contacts, all stay awake and falling
            t.Dynamic(sphere, new Vector3(20f + (i % 10) * 2f, 20f + (i / 10) * 2f, 500f), (uint)(1 + i));

        var bodies = new BodyState[30];
        var chars = new CharacterState[4];
        var contacts = new ContactReport[256];
        var seen = new HashSet<uint>();
        var stepsToCover = 0;
        for (var step = 1; step <= 10 && seen.Count < 100; step++)
        {
            var r = t.B.Step(Dt, bodies, chars, contacts);
            Assert.Equal(100, r.ActiveBodyCount);
            Assert.Equal(30, r.BodyUpdateCount);
            for (var i = 0; i < r.BodyUpdateCount; i++)
                seen.Add(bodies[i].UserData);
            stepsToCover = step;
        }
        _out.WriteLine($"steps to cover 100 active bodies with a 30-entry buffer: {stepsToCover} (seen {seen.Count})");
        Assert.Equal(100, seen.Count);
        Assert.True(stepsToCover <= 4, $"took {stepsToCover} steps");
    }

    [Fact]
    public void Each_settle_state_is_emitted_exactly_once_through_a_tiny_buffer()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        var box = t.B.CreateBoxShape(new Vector3(0.4f));
        const int n = 10;
        for (var i = 0; i < n; i++)   // resting on the ground, 3 m apart: they settle and sleep, never touch
            t.Dynamic(box, new Vector3(100f + i * 3f, 128f, 0.4f), (uint)(1 + i));

        var bodies = new BodyState[2];
        var chars = new CharacterState[4];
        var contacts = new ContactReport[1024];
        var settled = new Dictionary<uint, int>();
        var quietSteps = 0;
        for (var step = 0; step < 400 && quietSteps < 20; step++)
        {
            var r = t.B.Step(Dt, bodies, chars, contacts);
            for (var i = 0; i < r.BodyUpdateCount; i++)
                if ((bodies[i].Flags & BodyStateFlags.JustDeactivated) != 0)
                    settled[bodies[i].UserData] = settled.GetValueOrDefault(bodies[i].UserData) + 1;
            quietSteps = r.ActiveBodyCount == 0 && r.BodyUpdateCount == 0 ? quietSteps + 1 : 0;
        }
        _out.WriteLine("settle states: " + string.Join(", ", settled.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")));
        for (uint id = 1; id <= n; id++)
            Assert.Equal(1, settled.GetValueOrDefault(id));
    }

    [Fact]
    public void Characters_over_the_buffer_are_emitted_round_robin()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        const int n = 5;
        for (var i = 0; i < n; i++)
        {
            var cd = CharacterDesc.Default;
            cd.Position = new Vector3(100f + i * 3f, 100f, 0.8f);
            cd.UserData = (uint)(500 + i);
            t.B.CreateCharacter(cd);
        }

        var bodies = new BodyState[64];
        var chars = new CharacterState[2];
        var contacts = new ContactReport[1024];
        var seen = new HashSet<uint>();
        for (var step = 0; step < 3; step++)   // ceil(5 / 2) = 3
        {
            var r = t.B.Step(Dt, bodies, chars, contacts);
            Assert.Equal(2, r.CharacterUpdateCount);
            for (var i = 0; i < r.CharacterUpdateCount; i++)
                seen.Add(chars[i].UserData);
        }
        Assert.Equal(n, seen.Count);
    }

    // ------------------------------------------------------------------ impulse only when someone listens

    private static float FirstBeginImpulse(bool groundSubscribed, bool boxSubscribed)
    {
        using var t = new JoltTestBackend();
        var groundShape = t.B.CreateBoxShape(new Vector3(64f, 64f, 1f));
        var g = BodyDesc.Default;
        g.Shape = groundShape;
        g.Position = new Vector3(128f, 128f, -1f);
        g.UserData = 7;
        g.WantsContactEvents = groundSubscribed;
        t.B.CreateBody(g);

        var box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(128f, 128f, 1.5f), 42, boxSubscribed);
        t.B.SetBodyLinearVelocity(box, new Vector3(0f, 0f, -6f));

        var bodies = new BodyState[16];
        var chars = new CharacterState[4];
        var contacts = new ContactReport[256];
        for (var step = 0; step < 30; step++)
        {
            var r = t.B.Step(Dt, bodies, chars, contacts);
            for (var i = 0; i < r.ContactCount; i++)
                if (contacts[i].Phase == ContactPhase.Begin && (contacts[i].UserDataA == 42 || contacts[i].UserDataB == 42))
                    return contacts[i].Impulse;
        }
        Assert.Fail("no Begin contact between the box and the ground");
        return float.NaN;
    }

    [Fact]
    public void Begin_impulse_is_zero_between_unsubscribed_bodies()
        => Assert.Equal(0f, FirstBeginImpulse(groundSubscribed: false, boxSubscribed: false));

    [Fact]
    public void Begin_impulse_is_estimated_when_one_side_is_subscribed()
    {
        Assert.True(FirstBeginImpulse(groundSubscribed: false, boxSubscribed: true) > 0f);
        Assert.True(FirstBeginImpulse(groundSubscribed: true, boxSubscribed: false) > 0f);
    }
}
