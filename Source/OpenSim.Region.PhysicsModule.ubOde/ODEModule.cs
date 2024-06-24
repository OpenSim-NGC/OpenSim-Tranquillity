
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using OpenMetaverse;

using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    class ubOdeModule : INonSharedRegionModule
    {
        ODEScene m_odeScene = null;

        private string m_libVersion = string.Empty;

        private bool m_Enabled = false;
        
        private readonly IConfiguration m_config;

        private readonly ILogger<ubOdeModule> m_logger;
        
        public ubOdeModule(IConfiguration configuration, ILogger<ubOdeModule> logger)
        {
            m_config = configuration;
            m_logger = logger;
        }

        #region INonSharedRegionModule

        public string Name
        {
            get { return "ubODE"; }
        }

        public string Version
        {
            get { return "1.0"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfiguration pConfiguration)
        {
            // NOTE: we don't use the configuration passed in, we use the one passed in the constructor
            var config = m_config.GetSection("Startup");
            if (config.Exists() is false)
            {
                string physics = config.GetValue("physics", string.Empty);
                if (physics == Name)
                {
                    string mesher = config.GetValue("meshing", string.Empty);                   
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("ubODEMeshmerizer"))
                    {
                        m_logger.LogError("Opensim.ini meshing option must be set to \"ubODEMeshmerizer\"");
                        return;
                    }

                    DllmapConfigHelper.RegisterAssembly(typeof(ubOdeModule).Assembly);

                    UBOdeNative.InitODE();

                    string ode_config = UBOdeNative.GetConfiguration();
                    if (string.IsNullOrEmpty(ode_config))
                    {
                        m_logger.LogError("[ubODE] Native ode library version not supported");
                        return;
                    }

                    int indx = ode_config.IndexOf("ODE_OPENSIM");
                    if (indx < 0)
                    {
                        m_logger.LogError("[ubODE] Native ode library version not supported");
                        return;
                    }

                    indx += 12;
                    if (indx >= ode_config.Length)
                    {
                        m_logger.LogError("[ubODE] Native ode library version not supported");
                        return;
                    }

                    m_libVersion = ode_config.Substring(indx);
                    if (string.IsNullOrEmpty(m_libVersion))
                    {
                        m_logger.LogError("[ubODE] Native ode library version not supported");
                        return;
                    }

                    m_libVersion.Trim();
                    if(m_libVersion.StartsWith("OS"))
                        m_libVersion = m_libVersion.Substring(2);

                    m_logger.LogInformation($"[ubODE] ode library configuration: {ode_config}");
                    m_Enabled = true;
                }
            }
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            var sp = OpenSimServer.Instance.ServiceProvider;
            m_odeScene = new ODEScene(sp, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<ODEScene>>(),
                                        scene, Name, Version + "-" + m_libVersion);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            // a odescene.dispose is called later directly by scene.cs
            // since it is seen as a module interface

            m_odeScene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            if(m_odeScene != null)
                m_odeScene.RegionLoaded();

        }
        #endregion
    }
}
