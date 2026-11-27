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
/// An overlap query looks where its centre and orientation say. These pin the transform the backend hands the binding:
/// a transposed one loses the translation and inverts the rotation. Serial: it uses a real backend.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class OverlapQueryTests
{
    private static readonly Vector3 Away = new(128f, 128f, 25f);

    private static BodyId Box(JoltTestBackend t, Vector3 position)
    {
        var d = BodyDesc.Default;
        d.Shape = t.B.CreateBoxShape(new Vector3(0.5f));
        d.Position = position;
        return t.B.CreateBody(d);
    }

    [Fact]
    public void OverlapSphere_AwayFromTheOrigin_FindsOnlyTheBoxAtItsCentre()
    {
        using var t = new JoltTestBackend();
        BodyId there = Box(t, Away);
        Box(t, Vector3.Zero);
        var found = new BodyId[8];

        int n = t.B.OverlapSphere(Away, 1f, QueryFilter.All, found);

        Assert.Equal(new[] { there }, found[..n]);
    }

    [Fact]
    public void OverlapBox_AwayFromTheOrigin_FindsOnlyTheBoxAtItsCentre()
    {
        using var t = new JoltTestBackend();
        BodyId there = Box(t, Away);
        Box(t, Vector3.Zero);
        var found = new BodyId[8];

        int n = t.B.OverlapBox(Away, new Vector3(1f), Quaternion.Identity, QueryFilter.All, found);

        Assert.Equal(new[] { there }, found[..n]);
    }

    // A long thin query box turned 45 degrees about +Z reaches toward (+x, +y). The inverse turn reaches toward (+x, -y).
    // At the origin the translation is zero, so that row tests the rotation alone.
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(128f, 128f, 25f)]
    public void OverlapBox_Rotated_FindsOnlyTheBoxWhereTheRotationPutsIt(float x, float y, float z)
    {
        var centre = new Vector3(x, y, z);
        using var t = new JoltTestBackend();
        BodyId turned = Box(t, centre + new Vector3(5f, 5f, 0f));
        Box(t, centre + new Vector3(5f, -5f, 0f));
        var found = new BodyId[8];

        int n = t.B.OverlapBox(centre, new Vector3(10f, 0.2f, 0.2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f), QueryFilter.All, found);

        Assert.Equal(new[] { turned }, found[..n]);
    }
}
