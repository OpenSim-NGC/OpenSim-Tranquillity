using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Damage reaches ScenePresence.Health through ONE door, ApplyDamage, and nothing
/// changes for the caller: a collision with a Damage-bearing prim, a fall onto the ground and a
/// scripted llAdjustDamage take exactly what they took before, and Health 0 still fires
/// EventManager.OnAvatarKilled. These are characterisation tests - green before the refactor, green
/// after - so the PART 2 events can hang off the door without moving a number.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class DamageDoorTests
{
    private static ScenePresence Vulnerable(SchedulerHarness h)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        Assert.NotNull(sp);
        sp.Invulnerable = false;                       // the test scene has no damage parcel
        h.Scene.RegionInfo.RegionSettings.AllowDamage = true;
        Assert.Equal(100f, sp.Health);
        return sp;
    }

    private static void Collide(ScenePresence sp, uint localId, float relativeSpeed)
    {
        var cp = new ContactPoint(Vector3.Zero, Vector3.UnitZ, 0f) { RelativeSpeed = relativeSpeed };
        sp.PhysicsCollisionUpdate(new CollisionEventUpdate(new Dictionary<uint, ContactPoint> { { localId, cp } }));
    }

    [Fact]
    public void CollisionWithADamagePrimTakesItsDamageAndTheDamagePrimDies()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        var sog = SceneHelpers.AddSceneObject(h.Scene, "spike", UUID.Random());
        sog.Damage = 10f;

        Collide(sp, sog.RootPart.LocalId, 0f);

        Assert.Equal(90f, sp.Health);
        Assert.Null(h.Scene.GetSceneObjectPart(sog.RootPart.LocalId));   // llSetDamage prims die on contact
    }

    [Fact]
    public void GroundFallTakesTheVelocitySquaredRule()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);

        Collide(sp, 0, -10f);                          // 0.01 * 10 * 10

        Assert.Equal(99f, sp.Health, 3);
        Collide(sp, 0, -4f);                           // under the -5 threshold: nothing
        Assert.Equal(99f, sp.Health, 3);
    }

    // PART 1 drove this through llAdjustDamage(key, amount), the OpenSim-form door; PART 2 gave that name
    // its SL signature (number, new_damage), so the scripted door is llDamage now - same door, same numbers.
    [Fact]
    public void ScriptedDamageTakesTheAmountAndAHealClampsAt100()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        h.RezScript("default { state_entry() { llDamage(\"" + sp.UUID + "\", 30, DAMAGE_TYPE_GENERIC); llSay(0, \"h=\" + (string)llGetHealth(\"" + sp.UUID + "\")); llDamage(\"" + sp.UUID + "\", -50, DAMAGE_TYPE_GENERIC); llSay(0, \"h2=\" + (string)llGetHealth(\"" + sp.UUID + "\")); } }");
        // Until the script has said both lines (was a fixed 3 s window, too short under load)
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !h.Said.Any(s => s.StartsWith("h2=", StringComparison.Ordinal))) h.PumpOnce();

        Assert.Contains("h=70.000000", h.Said);
        Assert.Contains("h2=100.000000", h.Said);
        Assert.Equal(100f, sp.Health);
    }

    [Fact]
    public void HealthReachingZeroFiresOnAvatarKilledOnce()
    {
        using var h = new SchedulerHarness();
        var sp = Vulnerable(h);
        var kills = new List<(uint killer, UUID who)>();
        h.Scene.EventManager.OnAvatarKilled += (k, who) => kills.Add((k, who.UUID));
        var sog = SceneHelpers.AddSceneObject(h.Scene, "cannonball", UUID.Random());
        sog.Damage = 100f;

        Collide(sp, sog.RootPart.LocalId, 0f);

        Assert.Equal(0f, sp.Health);
        Assert.Single(kills);
        Assert.Equal((sog.RootPart.LocalId, sp.UUID), kills[0]);
    }
}
