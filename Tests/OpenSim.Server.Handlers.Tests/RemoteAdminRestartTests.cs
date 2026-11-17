using System.Collections;
using System.Net;
using System.Reflection;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.ApplicationPlugins.RemoteController;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// admin_restart names one region. On a simulator that hosts several regions, only that region may be
/// restarted, unless [Startup] InworldRestartShutsDown makes any restart a restart of the whole instance.
/// </summary>
public class RemoteAdminRestartTests
{
    private sealed class RecordingRestartModule : IRestartModule
    {
        public int? ScheduledSeconds { get; private set; }
        public bool Aborted { get; private set; }

        public TimeSpan TimeUntilRestart => TimeSpan.Zero;
        public void ScheduleRestart(UUID initiator, int seconds) => ScheduledSeconds = seconds;
        public void AbortRestart(string message) => Aborted = true;
        public void DelayRestart(int seconds, string message) { }
    }

    private static (RemoteAdminPlugin Plugin, Dictionary<string, RecordingRestartModule> Modules, Dictionary<string, Scene> Scenes)
        Setup(bool inworldRestartShutsDown)
    {
        SceneHelpers sh = new();
        var scenes = new Dictionary<string, Scene>
        {
            ["Alpha"] = sh.SetupScene("Alpha", UUID.Random(), 1000, 1000),
            ["Beta"] = sh.SetupScene("Beta", UUID.Random(), 1001, 1000),
            ["Gamma"] = sh.SetupScene("Gamma", UUID.Random(), 1002, 1000),
        };

        var modules = new Dictionary<string, RecordingRestartModule>();
        foreach (var (name, scene) in scenes)
        {
            var module = new RecordingRestartModule();
            scene.RegisterModuleInterface<IRestartModule>(module);
            modules[name] = module;
        }

        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("InworldRestartShutsDown", inworldRestartShutsDown ? "true" : "false");

        IOpenSimBase application = StubProxy.Create<IOpenSimBase>((m, a) =>
            m.Name == "get_SceneManager" ? (true, sh.SceneManager) : (false, null));
        RemoteAdminPlugin plugin = UninitializedService.Create<RemoteAdminPlugin>(
            ("m_application", application), ("m_configSource", config));

        return (plugin, modules, scenes);
    }

    private static Hashtable Restart(RemoteAdminPlugin plugin, Hashtable parameters)
    {
        XmlRpcRequest request = new();
        request.Params.Add(parameters);
        XmlRpcResponse response = new() { Value = new Hashtable() };
        MethodInfo method = typeof(RemoteAdminPlugin)
            .GetMethod("XmlRpcRestartMethod", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(plugin, new object[] { request, response, new IPEndPoint(IPAddress.Loopback, 9000) });
        return (Hashtable)response.Value;
    }

    [Xunit.Fact]
    public void RestartByRegionIdSchedulesOnlyThatRegion()
    {
        var (plugin, modules, scenes) = Setup(inworldRestartShutsDown: false);

        Hashtable result = Restart(plugin, new Hashtable
        {
            ["region_id"] = scenes["Beta"].RegionInfo.RegionID.ToString(),
            ["milliseconds"] = "60000",
        });

        Xunit.Assert.Equal(true, result["success"]);
        Xunit.Assert.Equal(60, modules["Beta"].ScheduledSeconds);
        Xunit.Assert.Null(modules["Alpha"].ScheduledSeconds);
        Xunit.Assert.Null(modules["Gamma"].ScheduledSeconds);
    }

    [Xunit.Fact]
    public void RestartByRegionNameSchedulesOnlyThatRegion()
    {
        var (plugin, modules, _) = Setup(inworldRestartShutsDown: false);

        Hashtable result = Restart(plugin, new Hashtable
        {
            ["region_name"] = "Gamma",
            ["alerts"] = "30,10",
        });

        Xunit.Assert.Equal(true, result["success"]);
        Xunit.Assert.Equal(40, modules["Gamma"].ScheduledSeconds);
        Xunit.Assert.Null(modules["Alpha"].ScheduledSeconds);
        Xunit.Assert.Null(modules["Beta"].ScheduledSeconds);
    }

    [Xunit.Fact]
    public void InworldRestartShutsDownStillRestartsEveryRegion()
    {
        var (plugin, modules, _) = Setup(inworldRestartShutsDown: true);

        Hashtable result = Restart(plugin, new Hashtable { ["milliseconds"] = "30000" });

        Xunit.Assert.Equal(true, result["success"]);
        Xunit.Assert.All(modules.Values, m => Xunit.Assert.Equal(30, m.ScheduledSeconds));
    }
}
