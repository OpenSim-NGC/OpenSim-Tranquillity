/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsRolemembership
{
    public string GroupId { get; set; } = String.Empty;
    public string RoleId { get; set; } = String.Empty;
    public string PrincipalId { get; set; } = String.Empty;
}
