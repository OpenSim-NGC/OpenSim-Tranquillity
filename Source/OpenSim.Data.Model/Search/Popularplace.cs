/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Search;

public partial class Popularplace
{
    public string ParcelUuid { get; set; }
    public string Name { get; set; }
    public float Dwell { get; set; }
    public string InfoUuid { get; set; }
    public bool HasPicture { get; set; }
    public string Mature { get; set; }
    public string GatekeeperUrl { get; set; }
}
