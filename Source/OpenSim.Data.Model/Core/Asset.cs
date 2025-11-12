using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class Asset
{
    public string Name { get; set; }
    public string Description { get; set; }
    public sbyte AssetType { get; set; }
    public bool Local { get; set; }
    public bool Temporary { get; set; }
    public byte[] Data { get; set; }
    public string AssetId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public int? CreateTime { get; set; } = 0;
    public int? AccessTime { get; set; } = 0;
    public int AssetFlags { get; set; }
    public string CreatorId { get; set; } = String.Empty;
}
