/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsMembership
{
    public string GroupId { get; set; } = String.Empty;
    public string PrincipalId { get; set; } = String.Empty;
    public string SelectedRoleId { get; set; } = String.Empty;
    public int Contribution { get; set; }
    public int ListInProfile { get; set; } = 1;
    public int AcceptNotices { get; set; } = 1;
    public string AccessToken { get; set; } = String.Empty;
}
