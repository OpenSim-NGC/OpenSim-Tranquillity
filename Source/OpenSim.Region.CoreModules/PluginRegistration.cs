/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        RegisterByName(registry, "/OpenSim/RegionModules", "AssetTransactionModule", "OpenSim.Region.CoreModules.Agent.AssetTransaction.AssetTransactionModule", "AssetTransactionModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "IPBanModule", "OpenSim.Region.CoreModules.Agent.IPBan.IPBanModule", "IPBanModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "J2KDecoderModule", "OpenSim.Region.CoreModules.Agent.TextureSender.J2KDecoderModule", "J2KDecoderModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "XferModule", "OpenSim.Region.CoreModules.Agent.Xfer.XferModule", "XferModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "FlotsamAssetCache", "OpenSim.Region.CoreModules.Asset.FlotsamAssetCache", "FlotsamAssetCache");
        RegisterByName(registry, "/OpenSim/RegionModules", "AttachmentsModule", "OpenSim.Region.CoreModules.Avatar.Attachments.AttachmentsModule", "AttachmentsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AvatarFactoryModule", "OpenSim.Region.CoreModules.Avatar.AvatarFactory.AvatarFactoryModule", "AvatarFactoryModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "XBakesModule", "OpenSim.Region.CoreModules.Avatar.BakedTextures.XBakesModule", "XBakesModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ChatModule", "OpenSim.Region.CoreModules.Avatar.Chat.ChatModule", "ChatModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "CombatModule", "OpenSim.Region.CoreModules.Avatar.Combat.CombatModule.CombatModule", "CombatModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "UserCommandsModule", "OpenSim.Region.CoreModules.Avatars.Commands.UserCommandsModule", "UserCommandsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DialogModule", "OpenSim.Region.CoreModules.Avatar.Dialog.DialogModule", "DialogModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "CallingCardModule", "OpenSim.Region.CoreModules.Avatar.Friends.CallingCardModule", "CallingCardModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "FriendsModule", "OpenSim.Region.CoreModules.Avatar.Friends.FriendsModule", "FriendsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGFriendsModule", "OpenSim.Region.CoreModules.Avatar.Friends.HGFriendsModule", "HGFriendsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "GesturesModule", "OpenSim.Region.CoreModules.Avatar.Gestures.GesturesModule", "GesturesModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "GodsModule", "OpenSim.Region.CoreModules.Avatar.Gods.GodsModule", "GodsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "GroupsModule", "OpenSim.Region.CoreModules.Avatar.Groups.GroupsModule", "GroupsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGMessageTransferModule", "OpenSim.Region.CoreModules.Avatar.InstantMessage.HGMessageTransferModule", "HGMessageTransferModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "InstantMessageModule", "OpenSim.Region.CoreModules.Avatar.InstantMessage.InstantMessageModule", "InstantMessageModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MessageTransferModule", "OpenSim.Region.CoreModules.Avatar.InstantMessage.MessageTransferModule", "MessageTransferModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MuteListModule", "OpenSim.Region.CoreModules.Avatar.InstantMessage.MuteListModule", "MuteListModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "OfflineMessageModule", "OpenSim.Region.CoreModules.Avatar.InstantMessage.OfflineMessageModule", "OfflineMessageModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "PresenceModule", "OpenSim.Region.CoreModules.Avatar.InstantMessage.PresenceModule", "PresenceModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "InventoryArchiverModule", "OpenSim.Region.CoreModules.Avatar.Inventory.Archiver.InventoryArchiverModule", "InventoryArchiverModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "InventoryTransferModule", "OpenSim.Region.CoreModules.Avatar.Inventory.Transfer.InventoryTransferModule", "InventoryTransferModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGLureModule", "OpenSim.Region.CoreModules.Avatar.Lure.HGLureModule", "HGLureModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LureModule", "OpenSim.Region.CoreModules.Avatar.Lure.LureModule", "LureModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "BasicProfileModule", "OpenSim.Region.CoreModules.Avatar.Profile.BasicProfileModule", "BasicProfileModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "UserProfilesModule", "OpenSim.Region.CoreModules.Avatar.UserProfiles.UserProfileModule", "UserProfilesModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "CapabilitiesModule", "OpenSim.Region.CoreModules.Framework.CapabilitiesModule", "CapabilitiesModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DAExampleModule", "OpenSim.Region.CoreModules.Framework.DynamicAttributes.DAExampleModule", "DAExampleModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DOExampleModule", "OpenSim.Region.CoreModules.Framework.DynamicAttributes.DOExampleModule", "DOExampleModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "EntityTransferModule", "OpenSim.Region.CoreModules.Framework.EntityTransfer.EntityTransferModule", "EntityTransferModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGEntityTransferModule", "OpenSim.Region.CoreModules.Framework.EntityTransfer.HGEntityTransferModule", "HGEntityTransferModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGInventoryAccessModule", "OpenSim.Region.CoreModules.Framework.InventoryAccess.HGInventoryAccessModule", "HGInventoryAccessModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "BasicInventoryAccessModule", "OpenSim.Region.CoreModules.Framework.InventoryAccess.BasicInventoryAccessModule", "BasicInventoryAccessModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LibraryModule", "OpenSim.Region.CoreModules.Framework.Library.LibraryModule", "LibraryModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MonitorModule", "OpenSim.Region.CoreModules.Framework.Monitoring.MonitorModule", "MonitorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "BasicSearchModule", "OpenSim.Region.CoreModules.Framework.Search.BasicSearchModule", "BasicSearchModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ServiceThrottleModule", "OpenSim.Region.CoreModules.Framework.ServiceThrottle.ServiceThrottleModule", "ServiceThrottleModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGUserManagementModule", "OpenSim.Region.CoreModules.Framework.UserManagement.HGUserManagementModule", "HGUserManagementModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "UserManagementModule", "OpenSim.Region.CoreModules.Framework.UserManagement.UserManagementModule", "UserManagementModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DynamicTextureModule", "OpenSim.Region.CoreModules.Scripting.DynamicTexture.DynamicTextureModule", "DynamicTextureModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "EmailModule", "OpenSim.Region.CoreModules.Scripting.EmailModules.EmailModule", "EmailModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HttpRequestModule", "OpenSim.Region.CoreModules.Scripting.HttpRequest.HttpRequestModule", "HttpRequestModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LoadImageURLModule", "OpenSim.Region.CoreModules.Scripting.LoadImageURL.LoadImageURLModule", "LoadImageURLModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "UrlModule", "OpenSim.Region.CoreModules.Scripting.LSLHttp.UrlModule", "UrlModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ScriptModuleCommsModule", "OpenSim.Region.CoreModules.Scripting.ScriptModuleComms.ScriptModuleCommsModule", "ScriptModuleCommsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "VectorRenderModule", "OpenSim.Region.CoreModules.Scripting.VectorRender.VectorRenderModule", "VectorRenderModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "WorldCommModule", "OpenSim.Region.CoreModules.Scripting.WorldComm.WorldCommModule", "WorldCommModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "XMLRPCModule", "OpenSim.Region.CoreModules.Scripting.XMLRPC.XMLRPCModule", "XMLRPCModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AssetServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Asset.AssetServiceInConnectorModule", "AssetServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "AuthenticationServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Authentication.AuthenticationServiceInConnectorModule", "AuthenticationServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "GridInfoServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Grid.GridInfoServiceInConnectorModule", "GridInfoServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "HypergridServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Hypergrid.HypergridServiceInConnectorModule", "HypergridServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "InventoryServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Inventory.InventoryServiceInConnectorModule", "InventoryServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LandServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Land.LandServiceInConnectorModule", "LandServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LLLoginServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Login.LLLoginServiceInConnectorModule", "LLLoginServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MapImageServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.MapImage.MapImageServiceInConnectorModule", "MapImageServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "NeighbourServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Neighbour.NeighbourServiceInConnectorModule", "NeighbourServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "SimulationServiceInConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsIn.Simulation.SimulationServiceInConnectorModule", "SimulationServiceInConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalUserProfilesServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Profile.LocalUserProfilesServicesConnector", "LocalUserProfilesServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalAgentPreferencesServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.AgentPreferences.LocalAgentPreferencesServicesConnector", "LocalAgentPreferencesServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteAgentPreferencesServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.AgentPreferences.RemoteAgentPreferencesServicesConnector", "RemoteAgentPreferencesServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalAssetServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset.LocalAssetServicesConnector", "LocalAssetServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RegionAssetConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset.RegionAssetConnector", "RegionAssetConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalAuthenticationServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Authentication.LocalAuthenticationServicesConnector", "LocalAuthenticationServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteAuthenticationServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Authentication.RemoteAuthenticationServicesConnector", "RemoteAuthenticationServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalAuthorizationServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Authorization.LocalAuthorizationServicesConnector", "LocalAuthorizationServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteAuthorizationServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Authorization.RemoteAuthorizationServicesConnector", "RemoteAuthorizationServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalAvatarServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Avatar.LocalAvatarServicesConnector", "LocalAvatarServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteAvatarServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Avatar.RemoteAvatarServicesConnector", "RemoteAvatarServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalExperienceServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Experience.LocalExperienceServicesConnector", "LocalExperienceServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteExperienceServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Experience.RemoteExperienceServicesConnector", "RemoteExperienceServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RegionGridServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Grid.RegionGridServicesConnector", "RegionGridServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalGridUserServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.GridUser.LocalGridUserServicesConnector", "LocalGridUserServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteGridUserServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.GridUser.RemoteGridUserServicesConnector", "RemoteGridUserServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGInventoryBroker", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory.HGInventoryBroker", "HGInventoryBroker");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalInventoryServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory.LocalInventoryServicesConnector", "LocalInventoryServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteXInventoryServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory.RemoteXInventoryServicesConnector", "RemoteXInventoryServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalLandServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Land.LocalLandServicesConnector", "LocalLandServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteLandServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Land.RemoteLandServicesConnector", "RemoteLandServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "MapImageServiceModule", "OpenSim.Region.CoreModules.ServiceConnectorsOut.MapImage.MapImageServiceModule", "MapImageServiceModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalMuteListServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.MuteList.LocalMuteListServicesConnector", "LocalMuteListServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteMuteListServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.MuteList.RemoteMuteListServicesConnector", "RemoteMuteListServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "NeighbourServicesOutConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Neighbour.NeighbourServicesOutConnector", "NeighbourServicesOutConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalPresenceServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Presence.LocalPresenceServicesConnector", "LocalPresenceServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemotePresenceServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Presence.RemotePresenceServicesConnector", "RemotePresenceServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalSimulationConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation.LocalSimulationConnectorModule", "LocalSimulationConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteSimulationConnectorModule", "OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation.RemoteSimulationConnectorModule", "RemoteSimulationConnectorModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalUserAccountServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAccounts.LocalUserAccountServicesConnector", "LocalUserAccountServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteUserAccountServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAccounts.RemoteUserAccountServicesConnector", "RemoteUserAccountServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "LocalUserAliasServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAliases.LocalUserAliasServicesConnector", "LocalUserAliasServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "RemoteUserAliasServicesConnector", "OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAliases.RemoteUserAliasServicesConnector", "RemoteUserAliasServicesConnector");
        RegisterByName(registry, "/OpenSim/RegionModules", "AccessModule", "OpenSim.Region.CoreModules.World.AccessModule", "AccessModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ArchiverModule", "OpenSim.Region.CoreModules.World.Archiver.ArchiverModule", "ArchiverModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "EstateManagementModule", "OpenSim.Region.CoreModules.World.Estate.EstateManagementModule", "EstateManagementModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "EstateModule", "OpenSim.Region.CoreModules.World.Estate.EstateModule", "EstateModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DefaultDwellModule", "OpenSim.Region.CoreModules.World.Land.DefaultDwellModule", "DefaultDwellModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "LandManagementModule", "OpenSim.Region.CoreModules.World.Land.LandManagementModule", "LandManagementModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "PrimCountModule", "OpenSim.Region.CoreModules.World.Land.PrimCountModule", "PrimCountModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MapImageModule", "OpenSim.Region.CoreModules.World.LegacyMap.MapImageModule", "MapImageModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "EnvironmentModule", "OpenSim.Region.CoreModules.World.LightShare.EnvironmentModule", "EnvironmentModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MoapModule", "OpenSim.Region.CoreModules.World.Media.Moap.MoapModule", "MoapModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "BuySellModule", "OpenSim.Region.CoreModules.World.Objects.BuySell.BuySellModule", "BuySellModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ObjectCommandsModule", "OpenSim.Region.CoreModules.World.Objects.Commands.ObjectCommandsModule", "ObjectCommandsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "DefaultPermissionsModule", "OpenSim.Region.CoreModules.World.Permissions.DefaultPermissionsModule", "DefaultPermissionsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "RegionCommandsModule", "OpenSim.Region.CoreModules.World.Objects.Commands.RegionCommandsModule", "RegionCommandsModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "RestartModule", "OpenSim.Region.CoreModules.World.Region.RestartModule", "RestartModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "SerialiserModule", "OpenSim.Region.CoreModules.World.Serialiser.SerialiserModule", "SerialiserModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "SoundModule", "OpenSim.Region.CoreModules.World.Sound.SoundModule", "SoundModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "TerrainModule", "OpenSim.Region.CoreModules.World.Terrain.TerrainModule", "TerrainModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "VegetationModule", "OpenSim.Region.CoreModules.World.Vegetation.VegetationModule", "VegetationModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "Warp3DImageModule", "OpenSim.Region.CoreModules.World.Warp3DMap.Warp3DImageModule", "Warp3DImageModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "WindModule", "OpenSim.Region.CoreModules.World.Wind.WindModule", "WindModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "ConfigurableWind", "OpenSim.Region.CoreModules.World.Wind.Plugins.ConfigurableWind", "ConfigurableWind");
        RegisterByName(registry, "/OpenSim/RegionModules", "SimpleRandomWind", "OpenSim.Region.CoreModules.World.Wind.Plugins.SimpleRandomWind", "SimpleRandomWind");
        RegisterByName(registry, "/OpenSim/RegionModules", "HGWorldMapModule", "OpenSim.Region.CoreModules.World.Land.HGWorldMapModule", "HGWorldMapModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "MapSearchModule", "OpenSim.Region.CoreModules.World.WorldMap.MapSearchModule", "MapSearchModule");
        RegisterByName(registry, "/OpenSim/RegionModules", "WorldMapModule", "OpenSim.Region.CoreModules.World.WorldMap.WorldMapModule", "WorldMapModule");
        RegisterByName(registry, "/OpenSim/WindModule", "ConfigurableWind", "OpenSim.Region.CoreModules.World.Wind.Plugins.ConfigurableWind", "ConfigurableWind");
        RegisterByName(registry, "/OpenSim/WindModule", "SimpleRandomWind", "OpenSim.Region.CoreModules.World.Wind.Plugins.SimpleRandomWind", "SimpleRandomWind");
    }

    private static void RegisterByName(PluginRegistry registry, string extensionPath, string id, string typeName, string displayName)
    {
        Assembly assembly = typeof(PluginRegistration).Assembly;
        Type type = assembly.GetType(typeName, false);
        if (type == null)
            return;

        registry.Register(
            extensionPath,
            new PluginDescriptor(id, type, displayName, "0.9"));
    }
}
