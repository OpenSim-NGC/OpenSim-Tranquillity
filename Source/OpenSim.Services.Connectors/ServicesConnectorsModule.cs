using Autofac;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.Connectors;

public class ServicesConnectorsModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<AssetServicesConnector>()
            .Named<IAssetService>("AssetServiceConnector")
            .AsImplementedInterfaces()
            .SingleInstance();
        
        builder.RegisterType<XInventoryServicesConnector>()
            .Named<IInventoryService>("XInventoryServiceConnector")
            .AsImplementedInterfaces()
            .SingleInstance();
    }
}