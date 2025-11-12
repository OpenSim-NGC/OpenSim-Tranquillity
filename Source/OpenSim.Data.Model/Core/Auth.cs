using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Auth
{
    public string Uuid { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string WebLoginKey { get; set; } = string.Empty;
    public string AccountType { get; set; } = "UserAccount";
}
