/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Autofac;

using OpenSim.Server.Handlers.Base;
using OpenSim.Server.Handlers.AgentPreferences;
using OpenSim.Server.Handlers.Asset;
using OpenSim.Server.Handlers.Authentication;
using OpenSim.Server.Handlers.Authorization;
using OpenSim.Server.Handlers.Avatar;
using OpenSim.Server.Handlers.BakedTextures;
using OpenSim.Server.Handlers.Freeswitch;
using OpenSim.Server.Handlers.Friends;
using OpenSim.Server.Handlers.Grid;
using OpenSim.Server.Handlers.GridUser;
using OpenSim.Server.Handlers.Hypergrid;
using OpenSim.Server.Handlers.Inventory;
using OpenSim.Server.Handlers.Login;
using OpenSim.Server.Handlers.MapImage;
using OpenSim.Server.Handlers.Presence;
using OpenSim.Server.Handlers.Profiles;
using OpenSim.Server.Handlers.UserAccounts;
using OpenSim.Server.Handlers.UserAlias;

namespace OpenSim.Server.Handlers
{
    public class OpenSimServerHandlersModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<AgentPreferencesServiceConnector>()
                .Named<IServiceConnector>("AgentPreferencesService")
                .AsImplementedInterfaces();

            builder.RegisterType<AssetServiceHandler>()
                .Named<IServiceConnector>("AssetServiceConnector")
                .AsImplementedInterfaces();

            builder.RegisterType<AuthenticationServiceConnector>()
                .Named<IServiceConnector>("AuthenticationServiceConnector")
                .AsImplementedInterfaces();

            builder.RegisterType<OpenIdServerConnector>()
                .Named<IServiceConnector>("OpenIdService")
                .AsImplementedInterfaces();

            builder.RegisterType<AuthorizationServerConnector>()
                .Named<IServiceConnector>("AuthorizationService")
                .AsImplementedInterfaces();

            builder.RegisterType<AvatarServiceConnector>()
                .Named<IServiceConnector>("AvatarService")
                .AsImplementedInterfaces();

            builder.RegisterType<XBakesConnector>()
                .Named<IServiceConnector>("BakedTextureService")
                .AsImplementedInterfaces();

            builder.RegisterType<EstateDataRobustConnector>()
                .Named<IServiceConnector>("EstateService")
                .AsImplementedInterfaces();

            builder.RegisterType<FreeswitchServerConnector>()
                .Named<IServiceConnector>("FreeswitchService")
                .AsImplementedInterfaces();

            builder.RegisterType<FriendsServiceConnector>()
                .Named<IServiceConnector>("GridInfoServerInConnector")
                .AsImplementedInterfaces();

            builder.RegisterType<GridInfoServerInConnector>()
                .Named<IServiceConnector>("GridInfoService")
                .AsImplementedInterfaces();

            builder.RegisterType<GridServiceConnector>()
                .Named<IServiceConnector>("GridServiceConnector")
                .AsImplementedInterfaces();

            builder.RegisterType<GridUserServiceConnector>()
                .Named<IServiceConnector>("GridUserService")
                .AsImplementedInterfaces();

            builder.RegisterType<GatekeeperServiceInConnector>()
                .Named<IServiceConnector>("GatekeeperService")
                .AsImplementedInterfaces();
                
            builder.RegisterType<HeloServiceInConnector>()
                .Named<IServiceConnector>("HeloService")
                .AsImplementedInterfaces();
                
            builder.RegisterType<HGFriendsServerConnector>()
                .Named<IServiceConnector>("HGFriendsService")
                .AsImplementedInterfaces();
                
            builder.RegisterType<LLLoginServiceInConnector>()
                .Named<IServiceConnector>("LoginService")
                .AsImplementedInterfaces();
                
            builder.RegisterType<UserAgentServerConnector>()
                .Named<IServiceConnector>("UserAgentService")
                .AsImplementedInterfaces();

            builder.RegisterType<InventoryServiceInConnector>()
                .Named<IServiceConnector>("InventoryService")
                .AsImplementedInterfaces();

            builder.RegisterType<XInventoryInConnector>()
                .Named<IServiceConnector>("XInventoryInConnector")
                .AsImplementedInterfaces();

            // builder.RegisterType<LandServiceInConnector>()
            //     .Named<IServiceConnector>("LandService")
            //     .AsImplementedInterfaces();

            // builder.RegisterType<LLLoginServiceInConnector>()
            //     .Named<IServiceConnector>("LoginService")
            //     .AsImplementedInterfaces();

            builder.RegisterType<MapAddServiceConnector>()
                .Named<IServiceConnector>("MapImageService")
                .AsImplementedInterfaces();

            builder.RegisterType<MapGetServiceConnector>()
                .Named<IServiceConnector>("MapImageService")
                .AsImplementedInterfaces();

            builder.RegisterType<MapRemoveServiceConnector>()
                .Named<IServiceConnector>("MapImageService")
                .AsImplementedInterfaces();

            builder.RegisterType<MuteListServiceConnector>()
                .Named<IServiceConnector>("MuteListService")
                .AsImplementedInterfaces();

            // builder.RegisterType<NeighbourServiceInConnector>()
            //     .Named<IServiceConnector>("NeighbourService")
            //     .AsImplementedInterfaces();

            builder.RegisterType<PresenceServiceConnector>()
                .Named<IServiceConnector>("PresenceService")
                .AsImplementedInterfaces();

            builder.RegisterType<UserProfilesConnector>()
                .Named<IServiceConnector>("UserProfilesService")
                .AsImplementedInterfaces();

            // builder.RegisterType<SimulationServiceInConnector>()
            //     .Named<IServiceConnector>("SimulationService")
            //     .AsImplementedInterfaces();

            builder.RegisterType<UserAccountServiceConnector>()
                .Named<IServiceConnector>("UserAccountService")
                .AsImplementedInterfaces();

            builder.RegisterType<UserAliasServiceConnector>()
                .Named<IServiceConnector>("UserAliasService")
                .AsImplementedInterfaces();
        }
    }
}
