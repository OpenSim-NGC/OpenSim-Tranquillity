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

using System.Collections;
using System.Web;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Autofac;

namespace OpenSim.Server.Handlers.Freeswitch;

public class FreeswitchServerConnector(
    IConfiguration config,
    ILogger<FreeswitchServerConnector> logger,
    IComponentContext componentContext)
    : IServiceConnector
{
    protected string FreeSwitchApiPrefix = "/fsapi";
    protected IFreeswitchService FreeswitchService;
    
    public string ConfigName { get; private set; }
    public IHttpServer HttpServer { get; private set; }
    
    public void Initialize(IHttpServer httpServer, string configName = "FreeswitchService")
    {
        HttpServer = httpServer;
        ConfigName = configName;

        var serverConfig = config.GetSection(ConfigName);
        if (serverConfig.Exists() is false)
            throw new Exception($"No section {ConfigName} in config file");

        string freeswitchServiceName = serverConfig.GetValue<string>("LocalServiceModule", String.Empty);
        if (string.IsNullOrEmpty(freeswitchServiceName))
            throw new Exception("No LocalServiceModule in config file");

        FreeswitchService = componentContext.ResolveNamed<IFreeswitchService>(freeswitchServiceName);

        HttpServer.AddHTTPHandler(String.Format("{0}/freeswitch-config", FreeSwitchApiPrefix), FreeSwitchConfigHttpHandler);
        HttpServer.AddHTTPHandler(String.Format("{0}/region-config", FreeSwitchApiPrefix), RegionConfigHttpHandler);
    }

    public Hashtable FreeSwitchConfigHttpHandler(Hashtable request)
    {
        Hashtable response = new Hashtable();
        response["str_response_string"] = string.Empty;
        response["content_type"] = "text/plain";
        response["keepalive"] = false;
        response["int_response_code"] = 500;

        Hashtable requestBody = ParseRequestBody((string) request["body"]);

        string section = (string) requestBody["section"];

        if (section == "directory")
            response = FreeswitchService.HandleDirectoryRequest(requestBody);
        else if (section == "dialplan")
            response = FreeswitchService.HandleDialplanRequest(requestBody);
        else
            logger.LogWarning($"section was {section}");

        return response;
    }

    private Hashtable ParseRequestBody(string body)
    {
        Hashtable bodyParams = new Hashtable();
        // split string
        string [] nvps = body.Split(new Char [] {'&'});

        foreach (string s in nvps)
        {
            if (s.Trim() != "")
            {
                string [] nvp = s.Split(new Char [] {'='});
                bodyParams.Add(HttpUtility.UrlDecode(nvp[0]), HttpUtility.UrlDecode(nvp[1]));
            }
        }

        return bodyParams;
    }

    public Hashtable RegionConfigHttpHandler(Hashtable request)
    {
        Hashtable response = new Hashtable();
        response["content_type"] = "text/json";
        response["keepalive"] = false;
        response["int_response_code"] = 200;

        response["str_response_string"] = FreeswitchService.GetJsonConfig();

        return response;
    }

}

