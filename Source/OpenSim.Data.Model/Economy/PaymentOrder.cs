/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;

namespace OpenSim.Data.Model.Economy;

public class PaymentOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string GatewayOrderId { get; set; } = string.Empty;
    public string GatewayCaptureId { get; set; }
    public int CurrencyAmount { get; set; }
    public decimal FiatAmount { get; set; }
    public string FiatCurrency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
