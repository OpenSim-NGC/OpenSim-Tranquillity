using System;
using OpenMetaverse;

namespace OpenSim.Server.RobustServer.Models;

public class AssetDataDTO
{
    public UUID AssetId;

    public AssetDataDTO(UUID AssetID)
    {
        this.AssetId = AssetID;
    }
}
