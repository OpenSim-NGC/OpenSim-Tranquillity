/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Economy;

/// <summary>
/// Rev.4
/// </summary>
public partial class Balance
{
    public string User { get; set; }
    public int Balance1 { get; set; }
    public sbyte? Status { get; set; }
    public sbyte Type { get; set; }
}
