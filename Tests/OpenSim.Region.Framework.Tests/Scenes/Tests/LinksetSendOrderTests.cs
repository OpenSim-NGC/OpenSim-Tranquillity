/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The object update messages carry no link numbers, so a viewer numbers a linkset's parts in the order they arrive
/// (Firestorm's "Link number" in the build floater, for one). Second Life sends the root first and then the children
/// in link order. These tests load a linkset with its parts in the order a region store hands them back (not link
/// order) and check that every path that sends a group's parts to a viewer sends root first, then link 2, 3, 4...
/// </summary>
public class LinksetSendOrderTests : OpenSimTestCase
{
    // the order the children's rows come back from the store: neither link order nor its reverse
    private static readonly int[] StoreRowOrder = { 5, 2, 8, 3, 7, 4, 6 };
    private static readonly int[] LinkOrder = { 1, 2, 3, 4, 5, 6, 7, 8 };

    private TestScene m_scene;
    private ScenePresence m_sp;
    private TestClient m_client;
    private SceneObjectGroup m_group;
    private readonly List<(uint LocalId, int LinkNum)> m_seen = new();

    /// <summary>
    /// Builds a group the way the region stores do on load (a group from the root row, then AddPart for each child
    /// row in row order, then the stored link number put back), adds it to a scene with one avatar watching, and
    /// records the link number of each part of the group the avatar's viewer is sent.
    /// </summary>
    private void Setup(int[] childRowOrder)
    {
        m_scene = new SceneHelpers().SetupScene();
        UUID owner = TestHelpers.ParseTail(0x99);
        m_group = CreateAsLoaded(owner, 0x100, childRowOrder);
        m_scene.AddNewSceneObject(m_group, false);

        m_sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
        m_client = (TestClient)m_sp.ControllingClient;
        m_client.OnReceivedEntityUpdate += (entity, flags) =>
        {
            if (entity is SceneObjectPart part && part.ParentGroup == m_group)
                m_seen.Add((part.LocalId, part.LinkNum));
        };
    }

    private static SceneObjectGroup CreateAsLoaded(UUID owner, int uuidTail, int[] childRowOrder)
    {
        SceneObjectPart root = SceneHelpers.CreateSceneObjectPart("Part1", TestHelpers.ParseTail(uuidTail), owner);
        root.LinkNum = 1;
        SceneObjectGroup group = new(root);
        foreach (int linkNum in childRowOrder)
        {
            SceneObjectPart child = SceneHelpers.CreateSceneObjectPart(
                "Part" + linkNum, TestHelpers.ParseTail(uuidTail + linkNum - 1), owner);
            child.OffsetPosition = new Vector3(linkNum * 0.5f, 0, 0);
            group.AddPart(child);
            child.LinkNum = linkNum; // the stores put the stored link number back after AddPart
        }
        return group;
    }

    private int[] SeenLinkNums() => m_seen.Select(s => s.LinkNum).ToArray();

    private int[] ExpectedLinkOrder() => Enumerable.Range(1, m_group.PrimCount).ToArray();

    private void AssertLinkOrderSent()
    {
        Assert.Equal(ExpectedLinkOrder(), SeenLinkNums());
        Assert.Equal(m_group.RootPart.LocalId, m_seen[0].LocalId);
    }

    [Fact]
    public void The_test_linkset_really_is_held_out_of_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        // guards the other tests: without this they could pass on a group that happens to be in order
        Assert.NotEqual(LinkOrder, m_group.Parts.Select(p => p.LinkNum).ToArray());
    }

    [Fact]
    public void PartsInLinkOrder_gives_the_root_first_then_the_children_by_link_number()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        SceneObjectPart[] ordered = m_group.PartsInLinkOrder;

        Assert.Same(m_group.RootPart, ordered[0]);
        Assert.Equal(LinkOrder, ordered.Select(p => p.LinkNum).ToArray());
        Assert.Equal(m_group.Parts.Length, ordered.Length);
    }

    [Fact]
    public void PartsInLinkOrder_follows_a_change_of_link_numbers()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        SceneObjectPart[] first = m_group.PartsInLinkOrder;

        // swap two link numbers in place, as a renumbering would
        SceneObjectPart two = m_group.GetLinkNumPart(2);
        SceneObjectPart seven = m_group.GetLinkNumPart(7);
        two.LinkNum = 7;
        seven.LinkNum = 2;

        SceneObjectPart[] second = m_group.PartsInLinkOrder;

        Assert.Equal(LinkOrder, second.Select(p => p.LinkNum).ToArray());
        Assert.Same(seven, second[1]);
        Assert.Same(two, second[6]);
        Assert.Same(seven, first[6]); // the earlier array is left as it was
    }

    [Fact]
    public void PartsInLinkOrder_reuses_its_ordered_copy_while_nothing_changes()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        // SceneObjectGroup.Update calls it on every scheduled send: no new array each time
        Assert.Same(m_group.PartsInLinkOrder, m_group.PartsInLinkOrder);
        Assert.NotSame(m_group.Parts, m_group.PartsInLinkOrder);
    }

    [Fact]
    public void A_viewer_queue_dequeues_one_groups_parts_in_the_order_they_were_queued()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        PriorityQueue queue = new(16);

        // the prioritizers give every part of a group the group's priority; within one priority the queue is first in, first out
        foreach (SceneObjectPart part in m_group.PartsInLinkOrder)
            queue.Enqueue(3, EntityUpdatesPool.Get(part, PrimUpdateFlags.FullUpdate));

        List<int> dequeued = new();
        while (queue.TryDequeue(out EntityUpdate update))
            dequeued.Add(((SceneObjectPart)update.Entity).LinkNum);

        Assert.Equal(LinkOrder, dequeued.ToArray());
    }

    [Fact]
    public void A_full_send_to_one_viewer_on_login_or_arrival_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        m_group.SendFullAnimUpdateToClient(m_client);

        AssertLinkOrderSent();
    }

    [Fact]
    public void The_cache_probes_on_arrival_go_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        m_group.SendUpdateProbes(m_client);

        AssertLinkOrderSent();
    }

    [Fact]
    public void A_scheduled_full_update_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        m_group.ScheduleGroupForFullUpdate();
        m_seen.Clear();

        m_group.Update();

        AssertLinkOrderSent();
    }

    [Fact]
    public void A_scheduled_full_anim_update_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        m_group.ScheduleGroupForFullAnimUpdate();
        m_seen.Clear();

        m_group.Update();

        AssertLinkOrderSent();
    }

    [Fact]
    public void A_scheduled_terse_update_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        m_group.Update(); // send whatever adding the group scheduled
        m_group.ScheduleGroupForTerseUpdate();
        m_seen.Clear();

        m_group.Update();

        AssertLinkOrderSent();
    }

    [Fact]
    public void An_immediate_terse_update_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        m_group.SendGroupTerseUpdate();

        AssertLinkOrderSent();
    }

    [Fact]
    public void After_a_link_the_new_order_is_sent()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        SceneObjectGroup other = CreateAsLoaded(m_group.OwnerID, 0x200, new[] { 3, 2 });
        m_scene.AddNewSceneObject(other, false);

        m_group.LinkToGroup(other);
        m_group.ScheduleGroupForFullUpdate();
        m_seen.Clear();
        m_group.Update();

        Assert.Equal(11, m_group.PrimCount);
        AssertLinkOrderSent();
    }

    [Fact]
    public void After_an_unlink_the_new_order_is_sent()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        m_group.DelinkFromGroup(m_group.GetLinkNumPart(4), true);
        m_group.ScheduleGroupForFullUpdate();
        m_seen.Clear();
        m_group.Update();

        Assert.Equal(7, m_group.PrimCount);
        AssertLinkOrderSent();
    }

    [Fact]
    public void An_attachment_full_update_to_a_viewer_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        m_sp.SendAttachmentFullUpdateToAgentNF(m_group, m_sp);

        AssertLinkOrderSent();
    }

    [Fact]
    public void An_attachment_update_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);

        m_sp.SendAttachmentUpdate(m_group, PrimUpdateFlags.FullUpdate);

        AssertLinkOrderSent();
    }

    [Fact]
    public void A_scheduled_attachment_update_goes_in_link_order()
    {
        TestHelpers.InMethod();
        Setup(StoreRowOrder);
        m_group.ScheduleGroupForFullUpdate();
        m_seen.Clear();

        m_sp.SendAttachmentScheduleUpdate(m_group);

        AssertLinkOrderSent();
    }

    [Fact]
    public void A_single_prim_object_is_sent_as_before()
    {
        TestHelpers.InMethod();
        Setup(Array.Empty<int>());

        Assert.Same(m_group.Parts, m_group.PartsInLinkOrder);

        m_group.SendFullAnimUpdateToClient(m_client);
        m_group.SendUpdateProbes(m_client);
        m_group.SendGroupTerseUpdate();

        Assert.Equal(3, m_seen.Count);
        Assert.All(m_seen, s => Assert.Equal(m_group.RootPart.LocalId, s.LocalId));
        Assert.Equal(0, m_group.RootPart.LinkNum);
    }

    [Fact]
    public void A_linkset_already_in_link_order_is_sent_unchanged()
    {
        TestHelpers.InMethod();
        Setup(new[] { 2, 3, 4, 5, 6, 7, 8 });

        Assert.Same(m_group.Parts, m_group.PartsInLinkOrder);

        m_group.SendFullAnimUpdateToClient(m_client);

        AssertLinkOrderSent();
    }
}
