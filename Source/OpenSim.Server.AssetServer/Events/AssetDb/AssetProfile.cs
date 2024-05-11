using AutoMapper;

using OpenSim.Data.Model.Core;
using OpenSim.Server.AssetServer.Models;

namespace OpenSim.GridServices.AssetService.Events.AssetDb
{
    public class AssetProfile : Profile
    {
        public AssetProfile()
        {
            CreateMap<Asset, AssetDto>();
        }
    }
}

