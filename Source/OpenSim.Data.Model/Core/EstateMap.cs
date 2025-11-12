using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class EstateMap
{
    public string RegionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public int EstateId { get; set; }
}
