/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Inventoryitem
{
    public string AssetId { get; set; }
    public int? AssetType { get; set; }
    public string InventoryName { get; set; }
    public string InventoryDescription { get; set; }
    public uint? InventoryNextPermissions { get; set; }
    public uint? InventoryCurrentPermissions { get; set; }
    public int? InvType { get; set; }
    public string CreatorId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public uint InventoryBasePermissions { get; set; }
    public uint InventoryEveryOnePermissions { get; set; }
    public int SalePrice { get; set; }
    public sbyte SaleType { get; set; }
    public int CreationDate { get; set; }
    public string GroupId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public sbyte GroupOwned { get; set; }
    public uint Flags { get; set; }
    public string InventoryId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string AvatarId { get; set; }
    public string ParentFolderId { get; set; }
    public uint InventoryGroupPermissions { get; set; }
}
