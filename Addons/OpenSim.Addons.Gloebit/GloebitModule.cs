using Autofac;
using Gloebit.GloebitMoneyModule;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Addons.Gloebit
{
    public class GloebitModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<GloebitMoneyModule>()
            .Named<ISharedRegionModule>("GloebitMoneyModule")
            .AsImplementedInterfaces()
            .SingleInstance();
        }
    }
}
