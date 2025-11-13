/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Economy;

/// <summary>
/// Rev.3
/// </summary>
public partial class Totalsale
{
    public string Uuid { get; set; }
    public string User { get; set; }
    public string ObjectUuid { get; set; }
    public int Type { get; set; }
    public int TotalCount { get; set; }
    public int TotalAmount { get; set; }
    public int Time { get; set; }
}
