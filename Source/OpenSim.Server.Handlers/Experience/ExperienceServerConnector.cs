using Autofac;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenSim.Server.Handlers.Estate;

namespace OpenSim.Server.Handlers.Experience;

public class ExperienceServiceConnector(
    IConfiguration config,
    ILogger<EstateDataRobustConnector> logger,
    IComponentContext componentContext)
    : IServiceConnector
{
    public IHttpServer HttpServer { get; private set; }
    public string ConfigName { get; private set; }

    public void Initialize(IHttpServer httpServer, string configName = "ExperienceService")
    {
        HttpServer = httpServer;
        ConfigName = configName;

        var serverConfig = config.GetSection(ConfigName);
        if (serverConfig.Exists() is false)
            throw new Exception($"No section {ConfigName} in config file");

        var serviceName = serverConfig.GetValue("LocalServiceModule", string.Empty);
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new Exception("No LocalServiceModule in config file");
        
        var experienceService = componentContext.ResolveNamed<IExperienceService>(serviceName);
        IServiceAuth auth = ServiceAuth.Create(config, ConfigName);
        HttpServer.AddStreamHandler(new ExperienceServerPostHandler(experienceService, auth));
    }
}