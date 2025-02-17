using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.POS
{
    public class POSModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<POSScene>()
                .Named<INonSharedRegionModule>("POSPhysicsScene")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
