using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The SL damage events off the one door, in SL order: on_damage (before, adjustable,
/// waited on), apply, final_damage (what landed), on_death. Wiki: on_damage, final_damage,
/// llDetectedDamage ([damage, damage_type, original_damage]), llAdjustDamage(number, new_damage), llDamage.
/// The harness drives the scheduler by hand, so the region-side call that waits on on_damage runs on a
/// second thread while the test thread pumps - the same shape as production, where physics or an async
/// syscall thread waits and the script thread runs the handlers.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class DamageEventsTests
{
    private readonly ITestOutputHelper _out;
    public DamageEventsTests(ITestOutputHelper o) { _out = o; }

    private static ScenePresence Vulnerable(SchedulerHarness h)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        Assert.NotNull(sp);
        sp.Invulnerable = false;
        h.Scene.RegionInfo.RegionSettings.AllowDamage = true;
        return sp;
    }

    private static void Wear(ScenePresence sp, SceneObjectGroup sog)
    {
        sog.AttachedAvatar = sp.UUID;
        sp.AddAttachment(sog);
    }

    /// <summary>Run the region-side damage on another thread and pump the scheduler until it returns.</summary>
    private static void DamageWhilePumping(SchedulerHarness h, Action damage, Func<bool> landed, int maxMs = 30000)
    {
        var t = Task.Run(damage);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted && sw.ElapsedMilliseconds < maxMs) h.PumpOnce();
        Assert.True(t.IsCompleted, "the damage call did not return");
        t.GetAwaiter().GetResult();
        h.PumpFor(TimeSpan.FromMilliseconds(300));   // let final_damage handlers run
        h.PumpUntil(landed);                         // And wait for the line the caller asserts
    }

    private const string Halver = @"default {
        state_entry() { llSay(0, ""up""); }
        on_damage(integer n) {
            list d = llDetectedDamage(0);
            llSay(0, ""od="" + (string)n + "":"" + (string)llList2Float(d, 0) + ""/"" + (string)llList2Integer(d, 1) + ""/"" + (string)llList2Float(d, 2) + "" from "" + llDetectedKey(0));
            llAdjustDamage(0, llList2Float(d, 0) / 2.0);
        }
        final_damage(integer n) {
            list d = llDetectedDamage(0);
            llSay(0, ""fd="" + (string)n + "":"" + (string)llList2Float(d, 0) + ""/"" + (string)llList2Integer(d, 1) + ""/"" + (string)llList2Float(d, 2));
        }
    }";

    [Fact]
    public void OnDamageCanHalveTheDamageAndFinalDamageReportsWhatLanded()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        h.RezScript(Halver);
        h.PumpUntil(() => h.Said.Contains("up"));
        Assert.Contains("up", h.Said);
        Wear(sp, h.Prim.ParentGroup);

        var src = UUID.Random();
        DamageWhilePumping(h, () => sp.ApplyDamage(src, UUID.Random(), 0, 20f, 5 /* DAMAGE_TYPE_FIRE */, true),
            () => h.Said.Contains("fd=1:10.000000/5/20.000000"));

        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Contains(h.Said, s => s.StartsWith("od=1:20.000000/5/20.000000 from " + src));
        Assert.Contains("fd=1:10.000000/5/20.000000", h.Said);
        Assert.Equal(90f, sp.Health);
    }

    [Fact]
    public void AnAttachmentWithoutOnDamageStillGetsFinalDamage()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        h.RezScript(Halver);
        var second = SceneHelpers.AddSceneObject(h.Scene, "second attachment", sp.UUID);
        h.RezScriptInto(second.RootPart, @"default {
            state_entry() { llSay(0, ""up2""); }
            final_damage(integer n) { llSay(0, ""fd2="" + (string)llList2Float(llDetectedDamage(0), 0)); }
        }");
        h.PumpUntil(() => h.Said.Contains("up") && h.Said.Contains("up2"));
        Assert.Contains("up", h.Said);
        Assert.Contains("up2", h.Said);
        Wear(sp, h.Prim.ParentGroup);
        Wear(sp, second);

        DamageWhilePumping(h, () => sp.ApplyDamage(UUID.Random(), UUID.Random(), 0, 20f, 0, true),
            () => h.Said.Contains("fd2=10.000000"));

        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Contains("fd2=10.000000", h.Said);       // the halver's adjustment is what everyone sees land
        Assert.Equal(90f, sp.Health);
    }

    [Fact]
    public void LlDamageFromAPrimReachesTheWornAttachmentWithThePrimAsDetectedKey()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        // the worn attachment: a second prim so the harness prim can be the shooter
        var worn = SceneHelpers.AddSceneObject(h.Scene, "worn", sp.UUID);
        h.RezScriptInto(worn.RootPart, @"default {
            state_entry() { llSay(0, ""wup""); }
            on_damage(integer n) { llSay(0, ""hit by "" + llDetectedKey(0) + "" owner "" + llDetectedOwner(0) + "" amt "" + (string)llList2Float(llDetectedDamage(0), 0)); }
        }");
        h.PumpUntil(() => h.Said.Contains("wup"));
        Assert.Contains("wup", h.Said);
        Wear(sp, worn);

        h.RezScript("default { state_entry() { llDamage(\"" + sp.UUID + "\", 10, DAMAGE_TYPE_GENERIC); llSay(0, \"shot\"); } }");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30000 && !(h.Said.Any(s => s.StartsWith("hit by")) && h.Said.Contains("shot"))) h.PumpOnce();
        h.PumpFor(TimeSpan.FromMilliseconds(300));

        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "]");
        Assert.Contains("hit by " + h.Prim.UUID + " owner " + h.Prim.OwnerID + " amt 10.000000", h.Said);
        Assert.Contains("shot", h.Said);
        Assert.Equal(90f, sp.Health);
    }

    [Fact]
    public void LlAdjustDamageOutsideOnDamageIsAnErrorNotAChange()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        h.RezScript("default { state_entry() { llAdjustDamage(0, 1.0); llSay(0, \"after\"); } }");
        h.PumpFor(TimeSpan.FromSeconds(1));
        h.PumpUntil(() => h.Said.Contains("after") && h.SaidOn.Any(s => s.Channel == 0x7FFFFFFF && s.Message.Contains("llAdjustDamage")));

        Assert.Contains("after", h.Said);
        Assert.Contains(h.SaidOn, s => s.Channel == 0x7FFFFFFF && s.Message.Contains("llAdjustDamage"));
        Assert.Equal(100f, sp.Health);
    }
}
