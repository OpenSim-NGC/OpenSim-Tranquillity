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
using OpenMetaverse;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.ServiceAuth;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Services.Connectors
{
    public class HGAssetServiceConnector : BaseServiceConnector, IAssetService
    {
        private readonly ILogger<HGAssetServiceConnector> _logger;
        private readonly IServiceAuth _auth;

        private ExpiringCacheOS<string, AssetServicesConnector> m_connectors = new ExpiringCacheOS<string, AssetServicesConnector>(60000);

        public HGAssetServiceConnector(
            IConfiguration configuration,
            ILogger<HGAssetServiceConnector> logger)
        {
            _logger = logger;
            _auth = AuthType(configuration, "AssetService");

            var moduleConfig = configuration.GetSection("Modules");
            if (moduleConfig.Exists())
            {
                var name = moduleConfig.GetValue("AssetServices", string.Empty);;

                var assetConfig = configuration.GetSection("AssetService");
                if (assetConfig.Exists() is false)
                {
                    _logger.LogError("[HG ASSET SERVICE]: AssetService missing from OpenSim.ini");
                    return;
                }

                _logger.LogInformation("[HG ASSET SERVICE]: HG asset service enabled");
            }
        }

        private AssetServicesConnector GetConnector(string url)
        {
            AssetServicesConnector connector = null;
            lock (m_connectors)
            {
                if (!m_connectors.TryGetValue(url, 60000, out connector))
                {
                    connector = new AssetServicesConnector(url);
                    m_connectors.Add(url, connector);
                }
            }
            return connector;
        }

        public AssetBase Get(string id)
        {
            string url = string.Empty;
            string assetID = string.Empty;

            if (Util.ParseForeignAssetID(id, out url, out assetID) > 0)
            {
                IAssetService connector = GetConnector(url);
                return connector.Get(assetID);
            }
            return null;
        }

        public AssetBase Get(string id, string ForeignAssetService, bool dummy)
        {
            IAssetService connector = GetConnector(ForeignAssetService);
            return connector.Get(id);
        }


        public AssetBase GetCached(string id)
        {
            string url = string.Empty;
            string assetID = string.Empty;

            if (Util.ParseForeignAssetID(id, out url, out assetID) > 0)
            {
                IAssetService connector = GetConnector(url);
                return connector.GetCached(assetID);
            }
            return null;
        }

        public AssetMetadata GetMetadata(string id)
        {
            string url = string.Empty;
            string assetID = string.Empty;

            if (Util.ParseForeignAssetID(id, out url, out assetID) > 0)
            {
                IAssetService connector = GetConnector(url);
                return connector.GetMetadata(assetID);
            }

            return null;
        }

        public byte[] GetData(string id)
        {
            return null;
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            string url = string.Empty;
            string assetID = string.Empty;

            if (id.Equals(Util.UUIDZeroString))
                return false;

            if (Util.ParseForeignAssetID(id, out url, out assetID) > 0)
            {
                IAssetService connector = GetConnector(url);
                return connector.Get(assetID, sender, handler);
            }

            return false;
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            return;
        }

        private struct AssetAndIndex
        {
            public UUID assetID;
            public int index;

            public AssetAndIndex(UUID assetID, int index)
            {
                this.assetID = assetID;
                this.index = index;
            }
        }

        public virtual bool[] AssetsExist(string[] ids)
        {
            var url2assets = new Dictionary<string, List<AssetAndIndex>>();

            for (int i = 0; i < ids.Length; i++)
            {
                string url = string.Empty;
                string assetID = string.Empty;

                if (Util.ParseForeignAssetID(ids[i], out url, out assetID) > 0)
                {
                    if (!url2assets.ContainsKey(url))
                        url2assets.Add(url, new List<AssetAndIndex>());
                    url2assets[url].Add(new AssetAndIndex(UUID.Parse(assetID), i));
                }
            }

            // Query each of the servers in turn

            bool[] exist = new bool[ids.Length];

            foreach (string url in url2assets.Keys)
            {
                AssetServicesConnector connector = GetConnector(url);
                lock (connector.ConnectorLock)
                {
                    List<AssetAndIndex> curAssets = url2assets[url];
                    string[] assetIDs = curAssets.ConvertAll(a => a.assetID.ToString()).ToArray();
                    bool[] curExist = connector.AssetsExist(assetIDs);

                    int i = 0;
                    foreach (AssetAndIndex ai in curAssets)
                    {
                        exist[ai.index] = curExist[i];
                        ++i;
                    }
                }
            }

            return exist;
        }

        public string Store(AssetBase asset)
        {
            string url = string.Empty;
            string assetID = string.Empty;

            if (Util.ParseForeignAssetID(asset.ID, out url, out assetID) > 0)
            {
                AssetServicesConnector connector = GetConnector(url);
                // Restore the assetID to a simple UUID
                asset.ID = assetID;
                lock ((connector.ConnectorLock))
                    return connector.Store(asset);
            }

            return string.Empty;
        }

        public bool UpdateContent(string id, byte[] data)
        {
            return false;
        }

        public bool Delete(string id)
        {
            return false;
        }
    }
}
