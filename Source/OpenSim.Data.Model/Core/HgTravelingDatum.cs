using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class HgTravelingDatum
{
    public string SessionId { get; set; }
    public string UserId { get; set; }
    public string GridExternalName { get; set; } = String.Empty;
    public string ServiceToken { get; set; } = String.Empty;
    public string ClientIpaddress { get; set; } = String.Empty;
    public string MyIpaddress { get; set; } = String.Empty;
    public DateTime Tmstamp { get; set; } = DateTime.Now;
}
