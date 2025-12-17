/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class Classified
{
    public string Classifieduuid { get; set; }
    public string Creatoruuid { get; set; }
    public int Creationdate { get; set; }
    public int Expirationdate { get; set; }
    public string Category { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Parceluuid { get; set; }
    public int Parentestate { get; set; }
    public string Snapshotuuid { get; set; }
    public string Simname { get; set; }
    public string Posglobal { get; set; }
    public string Parcelname { get; set; }
    public int Classifiedflags { get; set; }
    public int Priceforlisting { get; set; }
}
