using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetMass's scope follows YEngine (LSL_Api.llGetMass): the whole object's mass
/// (m_host.ParentGroup.GetMass()) from any prim of it, and the wearer's mass from an attachment.
/// Before, Phlox returned the script's own prim only. llGetMassMKS stays 100 x llGetMass.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class MassScopeTests
{
    private readonly ITestOutputHelper _out;
    public MassScopeTests(ITestOutputHelper o) => _out = o;

    private const float RootMass = 1.25f, ChildMass = 2.5f, AvatarMass = 70f;
    private const string Report = "default { state_entry() { llSay(0, \"mass=\" + (string)llGetMass() + \" mks=\" + (string)llGetMassMKS()); } }";

    private sealed class Weighted : NullPhysicsActor
    {
        private readonly float m_mass;
        public Weighted(float mass) => m_mass = mass;
        public override float Mass => m_mass;
    }

    /// <summary>The harness prim plus one linked child, each with a known mass.</summary>
    private static SceneObjectPart TwoPrimLinkset(SchedulerHarness h)
    {
        var other = SceneHelpers.AddSceneObject(h.Scene, "child", h.Prim.OwnerID);
        h.Prim.ParentGroup.LinkToGroup(other);
        var child = h.Prim.ParentGroup.Parts.Single(p => p != h.Prim);
        h.Prim.PhysActor = new Weighted(RootMass);
        child.PhysActor = new Weighted(ChildMass);
        Assert.Equal(2, h.Prim.ParentGroup.PrimCount);
        return child;
    }

    private (float Mass, float Mks) Run(SchedulerHarness h)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !h.Said.Any(s => s.StartsWith("mass="))) h.PumpOnce();
        var line = h.Said.FirstOrDefault(s => s.StartsWith("mass="));
        _out.WriteLine(line ?? "(nothing said) " + string.Join(" | ", h.Said));
        Assert.NotNull(line);
        var parts = line!.Split(' ');
        float mass = float.Parse(parts[0].Substring(5), CultureInfo.InvariantCulture);
        float mks = float.Parse(parts[1].Substring(4), CultureInfo.InvariantCulture);
        Assert.InRange(mks, mass * 100 * 0.999f, mass * 100 * 1.001f);
        return (mass, mks);
    }

    [Fact]
    public void RootPrimOfALinksetReportsTheWholeObject()
    {
        using var h = new SchedulerHarness();
        TwoPrimLinkset(h);
        h.RezScript(Report);
        Assert.Equal(RootMass + ChildMass, Run(h).Mass, 3);
    }

    [Fact]
    public void ChildPrimOfALinksetReportsTheWholeObject()
    {
        using var h = new SchedulerHarness();
        var child = TwoPrimLinkset(h);
        h.RezScriptInto(child, Report);
        Assert.Equal(RootMass + ChildMass, Run(h).Mass, 3);
    }

    [Fact]
    public void AnAttachmentReportsTheWearersMass()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new Weighted(RootMass);
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        typeof(ScenePresence).GetProperty("PhysicsActor")!.GetSetMethod(true)!.Invoke(sp, new object[] { new Weighted(AvatarMass) });
        Assert.Equal(AvatarMass, sp.GetMass());
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sp.AddAttachment(sog);
        h.RezScript(Report);
        Assert.Equal(AvatarMass, Run(h).Mass, 3);
    }
}
