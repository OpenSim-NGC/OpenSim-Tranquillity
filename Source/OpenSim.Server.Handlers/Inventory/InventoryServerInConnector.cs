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

using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using OpenMetaverse;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autofac;

namespace OpenSim.Server.Handlers.Inventory
{
    public class InventoryServiceInConnector : IServiceConnector
    {
        protected IInventoryService m_InventoryService;

        private bool m_doLookup = false;

        //private static readonly int INVENTORY_DEFAULT_SESSION_TIME = 30; // secs
        //private AuthedSessionCache m_session_cache = new AuthedSessionCache(INVENTORY_DEFAULT_SESSION_TIME);

        private string m_userserver_url;
        protected const string _configName = "InventoryService";

        public string ConfigName { get; private set; } = _configName;
        private readonly IConfiguration m_configuration;
        private readonly ILogger<InventoryServiceInConnector> m_logger;
        private readonly IComponentContext m_context;

        public IHttpServer HttpServer { get; private set; }


        public InventoryServiceInConnector(
            IConfiguration config, 
            ILogger<InventoryServiceInConnector> logger,
            IComponentContext componentContext)
        {
            m_configuration = config;
            m_logger = logger;
            m_context = componentContext;
        }

        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            var serverConfig = m_configuration.GetSection(ConfigName);
            if (serverConfig.Exists() is false)
                throw new Exception($"No section '{ConfigName}' in config file");

            string inventoryService = serverConfig.GetValue("LocalServiceModule", String.Empty);

            if (string.IsNullOrEmpty(inventoryService))
                throw new Exception("No LocalServiceModule in config file");

            m_InventoryService = m_context.ResolveNamed<IInventoryService>(inventoryService);

            m_userserver_url = serverConfig.GetValue("UserServerURI", String.Empty);
            m_doLookup = serverConfig.GetValue<bool>("SessionAuthentication", false);

            AddHttpHandlers(HttpServer);
            
            m_logger.LogDebug("Handlers initialized");
        }

        protected virtual void AddHttpHandlers(IHttpServer HttpServer)
        {
            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, List<InventoryFolderBase>>(
                "POST", "/SystemFolders/", GetSystemFolders, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, InventoryCollection>(
                "POST", "/GetFolderContent/", GetFolderContent, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/UpdateFolder/", m_InventoryService.UpdateFolder, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/MoveFolder/", m_InventoryService.MoveFolder, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/PurgeFolder/", m_InventoryService.PurgeFolder, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<List<Guid>, bool>(
                    "POST", "/DeleteFolders/", DeleteFolders, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<List<Guid>, bool>(
                    "POST", "/DeleteItem/", DeleteItems, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, InventoryItemBase>(
                    "POST", "/QueryItem/", GetItem, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, InventoryFolderBase>(
                    "POST", "/QueryFolder/", GetFolder, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<Guid, bool>(
                    "POST", "/CreateInventory/", CreateUsersInventory, CheckTrustSource));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/NewFolder/", m_InventoryService.AddFolder, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/CreateFolder/", m_InventoryService.AddFolder, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryItemBase, bool>(
                    "POST", "/NewItem/", m_InventoryService.AddItem, CheckAuthSession));

            HttpServer.AddStreamHandler(
             new RestDeserialiseTrustedHandler<InventoryItemBase, bool>(
                 "POST", "/AddNewItem/", m_InventoryService.AddItem, CheckTrustSource));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, List<InventoryItemBase>>(
                    "POST", "/GetItems/", GetFolderItems, CheckAuthSession));

            HttpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<List<InventoryItemBase>, bool>(
                    "POST", "/MoveItems/", MoveItems, CheckAuthSession));

            HttpServer.AddStreamHandler(new InventoryServerMoveItemsHandler(m_logger, m_InventoryService));

            // for persistent active gestures
            HttpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<Guid, List<InventoryItemBase>>
                    ("POST", "/ActiveGestures/", GetActiveGestures, CheckTrustSource));

            // WARNING: Root folders no longer just delivers the root and immediate child folders (e.g
            // system folders such as Objects, Textures), but it now returns the entire inventory skeleton.
            // It would have been better to rename this request, but complexities in the BaseHttpServer
            // (e.g. any http request not found is automatically treated as an xmlrpc request) make it easier
            // to do this for now.
            HttpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<Guid, List<InventoryFolderBase>>
                    ("POST", "/RootFolders/", GetInventorySkeleton, CheckTrustSource));

            HttpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<InventoryItemBase, int>
                ("POST", "/AssetPermissions/", GetAssetPermissions, CheckTrustSource));

        }

        #region Wrappers for converting the Guid parameter

        public List<InventoryFolderBase> GetSystemFolders(Guid guid)
        {
            UUID userID = new UUID(guid);
            return new List<InventoryFolderBase>(GetSystemFolders(userID).Values);
        }

        // This shouldn't be here, it should be in the inventory service.
        // But I don't want to deal with types and dependencies for now.
        private Dictionary<AssetType, InventoryFolderBase> GetSystemFolders(UUID userID)
        {
            InventoryFolderBase root = m_InventoryService.GetRootFolder(userID);
            if (root != null)
            {
                InventoryCollection content = m_InventoryService.GetFolderContent(userID, root.ID);
                if (content != null)
                {
                    Dictionary<AssetType, InventoryFolderBase> folders = new Dictionary<AssetType, InventoryFolderBase>();
                    foreach (InventoryFolderBase folder in content.Folders)
                    {
                        if ((folder.Type != (short)AssetType.Folder) && (folder.Type != (short)AssetType.Unknown))
                            folders[(AssetType)folder.Type] = folder;
                    }
                    // Put the root folder there, as type Folder
                    folders[AssetType.Folder] = root;
                    return folders;
                }
            }

            m_logger.LogWarning($"System folders for {userID} not found");
            return new Dictionary<AssetType, InventoryFolderBase>();
        }

        public InventoryItemBase GetItem(Guid guid)
        {
            return m_InventoryService.GetItem(UUID.Zero, new UUID(guid));
        }

        public InventoryFolderBase GetFolder(Guid guid)
        {
            return m_InventoryService.GetFolder(UUID.Zero, new UUID(guid));
        }

        public InventoryCollection GetFolderContent(Guid guid)
        {
            return m_InventoryService.GetFolderContent(UUID.Zero, new UUID(guid));
        }

        public List<InventoryItemBase> GetFolderItems(Guid folderID)
        {
            List<InventoryItemBase> allItems = new List<InventoryItemBase>();

            // TODO: UUID.Zero is passed as the userID here, making the old assumption that the OpenSim
            // inventory server only has a single inventory database and not per-user inventory databases.
            // This could be changed but it requirs a bit of hackery to pass another parameter into this
            // callback
            List<InventoryItemBase> items = m_InventoryService.GetFolderItems(UUID.Zero, new UUID(folderID));

            if (items != null)
            {
                allItems.InsertRange(0, items);
            }
            return allItems;
        }

        public bool CreateUsersInventory(Guid rawUserID)
        {
            UUID userID = new UUID(rawUserID);


            return m_InventoryService.CreateUserInventory(userID);
        }

        public List<InventoryItemBase> GetActiveGestures(Guid rawUserID)
        {
            UUID userID = new UUID(rawUserID);

            return m_InventoryService.GetActiveGestures(userID);
        }

        public List<InventoryFolderBase> GetInventorySkeleton(Guid rawUserID)
        {
            UUID userID = new UUID(rawUserID);
            return m_InventoryService.GetInventorySkeleton(userID);
        }

        public int GetAssetPermissions(InventoryItemBase item)
        {
            return m_InventoryService.GetAssetPermissions(item.Owner, item.AssetID);
        }

        public bool DeleteFolders(List<Guid> items)
        {
            List<UUID> uuids = new List<UUID>();
            foreach (Guid g in items)
                uuids.Add(new UUID(g));
            // oops we lost the user info here. Bad bad handlers
            return m_InventoryService.DeleteFolders(UUID.Zero, uuids);
        }

        public bool DeleteItems(List<Guid> items)
        {
            List<UUID> uuids = new List<UUID>();
            foreach (Guid g in items)
                uuids.Add(new UUID(g));
            // oops we lost the user info here. Bad bad handlers
            return m_InventoryService.DeleteItems(UUID.Zero, uuids);
        }

        public bool MoveItems(List<InventoryItemBase> items)
        {
            // oops we lost the user info here. Bad bad handlers
            // let's peek at one item
            UUID ownerID = UUID.Zero;
            if (items.Count > 0)
                ownerID = items[0].Owner;
            return m_InventoryService.MoveItems(ownerID, items);
        }
        #endregion

        /// <summary>
        /// Check that the source of an inventory request is one that we trust.
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        public bool CheckTrustSource(IPEndPoint peer)
        {
            if (m_doLookup)
            {
                m_logger.LogInformation($"Checking trusted source {peer}");

                UriBuilder ub = new UriBuilder(m_userserver_url);
                IPAddress[] uaddrs = Dns.GetHostAddresses(ub.Host);
                foreach (IPAddress uaddr in uaddrs)
                {
                    if (uaddr.Equals(peer.Address))
                    {
                        return true;
                    }
                }

                m_logger.LogWarning($"Rejecting request since source {peer} was not in the list of trusted sources");
                
                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Check that the source of an inventory request for a particular agent is a current session belonging to
        /// that agent.
        /// </summary>
        /// <param name="session_id"></param>
        /// <param name="avatar_id"></param>
        /// <returns></returns>
        public virtual bool CheckAuthSession(string session_id, string avatar_id)
        {
            return true;
        }

    }
}
