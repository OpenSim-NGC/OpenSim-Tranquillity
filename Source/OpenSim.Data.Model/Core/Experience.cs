/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class Experience
{
    public string public_id { get; set; }
    public string owner_id { get; set; }
    public string name { get; set; }
    public string description { get; set; }
    public string group_id { get; set; }
    public string logo { get; set; }
    public string marketplace { get; set; }
    public string slurl { get; set; }
    public int maturity { get; set; }
    public int properties { get; set; }    
}
