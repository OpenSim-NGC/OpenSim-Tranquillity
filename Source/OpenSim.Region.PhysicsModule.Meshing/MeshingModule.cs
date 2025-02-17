using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.Meshing
{
    public class MeshingModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<ZeroMesher>()
                .Named<INonSharedRegionModule>("ZeroMesher")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<Meshmerizer>()
                .Named<INonSharedRegionModule>("Meshmerizer")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
