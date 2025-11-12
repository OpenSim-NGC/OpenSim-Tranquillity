using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Friend
{
    public string PrincipalId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string Friend1 { get; set; }
    public string Flags { get; set; } = "0";
    public string Offered { get; set; } = "0";
}
