/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Search;

public partial class Event
{
    public string Owneruuid { get; set; }
    public string Name { get; set; }
    public uint Eventid { get; set; }
    public string Creatoruuid { get; set; }
    public int Category { get; set; }
    public string Description { get; set; }
    public int DateUtc { get; set; }
    public int Duration { get; set; }
    public bool Covercharge { get; set; }
    public int Coveramount { get; set; }
    public string Simname { get; set; }
    public string ParcelUuid { get; set; }
    public string GlobalPos { get; set; }
    public int Eventflags { get; set; }
    public string GatekeeperUrl { get; set; }
}
