/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Economy;

/// <summary>
/// Rev.12
/// </summary>
public partial class Transaction
{
    public string Uuid { get; set; }
    public string Sender { get; set; }
    public string Receiver { get; set; }
    public int Amount { get; set; }
    public int SenderBalance { get; set; } = -1;
    public int ReceiverBalance { get; set; } = -1;
    public string ObjectUuid { get; set; }
    public string ObjectName { get; set; }
    public string RegionHandle { get; set; }
    public string RegionUuid { get; set; }
    public int Type { get; set; }
    public int Time { get; set; }
    public string Secure { get; set; }
    public bool Status { get; set; }
    public string CommonName { get; set; }
    public string Description { get; set; }
}
