/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Search;

public partial class Allparcel
{
    public string RegionUuid { get; set; }
    public string Parcelname { get; set; }
    public string OwnerUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string GroupUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string Landingpoint { get; set; }
    public string ParcelUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string InfoUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public int Parcelarea { get; set; }
    public string GatekeeperUrl { get; set; }
}
