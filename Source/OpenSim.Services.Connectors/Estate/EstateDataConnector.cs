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
using System.Reflection;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.ServiceAuth;

namespace OpenSim.Services.Connectors
{
    public class EstateDataRemoteConnector : IEstateDataService
    {
        private const string _sectionName = "EstateService";
        private const string _uriName = "EstateServerURI";

        private readonly IConfiguration _config;
        private readonly ILogger<EstateDataRemoteConnector> _logger;

        private readonly IServiceAuth _auth;
        private readonly string _serverURI;

        private ExpiringCache<string, List<EstateSettings>> m_EstateCache = new ExpiringCache<string, List<EstateSettings>>();
        private const int EXPIRATION = 5 * 60; // 5 minutes in secs

        public EstateDataRemoteConnector(
            IConfiguration configuration,
            ILogger<EstateDataRemoteConnector> logger)
        {
            _config = configuration;
            _logger = logger;

            _auth = ServiceAuth.Create(_config, _sectionName);
            _serverURI = ServiceURI.LookupServiceURI(configuration, _sectionName, _uriName);
            
            if (string.IsNullOrEmpty(_serverURI))
            {
                _logger.LogError($"[ESTATE CONNECTOR]: No Server URI named in section {_sectionName}");
                throw new Exception("Estate connector init error");
            }
        }

        #region IEstateDataService

        public List<EstateSettings> LoadEstateSettingsAll()
        {
            string uri = _serverURI + "/estates";
            string reply = MakeRequest("GET", uri, string.Empty);

            if (String.IsNullOrEmpty(reply))
                return [];

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

            if (replyData != null && replyData.Count > 0)
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: LoadEstateSettingsAll returned {replyData.Count} elements");
                Dictionary<string, object>.ValueCollection estateData = replyData.Values;
                List<EstateSettings> estates = [];

                foreach (object r in estateData)
                {
                    if (r is Dictionary<string, object> dr)
                    {
                        EstateSettings es = new EstateSettings(dr);
                        estates.Add(es);
                    }
                }

                m_EstateCache.AddOrUpdate("estates", estates, EXPIRATION);
                return estates;
            }
            else
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: LoadEstateSettingsAll from {uri} received empty response");
            }

            return [];
        }

        public List<int> GetEstatesAll()
        {
            // If we don't have them, load them from the server
            if (!m_EstateCache.TryGetValue("estates", out List<EstateSettings> estates))
                estates = LoadEstateSettingsAll();

            List<int> eids = [];
            foreach (EstateSettings es in estates)
                eids.Add((int)es.EstateID);

            return eids;
        }

        public List<int> GetEstates(string search)
        {
            // If we don't have them, load them from the server
            if (!m_EstateCache.TryGetValue("estates", out List<EstateSettings> estates))
                estates = LoadEstateSettingsAll();

            List<int> eids = [];
            foreach (EstateSettings es in estates)
                if (es.EstateName == search)
                    eids.Add((int)es.EstateID);

            return eids;
        }

        public List<int> GetEstatesByOwner(UUID ownerID)
        {
            // If we don't have them, load them from the server
            if (!m_EstateCache.TryGetValue("estates", out List<EstateSettings> estates))
                estates = LoadEstateSettingsAll();

            List<int> eids = [];
            foreach (EstateSettings es in estates)
                if (es.EstateOwner.Equals(ownerID))
                    eids.Add((int)es.EstateID);

            return eids;
        }

        public List<UUID> GetRegions(int estateID)
        {
            // /estates/regions/?eid=int
            string uri = _serverURI + "/estates/regions/?eid=" + estateID.ToString();

            string reply = MakeRequest("GET", uri, string.Empty);
            if (string.IsNullOrEmpty(reply))
                return [];

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
            if (replyData != null && replyData.Count > 0)
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: GetRegions for estate {estateID} returned {replyData.Count} elements");

                List<UUID> regions = [];
                Dictionary<string, object>.ValueCollection data = replyData.Values;
                foreach (object r in data)
                {
                    if (UUID.TryParse(r.ToString(), out UUID uuid))
                        regions.Add(uuid);
                }
                return regions;
            }
            else
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: GetRegions from {uri} received null or zero response");
            }
            return [];
        }

        public EstateSettings LoadEstateSettings(UUID regionID, bool create)
        {
            // /estates/estate/?region=uuid&create=[t|f]
            string uri = _serverURI + $"/estates/estate/?region={regionID}&create={create}";

            //MakeRequest is bugged as its using the older deprecated WebRequest.  A call to the estate
            // service here will return a 404 if the estate doesnt exist which is correct but the code
            // assumes thats a fatal error.  BTW We should never ever call Enviroinment.Exit from a supporting
            // module or a library like this.  So its gonna go.
            string reply = MakeRequest("GET", uri, string.Empty);
            if (string.IsNullOrEmpty(reply))
            {
                m_log.DebugFormat("[ESTATE CONNECTOR] connection to remote estates service failed");
                return null;
            }

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

            if (replyData != null && replyData.Count > 0)
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: LoadEstateSettings({regionID}) returned {replyData.Count} elements");
                EstateSettings es = new EstateSettings(replyData);
                return es;
            }
            else
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: LoadEstateSettings(regionID) from {uri} received null or zero response");
            }

            return null;
        }

        public EstateSettings LoadEstateSettings(int estateID)
        {
            // /estates/estate/?eid=int
            string uri = _serverURI + $"/estates/estate/?eid={estateID}";

            string reply = MakeRequest("GET", uri, string.Empty);
            if (string.IsNullOrEmpty(reply))
                return null;

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

            if (replyData != null && replyData.Count > 0)
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: LoadEstateSettings({estateID}) returned {replyData.Count} elements");

                EstateSettings es = new EstateSettings(replyData);
                return es;
            }
            else
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: LoadEstateSettings(estateID) from {uri} received null or zero response");
            }

            return null;
        }

        /// <summary>
        /// Forbidden operation
        /// </summary>
        /// <returns></returns>
        public EstateSettings CreateNewEstate(int estateID)
        {
            // No can do
            return null;
        }

        public void StoreEstateSettings(EstateSettings es)
        {
            // /estates/estate/
            string uri = _serverURI + "/estates/estate";

            Dictionary<string, object> formdata = es.ToMap();
            formdata["OP"] = "STORE";

            PostRequest(uri, formdata);
        }

        public bool LinkRegion(UUID regionID, int estateID)
        {
            // /estates/estate/?eid=int&region=uuid
            string uri = _serverURI + $"/estates/estate/?eid={estateID}&region={regionID}";

            Dictionary<string, object> formdata = new()
            {
                ["OP"] = "LINK"
            };

            return PostRequest(uri, formdata);
        }

        private bool PostRequest(string uri, Dictionary<string, object> sendData)
        {
            string reqString = ServerUtils.BuildQueryString(sendData);

            string reply = MakeRequest("POST", uri, reqString);
            if (string.IsNullOrEmpty(reply))
                return false;

            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);
            if (replyData != null && replyData.Count > 0)
            {
                if (replyData.TryGetValue("Result", out object ortmp) && ortmp is string srtmp)
                {
                    if (bool.TryParse(srtmp, out bool result))
                    {
                        _logger.LogDebug($"[ESTATE CONNECTOR]: PostRequest {uri} returned {result}");
                        return result;
                    }
                }
            }
            else
            {
                _logger.LogDebug($"[ESTATE CONNECTOR]: PostRequest {uri} received empty response");
            }

            return false;
        }

        /// <summary>
        /// Forbidden operation
        /// </summary>
        /// <returns></returns>
        public bool DeleteEstate(int estateID)
        {
            return false;
        }

        #endregion

        private string MakeRequest(string verb, string uri, string formdata)
        {
            string reply = string.Empty;
            try
            {
                reply = SynchronousRestFormsRequester.MakeRequest(verb, uri, formdata, 30, _auth);
                return reply;
            }
            catch (HttpRequestException e)
            {
                if (e.StatusCode is HttpStatusCode status)
                {
                    if (status == HttpStatusCode.Unauthorized)
                    {
                        _logger.LogError($"[ESTATE CONNECTOR]: Web request {uri} requires authentication ");
                    }
                    else if (status != HttpStatusCode.NotFound)
                    {
                        _logger.LogError($"[ESTATE CONNECTOR]: Resource {uri} not found ");
                        return reply;
                    }
                }
                else
                {
                    _logger.LogError($"[ESTATE CONNECTOR]: WebException for {verb} {uri} {formdata} {e.Message}");
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, $"[ESTATE CONNECTOR]: Exception when contacting estate server at {uri}: {e.Message}");
            }

            return null;
        }
    }
}
