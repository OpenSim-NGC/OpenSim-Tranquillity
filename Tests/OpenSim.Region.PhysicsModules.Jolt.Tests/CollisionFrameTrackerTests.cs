/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using OmvVector3 = OpenMetaverse.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The accumulate / hold / collision_end decision, without a Scene. On a frame whose contact
/// buffer overflowed, absence proves nothing - the contact may simply not have fit - so a prim that collided last
/// frame gets no empty update (no false collision_end) and stays tracked for the next frame.
/// </summary>
public class CollisionFrameTrackerTests
{
    private static readonly ContactPoint Cp = new(OmvVector3.Zero, OmvVector3.UnitZ, 0f);

    private static List<uint> Frame(CollisionFrameTracker t, bool overflowed, params (uint prim, uint collider)[] touches)
    {
        t.BeginFrame();
        foreach (var (prim, collider) in touches)
            t.AddCollider(prim, collider, Cp);
        return new List<uint>(t.EndFrame(overflowed));
    }

    [Fact]
    public void Normal_frame_a_prim_absent_this_frame_gets_one_empty_update()
    {
        var t = new CollisionFrameTracker();
        Assert.Empty(Frame(t, false, (7, 0), (8, 7)));
        Assert.Equal(2, t.Current.Count);

        var ended = Frame(t, false, (8, 7));
        Assert.Equal(new uint[] { 7 }, ended);
        Assert.False(t.IsTracked(7));

        Assert.Empty(Frame(t, false, (8, 7)));   // exactly once: 7 is not ended again
    }

    [Fact]
    public void Overflowed_frame_a_prim_absent_this_frame_gets_none_and_stays_tracked()
    {
        var t = new CollisionFrameTracker();
        Frame(t, false, (7, 0), (8, 7));

        Assert.Empty(Frame(t, true, (8, 7)));    // 7 absent, but the buffer overflowed: hold
        Assert.True(t.IsTracked(7));
        Assert.True(t.IsTracked(8));

        Assert.Equal(new uint[] { 7 }, Frame(t, false, (8, 7)));   // the next honest frame ends it
    }

    [Fact]
    public void Overflowed_frame_still_delivers_what_it_did_see()
    {
        var t = new CollisionFrameTracker();
        Frame(t, true, (9, 3));
        Assert.True(t.Current.ContainsKey(9));
        Assert.True(t.IsTracked(9));
    }

    // A frame in which the module holds last frame's contacts of sleeping bodies (Hold) for prim 7.
    private static List<uint> HeldFrame(CollisionFrameTracker t, uint prim, params uint[] colliders)
    {
        t.BeginFrame();
        foreach (uint c in colliders)
            t.Hold(prim, c, Cp);
        return new List<uint>(t.EndFrame(false));
    }

    [Fact]
    public void A_held_contact_keeps_the_prim_tracked_and_ends_nothing()
    {
        var t = new CollisionFrameTracker();
        Frame(t, false, (7, 3));
        Assert.Empty(HeldFrame(t, 7, 3));
        Assert.True(t.IsTracked(7));
        Assert.True(t.Touches(7, 3));
        Assert.Equal(new uint[] { 3 }, t.Previous[7].m_objCollisionList.Keys);   // last frame's set, read by the next hold
        Assert.Equal(new uint[] { 7 }, Frame(t, false));   // once it is no longer held it ends
    }

    [Fact]
    public void Only_held_contacts_that_are_unchanged_are_not_sent_again()
    {
        var t = new CollisionFrameTracker();
        Frame(t, false, (7, 3), (7, 4));
        HeldFrame(t, 7, 3, 4);
        Assert.False(t.ShouldDeliver(7, t.Current[7]));   // at rest: no more collision events
        HeldFrame(t, 7, 3);
        Assert.True(t.ShouldDeliver(7, t.Current[7]));    // 4 went away: the scene must end it
    }

    [Fact]
    public void Held_contact_with_the_ground_alone_is_sent_every_frame()
    {
        var t = new CollisionFrameTracker();
        Frame(t, false, (7, 0));
        HeldFrame(t, 7, 0);
        Assert.True(t.ShouldDeliver(7, t.Current[7]));    // land_collision goes on, as ubODE sends it
    }

    [Fact]
    public void A_set_with_a_reported_contact_is_always_sent()
    {
        var t = new CollisionFrameTracker();
        Frame(t, false, (7, 3));
        t.BeginFrame();
        t.AddCollider(7, 3, Cp);
        Assert.True(t.ShouldDeliver(7, t.Current[7]));
        t.EndFrame(false);
    }
}

/// <summary>
/// Top Colliders, ubODE's model: CollisionScore = the Begin/Persist contact reports that named
/// the prim this frame (counted before the subscription filter), reset every frame; the top 25 by score.
/// </summary>
public class TopCollidersTests
{
    [Fact]
    public void Scores_count_contacts_per_prim()
    {
        var t = new CollisionFrameTracker();
        t.BeginFrame();
        t.CountContact(7);
        t.CountContact(7);
        t.CountContact(7);
        t.CountContact(8);
        t.CountContact(0);   // terrain is not a prim
        Assert.Equal(3, t.Scores[7]);
        Assert.Equal(1, t.Scores[8]);
        Assert.False(t.Scores.ContainsKey(0));
    }

    [Fact]
    public void Scores_reset_next_frame()
    {
        var t = new CollisionFrameTracker();
        t.BeginFrame();
        t.CountContact(7);
        t.CountContact(8);
        t.EndFrame(false);

        t.BeginFrame();
        t.CountContact(8);
        Assert.False(t.Scores.ContainsKey(7));
        Assert.Equal(1, t.Scores[8]);
        Assert.Equal(new uint[] { 7, 8 }, t.PreviouslyScored.OrderBy(x => x));   // the module zeroes 7's CollisionScore
    }

    [Fact]
    public void Top_colliders_are_the_25_highest_in_order()
    {
        var scored = Enumerable.Range(1, 30).Select(i => new KeyValuePair<uint, float>((uint)i, i % 7 == 0 ? 100f + i : i)).ToList();
        var top = CollisionFrameTracker.TopColliders(scored, 25);
        Assert.Equal(25, top.Count);
        Assert.Equal(new uint[] { 28, 21, 14, 7 }, top.Take(4).Select(kv => kv.Key));
        for (var i = 1; i < top.Count; i++)
            Assert.True(top[i - 1].Value >= top[i].Value);
        Assert.DoesNotContain(top, kv => kv.Key is 1 or 2 or 3 or 4 or 5);   // the five lowest are cut

        Assert.Empty(CollisionFrameTracker.TopColliders(new List<KeyValuePair<uint, float>>(), 25));
    }
}
