/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Search;

public partial class Parcelsale
{
    public string RegionUuid { get; set; }
    public string Parcelname { get; set; }
    public string ParcelUuid { get; set; }
    public int Area { get; set; }
    public int Saleprice { get; set; }
    public string Landingpoint { get; set; }
    public string InfoUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public int Dwell { get; set; }
    public int Parentestate { get; set; } = 1;
    public string Mature { get; set; } = "PG";
    public string GatekeeperUrl { get; set; }
}
