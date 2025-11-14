/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class Fsasset
{
    public string AssetId { get; set; }
    public string Name { get; set; } = String.Empty;
    public string Description { get; set; } = String.Empty;
    public int Type { get; set; }
    public string Hash { get; set; }
    public int CreateTime { get; set; }
    public int AccessTime { get; set; }
    public int AssetFlags { get; set; }
}
