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

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Console;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autofac;

namespace OpenSim.Server.Handlers.Asset
{
    public class AssetServiceConnector(
        IComponentContext componentContext,
        IConfiguration configuration,
        ILogger<AssetServiceConnector> logger)
        : IServiceConnector
    {
        private IAssetService _assetService;

        public string ConfigName { get; private set; }
        public IHttpServer HttpServer { get; private set; }

        public void Initialize(IHttpServer httpServer, string configName = "AssetService")
        {
            HttpServer = httpServer;
            ConfigName = configName;

            var serverConfig = configuration.GetSection(ConfigName);
            if (serverConfig.Exists() is false)
                throw new Exception($"No section {ConfigName} in config file");

            string assetService = serverConfig.GetValue<string>("LocalServiceModule", String.Empty);
            if (string.IsNullOrEmpty(assetService))
                throw new Exception("No LocalServiceModule in config file");

            var serviceName = assetService.Split(":")[1];
            _assetService = componentContext.ResolveNamed<IAssetService>(serviceName);

            if (_assetService == null)
            {
                throw new Exception($"Failed to load AssetService from {assetService}; config is {ConfigName}");
            }

            var allowDelete = serverConfig.GetValue<bool>("AllowRemoteDelete", false);
            var allowDeleteAllTypes = serverConfig.GetValue<bool>("AllowRemoteDeleteAllTypes", false);
            var redirectUrl = serverConfig.GetValue<string>("RedirectURL", string.Empty);

            AllowedRemoteDeleteTypes allowedRemoteDeleteTypes;

            if (!allowDelete)
            {
                allowedRemoteDeleteTypes = AllowedRemoteDeleteTypes.None;
            }
            else
            {
                if (allowDeleteAllTypes)
                    allowedRemoteDeleteTypes = AllowedRemoteDeleteTypes.All;
                else
                    allowedRemoteDeleteTypes = AllowedRemoteDeleteTypes.MapTile;
            }

            IServiceAuth auth = ServiceAuth.Create(configuration, ConfigName);

            HttpServer.AddStreamHandler(new AssetServerGetHandler(logger, _assetService, auth, redirectUrl));
            HttpServer.AddStreamHandler(new AssetServerPostHandler(_assetService, auth));
            HttpServer.AddStreamHandler(new AssetServerDeleteHandler(logger, _assetService, allowedRemoteDeleteTypes, auth));
            HttpServer.AddStreamHandler(new AssetsExistHandler(_assetService));

            MainConsole.Instance.Commands.AddCommand("Assets", false,
                    "show asset",
                    "show asset <ID>",
                    "Show asset information",
                    HandleShowAsset);

            MainConsole.Instance.Commands.AddCommand("Assets", false,
                    "delete asset",
                    "delete asset <ID>",
                    "Delete asset from database",
                    HandleDeleteAsset);

            MainConsole.Instance.Commands.AddCommand("Assets", false,
                    "dump asset",
                    "dump asset <ID>",
                    "Dump asset to a file",
                    "The filename is the same as the ID given.",
                    HandleDumpAsset);
        }

        void HandleDeleteAsset(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: delete asset <ID>");
                return;
            }

            AssetBase asset = _assetService.Get(args[2]);

            if (asset == null || asset.Data.Length == 0)
            {
                MainConsole.Instance.Output("Could not find asset with ID {0}", args[2]);
                return;
            }

            if (!_assetService.Delete(asset.ID))
                MainConsole.Instance.Output("ERROR: Could not delete asset {0} {1}", asset.ID, asset.Name);
            else
                MainConsole.Instance.Output("Deleted asset {0} {1}", asset.ID, asset.Name);
        }

        void HandleDumpAsset(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage is dump asset <ID>");
                return;
            }

            UUID assetId;
            string rawAssetId = args[2];

            if (!UUID.TryParse(rawAssetId, out assetId))
            {
                MainConsole.Instance.Output("ERROR: {0} is not a valid ID format", rawAssetId);
                return;
            }

            AssetBase asset = _assetService.Get(assetId.ToString());
            if (asset == null)
            {
                MainConsole.Instance.Output("ERROR: No asset found with ID {0}", assetId);
                return;
            }

            string fileName = rawAssetId;

            if (!ConsoleUtil.CheckFileDoesNotExist(MainConsole.Instance, fileName))
                return;

            using (FileStream fs = new FileStream(fileName, FileMode.CreateNew))
            {
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(asset.Data);
                }
            }

            MainConsole.Instance.Output("Asset dumped to file {0}", fileName);
        }

        void HandleShowAsset(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: show asset <ID>");
                return;
            }

            AssetBase asset = _assetService.Get(args[2]);

            if (asset == null || asset.Data.Length == 0)
            {
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            int i;

            MainConsole.Instance.Output("Name: {0}", asset.Name);
            MainConsole.Instance.Output("Description: {0}", asset.Description);
            MainConsole.Instance.Output("Type: {0} (type number = {1})", (AssetType)asset.Type, asset.Type);
            MainConsole.Instance.Output("Content-type: {0}", asset.Metadata.ContentType);
            MainConsole.Instance.Output("Size: {0} bytes", asset.Data.Length);
            MainConsole.Instance.Output("Temporary: {0}", asset.Temporary ? "yes" : "no");
            MainConsole.Instance.Output("Flags: {0}", asset.Metadata.Flags);

            for (i = 0 ; i < 5 ; i++)
            {
                int off = i * 16;
                if (asset.Data.Length <= off)
                    break;
                int len = 16;
                if (asset.Data.Length < off + len)
                    len = asset.Data.Length - off;

                byte[] line = new byte[len];
                Array.Copy(asset.Data, off, line, 0, len);

                string text = BitConverter.ToString(line);
                MainConsole.Instance.Output(String.Format("{0:x4}: {1}", off, text));
            }
        }
    }
}
