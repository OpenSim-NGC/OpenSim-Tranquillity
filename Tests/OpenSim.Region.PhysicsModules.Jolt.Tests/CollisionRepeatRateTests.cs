/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using OmvVector3 = OpenMetaverse.Vector3;
using SVector3 = System.Numerics.Vector3;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The collision event's repeat rate. The Second Life wiki page for the collision event says "Smallest repeat rate is
/// approximatively 0.13 seconds", while a part that keeps touching something was sent an update every heartbeat (0.09 s
/// at the usual rate). A repeat of an unchanged set now waits for at least that long; a change (something new touched,
/// something left) is sent at once, so collision_start and collision_end are not delayed. The tracker tests need no
/// scene (a tracker's clock is set by the test); the scene tests step the heartbeat's own clock, without the engine.
/// </summary>
public class CollisionRepeatRateTests
{
    private static readonly ContactPoint Cp = new(OmvVector3.Zero, OmvVector3.UnitZ, 0f);

    // One frame at `now` seconds in which each (prim, collider) was reported by the engine; returns what the scene would
    // send to a prim (the throttled decision) as prim -> sent.
    private static Dictionary<uint, bool> Frame(CollisionFrameTracker t, double now, params (uint prim, uint collider)[] touches)
    {
        t.BeginFrame();
        t.Now = now;
        foreach (var (prim, collider) in touches)
            t.AddCollider(prim, collider, Cp);
        var sent = new Dictionary<uint, bool>();
        foreach (var kv in t.Current)
            sent[kv.Key] = t.ShouldDeliverToPart(kv.Key, kv.Value);
        t.EndFrame(false);
        return sent;
    }

    [Fact]
    public void An_unchanged_set_is_repeated_no_faster_than_once_in_0_13_seconds()
    {
        var t = new CollisionFrameTracker();
        Assert.True(Frame(t, 0.0, (7, 3))[7]);       // collision_start
        Assert.False(Frame(t, 0.0909, (7, 3))[7]);   // 0.09 s on: too soon
        Assert.True(Frame(t, 0.1818, (7, 3))[7]);    // 0.18 s since the last one sent
        Assert.False(Frame(t, 0.2727, (7, 3))[7]);
        Assert.True(Frame(t, 0.3636, (7, 3))[7]);
    }

    [Fact]
    public void A_repeat_exactly_0_13_seconds_after_the_last_is_sent()
    {
        var t = new CollisionFrameTracker();
        Assert.True(Frame(t, 1.0, (7, 3))[7]);
        Assert.False(Frame(t, 1.129, (7, 3))[7]);
        Assert.True(Frame(t, 1.13, (7, 3))[7]);
    }

    [Fact]
    public void A_new_collider_is_sent_at_once_whatever_the_repeat_rate()
    {
        var t = new CollisionFrameTracker();
        Assert.True(Frame(t, 0.0, (7, 3))[7]);
        Assert.True(Frame(t, 0.0909, (7, 3), (7, 4))[7]);   // 4 is new: collision_start must not wait
    }

    [Fact]
    public void A_collider_that_left_is_sent_at_once_and_so_is_the_end()
    {
        var t = new CollisionFrameTracker();
        Frame(t, 0.0, (7, 3), (7, 4));
        Assert.True(Frame(t, 0.0909, (7, 3))[7]);           // 4 went: the part must hear it
        t.BeginFrame();
        t.Now = 0.1818;
        Assert.Equal(new uint[] { 7 }, new List<uint>(t.EndFrame(false)));   // and collision_end is never held back
    }

    [Fact]
    public void Touching_again_after_an_end_is_a_new_start_not_a_throttled_repeat()
    {
        var t = new CollisionFrameTracker();
        Assert.True(Frame(t, 0.0, (7, 3))[7]);
        Frame(t, 0.05);                                      // nothing: ended
        Assert.True(Frame(t, 0.1, (7, 3))[7]);      // only 0.1 s after the first, but a fresh start after the gap
    }

    [Fact]
    public void The_ground_alone_is_land_collision_and_is_sent_every_heartbeat()
    {
        // The land_collision wiki page gives no repeat rate; ubODE sends it every frame, so it is not throttled.
        var t = new CollisionFrameTracker();
        Assert.True(Frame(t, 0.0, (7, 0))[7]);
        Assert.True(Frame(t, 0.0909, (7, 0))[7]);
        Assert.True(Frame(t, 0.1818, (7, 0))[7]);
    }

    [Fact]
    public void Each_part_has_its_own_clock()
    {
        var t = new CollisionFrameTracker();
        Frame(t, 0.0, (7, 3));
        var s = Frame(t, 0.0909, (7, 3), (8, 3));
        Assert.False(s[7]);
        Assert.True(s[8]);   // 8 is only now touching: its own start
    }

    // ------------------------------------------------------------------ in a scene

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[256 * 256];
        Array.Fill(heights, 25f);
        scene.InitialiseWithoutScene("Test Region", 256, 256, heights, 20f, 0.0909f);
        return scene;
    }

    private static ContactReport Persist(uint a, uint b)
        => new ContactReport { ChildUserDataA = a, ChildUserDataB = b, UserDataA = a, UserDataB = b, Phase = ContactPhase.Persist, Normal = new SVector3(0f, 0f, 1f) };

    [Collection(JoltNativeSerial.Name)]
    public class InAScene
    {
        [Fact]
        public void A_part_that_keeps_touching_hears_collision_about_every_0_18_seconds_not_every_heartbeat()
        {
            JoltScene s = NewScene();
            try
            {
                PhysicsActor prim = s.AddPrimShape("box", PrimitiveBaseShape.CreateBox(), new OmvVector3(128f, 128f, 35f), new OmvVector3(1f, 1f, 1f), Quaternion.Identity, false, 1000);
                prim.SubscribeEvents(50);
                var times = new List<double>();
                prim.OnCollisionUpdate += e => { if (((CollisionEventUpdate)e).m_objCollisionList.Count > 0) times.Add(s.SimTime); };
                for (int i = 0; i < 33; i++)   // three seconds of heartbeats
                {
                    s.SimTime += 0.0909;
                    s.DispatchContacts(new[] { Persist(1000, 2000) }, 1, false, mergeSubsteps: false);
                }
                Assert.InRange(times.Count, 15, 18);   // not 33
                for (int i = 1; i < times.Count; i++)
                    Assert.True(times[i] - times[i - 1] >= 0.13 - 1e-9, $"repeat {i} came after {times[i] - times[i - 1]:0.000} s");
            }
            finally { s.Dispose(); }
        }
    }
}
