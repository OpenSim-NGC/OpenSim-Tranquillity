using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsMembership
{
    public string GroupId { get; set; } = String.Empty;
    public string PrincipalId { get; set; } = String.Empty;
    public string SelectedRoleId { get; set; } = String.Empty;
    public int Contribution { get; set; }
    public int ListInProfile { get; set; } = 1;
    public int AcceptNotices { get; set; } = 1;
    public string AccessToken { get; set; } = String.Empty;
}
