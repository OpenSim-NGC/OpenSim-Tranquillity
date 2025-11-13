/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Identity;

public partial class IdentityRole
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string NormalizedName { get; set; }

    public string ConcurrencyStamp { get; set; }

    public virtual ICollection<IdentityRoleClaim> IdentityRoleClaims { get; set; } = new List<IdentityRoleClaim>();

    public virtual ICollection<IdentityUser> Users { get; set; } = new List<IdentityUser>();
}
