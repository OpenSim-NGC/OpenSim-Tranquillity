using Autofac;
using OpenSim.Groups;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Addons.Groups
{
    public class GroupsAddonModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<GroupsMessagingModule>()
                .Named<ISharedRegionModule>("GroupsMessagingModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsModule>()
                .Named<ISharedRegionModule>("GroupsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsServiceHGConnectorModule>()
                .Named<ISharedRegionModule>("GroupsServiceHGConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsServiceLocalConnectorModule>()
                .Named<ISharedRegionModule>("GroupsServiceLocalConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsServiceRemoteConnectorModule>()
                .Named<ISharedRegionModule>("GroupsServiceRemoteConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}