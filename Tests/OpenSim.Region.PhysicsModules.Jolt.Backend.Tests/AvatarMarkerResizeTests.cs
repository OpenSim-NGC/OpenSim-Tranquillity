/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An avatar's query marker follows a resize of the avatar's capsule. Serial: it steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarMarkerResizeTests
{
    [Fact]
    public void A_resized_avatars_query_marker_has_the_new_capsule_radius()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        CharacterDesc d = CharacterDesc.Default;
        d.Position = new Vector3(128f, 128f, 2f);
        d.CapsuleHalfHeight = 0.45f;
        d.CapsuleRadius = 0.30f;
        d.UserData = 77;
        CharacterId id = t.B.CreateCharacter(d);
        for (int i = 0; i < 8; i++)
            t.Step();

        t.B.SetCharacterShape(id, 0.40f, 0.55f);
        for (int i = 0; i < 3; i++)
            t.Step();

        Assert.True(t.B.TryGetCharacterState(id, out CharacterState s));
        const float From = 3f;
        Assert.True(t.B.RayCast(new Vector3(s.Position.X + From, s.Position.Y, s.Position.Z), -Vector3.UnitX, 10f, QueryFilter.All, out RayHit hit));
        Assert.Equal(77u, hit.UserData);
        // A ray at the middle of the capsule meets its side at the radius: 0.55, not the 0.30 it was made with.
        Assert.InRange(From - hit.Distance, 0.50f, 0.60f);
    }
}
