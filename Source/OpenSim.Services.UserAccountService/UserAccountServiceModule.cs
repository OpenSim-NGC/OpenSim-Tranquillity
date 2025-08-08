using Autofac;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.UserAccountService;

public class UserAccountServiceModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {  
        builder.RegisterType<UserAccountService>()
            .Named<IUserAccountService>("UserAccountService")
            .AsImplementedInterfaces();
        
        builder.RegisterType<UserAliasService>()
            .Named<IUserAliasService>("UserAliasService")
            .AsImplementedInterfaces();
    }
}