using Autofac;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModule.ubODEMeshing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSim.Region.PhysicsModule.ubOdeMeshing
{
    public class ubOdePhysicsMeshingModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<ubMeshmerizer>()
                .Named<INonSharedRegionModule>("ubODEMeshmerizer")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
