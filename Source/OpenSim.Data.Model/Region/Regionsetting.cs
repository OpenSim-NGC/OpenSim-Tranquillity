/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Region;

public partial class Regionsetting
{
    public string RegionUuid { get; set; }
    public int BlockTerraform { get; set; }
    public int BlockFly { get; set; }
    public int AllowDamage { get; set; }
    public int RestrictPushing { get; set; }
    public int AllowLandResell { get; set; }
    public int AllowLandJoinDivide { get; set; }
    public int BlockShowInSearch { get; set; }
    public int AgentLimit { get; set; }
    public double ObjectBonus { get; set; }
    public int Maturity { get; set; }
    public int DisableScripts { get; set; }
    public int DisableCollisions { get; set; }
    public int DisablePhysics { get; set; }
    public string TerrainTexture1 { get; set; }
    public string TerrainTexture2 { get; set; }
    public string TerrainTexture3 { get; set; }
    public string TerrainTexture4 { get; set; }
    public double Elevation1Nw { get; set; }
    public double Elevation2Nw { get; set; }
    public double Elevation1Ne { get; set; }
    public double Elevation2Ne { get; set; }
    public double Elevation1Se { get; set; }
    public double Elevation2Se { get; set; }
    public double Elevation1Sw { get; set; }
    public double Elevation2Sw { get; set; }
    public double WaterHeight { get; set; }
    public double TerrainRaiseLimit { get; set; }
    public double TerrainLowerLimit { get; set; }
    public int UseEstateSun { get; set; }
    public int FixedSun { get; set; }
    public double SunPosition { get; set; }
    public string Covenant { get; set; }
    public sbyte Sandbox { get; set; }
    public double Sunvectorx { get; set; }
    public double Sunvectory { get; set; }
    public double Sunvectorz { get; set; }
    public string LoadedCreationId { get; set; }
    public uint LoadedCreationDatetime { get; set; }
    public string MapTileId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string TelehubObject { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string ParcelTileId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public uint CovenantDatetime { get; set; }
    public sbyte BlockSearch { get; set; }
    public sbyte Casino { get; set; }
    public string CacheId { get; set; }
    public string TerrainPbr1 { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string TerrainPbr2 { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string TerrainPbr3 { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string TerrainPbr4 { get; set; } = "00000000-0000-0000-0000-000000000000";
}
