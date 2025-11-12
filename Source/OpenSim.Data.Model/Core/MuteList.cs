using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class MuteList
{
    public string AgentId { get; set; }
    public string MuteId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string MuteName { get; set; } = String.Empty;
    public int MuteType { get; set; } = 1;
    public int MuteFlags { get; set; }
    public int Stamp { get; set; }
}
