/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsGroup
{
    public string GroupId { get; set; } = String.Empty;
    public string Location { get; set; } = String.Empty;
    public string Name { get; set; } = String.Empty;
    public string Charter { get; set; } = String.Empty;
    public string InsigniaId { get; set; } = String.Empty;
    public string FounderId { get; set; } = String.Empty;
    public int MembershipFee { get; set; }
    public string OpenEnrollment { get; set; } = String.Empty;
    public int ShowInList { get; set; }
    public int AllowPublish { get; set; }
    public int MaturePublish { get; set; }
    public string OwnerRoleId { get; set; }
}
