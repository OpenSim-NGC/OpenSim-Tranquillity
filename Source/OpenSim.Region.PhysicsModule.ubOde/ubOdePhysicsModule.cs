using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    public class ubOdePhysicsModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<ubOdeModule>()
                .Named<INonSharedRegionModule>("ubODEPhysicsScene")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
