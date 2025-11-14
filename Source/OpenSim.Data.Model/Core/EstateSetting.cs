/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class EstateSetting
{
    public uint EstateId { get; set; }
    public string EstateName { get; set; }
    public sbyte AbuseEmailToEstateOwner { get; set; }
    public sbyte DenyAnonymous { get; set; }
    public sbyte ResetHomeOnTeleport { get; set; }
    public sbyte FixedSun { get; set; }
    public sbyte DenyTransacted { get; set; }
    public sbyte BlockDwell { get; set; }
    public sbyte DenyIdentified { get; set; }
    public sbyte AllowVoice { get; set; }
    public sbyte UseGlobalTime { get; set; }
    public int PricePerMeter { get; set; }
    public sbyte TaxFree { get; set; }
    public sbyte AllowDirectTeleport { get; set; }
    public int RedirectGridX { get; set; }
    public int RedirectGridY { get; set; }
    public uint ParentEstateId { get; set; }
    public double SunPosition { get; set; }
    public sbyte EstateSkipScripts { get; set; }
    public float BillableFactor { get; set; }
    public sbyte PublicAccess { get; set; }
    public string AbuseEmail { get; set; }
    public string EstateOwner { get; set; }
    public sbyte DenyMinors { get; set; }
    public sbyte AllowLandmark { get; set; } = 1;
    public sbyte AllowParcelChanges { get; set; } = 1;
    public sbyte AllowSetHome { get; set; } = 1;
    public sbyte AllowEnviromentOverride { get; set; }
}
