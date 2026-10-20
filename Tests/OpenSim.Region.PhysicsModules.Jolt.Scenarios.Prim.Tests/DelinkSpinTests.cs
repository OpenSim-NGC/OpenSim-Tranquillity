/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A part delinked from a linkset that is spinning goes on at the velocity its point had: the root's velocity plus the
/// root's angular velocity crossed with the part's offset from the root. Serial with the other native tests: every run
/// steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class DelinkSpinTests
{
    [Fact]
    public void A_part_delinked_from_a_spinning_linkset_keeps_the_speed_the_spin_gave_its_point()
    {
        Vector3 rootVelocity = default, partVelocity = default, rootSpin = default, offset = default;
        bool done = false;
        PhysicsActor part = null;
        var sc = new Scenario
        {
            Name = "delink-spin",
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                // Two 1 m cubes high in the air, 1 m apart along x; the second is linked to the first.
                Vector3 at = new Vector3(128f, 128f, 150f);
                PhysicsActor root = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), at, Quaternion.Identity, true, 1401);
                r.Actor = root;
                part = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(1f, 1f, 1f), at + new Vector3(1f, 0f, 0f), Quaternion.Identity, true, 1402, root);
            },
            Input = r =>
            {
                PhysicsActor root = r.Actor;
                if (r.Heartbeats == 4)
                    root.RotationalVelocity = new Vector3(0f, 0f, 2f);
                else if (r.Heartbeats == 8 && !done)
                {
                    rootVelocity = root.Velocity;
                    rootSpin = root.RotationalVelocity;
                    offset = part.Position - root.Position;
                    part.delink();
                    partVelocity = part.Velocity;
                    done = true;
                }
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 });

        Assert.True(done);
        Assert.True(rootSpin.Z > 1f, $"the linkset was not spinning ({rootSpin})");
        Vector3 expected = rootVelocity + Vector3.Cross(rootSpin, offset);
        Assert.True(Math.Abs(Vector3.Mag(offset) - 1f) < 0.1f, $"the part is not 1 m from the root ({offset})");
        Assert.True((partVelocity - expected).Length() < 0.05f, $"part velocity {partVelocity}, expected {expected} (root {rootVelocity}, spin {rootSpin}, offset {offset})");
        Assert.True((partVelocity - rootVelocity).Length() > 1f, "the part only took the root's linear velocity");
    }
}
