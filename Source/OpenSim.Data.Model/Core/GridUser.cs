/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class GridUser
{
    public string UserId { get; set; }
    public string HomeRegionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string HomePosition { get; set; } = "<0,0,0>";
    public string HomeLookAt { get; set; } = "<0,0,0>";
    public string LastRegionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string LastPosition { get; set; } = "<0,0,0>";
    public string LastLookAt { get; set; } = "<0,0,0>";
    public string Online { get; set; } = "false";
    public string Login { get; set; } = "0";
    public string Logout { get; set; } = "0";
}
