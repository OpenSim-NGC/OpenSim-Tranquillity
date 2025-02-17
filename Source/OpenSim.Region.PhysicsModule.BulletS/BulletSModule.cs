using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    public class BulletSModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<BSScene>()
                .Named<INonSharedRegionModule>("BulletSPhysicsScene")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ExtendedPhysics>()
                .Named<INonSharedRegionModule>("ExtendedPhysics")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
