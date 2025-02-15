using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.Framework.Interfaces
{
    public interface IOpenSimBase
    {
        /// <summary>
        /// Allow all plugin loading to be disabled for tests/debug.
        /// </summary>
        /// <remarks>
        /// true by default
        /// </remarks>
        bool EnableInitialPluginLoad { get; set; }

        /// <summary>
        /// Control whether we attempt to load an estate data service.
        /// </summary>
        /// <remarks>For tests/debugging</remarks>
        bool LoadEstateDataService { get; set; }

        ConfigSettings ConfigurationSettings { get; set; }

        /// <value>
        /// The config information passed into the OpenSimulator region server.
        /// </value>
        IConfigSource ConfigSource { get; }

        EnvConfigSource envConfigSource { get; }
        uint HttpServerPort { get; }
        IRegistryCore ApplicationRegistry { get; }
        IConfigSource Config { get; }

        /// <summary>
        /// Used by tests to suppress Environment.Exit(0) so that post-run operations are possible.
        /// </summary>
        bool SuppressExit { get; set; }

        BaseHttpServer HttpServer { get; }

        string osSecret
        {
            // Secret uuid for the simulator
            get;
        }

        SceneManager SceneManager { get; }
        NetworkServersInfo NetServersInfo { get; }
        ISimulationDataService SimulationDataService { get; }
        IEstateDataService EstateDataService { get; }

        /// <summary>
        /// Execute the region creation process.  This includes setting up scene infrastructure.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="portadd_flag"></param>
        /// <returns></returns>
        void CreateRegion(RegionInfo regionInfo, bool portadd_flag, out IScene scene);

        /// <summary>
        /// Execute the region creation process.  This includes setting up scene infrastructure.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <returns></returns>
        void CreateRegion(RegionInfo regionInfo, out IScene scene);

        /// <summary>
        /// Execute the region creation process.  This includes setting up scene infrastructure.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="portadd_flag"></param>
        /// <param name="do_post_init"></param>
        /// <returns></returns>
        void CreateRegion(RegionInfo regionInfo, bool portadd_flag, bool do_post_init, out IScene mscene);

        void RemoveRegion(Scene scene, bool cleanup);
        void RemoveRegion(string name, bool cleanUp);

        /// <summary>
        /// Remove a region from the simulator without deleting it permanently.
        /// </summary>
        /// <param name="scene"></param>
        /// <returns></returns>
        void CloseRegion(Scene scene);

        /// <summary>
        /// Remove a region from the simulator without deleting it permanently.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        void CloseRegion(string name);

        /// <summary>
        /// Get the start time and up time of Region server
        /// </summary>
        /// <param name="starttime">The first out parameter describing when the Region server started</param>
        /// <param name="uptime">The second out parameter describing how long the Region server has run</param>
        void GetRunTime(out string starttime, out string uptime);

        /// <summary>
        /// Get the number of the avatars in the Region server
        /// </summary>
        /// <param name="usernum">The first out parameter describing the number of all the avatars in the Region server</param>
        void GetAvatarNumber(out int usernum);

        /// <summary>
        /// Get the number of regions
        /// </summary>
        /// <param name="regionnum">The first out parameter describing the number of regions</param>
        void GetRegionNumber(out int regionnum);

        /// <summary>
        /// Create an estate with an initial region.
        /// </summary>
        /// <remarks>
        /// This method doesn't allow an estate to be created with the same name as existing estates.
        /// </remarks>
        /// <param name="regInfo"></param>
        /// <param name="estatesByName">A list of estate names that already exist.</param>
        /// <param name="estateName">Estate name to create if already known</param>
        /// <returns>true if the estate was created, false otherwise</returns>
        bool CreateEstate(RegionInfo regInfo, Dictionary<string, EstateSettings> estatesByName, string estateName);

        /// <summary>
        /// Load the estate information for the provided RegionInfo object.
        /// </summary>
        /// <param name="regInfo"></param>
        bool PopulateRegionEstateInfo(RegionInfo regInfo);

        /// <summary>
        /// Log information about the circumstances in which we're running (OpenSimulator version number, CLR details,
        /// etc.).
        /// </summary>
        void LogEnvironmentInformation();

        void RegisterCommonAppenders(IConfig startupConfig);

        /// <summary>
        /// Register common commands once m_console has been set if it is going to be set
        /// </summary>
        void RegisterCommonCommands();

        void RegisterCommonComponents(IConfigSource configSource);
        void HandleShow(string module, string[] cmd);
        string GetVersionText();
        void HandleThreadsAbort(string module, string[] cmd);
        void Shutdown();

        /// <summary>
        /// Performs initialisation of the scene, such as loading configuration from disk.
        /// </summary>
        void Startup();

        string StatReport(IOSHttpRequest httpRequest);
    }
}