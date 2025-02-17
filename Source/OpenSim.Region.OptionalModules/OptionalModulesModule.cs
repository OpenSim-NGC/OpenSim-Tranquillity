using Autofac;
using OpenSim.Region.Framework.Interfaces;

using OpenSim.Region.OptionalModules.Agent.InternetRelayClientView;
using OpenSim.Region.OptionalModules.Agent.TextureSender;
using OpenSim.Region.OptionalModules.Agent.UDP.Linden;
using OpenSim.Region.OptionalModules.Asset;
using OpenSim.Region.OptionalModules.Avatar.Animations;
using OpenSim.Region.OptionalModules.Avatar.Appearance;
using OpenSim.Region.OptionalModules.Avatar.Chat;
using OpenSim.Region.OptionalModules.Avatar.Concierge;
using OpenSim.Region.OptionalModules.Avatar.Friends;
using OpenSim.Region.OptionalModules.Avatar.SitStand;
using OpenSim.Region.OptionalModules.Avatar.Voice.FreeSwitchVoice;
using OpenSim.Region.OptionalModules.Avatar.Voice.VivoxVoice;
using OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups;
using OpenSim.Region.OptionalModules.DataSnapshot;
using OpenSim.Region.OptionalModules.Framework.Monitoring;
using OpenSim.Region.OptionalModules.Materials;
using OpenSim.Region.OptionalModules.Scripting.JsonStore;
using OpenSim.Region.OptionalModules.Scripting.RegionReady;
using OpenSim.Region.OptionalModules.Scripting.XmlRpcGridRouterModule;
using OpenSim.Region.OptionalModules.Scripting.XmlRpcRouterModule;
using OpenSim.Region.OptionalModules.UserStatistics;
using OpenSim.Region.OptionalModules.ViewerSupport;
using OpenSim.Region.OptionalModules.World.AutoBackup;
using OpenSim.Region.OptionalModules.World.MoneyModule;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Region.OptionalModules.World.SceneCommands;
using OpenSim.Region.OptionalModules.World.TreePopulator;
using OpenSim.Region.OptionalModules.World.WorldView;

namespace OpenSim.Region.OptionalModules
{
    public class OptionalModulesModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<IRCStackModule>()
                .Named<INonSharedRegionModule>("IRCStackModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<J2KDecoderCommandModule>()
                .Named<ISharedRegionModule>("J2KDecoderCommandModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LindenUDPInfoModule>()
                .Named<ISharedRegionModule>("LindenUDPInfoModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AssetInfoModule>()
                .Named<ISharedRegionModule>("AssetInfoModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AnimationsCommandModule>()
                .Named<ISharedRegionModule>("AnimationsCommandModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AppearanceInfoModule>()
                .Named<ISharedRegionModule>("AppearanceInfoModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<IRCBridgeModule>()
                .Named<INonSharedRegionModule>("IRCBridgeModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ConciergeModule>()
                .Named<ISharedRegionModule>("ConciergeModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<FriendsCommandsModule>()
                .Named<ISharedRegionModule>("FriendsCommandModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SitStandCommandModule>()
                .Named<INonSharedRegionModule>("SitStandCommandModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<FreeSwitchVoiceModule>()
                .Named<ISharedRegionModule>("FreeSwitchVoiceModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<VivoxVoiceModule>()
                .Named<ISharedRegionModule>("VivoxVoiceModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsMessagingModule>()
                .Named<ISharedRegionModule>("GroupsMessagingModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsModule>()
                .Named<ISharedRegionModule>("GroupsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<XmlRpcGroupsServicesConnectorModule>()
                .Named<ISharedRegionModule>("XmlRpcGroupsServicesConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DataSnapshotManager>()
                .Named<ISharedRegionModule>("DataSnapshotManager")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EtcdMonitoringModule>()
                .Named<INonSharedRegionModule>("EtcdMonitoringModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MaterialsModule>()
                .Named<INonSharedRegionModule>("MaterialsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<PhysicsParameters.PhysicsParameters>()
                .Named<ISharedRegionModule>("PhysicsParameters")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<PrimLimits.PrimLimitsModule>()
                .Named<INonSharedRegionModule>("PrimLimitsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<JsonStoreCommandsModule>()
                .Named<INonSharedRegionModule>("JsonStoreCommandsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<JsonStoreModule>()
                .Named<INonSharedRegionModule>("JsonStoreModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<JsonStoreScriptModule>()
                .Named<INonSharedRegionModule>("JsonStoreScriptModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RegionReadyModule>()
                .Named<INonSharedRegionModule>("RegionReadyModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<XmlRpcGridRouter>()
                .Named<INonSharedRegionModule>("XmlRpcGridRouter")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<XmlRpcRouter>()
                .Named<INonSharedRegionModule>("XmlRpcRouter")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<WebStatsModule>()
                .Named<ISharedRegionModule>("WebStatsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<CameraOnlyModeModule>()
                .Named<INonSharedRegionModule>("CameraOnlyMode")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DynamicFloaterModule>()
                .Named<INonSharedRegionModule>("DynamicFloater")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DynamicMenuModule>()
                .Named<INonSharedRegionModule>("DynamicMenu")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GodNamesModule>()
                .Named<ISharedRegionModule>("GodNamesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SpecialUIModule>()
                .Named<INonSharedRegionModule>("SpecialUI")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AutoBackupModule>()
                .Named<ISharedRegionModule>("AutoBackupModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SampleMoneyModule>()
                .Named<ISharedRegionModule>("SampleMoneyModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<NPCModule>()
                .Named<ISharedRegionModule>("NPCModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SceneCommandsModule>()
                .Named<INonSharedRegionModule>("SceneCommandsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<TreePopulatorModule>()
                .Named<INonSharedRegionModule>("TreePopulatorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<WorldViewModule>()
                .Named<INonSharedRegionModule>("WorldViewModule")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
