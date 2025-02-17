using Autofac;

using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Data.MySQL;

public class MySQLDataModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<MySQLAgentPreferencesData>()
            .Named<IAgentPreferencesData>("MySQLAgentPreferencesData")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySQLAssetData>()
            .Named<IAssetDataPlugin>("MySQLAssetData")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySqlAuthenticationData>()
            .Named<IAuthenticationData>("MySqlAuthenticationData")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySQLAvatarData>()
            .Named<IAvatarData>("MySQLAvatarData")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySQLEstateStore>()
            .Named<IEstateDataStore>("MySQLEstateStore")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySqlFriendsData>()
            .Named<IFriendsData>("MySqlFriendsData")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySQLFSAssetData>()
            .Named<IFSAssetDataPlugin>("MySQLFSAssetData")
            .AsImplementedInterfaces().SingleInstance();

        builder.RegisterType<MySQLGridUserData>()
            .Named<IGridUserData>("MySQLGridUserData")
            .AsImplementedInterfaces().SingleInstance(); 

        builder.RegisterType<MySQLGroupsData>()
            .Named<IGroupsData>("MySQLGroupsData")
            .AsImplementedInterfaces().SingleInstance(); 

        builder.RegisterType<MySQLHGTravelData>()
            .Named<IHGTravelingData>("MySQLHGTravelData")
            .AsImplementedInterfaces().SingleInstance(); 

        builder.RegisterType<MySQLInventoryData>()
            .Named<IInventoryDataPlugin>("MySQLInventoryData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySqlMuteListData>()
            .Named<IMuteListData>("MySqlMuteListData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySQLOfflineIMData>()
            .Named<IOfflineIMData>("MySQLOfflineIMData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySQLPresenceData>()
            .Named<IPresenceData>("MySQLPresenceData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySqlRegionData>()
            .Named<IRegionData>("MySqlRegionData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySQLSimulationData>()
            .Named<ISimulationDataStore>("MySQLSimulationData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySqlUserAccountData>()
            .Named<IUserAccountData>("MySqlUserAccountData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySQLUserAliasData>()
            .Named<IUserAliasData>("MySQLUserAliasData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<UserProfilesData>()
            .Named<IProfilesData>("UserProfilesData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySQLXAssetData>()
            .Named<IXAssetDataPlugin>("MySQLXAssetData")
            .AsImplementedInterfaces().SingleInstance();                                                        

        builder.RegisterType<MySQLXInventoryData>()
            .Named<IXInventoryData>("MySQLXInventoryData")
            .AsImplementedInterfaces().SingleInstance();                                                        
    }
}
