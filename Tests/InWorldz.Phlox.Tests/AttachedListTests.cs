using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetAttachedList leaves out HUD attachments, as Halcyon (ScenePresence.CollectVisibleAttachmentIds,
/// which skips IsAttachedHUD) and the SL wiki ("does not include HUD attachments") do.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class AttachedListTests
{
    private readonly ITestOutputHelper _out;
    public AttachedListTests(ITestOutputHelper o) => _out = o;

    private static SceneObjectGroup Wear(SchedulerHarness h, ScenePresence sp, uint point)
    {
        var sog = SceneHelpers.AddSceneObject(h.Scene, "worn at " + point, sp.UUID);
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = point;
        sp.AddAttachment(sog);
        return sog;
    }

    [Fact]
    public void HudAttachmentsAreLeftOut()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);

        var chest = Wear(h, sp, (uint)AttachmentPoint.Chest);
        var belowHuds = Wear(h, sp, (uint)AttachmentPoint.HUDCenter2 - 1);
        var hudFirst = Wear(h, sp, (uint)AttachmentPoint.HUDCenter2);
        var hudLast = Wear(h, sp, (uint)AttachmentPoint.HUDBottomRight);
        var aboveHuds = Wear(h, sp, (uint)AttachmentPoint.HUDBottomRight + 1);

        h.RezScript($"default {{ state_entry() {{ llSay(0, \"worn=\" + llDumpList2String(llGetAttachedList(\"{sp.UUID}\"), \",\")); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("worn=")));
        var line = h.Said.FirstOrDefault(s => s.StartsWith("worn="));
        Assert.True(line != null, string.Join(" | ", h.Said));
        _out.WriteLine(line);
        var worn = line.Substring("worn=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        Assert.Equal(new[] { chest.UUID, belowHuds.UUID, aboveHuds.UUID }.Select(u => u.ToString()).ToHashSet(), worn);
        Assert.DoesNotContain(hudFirst.UUID.ToString(), worn);
        Assert.DoesNotContain(hudLast.UUID.ToString(), worn);
    }

    [Fact]
    public void AnAvatarWearingOnlyHudsHasAnEmptyList()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        Wear(h, sp, (uint)AttachmentPoint.HUDCenter);

        h.RezScript($"default {{ state_entry() {{ list l = llGetAttachedList(\"{sp.UUID}\"); llSay(0, \"count=\" + (string)llGetListLength(l)); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("count=")));
        Assert.Contains("count=0", h.Said);
    }
}
