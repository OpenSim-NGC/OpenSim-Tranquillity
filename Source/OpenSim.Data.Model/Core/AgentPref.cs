/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

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
