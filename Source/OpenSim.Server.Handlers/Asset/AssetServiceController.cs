using Microsoft.AspNetCore.Mvc;

namespace OpenSim.Server.Handlers.Asset;

[Route("api/[controller]")]
[ApiController]
public class AssetServiceController : ControllerBase
{
        // server.AddStreamHandler(new AssetServerGetHandler(m_AssetService, auth, redirectURL));
        // server.AddStreamHandler(new AssetServerPostHandler(m_AssetService, auth));
        // server.AddStreamHandler(new AssetServerDeleteHandler(m_AssetService, allowedRemoteDeleteTypes, auth));
        // server.AddStreamHandler(new AssetsExistHandler(m_AssetService));
}
