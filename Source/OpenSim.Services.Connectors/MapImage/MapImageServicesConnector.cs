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
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenMetaverse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Services.Connectors
{
    public class MapImageServicesConnector : IMapImageService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<MapImageServicesConnector> _logger;
        private readonly IServiceAuth _auth;

        private readonly string _serverURI = string.Empty;

        public MapImageServicesConnector(
            IConfiguration configuration,
            ILogger<MapImageServicesConnector> logger
            )
        {
            _configuration = configuration;
            _logger = logger;
            _auth = ServiceAuth.Create(configuration, "MapImageService");
            var serviceURI = ServiceURI.LookupServiceURI(configuration, "MapImageService", "MapImageServerURI");

            _serverURI = serviceURI.TrimEnd('/');
        }

        public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            string reqString;
            if (scopeID.IsNotZero())
            {
                reqString = ServerUtils.BuildQueryString(
                    new Dictionary<string, object>()
                        {
                            {"X" , x.ToString() },
                            {"Y" , y.ToString() },
                            { "SCOPE" , scopeID.ToString() },
                        }
                    );
            }
            else
            {
                reqString = ServerUtils.BuildQueryString(
                    new Dictionary<string, object>()
                        {
                            {"X" , x.ToString() },
                            {"Y" , y.ToString() },
                            { "SCOPE" , scopeID.ToString() },
                        }
                    );
            }

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", _serverURI + "/map", reqString, 10, null, false);
                if (reply.Length > 0)
                {
                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
                    if(replyData.TryGetValue("Result", out object resultobj))
                    {
                        string res = resultobj as string;
                        if (string.IsNullOrEmpty(res))
                        {
                            _logger.LogDebug("[MAP IMAGE CONNECTOR]: unknown result field");
                            return false;
                        }
                        else if (res.Equals("success", StringComparison.InvariantCultureIgnoreCase))
                        {
                            return true;
                        }
                        else if (res.Equals("failure", StringComparison.InvariantCultureIgnoreCase))
                        {
                            reason = replyData["Message"].ToString();
                            _logger.LogDebug($"[MAP IMAGE CONNECTOR]: RemoveMapTile failed: {reason}");
                            return false;
                        }

                        _logger.LogDebug("[MAP IMAGE CONNECTOR]: RemoveMapTile unknown result field contents");
                        return false;
                    }
                }
                else
                {
                    _logger.LogDebug("[MAP IMAGE CONNECTOR]: RemoveMapTile reply data does not contain result field");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[MAP IMAGE CONNECTOR]: RemoveMapTile Exception at {_serverURI}/map: {e.Message}");
            }

            return false;
        }

        public bool AddMapTile(int x, int y, byte[] jpgData, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            int tickstart = Util.EnvironmentTickCount();

            string reqString;
            if (scopeID.IsNotZero())
            {
                reqString = ServerUtils.BuildQueryString(
                    new Dictionary<string, object>()
                        {
                            {"X" , x.ToString() },
                            {"Y" , y.ToString() },
                            { "SCOPE" , scopeID.ToString() },
                            { "TYPE" , "image/jpeg" },
                            { "DATA" , Convert.ToBase64String(jpgData) }
                        }
                    );
            }
            else
            {
                reqString = ServerUtils.BuildQueryString(
                    new Dictionary<string, object>()
                        {
                            {"X" , x.ToString() },
                            {"Y" , y.ToString() },
                            { "TYPE" , "image/jpeg" },
                            { "DATA" , Convert.ToBase64String(jpgData) }
                        }
                    );
            }

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", _serverURI + "/map", reqString, 10, _auth, false);
                if (reply.Length > 0)
                {
                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
                    if (replyData.TryGetValue("Result", out object resultobj))
                    {
                        string res = resultobj as string;

                        if (string.IsNullOrEmpty(res))
                        {
                            _logger.LogDebug("[MAP IMAGE CONNECTOR]: AddMapTile unknown result field");
                            return false;
                        }
                        else if (res.Equals("success", StringComparison.InvariantCultureIgnoreCase))
                        {
                            return true;
                        }
                        else if (res.Equals("failure", StringComparison.InvariantCultureIgnoreCase))
                        {
                            reason = replyData["Message"].ToString();
                            _logger.LogDebug($"[MAP IMAGE CONNECTOR]: AddMapTile failed: {reason}");
                            return false;
                        }

                        _logger.LogDebug("[MAP IMAGE CONNECTOR]: AddMapTile unknown result field contents");
                        return false;
                    }
                }
                else
                {
                    _logger.LogDebug("[MAP IMAGE CONNECTOR]: AddMapTile reply data does not contain result field");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[MAP IMAGE CONNECTOR]: AddMapTile Exception at {_serverURI}/map: {e.Message}");
            }
            finally
            {
                // This just dumps a warning for any operation that takes more than 100 ms
                int tickdiff = Util.EnvironmentTickCountSubtract(tickstart);
                _logger.LogDebug($"[MAP IMAGE CONNECTOR]: AddMapTile {jpgData.Length} Bytes in {tickdiff}ms");
            }

            return false;
        }

        public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
        {
            format = string.Empty;
            new Exception("GetMapTile method not Implemented");
            return null;
        }
    }
}
