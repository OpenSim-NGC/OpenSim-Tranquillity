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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using OpenSim.Framework;
using OpenSim.Region.Framework;

namespace OpenSim.ApplicationPlugins.LoadRegions
{
    public class LoadRegionsPlugin : IApplicationPlugin, IRegionCreator
    {
        public event NewRegionCreated OnNewRegionCreated;
        private NewRegionCreated m_newRegionCreatedHandler;

        #region IApplicationPlugin Members

        // TODO: required by IPlugin, but likely not at all right
        private string m_name = "LoadRegionsPlugin";
        private string m_version = "0.0";

        private IConfiguration _configuration;
        private ILogger<LoadRegionsPlugin> _logger;

        public string Version
        {
            get { return m_version; }
        }

        public string Name
        {
            get { return m_name; }
        }

        protected IOpenSimBase m_openSim;

        public LoadRegionsPlugin(
            IConfiguration configuration,
            ILogger<LoadRegionsPlugin> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public void Initialise()
        {
            _logger.LogError($"[LOAD REGIONS PLUGIN]: {Name} cannot be default-initialized!");
            throw new PluginNotInitialisedException(Name);
        }

        public void Initialise(IOpenSimBase openSim)
        {
            m_openSim = openSim;
            m_openSim.ApplicationRegistry.RegisterInterface<IRegionCreator>(this);
        }

        public void PostInitialise()
        {
            //_logger.LogInformation("[LOADREGIONS]: Load Regions addin being initialised");

            IEstateLoader estateLoader = null;
            IRegionLoader regionLoader;

            if (_configuration.GetSection("Startup").GetValue<string>("region_info_source", "filesystem") == "filesystem")
            {
                _logger.LogInformation("[LOAD REGIONS PLUGIN]: Loading region configurations from filesystem");

                regionLoader = new RegionLoaderFileSystem();
                estateLoader = new EstateLoaderFileSystem(m_openSim);
            }
            else
            {
                _logger.LogInformation("[LOAD REGIONS PLUGIN]: Loading region configurations from web");
                regionLoader = new RegionLoaderWebServer();
            }

            // Load Estates Before Regions!
            if(estateLoader != null)
            {
                estateLoader.SetIniConfigSource(_configuration);
                estateLoader.LoadEstates();
            }

            regionLoader.SetIniConfigSource(_configuration);
            RegionInfo[] regionsToLoad = regionLoader.LoadRegions();

            _logger.LogInformation("[LOAD REGIONS PLUGIN]: Loading specific shared modules...");
            //_logger.LogInformation("[LOAD REGIONS PLUGIN]: DynamicTextureModule...");
            //m_openSim.ModuleLoader.LoadDefaultSharedModule(new DynamicTextureModule());
            //_logger.LogInformation("[LOAD REGIONS PLUGIN]: LoadImageURLModule...");
            //m_openSim.ModuleLoader.LoadDefaultSharedModule(new LoadImageURLModule());
            //_logger.LogInformation("[LOAD REGIONS PLUGIN]: XMLRPCModule...");
            //m_openSim.ModuleLoader.LoadDefaultSharedModule(new XMLRPCModule());
//            _logger.LogInformation("[LOADREGIONSPLUGIN]: AssetTransactionModule...");
//            m_openSim.ModuleLoader.LoadDefaultSharedModule(new AssetTransactionModule());
            _logger.LogInformation("[LOAD REGIONS PLUGIN]: Done.");

            if (!CheckRegionsForSanity(regionsToLoad))
            {
                _logger.LogError($"[LOAD REGIONS PLUGIN]: Halting startup due to conflicts in region configurations");
                Environment.Exit(1);
            }

            List<IScene> createdScenes = new List<IScene>();

            for (int i = 0; i < regionsToLoad.Length; i++)
            {
                IScene scene;
                
                bool changed = m_openSim.PopulateRegionEstateInfo(regionsToLoad[i]);

                m_openSim.CreateRegion(regionsToLoad[i], true, out scene);
                createdScenes.Add(scene);

                if (changed)
                    m_openSim.EstateDataService.StoreEstateSettings(regionsToLoad[i].EstateSettings);
            }

            foreach (IScene scene in createdScenes)
            {
                scene.Start();

                m_newRegionCreatedHandler = OnNewRegionCreated;
                if (m_newRegionCreatedHandler != null)
                {
                    m_newRegionCreatedHandler(scene);
                }
            }
        }

        public void Dispose()
        {
        }

        #endregion

        /// <summary>
        /// Check that region configuration information makes sense.
        /// </summary>
        /// <param name="regions"></param>
        /// <returns>True if we're sane, false if we're insane</returns>
        private bool CheckRegionsForSanity(RegionInfo[] regions)
        {
            if (regions.Length == 0)
                return true;

            foreach (RegionInfo region in regions)
            {
                if (region.RegionID.IsZero())
                {
                    _logger.LogError($"[LOAD REGIONS PLUGIN]: Region {region.RegionName} has invalid UUID {region.RegionID}");
                    return false;
                }
            }

            for (int i = 0; i < regions.Length - 1; i++)
            {
                for (int j = i + 1; j < regions.Length; j++)
                {
                    if (regions[i].RegionID == regions[j].RegionID)
                    {
                        _logger.LogError($"[LOAD REGIONS PLUGIN]: Regions {regions[i].RegionName} and {regions[j].RegionName} have the same UUID {regions[i].RegionID}");
                        return false;
                    }
                    else if (
                        regions[i].RegionLocX == regions[j].RegionLocX && regions[i].RegionLocY == regions[j].RegionLocY)
                    {
                        _logger.LogError($"[LOAD REGIONS PLUGIN]: Regions {regions[i].RegionName} and {regions[j].RegionName} have the same grid location ({regions[i].RegionLocX}, {regions[i].RegionLocY})");
                        return false;
                    }
                    else if (regions[i].InternalEndPoint.Port == regions[j].InternalEndPoint.Port)
                    {
                        _logger.LogError($"[LOAD REGIONS PLUGIN]: Regions {regions[i].RegionName} and {regions[j].RegionName} have the same internal IP port {regions[i].InternalEndPoint.Port}");
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
