/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.Objects.Tests;

/// <summary>
/// A linkset's parts get their local IDs when the group is added to a scene. These tests add a linkset whose
/// parts are held out of link order (the order a region store hands rows back, or the order of the parts in a
/// serialised object) on each path that adds a whole linkset, and check that the local IDs go root first, then
/// by link number: a region store load, an OAR load, a rez from inventory and an attachment rez from inventory.
/// </summary>
public class LinksetLocalIdOrderTests : OpenSimTestCase
{
    // the order the children are held in: neither link order nor its reverse
    private static readonly int[] HeldOrder = { 5, 2, 8, 3, 7, 4, 6 };
    private static readonly int[] LinkOrder = { 1, 2, 3, 4, 5, 6, 7, 8 };

    private static readonly UUID Owner = TestHelpers.ParseTail(0x99);

    /// <summary>
    /// Builds a group the way the region stores do on load: a group from the root row, then AddPart for each
    /// child row in row order, then the stored link number put back.
    /// </summary>
    private static SceneObjectGroup CreateOutOfOrder(UUID owner, int uuidTail)
    {
        SceneObjectPart root = SceneHelpers.CreateSceneObjectPart("Part1", TestHelpers.ParseTail(uuidTail), owner);
        root.LinkNum = 1;
        SceneObjectGroup group = new(root);
        foreach (int linkNum in HeldOrder)
        {
            SceneObjectPart child = SceneHelpers.CreateSceneObjectPart(
                "Part" + linkNum, TestHelpers.ParseTail(uuidTail + linkNum - 1), owner);
            child.OffsetPosition = new Vector3(linkNum * 0.5f, 0, 0);
            group.AddPart(child);
            child.LinkNum = linkNum;
        }
        return group;
    }

    private static int[] HeldLinkNums(SceneObjectGroup group) => group.Parts.Select(p => p.LinkNum).ToArray();

    /// <summary>
    /// The group's parts are still held out of link order, so the local IDs were not given in link order by
    /// accident of storage order; and in link order (root first, then link 2, 3...) the local IDs rise.
    /// </summary>
    private static void AssertLocalIdsInLinkOrder(SceneObjectGroup group)
    {
        Assert.Equal(LinkOrder.Length, group.PrimCount);
        Assert.NotEqual(LinkOrder, HeldLinkNums(group));

        SceneObjectPart[] byLink = group.Parts
            .OrderBy(p => p == group.RootPart ? int.MinValue : p.LinkNum)
            .ToArray();
        Assert.Equal(LinkOrder, byLink.Select(p => p.LinkNum).ToArray());

        uint[] ids = byLink.Select(p => p.LocalId).ToArray();
        Assert.All(ids, id => Assert.NotEqual(0u, id));
        for (int i = 1; i < ids.Length; i++)
        {
            Assert.True(ids[i - 1] < ids[i],
                $"local ids in link order: {string.Join(",", ids)} (link {byLink[i - 1].LinkNum} has {ids[i - 1]}, link {byLink[i].LinkNum} has {ids[i]})");
        }
    }

    [Fact]
    public void Adding_a_linkset_to_a_scene_gives_its_local_ids_in_link_order()
    {
        TestHelpers.InMethod();
        TestScene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup group = CreateOutOfOrder(Owner, 0x100);

        // SceneGraph.AddSceneObject -> SceneObjectGroup.AttachToScene, the path every whole-linkset add takes
        scene.AddNewSceneObject(group, false);

        AssertLocalIdsInLinkOrder(group);
    }

    [Fact]
    public void A_linkset_loaded_from_the_region_store_gets_its_local_ids_in_link_order()
    {
        TestHelpers.InMethod();
        SceneHelpers helpers = new();
        TestScene scene = helpers.SetupScene();

        // the test store keeps the parts and hands the children back in the order they were stored
        helpers.SimDataService.StoreObject(CreateOutOfOrder(Owner, 0x100), scene.RegionInfo.RegionID);

        // Scene.LoadPrimsFromStorage -> Scene.AddRestoredSceneObject for each stored group
        scene.LoadPrimsFromStorage(scene.RegionInfo.RegionID);

        SceneObjectGroup loaded = scene.GetSceneObjectGroup(TestHelpers.ParseTail(0x100));
        Assert.NotNull(loaded);
        AssertLocalIdsInLinkOrder(loaded);
    }

    [Fact]
    public void A_linkset_loaded_from_an_oar_gets_its_local_ids_in_link_order()
    {
        TestHelpers.InMethod();
        TestScene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, new SerialiserModule());

        SceneObjectGroup source = CreateOutOfOrder(Owner, 0x100);
        string xml = SceneObjectSerializer.ToXml2Format(source);

        MemoryStream archiveStream = new();
        TarArchiveWriter tar = new(archiveStream);
        tar.WriteFile(ArchiveConstants.CONTROL_FILE_PATH,
            "<?xml version=\"1.0\" encoding=\"utf-16\"?><archive major_version=\"0\" minor_version=\"8\"></archive>");
        tar.WriteFile(ArchiveConstants.CreateOarObjectPath(source.Name, source.UUID, source.AbsolutePosition), xml);
        tar.Close();

        // ArchiveReadRequest.LoadObjects: DeserializeGroupFromXml2, ResetIDs, Scene.AddRestoredSceneObject
        ArchiveReadRequest request = new(scene, new MemoryStream(archiveStream.ToArray()), Guid.Empty,
            new Dictionary<string, object> { { "skipAssets", null! } });
        request.DearchiveRegion(false);

        SceneObjectGroup loaded = scene.GetSceneObjectGroups().Single();
        AssertLocalIdsInLinkOrder(loaded);
    }

    private static (TestScene Scene, ScenePresence Sp, InventoryItemBase Item) SetupInventoryRez()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        TestScene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, config, new AttachmentsModule(), new BasicInventoryAccessModule());

        UserAccount user = UserAccountHelpers.CreateUserWithInventory(scene, TestHelpers.ParseTail(0x1));
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, user);

        // the item's asset is the group serialised with its parts in the order they are held
        SceneObjectGroup source = CreateOutOfOrder(sp.UUID, 0x100);
        InventoryItemBase item = UserInventoryHelpers.AddInventoryItem(scene, source, 0x200, 0x300);
        return (scene, sp, item);
    }

    [Fact]
    public void A_linkset_rezzed_from_inventory_gets_its_local_ids_in_link_order()
    {
        TestHelpers.InMethod();
        (TestScene scene, ScenePresence sp, InventoryItemBase item) = SetupInventoryRez();

        // InventoryAccessModule.RezObject: CreateObjectFromInventory, ResetIDs, Scene.AddNewSceneObject
        SceneObjectGroup rezzed = scene.RequestModuleInterface<IInventoryAccessModule>().RezObject(
            sp.ControllingClient, item.ID, UUID.Zero, new Vector3(128, 128, 30), new Vector3(128, 128, 31), UUID.Zero, 1,
            true, false, false, UUID.Zero, false);

        Assert.NotNull(rezzed);
        AssertLocalIdsInLinkOrder(rezzed);
    }

    [Fact]
    public void A_linkset_worn_from_inventory_gets_its_local_ids_in_link_order()
    {
        TestHelpers.InMethod();
        (TestScene scene, ScenePresence sp, InventoryItemBase item) = SetupInventoryRez();

        // AttachmentsModule.RezSingleAttachmentFromInventory -> InventoryAccessModule.RezObject (as an attachment)
        SceneObjectGroup worn = (SceneObjectGroup)scene.AttachmentsModule.RezSingleAttachmentFromInventory(
            sp, item.ID, (uint)AttachmentPoint.Chest);

        Assert.NotNull(worn);
        Assert.True(worn.IsAttachment);
        AssertLocalIdsInLinkOrder(worn);
    }
}
