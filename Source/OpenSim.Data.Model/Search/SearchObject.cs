/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Search;

public partial class SearchObject
{
    public string Objectuuid { get; set; }
    public string Parceluuid { get; set; }
    public string Location { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Regionuuid { get; set; }
    public string GatekeeperUrl { get; set; }
}
