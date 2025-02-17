using Autofac;
using OpenSim.Region.Framework.Interfaces;

using OpenSim.Region.CoreModules.Agent.AssetTransaction;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.CoreModules.World.Wind.Plugins;
using OpenSim.Region.CoreModules.Agent.IPBan;
using OpenSim.Region.CoreModules.Agent.TextureSender;
using OpenSim.Region.CoreModules.Agent.Xfer;
using OpenSim.Region.CoreModules.Asset;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.BakedTextures;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Avatar.Combat.CombatModule;
using OpenSim.Region.CoreModules.Avatars.Commands;
using OpenSim.Region.CoreModules.Avatar.Dialog;
using OpenSim.Region.CoreModules.Avatar.Friends;
using OpenSim.Region.CoreModules.Avatar.Gestures;
using OpenSim.Region.CoreModules.Avatar.Gods;
using OpenSim.Region.CoreModules.Avatar.Groups;
using OpenSim.Region.CoreModules.Avatar.InstantMessage;
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.CoreModules.Avatar.Inventory.Transfer;
using OpenSim.Region.CoreModules.Avatar.Lure;
using OpenSim.Region.CoreModules.Avatar.Profile;
using OpenSim.Region.CoreModules.Avatar.UserProfiles;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.DynamicAttributes;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.Library;
using OpenSim.Region.CoreModules.Framework.Monitoring;
using OpenSim.Region.CoreModules.Framework.Search;
using OpenSim.Region.CoreModules.Framework.ServiceThrottle;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.CoreModules.Scripting.DynamicTexture;
using OpenSim.Region.CoreModules.Scripting.EmailModules;
using OpenSim.Region.CoreModules.Scripting.HttpRequest;
using OpenSim.Region.CoreModules.Scripting.LoadImageURL;
using OpenSim.Region.CoreModules.Scripting.LSLHttp;
using OpenSim.Region.CoreModules.Scripting.ScriptModuleComms;
using OpenSim.Region.CoreModules.Scripting.VectorRender;
using OpenSim.Region.CoreModules.Scripting.WorldComm;
using OpenSim.Region.CoreModules.Scripting.XMLRPC;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Asset;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Authentication;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Grid;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Hypergrid;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Inventory;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Land;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Login;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.MapImage;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Neighbour;
using OpenSim.Region.CoreModules.ServiceConnectorsIn.Simulation;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Profile;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.AgentPreferences;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Authentication;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Authorization;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Avatar;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Grid;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.GridUser;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Land;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.MapImage;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.MuteList;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Neighbour;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Presence;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAccounts;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAliases;
using OpenSim.Region.CoreModules.World;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.CoreModules.World.LegacyMap;
using OpenSim.Region.CoreModules.World.LightShare;
using OpenSim.Region.CoreModules.World.Media.Moap;
using OpenSim.Region.CoreModules.World.Objects.BuySell;
using OpenSim.Region.CoreModules.World.Objects.Commands;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.CoreModules.World.Region;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.CoreModules.World.Sound;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.CoreModules.World.Vegetation;
using OpenSim.Region.CoreModules.World.Warp3DMap;
using OpenSim.Region.CoreModules.World.WorldMap;

namespace OpenSim.Region.CoreModules
{
    public class CoreModulesModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<AssetTransactionModule>()
                .Named<INonSharedRegionModule>("AssetTransactionModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<IPBanModule>()
                .Named<ISharedRegionModule>("IPBanModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<J2KDecoderModule>()
                .Named<ISharedRegionModule>("J2KDecoderModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<XferModule>()
                .Named<INonSharedRegionModule>("XferModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<FlotsamAssetCache>()
                .Named<ISharedRegionModule>("FlotsamAssetCache")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AttachmentsModule>()
                .Named<INonSharedRegionModule>("AttachmentsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AvatarFactoryModule>()
                .Named<INonSharedRegionModule>("AvatarFactoryModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<XBakesModule>()
                .Named<INonSharedRegionModule>("XBakesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ChatModule>()
                .Named<ISharedRegionModule>("ChatModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<CombatModule>()
                .Named<ISharedRegionModule>("CombatModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<UserCommandsModule>()
                .Named<ISharedRegionModule>("UserCommandsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DialogModule>()
                .Named<INonSharedRegionModule>("DialogModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<CallingCardModule>()
                .Named<ISharedRegionModule>("CallingCardModule")
                .AsImplementedInterfaces()
                .SingleInstance();
    
            builder.RegisterType<FriendsModule>()
                .Named<ISharedRegionModule>("FriendsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGFriendsModule>()
                .Named<ISharedRegionModule>("DialogMHGFriendsModuleodule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GesturesModule>()
                .Named<INonSharedRegionModule>("GesturesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GodsModule>()
                .Named<INonSharedRegionModule>("GodsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GroupsModule>()
                .Named<ISharedRegionModule>("GroupsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGMessageTransferModule>()
                .Named<ISharedRegionModule>("HGMessageTransferModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<InstantMessageModule>()
                .Named<ISharedRegionModule>("InstantMessageModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MessageTransferModule>()
                .Named<ISharedRegionModule>("MessageTransferModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MuteListModule>()
                .Named<ISharedRegionModule>("MuteListModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<OfflineMessageModule>()
                .Named<ISharedRegionModule>("OfflineMessageModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<PresenceModule>()
                .Named<ISharedRegionModule>("PresenceModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<InventoryArchiverModule>()
                .Named<ISharedRegionModule>("InventoryArchiverModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<InventoryTransferModule>()
                .Named<ISharedRegionModule>("InventoryTransferModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGLureModule>()
                .Named<ISharedRegionModule>("HGLureModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LureModule>()
                .Named<ISharedRegionModule>("LureModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<BasicProfileModule>()
                .Named<ISharedRegionModule>("BasicProfileModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<UserProfileModule>()
                .Named<INonSharedRegionModule>("UserProfilesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<CapabilitiesModule>()
                .Named<INonSharedRegionModule>("CapabilitiesModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DAExampleModule>()
                .Named<INonSharedRegionModule>("DAExampleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DOExampleModule>()
                .Named<INonSharedRegionModule>("DOExampleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DOExampleModule>()
                .Named<INonSharedRegionModule>("DOExampleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DOExampleModule>()
                .Named<INonSharedRegionModule>("DOExampleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DOExampleModule>()
                .Named<INonSharedRegionModule>("DOExampleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DOExampleModule>()
                .Named<INonSharedRegionModule>("DOExampleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EntityTransferModule>()
                .Named<INonSharedRegionModule>("EntityTransferModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGEntityTransferModule>()
                .Named<INonSharedRegionModule>("HGEntityTransferModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGInventoryAccessModule>()
                .Named<INonSharedRegionModule>("HGInventoryAccessModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<BasicInventoryAccessModule>()
                .Named<INonSharedRegionModule>("BasicInventoryAccessModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LibraryModule>()
                .Named<ISharedRegionModule>("LibraryModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MonitorModule>()
                .Named<INonSharedRegionModule>("MonitorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<BasicSearchModule>()
                .Named<ISharedRegionModule>("BasicSearchModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ServiceThrottleModule>()
                .Named<ISharedRegionModule>("ServiceThrottleModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGUserManagementModule>()
                .Named<ISharedRegionModule>("HGUserManagementModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<UserManagementModule>()
                .Named<ISharedRegionModule>("UserManagementModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DynamicTextureModule>()
                .Named<ISharedRegionModule>("DynamicTextureModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EmailModule>()
                .Named<ISharedRegionModule>("EmailModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HttpRequestModule>()
                .Named<INonSharedRegionModule>("HttpRequestModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LoadImageURLModule>()
                .Named<ISharedRegionModule>("LoadImageURLModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<UrlModule>()
                .Named<ISharedRegionModule>("UrlModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ScriptModuleCommsModule>()
                .Named<INonSharedRegionModule>("ScriptModuleCommsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<VectorRenderModule>()
                .Named<ISharedRegionModule>("VectorRenderModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<WorldCommModule>()
                .Named<INonSharedRegionModule>("WorldCommModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<XMLRPCModule>()
                .Named<ISharedRegionModule>("XMLRPCModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AssetServiceInConnectorModule>()
                .Named<ISharedRegionModule>("AssetServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AuthenticationServiceInConnectorModule>()
                .Named<ISharedRegionModule>("AuthenticationServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<GridInfoServiceInConnectorModule>()
                .Named<ISharedRegionModule>("GridInfoServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HypergridServiceInConnectorModule>()
                .Named<ISharedRegionModule>("HypergridServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<InventoryServiceInConnectorModule>()
                .Named<ISharedRegionModule>("InventoryServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LandServiceInConnectorModule>()
                .Named<ISharedRegionModule>("LandServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LLLoginServiceInConnectorModule>()
                .Named<ISharedRegionModule>("LLLoginServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MapImageServiceInConnectorModule>()
                .Named<ISharedRegionModule>("MapImageServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<NeighbourServiceInConnectorModule>()
                .Named<ISharedRegionModule>("NeighbourServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SimulationServiceInConnectorModule>()
                .Named<ISharedRegionModule>("SimulationServiceInConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();
            
            builder.RegisterType<LocalUserProfilesServicesConnector>()
                .Named<ISharedRegionModule>("LocalUserProfilesServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();
            
            builder.RegisterType<LocalAgentPreferencesServicesConnector>()
                .Named<ISharedRegionModule>("LocalAgentPreferencesServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteAgentPreferencesServicesConnector>()
                .Named<ISharedRegionModule>("RemoteAgentPreferencesServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalAssetServicesConnector>()
                .Named<ISharedRegionModule>("LocalAssetServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RegionAssetConnector>()
                .Named<ISharedRegionModule>("RegionAssetConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalAuthenticationServicesConnector>()
                .Named<ISharedRegionModule>("LocalAuthenticationServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteAuthenticationServicesConnector>()
                .Named<ISharedRegionModule>("RemoteAuthenticationServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalAuthorizationServicesConnector>()
                .Named<INonSharedRegionModule>("LocalAuthorizationServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteAuthorizationServicesConnector>()
                .Named<ISharedRegionModule>("RemoteAuthorizationServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalAvatarServicesConnector>()
                .Named<ISharedRegionModule>("LocalAvatarServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteAvatarServicesConnector>()
                .Named<ISharedRegionModule>("RemoteAvatarServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RegionGridServicesConnector>()
                .Named<ISharedRegionModule>("RegionGridServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalGridUserServicesConnector>()
                .Named<ISharedRegionModule>("LocalGridUserServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteGridUserServicesConnector>()
                .Named<ISharedRegionModule>("RemoteGridUserServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGInventoryBroker>()
                .Named<ISharedRegionModule>("HGInventoryBroker")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalInventoryServicesConnector>()
                .Named<ISharedRegionModule>("LocalInventoryServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteXInventoryServicesConnector>()
                .Named<ISharedRegionModule>("RemoteXInventoryServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalLandServicesConnector>()
                .Named<ISharedRegionModule>("LocalLandServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteLandServicesConnector>()
                .Named<ISharedRegionModule>("RemoteLandServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MapImageServiceModule>()
                .Named<ISharedRegionModule>("MapImageServiceModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalMuteListServicesConnector>()
                .Named<ISharedRegionModule>("LocalMuteListServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteMuteListServicesConnector>()
                .Named<ISharedRegionModule>("RemoteMuteListServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<NeighbourServicesOutConnector>()
                .Named<ISharedRegionModule>("NeighbourServicesOutConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalPresenceServicesConnector>()
                .Named<ISharedRegionModule>("LocalPresenceServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemotePresenceServicesConnector>()
                .Named<ISharedRegionModule>("RemotePresenceServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalSimulationConnectorModule>()
                .Named<ISharedRegionModule>("LocalSimulationConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteSimulationConnectorModule>()
                .Named<ISharedRegionModule>("RemoteSimulationConnectorModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalUserAccountServicesConnector>()
                .Named<ISharedRegionModule>("LocalUserAccountServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteUserAccountServicesConnector>()
                .Named<ISharedRegionModule>("RemoteUserAccountServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LocalUserAliasServicesConnector>()
                .Named<ISharedRegionModule>("LocalUserAliasServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RemoteUserAliasServicesConnector>()
                .Named<ISharedRegionModule>("RemoteUserAliasServicesConnector")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<AccessModule>()
                .Named<ISharedRegionModule>("AccessModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ArchiverModule>()
                .Named<INonSharedRegionModule>("ArchiverModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EstateManagementModule>()
                .Named<INonSharedRegionModule>("EstateManagementModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EstateModule>()
                .Named<ISharedRegionModule>("EstateModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DefaultDwellModule>()
                .Named<INonSharedRegionModule>("DefaultDwellModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<LandManagementModule>()
                .Named<INonSharedRegionModule>("LandManagementModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<PrimCountModule>()
                .Named<INonSharedRegionModule>("PrimCountModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MapImageModule>()
                .Named<INonSharedRegionModule>("MapImageModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<EnvironmentModule>()
                .Named<INonSharedRegionModule>("EnvironmentModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MoapModule>()
                .Named<INonSharedRegionModule>("MoapModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<BuySellModule>()
                .Named<INonSharedRegionModule>("BuySellModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<ObjectCommandsModule>()
                .Named<INonSharedRegionModule>("ObjectCommandsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<DefaultPermissionsModule>()
                .Named<INonSharedRegionModule>("DefaultPermissionsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RegionCommandsModule>()
                .Named<INonSharedRegionModule>("RegionCommandsModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<RestartModule>()
                .Named<INonSharedRegionModule>("RestartModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SerialiserModule>()
                .Named<ISharedRegionModule>("SerialiserModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SoundModule>()
                .Named<INonSharedRegionModule>("SoundModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<TerrainModule>()
                .Named<INonSharedRegionModule>("TerrainModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<VegetationModule>()
                .Named<INonSharedRegionModule>("VegetationModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<Warp3DImageModule>()
                .Named<INonSharedRegionModule>("Warp3DImageModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<WindModule>()
                .Named<INonSharedRegionModule>("WindModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<HGWorldMapModule>()
                .Named<INonSharedRegionModule>("HGWorldMapModule")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<MapSearchModule>()
                .Named<ISharedRegionModule>("MapSearchModule")
                .AsImplementedInterfaces()
                .SingleInstance();
  
            builder.RegisterType<WorldMapModule>()
                .Named<INonSharedRegionModule>("WorldMapModule")
                .AsImplementedInterfaces()
                .SingleInstance();


            builder.RegisterType<ConfigurableWind>()
                .Named<IWindModelPlugin>("ConfigurableWind")
                .AsImplementedInterfaces()
                .SingleInstance();

            builder.RegisterType<SimpleRandomWind>()
                .Named<IWindModelPlugin>("SimpleRandomWind")
                .AsImplementedInterfaces()
                .SingleInstance();
        }
    }
}
