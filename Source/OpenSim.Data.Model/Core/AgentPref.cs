using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class AgentPref
{
    public string PrincipalId { get; set; }
    public string AccessPrefs { get; set; } = "M";
    public double HoverHeight { get; set; }
    public string Language { get; set; } = "en-us";
    public bool? LanguageIsPublic { get; set; } = true;
    public int PermEveryone { get; set; }
    public int PermGroup { get; set; }
    public int PermNextOwner { get; set; } = 532480;
}
