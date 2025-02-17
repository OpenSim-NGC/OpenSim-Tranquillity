using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.BasicPhysics
{
    public class BasicPhysicsModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<BasicScene>()
                .Named<INonSharedRegionModule>("BasicPhysicsScene")
                .AsImplementedInterfaces()
                .SingleInstance();  
        }
    }
}
