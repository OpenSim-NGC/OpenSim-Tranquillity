using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Addons.Search;

public class OpenSimSearchModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<OpenSearchModule>()
            .Named<ISharedRegionModule>("OpenSimSearch")
            .AsImplementedInterfaces()
            .SingleInstance();
    }
}
