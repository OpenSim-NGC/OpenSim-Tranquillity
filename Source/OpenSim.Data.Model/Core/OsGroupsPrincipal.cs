using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsPrincipal
{
    public string PrincipalId { get; set; } = String.Empty;
    public string ActiveGroupId { get; set; } = String.Empty;
}
