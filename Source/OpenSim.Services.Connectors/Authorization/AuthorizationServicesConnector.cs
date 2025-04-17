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

using OpenSim.Framework;
using OpenSim.Services.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.ServiceAuth;

namespace OpenSim.Services.Connectors
{
    public class AuthorizationServicesConnector : IAuthorizationService
    {
        private const string _section = "AuthorizationService";
        private IServiceAuth _auth = null;
    
        private ILogger<AuthorizationServicesConnector> _logger;
        private IConfiguration _config;

        private string _serverURI = String.Empty;
        private bool m_ResponseOnFailure = true;

        public AuthorizationServicesConnector(
            IConfiguration configuration, 
            ILogger<AuthorizationServicesConnector> logger)
        {
            _config = configuration;
            _logger = logger;

            _auth = ServiceAuth.Create(configuration, _section);           
            _serverURI = ServiceURI.LookupServiceURI(configuration, _section, "AuthorizationServerURI");

            var authorizationConfig = _config.GetSection("AuthorizationService");
            if (authorizationConfig.Exists() is false)
            {
                _logger.LogInformation("[AUTHORIZATION CONNECTOR]: AuthorizationService missing from OpenSim.ini");
                throw new Exception("Authorization connector init error");
            }

            // this dictates what happens if the remote service fails, if the service fails and the value is true
            // the user is authorized for the region.
            var responseOnFailure = authorizationConfig.GetValue<bool>("ResponseOnFailure", true);
            m_ResponseOnFailure = responseOnFailure;

            _logger.LogInformation("[AUTHORIZATION CONNECTOR]: AuthorizationService initialized");
        }

        public bool IsAuthorizedForRegion(string userID, string firstname, string lastname, string email, string regionName, string regionID, out string message)
        {
            // do a remote call to the authorization server specified in the AuthorizationServerURI
            _logger.LogInformation($"[AUTHORIZATION CONNECTOR]: IsAuthorizedForRegion checking {userID} at remote server {_serverURI}");

            string uri = _serverURI;

            AuthorizationRequest req = new AuthorizationRequest(userID, firstname, lastname, email, regionName, regionID);
            AuthorizationResponse response;

            try
            {
                response = SynchronousRestObjectRequester.MakeRequest<AuthorizationRequest, AuthorizationResponse>("POST", uri, req);
            }
            catch (Exception e)
            {
                _logger.LogWarning(
                    $"[AUTHORIZATION CONNECTOR]: Unable to send authorize {userID} for region {regionID} "+
                    $"error thrown during comms with remote server. Reason: {e.Message}");

                message = e.Message;
                return m_ResponseOnFailure;
            }

            if (response == null)
            {
                message = "Null response";
                return m_ResponseOnFailure;
            }

            _logger.LogDebug($"[AUTHORIZATION CONNECTOR] response from remote service was {response.Message}");
            message = response.Message;

            return response.IsAuthorized;
        }
    }
}
