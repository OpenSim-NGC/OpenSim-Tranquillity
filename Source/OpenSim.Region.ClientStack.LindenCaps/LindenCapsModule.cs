using Autofac;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.ClientStack.Linden
{
    public class LindenCapsModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<AgentPreferencesModule>()
                .Named<ISharedRegionModule>("AgentPreferencesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AvatarPickerSearchModule>()
                .Named<ISharedRegionModule>("AvatarPickerSearchModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<BunchOfCapsModule>()
                .Named<INonSharedRegionModule>("BunchOfCapsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EstateAccessCapModule>()
                .Named<INonSharedRegionModule>("EstateAccessCapModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EstateChangeInfoCapModule>()
                .Named<INonSharedRegionModule>("EstateChangeInfoCapModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<FetchInventory2Module>()
                .Named<ISharedRegionModule>("FetchInventory2Module")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<FetchLibDescModule>()
                .Named<INonSharedRegionModule>("FetchLibDescModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GetAssetsModule>()
                .Named<INonSharedRegionModule>("GetAssetsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MeshUploadFlagModule>()
                .Named<INonSharedRegionModule>("MeshUploadFlagModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ObjectAdd>()
                .Named<INonSharedRegionModule>("ObjectAdd")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<UploadObjectAssetModule>()
                .Named<INonSharedRegionModule>("UploadObjectAssetModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RegionConsoleModule>()
                .Named<INonSharedRegionModule>("RegionConsoleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ServerReleaseNotesModule>()
                .Named<ISharedRegionModule>("ServerReleaseNotesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SimulatorFeaturesModule>()
                .Named<INonSharedRegionModule>("SimulatorFeaturesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<UploadBakedTextureModule>()
                .Named<ISharedRegionModule>("UploadBakedTextureModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<WebFetchInvDescModule>()
                .Named<INonSharedRegionModule>("WebFetchInvDescModule")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
