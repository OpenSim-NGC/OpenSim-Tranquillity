using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsInvite
{
    public string InviteId { get; set; } = String.Empty;
    public string GroupId { get; set; } = String.Empty; 
    public string RoleId { get; set; } = String.Empty;
    public string PrincipalId { get; set; } = String.Empty;
    public DateTime Tmstamp { get; set; } = DateTime.Now;
}
