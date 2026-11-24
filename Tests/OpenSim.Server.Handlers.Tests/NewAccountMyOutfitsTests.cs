/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Server.Handlers.Inventory;
using OpenSim.Services.Interfaces;
using OpenSim.Services.InventoryService;
using OpenSim.Services.UserAccountService;
using OpenSim.Tests.Common;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A new account's inventory had every system folder but My Outfits, which the Linden viewer and Firestorm make
/// themselves at login and other clients do not. CreateUserInventory now makes it, once, directly under the root,
/// on a standalone and on Robust (the same XInventoryService), and leaves alone an inventory that already has one.
/// </summary>
public class NewAccountMyOutfitsTests
{
    private static IniConfigSource Config()
    {
        IniConfigSource config = new();
        IConfig accounts = config.AddConfig("UserAccountService");
        accounts.Set("StorageProvider", "OpenSim.Data.Null.dll");
        accounts.Set("InventoryService", "OpenSim.Services.InventoryService.dll:XInventoryService");
        config.AddConfig("InventoryService")
            .Set("StorageProvider", "OpenSim.Tests.Common.dll:TestXInventoryDataPlugin");
        return config;
    }

    private static IInventoryService Inventory(IniConfigSource config) =>
        ServerUtils.LoadPlugin<IInventoryService>("OpenSim.Services.InventoryService.dll:XInventoryService", [config]);

    private static List<InventoryFolderBase> MyOutfits(IInventoryService inventory, UUID user) =>
        inventory.GetInventorySkeleton(user).Where(f => f.Type == (short)FolderType.MyOutfits).ToList();

    private sealed class Accounts : UserAccountService
    {
        public Accounts(IConfigSource config) : base(config) { }
        public IInventoryService Inventory => m_InventoryService;
    }

    [Xunit.Fact]
    public void ANewAccountHasOneMyOutfitsFolderUnderTheRoot()
    {
        Accounts accounts = new(Config());
        UUID user = UUID.Random();

        Xunit.Assert.NotNull(accounts.CreateUser(UUID.Zero, user, "Outfits", "Tester" + user.ToString()[..8], "secret", ""));

        IInventoryService inventory = accounts.Inventory;
        InventoryFolderBase myOutfits = Xunit.Assert.Single(MyOutfits(inventory, user));
        Xunit.Assert.Equal(48, myOutfits.Type);
        Xunit.Assert.Equal("My Outfits", myOutfits.Name);
        Xunit.Assert.Equal(inventory.GetRootFolder(user).ID, myOutfits.ParentID);
        Xunit.Assert.Equal(myOutfits.ID, inventory.GetFolderForType(user, FolderType.MyOutfits).ID);
    }

    [Xunit.Fact]
    public void CreatingTheInventoryAgainOverRobustKeepsOneMyOutfitsFolder()
    {
        IInventoryService inventory = Inventory(Config());
        XInventoryConnectorPostHandler handler = new(inventory, null, new ControlPlaneAccess(new IniConfigSource()));
        UUID user = UUID.Random();

        for (int i = 0; i < 2; i++)
        {
            using MemoryStream body = new(Encoding.UTF8.GetBytes($"METHOD=CREATEUSERINVENTORY&PRINCIPAL={user}"));
            byte[] result = handler.Handle("/xinventory", body, new TestOSHttpRequest(), new TestOSHttpResponse());
            // the answer is whether a root folder was made, so the second call answers False
            Xunit.Assert.Equal(i == 0 ? "True" : "False", ServerUtils.ParseXmlResponse(Encoding.UTF8.GetString(result))["RESULT"]);
        }

        Xunit.Assert.Single(MyOutfits(inventory, user));
    }

    private sealed class Store : XInventoryService
    {
        public Store(IConfigSource config) : base(config) { }
        public IXInventoryData Data => m_Database;
    }

    [Xunit.Fact]
    public void AnInventoryWhoseViewerMadeMyOutfitsGetsNoSecond()
    {
        Store inventory = new(Config());
        UUID user = UUID.Random();
        UUID root = UUID.Random();
        XInventoryFolder Folder(UUID id, UUID parent, FolderType type, string name) => new()
        {
            folderID = id, parentFolderID = parent, agentID = user, type = (int)type, folderName = name, version = 1,
        };

        // an inventory made before this change, whose viewer then made My Outfits as
        // LLInventoryModel::ensureCategoryForTypeExists does: under the root, with the viewer's own name
        UUID viewerMade = UUID.Random();
        Xunit.Assert.True(inventory.Data.StoreFolder(Folder(root, UUID.Zero, FolderType.Root, InventoryFolderBase.ROOT_FOLDER_NAME)));
        Xunit.Assert.True(inventory.Data.StoreFolder(Folder(viewerMade, root, FolderType.MyOutfits, "Outfits")));

        inventory.CreateUserInventory(user);

        Xunit.Assert.Equal(viewerMade, Xunit.Assert.Single(MyOutfits(inventory, user)).ID);
        Xunit.Assert.NotNull(inventory.GetFolderForType(user, FolderType.Trash));
    }
}
