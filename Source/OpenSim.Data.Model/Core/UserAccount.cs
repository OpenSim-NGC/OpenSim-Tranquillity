/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class UserAccount
{
    public string PrincipalId { get; set; }
    public string ScopeId { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public string ServiceUrls { get; set; }
    public int? Created { get; set; }
    public int UserLevel { get; set; }
    public int UserFlags { get; set; }
    public string UserTitle { get; set; } = String.Empty;
    public int Active { get; set; } = 1;
}
