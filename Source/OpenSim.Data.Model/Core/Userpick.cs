/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class Userpick
{
    public string Pickuuid { get; set; }
    public string Creatoruuid { get; set; }
    public string Toppick { get; set; }
    public string Parceluuid { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Snapshotuuid { get; set; }
    public string User { get; set; }
    public string Originalname { get; set; }
    public string Simname { get; set; }
    public string Posglobal { get; set; }
    public int Sortorder { get; set; }
    public string Enabled { get; set; }
    public string Gatekeeper { get; set; }
}
