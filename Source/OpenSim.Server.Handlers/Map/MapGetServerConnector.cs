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
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Map;

public class MapGetServiceConnector(
    IConfiguration config,
    ILogger<MapGetServiceConnector> logger,
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

        var gridServiceName = serverConfig.GetValue("LocalServiceModule", string.Empty);
        if (string.IsNullOrWhiteSpace(gridServiceName))
            throw new Exception("No LocalServiceModule in config file");
        
        var mapService = componentContext.ResolveNamed<IMapImageService>(gridServiceName);
        HttpServer.AddStreamHandler(new MapServerGetHandler(logger, mapService));
    }
}

class MapServerGetHandler(ILogger logger, IMapImageService service) : BaseStreamHandler("GET", "/map")
{
    public static readonly object ev = new object();

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        if(!Monitor.TryEnter(ev, 5000))
        {
            httpResponse.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            httpResponse.AddHeader("Retry-After", "10");
            return Array.Empty<byte>();
        }

        byte[] result = Array.Empty<byte>();
        string format = string.Empty;

        //UUID scopeID = new UUID("07f8d88e-cd5e-4239-a0ed-843f75d09992");
        UUID scopeID = UUID.Zero;

        // This will be map/tilefile.ext, but on multitenancy it will be
        // map/scope/teilefile.ext
        path = path.Trim('/');
        string[] bits = path.Split(new char[] {'/'});
        if (bits.Length > 2)
        {
            try
            {
                scopeID = new UUID(bits[1]);
            }
            catch
            {
                return new byte[9];
            }
            path = bits[2];
            path = path.Trim('/');
        }

        if(path.Length == 0)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            httpResponse.ContentType = "text/plain";
            return Array.Empty<byte>();
        }

        result = service.GetMapTile(path, scopeID, out format);
        if (result.Length > 0)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
            if (format.Equals(".png"))
                httpResponse.ContentType = "image/png";
            else if (format.Equals(".jpg") || format.Equals(".jpeg"))
                httpResponse.ContentType = "image/jpeg";
        }
        else
        {
            httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
            httpResponse.ContentType = "text/plain";
        }

        Monitor.Exit(ev);

        return result;
    }
}

