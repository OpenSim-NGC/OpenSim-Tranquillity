/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Userdatum
{
    public string UserId { get; set; }
    public string TagId { get; set; }
    public string DataKey { get; set; }
    public string DataVal { get; set; }
}
