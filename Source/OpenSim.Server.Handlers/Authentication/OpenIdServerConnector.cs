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

using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Autofac;

namespace OpenSim.Server.Handlers.Authentication;

public class OpenIdServerConnector(
    IConfiguration configuration,
    ILogger<OpenIdServerConnector> logger,
    IComponentContext componentContext)
    : IServiceConnector
{
    private IAuthenticationService _authenticationService;
    private IUserAccountService _userAccountService;
    
    public string ConfigName { get; private set; }
    public IHttpServer HttpServer { get; private set; }
    
    public void Initialize(IHttpServer httpServer, string configName = "OpenIdService")
    {
        var serverConfig = configuration.GetSection(ConfigName);
        if (serverConfig.Exists() is false)
            throw new Exception($"No section {ConfigName} in config file");

        var authServiceName = serverConfig.GetValue<string>("AuthenticationServiceModule", String.Empty);
        if (string.IsNullOrEmpty(authServiceName))
            throw new Exception("No AuthenticationServiceModule in config file for OpenId authentication");
        
        var userServiceName = serverConfig.GetValue<string>("UserAccountServiceModule", String.Empty);
        if (string.IsNullOrEmpty(userServiceName))
            throw new Exception("No UserAccountServiceModule in config file for OpenId authentication");

        try
        {
            _authenticationService = componentContext.ResolveNamed<IAuthenticationService>(authServiceName);
            _userAccountService = componentContext.ResolveNamed<IUserAccountService>(userServiceName);
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            throw;
        }
        
        // Handler for OpenID user identity pages
        HttpServer.AddStreamHandler(
            new OpenIdStreamHandler("GET", "/users", _userAccountService, _authenticationService));
        
        // Handlers for the OpenID endpoint server
        HttpServer.AddStreamHandler(
            new OpenIdStreamHandler("POST", "/openid/server", _userAccountService, _authenticationService));
        HttpServer.AddStreamHandler(
            new OpenIdStreamHandler("GET", "/openid/server", _userAccountService, _authenticationService));
    }
}