using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsRole
{
    public string GroupId { get; set; } = String.Empty;
    public string RoleId { get; set; } = String.Empty;
    public string Name { get; set; } = String.Empty;
    public string Description { get; set; } = String.Empty;
    public string Title { get; set; } = String.Empty;
    public ulong Powers { get; set; }
}
