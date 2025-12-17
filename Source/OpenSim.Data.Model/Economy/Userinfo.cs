/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Economy;

/// <summary>
/// Rev.3
/// </summary>
public partial class Userinfo
{
    public string User { get; set; }
    public string Simip { get; set; }
    public string Avatar { get; set; }
    public string Pass { get; set; } = String.Empty;
    public sbyte Type { get; set; }
    public sbyte Class { get; set; }
    public string Serverurl { get; set; } = String.Empty;
}
