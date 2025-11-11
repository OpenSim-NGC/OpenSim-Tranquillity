using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Search;

public partial class Allparcel
{
    public string RegionUuid { get; set; }
    public string Parcelname { get; set; }
    public string OwnerUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string GroupUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string Landingpoint { get; set; }
    public string ParcelUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string InfoUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public int Parcelarea { get; set; }
    public string GatekeeperUrl { get; set; }
}
