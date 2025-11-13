/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class HgTravelingDatum
{
    public string SessionId { get; set; }
    public string UserId { get; set; }
    public string GridExternalName { get; set; } = String.Empty;
    public string ServiceToken { get; set; } = String.Empty;
    public string ClientIpaddress { get; set; } = String.Empty;
    public string MyIpaddress { get; set; } = String.Empty;
    public DateTime Tmstamp { get; set; } = DateTime.Now;
}
