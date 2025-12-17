/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Search;

public partial class Region
{
    public string Regionname { get; set; }
    public string RegionUuid { get; set; }
    public string Regionhandle { get; set; }
    public string Url { get; set; }
    public string Owner { get; set; }
    public string Owneruuid { get; set; }
    public string GatekeeperUrl { get; set; }
}
