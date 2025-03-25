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
using OpenSim.Framework.ServiceAuth;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Services.Connectors
{
    public class AuthenticationServicesConnector : BaseServiceConnector, IAuthenticationService
    {
        private string _serverURI = String.Empty;
        private IServiceAuth _auth = null;
        private IConfiguration _config;
        private ILogger<AuthenticationServicesConnector> _logger;
        private const string _section = "AuthenticationService";

        public AuthenticationServicesConnector(
            IConfiguration configuration, 
            ILogger<AuthenticationServicesConnector> logger)
        {
            _logger = logger;
            _auth = AuthType(configuration, _section);
            _serverURI = ServiceURI(configuration, _section, "AuthenticationServerURI");
        }

        public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID)
        {
            realID = UUID.Zero;

            return Authenticate(principalID, password, lifetime);
        }

        public string Authenticate(UUID principalID, string password, int lifetime)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["LIFETIME"] = lifetime.ToString();
            sendData["PRINCIPAL"] = principalID.ToString();
            sendData["PASSWORD"] = password;

            sendData["METHOD"] = "authenticate";

            string reply = SynchronousRestFormsRequester.MakeRequest("POST",
                    _serverURI + "/auth/plain",
                    ServerUtils.BuildQueryString(sendData), _auth);

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(
                    reply);

            if (replyData["Result"].ToString() != "Success")
                return String.Empty;

            return replyData["Token"].ToString();
        }

        public bool Verify(UUID principalID, string token, int lifetime)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();

            sendData["LIFETIME"] = lifetime.ToString();
            sendData["PRINCIPAL"] = principalID.ToString();
            sendData["TOKEN"] = token;

            sendData["METHOD"] = "verify";

            string reply = SynchronousRestFormsRequester.MakeRequest("POST",
                    _serverURI + "/auth/plain",
                    ServerUtils.BuildQueryString(sendData), _auth);

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

            if (replyData["Result"].ToString() != "Success")
                return false;

            return true;
        }

        public bool Release(UUID principalID, string token)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["PRINCIPAL"] = principalID.ToString();
            sendData["TOKEN"] = token;

            sendData["METHOD"] = "release";

            string reply = SynchronousRestFormsRequester.MakeRequest("POST",
                    _serverURI + "/auth/plain",
                    ServerUtils.BuildQueryString(sendData), _auth);

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

            if (replyData["Result"].ToString() != "Success")
                return false;

            return true;
        }

        public bool SetPassword(UUID principalID, string passwd)
        {
            // nope, we don't do this
            return false;
        }

        public AuthInfo GetAuthInfo(UUID principalID)
        {
            // not done from remote simulators
            return null;
        }

        public bool SetAuthInfo(AuthInfo info)
        {
            // not done from remote simulators
            return false;
        }
    }
}
