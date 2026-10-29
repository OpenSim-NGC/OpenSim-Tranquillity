using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetSimulatorHostname and llGetEnv("simulator_hostname") give the region's configured external
/// host name, as Halcyon (Scene.GetEnv "simulator_hostname" => RegionInfo.ExternalHostName) does,
/// not the name of the machine the simulator runs on.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SimulatorHostnameTests
{
    private readonly ITestOutputHelper _out;
    public SimulatorHostnameTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void TheHostnameIsTheRegionsExternalHostName()
    {
        using var h = new SchedulerHarness();
        const string external = "region-host.example.net";
        h.Scene.RegionInfo.ExternalHostName = external;
        Assert.NotEqual(external, System.Net.Dns.GetHostName());

        h.RezScript("default { state_entry() { llSay(0, \"host=\" + llGetSimulatorHostname()); llSay(0, \"env=\" + llGetEnv(\"simulator_hostname\")); } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("env=")));
        _out.WriteLine(string.Join(" | ", h.Said));
        Assert.Contains("host=" + external, h.Said);
        Assert.Contains("env=" + external, h.Said);
    }
}
