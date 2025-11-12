using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class ImOffline
{
    public int Id { get; set; }
    public string PrincipalId { get; set; } = String.Empty;
    public string FromId { get; set; } = String.Empty;
    public string Message { get; set; } = String.Empty;
    public DateTime Tmstamp { get; set; } = DateTime.Now;
}
