/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

namespace OpenSim.Data.Model.Core;

public partial class Region
{
    public string Uuid { get; set; }
    public ulong RegionHandle { get; set; }
    public string RegionName { get; set; }
    public string RegionRecvKey { get; set; }
    public string RegionSendKey { get; set; }
    public string RegionSecret { get; set; }
    public string RegionDataUri { get; set; }
    public string ServerIp { get; set; }
    public uint? ServerPort { get; set; }
    public string ServerUri { get; set; }
    public uint? LocX { get; set; }
    public uint? LocY { get; set; }
    public uint? LocZ { get; set; }
    public ulong? EastOverrideHandle { get; set; }
    public ulong? WestOverrideHandle { get; set; }
    public ulong? SouthOverrideHandle { get; set; }
    public ulong? NorthOverrideHandle { get; set; }
    public string RegionAssetUri { get; set; }
    public string RegionAssetRecvKey { get; set; }
    public string RegionAssetSendKey { get; set; }
    public string RegionUserUri { get; set; }
    public string RegionUserRecvKey { get; set; }
    public string RegionUserSendKey { get; set; }
    public string RegionMapTexture { get; set; }
    public int? ServerHttpPort { get; set; }
    public int? ServerRemotingPort { get; set; }
    public string OwnerUuid { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string OriginUuid { get; set; }
    public uint? Access { get; set; } = 1;
    public string ScopeId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public int SizeX { get; set; }
    public int SizeY { get; set; }
    public int Flags { get; set; }
    public int LastSeen { get; set; }
    public string PrincipalId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string Token { get; set; }
    public string ParcelMapTexture { get; set; }
}
