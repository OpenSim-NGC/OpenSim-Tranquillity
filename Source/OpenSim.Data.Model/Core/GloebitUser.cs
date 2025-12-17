/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class GloebitUser
{
    public string AppKey { get; set; }
    public string PrincipalId { get; set; }
    public string GloebitId { get; set; }
    public string GloebitToken { get; set; }
    public string LastSessionId { get; set; }
}
