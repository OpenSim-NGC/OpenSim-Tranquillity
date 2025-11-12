using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Presence
{
    public string UserId { get; set; }
    public string RegionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string SessionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string SecureSessionId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public DateTime LastSeen { get; set; } = DateTime.Now;
}
