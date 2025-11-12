using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsRolemembership
{
    public string GroupId { get; set; } = String.Empty;
    public string RoleId { get; set; } = String.Empty;
    public string PrincipalId { get; set; } = String.Empty;
}
