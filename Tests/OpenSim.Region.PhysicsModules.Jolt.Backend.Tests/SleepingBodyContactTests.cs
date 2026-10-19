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
/// A body asleep on the ground still touches it, though the engine reports no contact for a sleeping body. Serial: it steps
/// a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SleepingBodyContactTests
{
    [Fact]
    public void A_body_asleep_on_the_ground_still_had_contact()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        BodyId box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(128f, 128f, 0.52f));
        int steps = 0;
        while (t.B.IsBodyAwake(box) || steps < 5)
        {
            t.Step();
            Assert.True(++steps < 600, "the box never fell asleep");
        }
        // A few more steps asleep: the engine reports no contact for it now.
        for (int i = 0; i < 5; i++)
            t.Step();

        Assert.False(t.B.IsBodyAwake(box));
        Assert.True(t.B.BodyHadContact(box), "a body asleep on the ground reads as touching nothing");
    }

    [Fact]
    public void A_body_asleep_in_the_air_that_never_touched_anything_had_no_contact()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        BodyId box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(128f, 128f, 40f), startActive: false);
        for (int i = 0; i < 5; i++)
            t.Step();

        Assert.False(t.B.IsBodyAwake(box));
        Assert.False(t.B.BodyHadContact(box));
    }
}
