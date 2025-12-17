/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Region;

public partial class Landaccesslist
{
    public string LandUuid { get; set; }
    public string AccessUuid { get; set; }
    public int? Flags { get; set; }
    public int Expires { get; set; }
}
