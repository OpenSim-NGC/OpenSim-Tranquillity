/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;

namespace OpenSim.Data.Model.Core;

public class PartnerRequest
{
    public long Id { get; set; }
    public string RequesterId { get; set; } = string.Empty;
    public string RecipientId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}
