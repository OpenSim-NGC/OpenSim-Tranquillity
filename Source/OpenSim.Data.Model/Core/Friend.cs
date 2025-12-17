/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class Friend
{
    public string PrincipalId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string Friend1 { get; set; }
    public string Flags { get; set; } = "0";
    public string Offered { get; set; } = "0";
}
