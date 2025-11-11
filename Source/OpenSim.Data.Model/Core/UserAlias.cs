using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class UserAlias
{
    public int Id { get; set; }
    public string AliasId { get; set; }
    public string UserId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string Description { get; set; }
}
