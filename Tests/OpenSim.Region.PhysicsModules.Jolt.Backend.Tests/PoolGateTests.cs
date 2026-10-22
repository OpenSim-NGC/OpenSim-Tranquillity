/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The lock order is pool gate, then _simLock: a region waiting for its job pool must not be holding its
/// own _simLock, or its scene-thread physics calls (body creation, raycasts) wait behind its heartbeat's wait for
/// the pool. With the gate taken inside _simLock, the teleport crossing harness went from under 5 s to ~2 min.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PoolGateTests
{
    private readonly ITestOutputHelper _out;
    public PoolGateTests(ITestOutputHelper output) { _out = output; }

    // A call that waits for B's _simLock waits for as long as the pool's gate is held, and the test holds it until the
    // calls have returned or this has run out: a cap on a call that should return, generous so that a stalled
    // machine cannot reach it.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private static PhysicsBackendSettings OnePool(string name)
    {
        var s = JoltTestBackend.Settings();
        s.JobPools = 1;
        s.RegionName = name;
        return s;
    }

    /// <summary>Run <paramref name="call"/> on a worker thread; true when it returns within <paramref name="limit"/>.</summary>
    private static bool CompletesWithin(Action call, TimeSpan limit, out TimeSpan took)
    {
        var sw = Stopwatch.StartNew();
        var th = new Thread(() => call()) { IsBackground = true, Name = "scene-call" };
        th.Start();
        var done = th.Join(limit);
        took = sw.Elapsed;
        return done;
    }

    [Fact]
    public void Waiting_for_the_pool_does_not_hold_the_region_lock()
    {
        using var a = new JoltTestBackend(OnePool("A"));
        var b = new JoltTestBackend(OnePool("B"));
        Assert.Equal(1, b.B.GetCapacityStats().JobPools);
        Assert.Equal(0, b.B.GetCapacityStats().PoolIndex);
        b.Ground();
        var box = b.B.CreateBoxShape(new Vector3(0.5f));

        // Hold the one pool's gate, as region A's Step would, then start B's Step: it now waits at the gate. The pool
        // reports when B's Step has found the gate held; from then on it waits until the hold ends.
        var bWaits = new ManualResetEventSlim(false);
        b.B.SetPoolHooksForTest((region, mustWait) => { if (mustWait && region == "B") bWaits.Set(); }, null);
        var hold = a.B.HoldPoolGateForTest();
        var stepped = new ManualResetEventSlim(false);
        var stepper = new Thread(() => { b.Step(); stepped.Set(); }) { IsBackground = true, Name = "region-B-step" };
        stepper.Start();
        var waiting = bWaits.Wait(Limit) && !stepped.IsSet;

        bool created, cast;
        TimeSpan createTook, castTook;
        try
        {
            Assert.True(waiting, "B's Step finished while the pool gate was held - it did not wait for the pool");

            var d = BodyDesc.Default;
            d.Shape = box;
            d.Position = new Vector3(128f, 128f, 5f);
            var id = BodyId.Invalid;
            created = CompletesWithin(() => id = b.B.CreateBody(d), Limit, out createTook);
            cast = CompletesWithin(() => b.B.RayCast(new Vector3(128f, 128f, 20f), -Vector3.UnitZ, 40f, QueryFilter.All, out _), Limit, out castTook);
            _out.WriteLine($"while B waits for the pool: CreateBody {(created ? "returned" : "BLOCKED")} in {createTook.TotalMilliseconds:0} ms, " +
                           $"RayCast {(cast ? "returned" : "BLOCKED")} in {castTook.TotalMilliseconds:0} ms");
        }
        finally
        {
            hold.Dispose();   // same thread that took it (Monitor)
        }

        Assert.True(stepped.Wait(Limit), "B's Step did not complete after the pool gate was released");
        b.B.SetPoolHooksForTest(null, null);
        Assert.True(created, "CreateBody blocked while B's Step waited for the pool - B's _simLock was held across the wait");
        Assert.True(cast, "RayCast blocked while B's Step waited for the pool - B's _simLock was held across the wait");
        Assert.True(b.B.GetCapacityStats().UpdateGateWaits >= 1);
        b.Dispose();
    }
}
