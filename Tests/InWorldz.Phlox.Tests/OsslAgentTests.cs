using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The OSSL agent, teleport, kick, animation and group family, each ported from OSSL_Api.cs under its
/// upstream threat level through OsslGate. One dispatch test per group with an assertion on the scene.
/// </summary>
public class OsslAgentTests
{
    private readonly ITestOutputHelper _out;
    public OsslAgentTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private static SchedulerHarness Scene(string threat = "Severe")
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", threat));

    private string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    private static ScenePresence Vulnerable(SchedulerHarness h)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.Invulnerable = false;
        h.Scene.RegionInfo.RegionSettings.AllowDamage = true;
        var land = h.Scene.LandChannel?.GetLandObject(h.Prim.AbsolutePosition.X, h.Prim.AbsolutePosition.Y);
        if (land?.LandData != null) land.LandData.Flags |= (uint)ParcelFlags.AllowDamage;   // upstream's parcel rule
        return sp;
    }

    private static void PumpUntil(SchedulerHarness h, Func<bool> done, int maxMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs && !done()) h.PumpOnce();
        h.PumpFor(TimeSpan.FromMilliseconds(300));
    }

    // ------------------------------------------------------------------ damage through llDamage's door

    [Fact]
    public void CauseDamageReachesTheWornAttachmentWithTheCallerAsDetectedKey()
    {
        using var h = Scene();
        var sp = Vulnerable(h);
        var worn = SceneHelpers.AddSceneObject(h.Scene, "worn", sp.UUID);
        h.RezScriptInto(worn.RootPart, @"default {
            state_entry() { llSay(0, ""wup""); }
            on_damage(integer n) { llSay(0, ""hit by "" + llDetectedKey(0) + "" amt "" + (string)llList2Float(llDetectedDamage(0), 0)); }
        }");
        h.PumpUntil(() => h.Said.Contains("wup"));
        Assert.Contains("wup", h.Said);
        worn.AttachedAvatar = sp.UUID;
        sp.AddAttachment(worn);

        h.RezScript("default { state_entry() { osCauseDamage(\"" + sp.UUID + "\", 10.0); llSay(0, \"shot\"); } }");
        PumpUntil(h, () => h.Said.Any(s => s.StartsWith("hit by")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] health=" + sp.Health + " errors=[" + Errors(h) + "]");

        Assert.Contains("hit by " + h.Prim.UUID + " amt 10.000000", h.Said);
        Assert.Contains("shot", h.Said);
        Assert.Equal(90f, sp.Health);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void HealingHealthAndHealRateLandOnThePresence()
    {
        using var h = Scene();
        var sp = Vulnerable(h);
        sp.Health = 50f;
        h.RezScript("default { state_entry() { osSetHealRate(\"" + sp.UUID + "\", 2.5); osCauseHealing(\"" + sp.UUID + "\", 5.0); llSay(0, \"h=\" + (string)osGetHealth(\"" + sp.UUID + "\")); osSetHealth(\"" + sp.UUID + "\", 30.0); llSay(0, \"done\"); } }");
        PumpUntil(h, () => h.Said.Contains("done") && sp.Health == 30f);
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] health=" + sp.Health + " rate=" + sp.HealRate + " errors=[" + Errors(h) + "]");

        Assert.Equal(2.5f, sp.HealRate);
        Assert.Contains("h=55.000000", h.Said);
        Assert.Equal(30f, sp.Health);   // the decrease went through the damage door
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ identity

    [Fact]
    public void AvatarTypeNameAndKeyAgreeOnAPresence()
    {
        using var h = Scene();
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        h.RezScript(@"default { state_entry() {
            key k = """ + sp.UUID + @""";
            llSay(0, ""type="" + (string)osAvatarType(k) + ""|byname="" + (string)osAvatarType(""" + sp.Firstname + @""", """ + sp.Lastname + @""") + ""|bogus="" + (string)osAvatarType(""not a key""));
            llSay(0, ""name="" + osKey2Name(k));
            llSay(0, ""key="" + (string)osAvatarName2Key(""" + sp.Firstname + @""", """ + sp.Lastname + @"""));
        } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("key=")));
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Contains("type=1|byname=1|bogus=-1", h.Said);
        Assert.Contains("name=" + sp.Name, h.Said);
        Assert.Contains("key=" + sp.UUID, h.Said);
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ animation, sit, kick, die

    [Fact]
    public void PlayAndStopAnimationLandOnThePresence()
    {
        using var h = Scene();
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        var anim = UUID.Random();
        // osAvatarPlayAnimation plays an inventory or built-in animation, never a raw key (OSSL_Api.osAvatarPlayAnimation):
        // the animation is in the prim's inventory, named by its key.
        h.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(), AssetID = anim, Name = anim.ToString(),
            Type = (int)AssetType.Animation, InvType = (int)InventoryType.Animation,
        }, false);
        h.RezScript("default { state_entry() { osAvatarPlayAnimation(\"" + sp.UUID + "\", \"" + anim + "\"); llSay(0, \"playing=\" + (string)llGetListLength(llGetAnimationList(\"" + sp.UUID + "\"))); llSetTimerEvent(1.0); } timer() { llSetTimerEvent(0); osAvatarStopAnimation(\"" + sp.UUID + "\", \"" + anim + "\"); llSay(0, \"stopped\"); } }");
        PumpUntil(h, () => h.Said.Any(x => x.StartsWith("playing")));
        Assert.Contains(h.Said, x => x.StartsWith("playing"));
        Assert.True(sp.Animator.HasAnimation(anim));

        PumpUntil(h, () => h.Said.Contains("stopped"));
        Assert.False(sp.Animator.HasAnimation(anim));
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void ForceOtherSitSeatsThePresenceOnThePrim()
    {
        using var h = Scene();
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        h.RezScript("default { state_entry() { llSitTarget(<0,0,1>, ZERO_ROTATION); osForceOtherSit(\"" + sp.UUID + "\"); llSay(0, \"sat\"); } }");
        PumpUntil(h, () => h.Said.Contains("sat") && sp.IsSatOnObject);
        _out.WriteLine("sat=" + sp.IsSatOnObject + " parent=" + sp.ParentID + " errors=[" + Errors(h) + "]");

        Assert.True(sp.IsSatOnObject);
        Assert.Equal(h.Prim.LocalId, sp.ParentID);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void KickClosesThePresence()
    {
        using var h = Scene();
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        h.RezScript("default { state_entry() { osKickAvatar(\"" + sp.UUID + "\", \"bye\"); llSay(0, \"kicked\"); } }");
        PumpUntil(h, () => h.Scene.GetScenePresence(sp.UUID) == null);
        h.PumpUntil(() => h.Said.Contains("kicked"));

        Assert.Contains("kicked", h.Said);
        Assert.Null(h.Scene.GetScenePresence(sp.UUID));
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void DieDeletesOnlyAnObjectThisPrimRezzed()
    {
        using var h = Scene();
        var mine = SceneHelpers.AddSceneObject(h.Scene, "rezzed by me", h.Prim.OwnerID);
        mine.RezzerID = h.Prim.ParentGroup.UUID;
        var other = SceneHelpers.AddSceneObject(h.Scene, "someone else's rez", h.Prim.OwnerID);
        h.RezScript("default { state_entry() { osDie(\"" + mine.UUID + "\"); osDie(\"" + other.UUID + "\"); llSay(0, \"done\"); } }");
        PumpUntil(h, () => h.Said.Contains("done"));

        Assert.Null(h.Scene.GetSceneObjectGroup(mine.UUID));
        Assert.NotNull(h.Scene.GetSceneObjectGroup(other.UUID));
        Assert.Empty(Errors(h));
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void TheGateHoldsAtItsUpstreamLevel()
    {
        using var h = Scene("VeryLow");
        var sp = h.Scene.GetScenePresence(h.AddClient().AgentId);
        h.RezScript("default { state_entry() { osKickAvatar(\"" + sp.UUID + "\", \"bye\"); llSay(0, \"after\"); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osKickAvatar permission denied")));

        Assert.DoesNotContain("after", h.Said);
        Assert.Single(h.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osKickAvatar permission denied"));
        Assert.NotNull(h.Scene.GetScenePresence(sp.UUID));
    }
}
