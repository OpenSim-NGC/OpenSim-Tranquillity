using System;
using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Appearance;

/// <summary>
/// Robust connector for the <c>agent_appearance_service</c> read path (Design Brief C4, ADR-002), in the shape
/// <c>XBakesConnector</c> established: a <see cref="ServiceConnector"/> that loads its
/// <c>LocalServiceModule</c> from its own config section and registers one
/// <see cref="SimpleStreamHandler"/> on the server it is given.
///
/// <para>Robust.HG.ini.example / Robust.ini.example:</para>
/// <code>
/// [ServiceList]
///     AppearanceServiceConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:AppearanceServerConnector"
///
/// [AppearanceService]
///     LocalServiceModule = "OpenSim.Services.AvatarService.dll:AppearanceService"
///     AvatarService      = "OpenSim.Services.AvatarService.dll:AvatarService"
///     AssetService       = "OpenSim.Services.AssetService.dll:AssetService"
/// </code>
///
/// <para>
/// It goes on the <b>public</b> port because the viewer fetches from it directly, unlike most Robust connectors.
/// The same class registers on a standalone's HTTP server, which hosts Robust connectors in process already
/// (ADR-002).
/// </para>
/// </summary>
public class AppearanceServerConnector : ServiceConnector
{
    private string m_ConfigName = "AppearanceService";

    public AppearanceServerConnector(IConfigSource config, IHttpServer server, string configName) :
            base(config, server, configName)
    {
        if (configName != string.Empty)
            m_ConfigName = configName;

        IConfig serverConfig = config.Configs[m_ConfigName];
        if (serverConfig is null)
            throw new Exception($"No section '{m_ConfigName}' in config file");

        string serviceName = serverConfig.GetString("LocalServiceModule", string.Empty);
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new Exception($"No LocalServiceModule in [{m_ConfigName}]");

        object[] args = new object[] { config };
        IAppearanceService service = ServerUtils.LoadPlugin<IAppearanceService>(serviceName, args);
        if (service is null)
            throw new Exception($"Could not load an IAppearanceService from '{serviceName}'");

        IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

        server.AddSimpleStreamHandler(new AppearanceServerHandler(service, auth), true);
    }
}

/// <summary>
/// <c>GET /texture/&lt;agent&gt;/&lt;channel&gt;/&lt;uuid&gt;</c>.
///
/// <para>
/// The path is exactly what <c>LLVOAvatar::getImageURL</c> builds — <c>appearance_service_url</c> with
/// <c>"texture/"</c> appended and no separator (<c>indra/newview/llvoavatar.cpp:5912</c>) — so the configured
/// <c>AgentAppearanceServiceURL</c> must end in <c>/</c> and this handler sits at <c>/texture</c>. The channel is
/// a name, not a number; see <see cref="AppearanceChannels"/> for the eleven tokens and where they come from.
/// </para>
///
/// <para>
/// Everything that is not a hit is <b>404</b>: an unknown channel token, an agent with no bake index, an index
/// whose UUID disagrees with the one in the path, an index pointing at an asset the asset service has lost. The
/// one thing this must never do is answer a mismatched request with whatever it does have.
/// </para>
/// </summary>
public class AppearanceServerHandler : SimpleStreamHandler
{
    private readonly IAppearanceService m_service;

    public AppearanceServerHandler(IAppearanceService service, IServiceAuth auth) :
            base("/texture", auth)
    {
        m_service = service;
    }

    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        httpRequest.InputStream?.Dispose();

        if (m_service is null)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
            return;
        }

        if (httpRequest.HttpMethod != "GET")
        {
            httpResponse.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }

        string[] p = GetParam(httpRequest.UriPath).Split(new char[] { '/', '?', '&' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3
            || !UUID.TryParse(p[0], out UUID agentId)
            || !UUID.TryParse(p[2], out UUID expectedAssetId))
        {
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        AssetBase asset = m_service.GetBake(agentId, p[1], expectedAssetId);
        if (asset?.Data is not { Length: > 0 })
        {
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        httpResponse.ContentType = "image/x-j2c";
        httpResponse.RawBuffer = asset.Data;
        httpResponse.StatusCode = (int)HttpStatusCode.OK;
    }
}
