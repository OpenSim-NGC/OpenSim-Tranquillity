using Autofac;
using OpenSim.Region.Framework.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    public  class LindenUDPModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<LLUDPServerShim>()
                .Named<INonSharedRegionModule>("LLUDPServerShim")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
