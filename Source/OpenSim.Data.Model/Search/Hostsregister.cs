/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Search;

public partial class Hostsregister
{
    public string Host { get; set; }
    public int Port { get; set; }
    public int Register { get; set; }
    public int Nextcheck { get; set; }
    public bool Checked { get; set; }
    public int Failcounter { get; set; }
    public string GatekeeperUrl { get; set; }
}
