using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwCheckRezError(pos, isTemp, landImpact) asks the region whether the owner could rez that many prims at pos
/// (Halcyon: Scene.CheckRezError, OpenSim/Region/Framework/Scenes/Scene.cs:2341-2367). The answer comes from the checks a
/// rez itself goes through: no parcel at pos is IW_REZ_NO_LAND_PARCEL; the region's rez permission
/// (Scene.Permissions.CanRezObject) refusing the owner there is IW_REZ_NOT_PERMITTED; the same check refusing that many
/// more prims is IW_REZ_PARCEL_LAND_IMPACT; otherwise IW_REZ_OK. The values are the IW_REZ_* constants of Halcyon's
/// compiler (OK 0, NOT_PERMITTED 1, REGION_SCENIC 2, NO_LAND_PARCEL 3, PARCEL_LAND_IMPACT 4, REGION_LAND_IMPACT 5).
/// The land is three strips (x &lt; 86, x &lt; 172, the rest); the test's permission handlers refuse the owner on the
/// first and allow 10 more prims on the second; past x = 240 there is no parcel.
/// No process-wide state, so the class runs in parallel.
/// </summary>
public class CheckRezErrorTests
{
    private readonly ITestOutputHelper _out;
    public CheckRezErrorTests(ITestOutputHelper o) => _out = o;

    /// <summary>The strips, with no parcel past x = 240.</summary>
    public class LandWithAHole : DispatchProxy
    {
        public ILandChannel Inner;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(ILandChannel.GetLandObject) && args.Length == 2
                && Convert.ToSingle(args[0]) >= 240f)
                return null;
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
    }

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            h.PumpOnce();
            Thread.Sleep(1);
        }
        return true;
    }

    [Fact]
    public void EachRezErrorIsReportedWithHalcyonsConstants()
    {
        using var h = new SchedulerHarness();
        var owner = h.Prim.OwnerID;
        var land = new StripLand(h.Scene, (86, UUID.Random()), (172, owner), (256, UUID.Random()));
        var channel = DispatchProxy.Create<ILandChannel, LandWithAHole>();
        ((LandWithAHole)(object)channel).Inner = land;
        h.Scene.LandChannel = channel;

        var asked = new List<(int Count, float X)>();
        h.Scene.Permissions.OnRezObject += (count, who, pos) =>
        {
            lock (asked) asked.Add((count, pos.X));
            if (who != owner) return false;
            if (pos.X < 86) return false;                     // not the owner's land, no rez rights there
            if (pos.X < 172) return count <= 10;              // the owner's land: 10 prims left
            return true;
        };

        h.RezScript(
            "default { state_entry() {\n" +
            "  llSay(0, \"forbidden \" + (string)iwCheckRezError(<40, 128, 25>, FALSE, 1));\n" +
            "  llSay(0, \"fits \" + (string)iwCheckRezError(<128, 128, 25>, FALSE, 10));\n" +
            "  llSay(0, \"full \" + (string)iwCheckRezError(<128, 128, 25>, TRUE, 11));\n" +
            "  llSay(0, \"open \" + (string)iwCheckRezError(<200, 128, 25>, FALSE, 500));\n" +
            "  llSay(0, \"nowhere \" + (string)iwCheckRezError(<250, 128, 25>, FALSE, 1));\n" +
            "  llSay(0, \"names \" + (string)[IW_REZ_OK, IW_REZ_NOT_PERMITTED, IW_REZ_REGION_SCENIC, IW_REZ_NO_LAND_PARCEL, IW_REZ_PARCEL_LAND_IMPACT, IW_REZ_REGION_LAND_IMPACT]);\n" +
            "  llSay(0, \"done\");\n" +
            "} }");
        Assert.True(PumpUntil(h, () => h.Said.Contains("done"), TimeSpan.FromSeconds(30)),
            "the script never finished: " + string.Join(" | ", h.Said));
        _out.WriteLine(string.Join("\n", h.Said));

        Assert.Contains("forbidden 1", h.Said);
        Assert.Contains("fits 0", h.Said);
        Assert.Contains("full 4", h.Said);
        Assert.Contains("open 0", h.Said);
        Assert.Contains("nowhere 3", h.Said);
        Assert.Contains("names 012345", h.Said);

        // The permission is asked first with no prims (as Halcyon asked it), then with the prims the rez would add.
        lock (asked)
        {
            Assert.Contains((0, 40f), asked);
            Assert.DoesNotContain(asked, a => a.X == 250f);
            Assert.Contains((11, 128f), asked);
        }
    }
}
