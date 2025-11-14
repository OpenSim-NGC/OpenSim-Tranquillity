/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class GloebitSubscription
{
    public string SubscriptionId { get; set; }
    public string ObjectId { get; set; }
    public string AppKey { get; set; }
    public string GlbApiUrl { get; set; }
    public bool Enabled { get; set; }
    public string ObjectName { get; set; }
    public string Description { get; set; }
    public DateTime CTime { get; set; }
}
