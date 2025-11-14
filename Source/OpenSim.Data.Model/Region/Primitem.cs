/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Region;

public partial class Primitem
{
    public int? InvType { get; set; }
    public int? AssetType { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public long? CreationDate { get; set; }
    public int? NextPermissions { get; set; }
    public int? CurrentPermissions { get; set; }
    public int? BasePermissions { get; set; }
    public int? EveryonePermissions { get; set; }
    public int? GroupPermissions { get; set; }
    public int Flags { get; set; }
    public string ItemId { get; set; } = String.Empty;
    public string PrimId { get; set; }
    public string AssetId { get; set; }
    public string ParentFolderId { get; set; }
    public string CreatorId { get; set; } = String.Empty;
    public string OwnerId { get; set; }
    public string GroupId { get; set; }
    public string LastOwnerId { get; set; }
}
