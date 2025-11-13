/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Search;

public partial class Parcel
{
    public string ParcelUuid { get; set; }
    public string RegionUuid { get; set; }
    public string Parcelname { get; set; }
    public string Landingpoint { get; set; }
    public string Description { get; set; }
    public string Searchcategory { get; set; }
    public string Build { get; set; }
    public string Script { get; set; }
    public string Public { get; set; }
    public float Dwell { get; set; }
    public string Infouuid { get; set; } = String.Empty;
    public string Mature { get; set; } = "PG";
    public string GatekeeperUrl { get; set; }
    public string ImageUuid { get; set; }
}
