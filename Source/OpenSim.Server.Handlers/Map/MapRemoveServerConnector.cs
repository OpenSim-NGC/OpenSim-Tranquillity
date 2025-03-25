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

using System.Net;
using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Server.Handlers.Map;

public class MapRemoveServiceConnector(
    IConfiguration config,
    ILogger<MapRemoveServiceConnector> logger,
    IComponentContext componentContext)
    : IServiceConnector
{
    public string ConfigName { get; private set; }
    public IHttpServer HttpServer { get; private set; }
    
    public void Initialize(IHttpServer httpServer, string configName = "MapImageService")
    {
        HttpServer = httpServer;
        ConfigName = configName;

        var serverConfig = config.GetSection(ConfigName);
        if (serverConfig.Exists() is false)
            throw new Exception($"No section {ConfigName} in config file");

        var mapServiceName = serverConfig.GetValue("LocalServiceModule", string.Empty);
        if (string.IsNullOrEmpty(mapServiceName))
            throw new Exception("No LocalServiceModule in config file");

        var mapService = componentContext.ResolveNamed<IMapImageService>(mapServiceName);

        IGridService gridService = null;
        var gridServiceName = serverConfig.GetValue("GridService", string.Empty);
        if (!string.IsNullOrEmpty(gridServiceName))
        {
            gridService = componentContext.ResolveNamed<IGridService>(gridServiceName);
            if (gridService != null)
                logger.LogInformation($"GridService check is ON");
            else
                logger.LogInformation($"GridService check is OFF");
        }

        var auth = ServiceAuth.Create(config, ConfigName);
        HttpServer.AddSimpleStreamHandler(new MapServerRemoveHandler(logger, mapService, gridService, auth));
    }

}

class MapServerRemoveHandler(
    ILogger logger,
    IMapImageService service,
    IGridService grid,
    IServiceAuth auth)
    : SimpleStreamHandler("/removemap", auth)
{
    protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        try
        {
            string body;
            using (StreamReader sr = new StreamReader(httpRequest.InputStream))
                body = sr.ReadToEnd();

            body = body.Trim();

            httpRequest.InputStream.Dispose();
            Dictionary<string, object> request = ServerUtils.ParseQueryString(body);

            if (!request.ContainsKey("X") || !request.ContainsKey("Y"))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                httpResponse.RawBuffer = Util.ResultFailureMessage("Bad request.");
                return;
            }

            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            int x = 0, y = 0;
            Int32.TryParse(request["X"].ToString(), out x);
            Int32.TryParse(request["Y"].ToString(), out y);
//                UUID scopeID = new UUID("07f8d88e-cd5e-4239-a0ed-843f75d09992");
            UUID scopeID = UUID.Zero;
            if (request.ContainsKey("SCOPE"))
                UUID.TryParse(request["SCOPE"].ToString(), out scopeID);

            logger.LogDebug($"Received position data for region at {x}-{y}");

            if (grid != null)
            {
                System.Net.IPAddress ipAddr = httpRequest.RemoteIPEndPoint.Address;
                GridRegion r = grid.GetRegionByPosition(UUID.Zero, (int)Util.RegionToWorldLoc((uint)x), (int)Util.RegionToWorldLoc((uint)y));
                if (r != null)
                {
                    if (r.ExternalEndPoint.Address.ToString() != ipAddr.ToString())
                    {
                        logger.LogWarning($"IP address {ipAddr} may be trying to impersonate region in IP {r.ExternalEndPoint.Address}");
                        httpResponse.RawBuffer = Util.ResultFailureMessage("IP address of caller does not match IP address of registered region");
                        return;
                    }
                }
                else
                {
                    logger.LogWarning($"IP address {ipAddr} may be rogue. Region not found at coordinates {x}-{y}");
                    httpResponse.RawBuffer = Util.ResultFailureMessage("Region not found at given coordinates");
                    return;
                }
            }
            
            bool result = service.RemoveMapTile(x, y, scopeID, out string reason);

            if (result)
                httpResponse.RawBuffer = Util.sucessResultSuccess;
            else
                httpResponse.RawBuffer = Util.ResultFailureMessage(reason);
            return;
        }
        catch (Exception e)
        {
            logger.LogError(e, $"Exception");
        }

        httpResponse.RawBuffer = Util.ResultFailureMessage("Unexpected server error");
    }
}