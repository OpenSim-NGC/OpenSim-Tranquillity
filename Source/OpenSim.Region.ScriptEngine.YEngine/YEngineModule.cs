using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.ScriptEngine.Yengine
{
    public class YEngineModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<Yengine>()
                .Named<INonSharedRegionModule>("YEngine")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
