/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Where collision, money and sensor detect data come from.
/// <list type="bullet">
/// <item>A collider that has left the region between the physics step and delivery still reports what physics saw
/// (Halcyon DetectParams.FromDetectedObject copies the DetectedObject).</item>
/// <item>money(): llDetectedLinkNumber(0) is the prim that was paid, also when the root's money() takes it (Halcyon
/// EventRouter.HandleObjectPaid passes the paid link).</item>
/// <item>A sensor counts an object as SCRIPTED when any of its prims holds a script (SL llDetectedType: "Objects
/// containing any active script"; Halcyon SensorRepeat tests the whole linkset).</item>
/// </list>
/// </summary>
// No process-wide state: each test has its own scene, so the class runs in parallel.
public class DetectDataSourceTests
{
    private readonly ITestOutputHelper _out;
    public DetectDataSourceTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AColliderNoLongerInTheRegionReportsWhatPhysicsSaw()
    {
        using var h = new SchedulerHarness();
        h.RezScript(@"default {
            state_entry() { llSay(0, ""armed""); }
            collision_start(integer n) {
                llSay(0, ""hit "" + llDetectedName(0) + "" "" + (string)llDetectedOwner(0) + "" ""
                    + (string)llDetectedPos(0) + "" "" + (string)llDetectedType(0));
            }
        }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("armed")));
        UUID owner = UUID.Random();
        var args = new ColliderArgs();
        args.Colliders.Add(new DetectedObject
        {
            keyUUID = UUID.Random(), nameStr = "Gone", ownerUUID = owner, posVector = new Vector3(1, 2, 3),
            rotQuat = Quaternion.Identity, colliderType = 4,
        });

        h.Scene.EventManager.TriggerScriptCollidingStart(h.Prim.LocalId, args);

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("hit ", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        Assert.Equal("hit Gone " + owner + " <1.00000, 2.00000, 3.00000> 4",
            h.Said.First(s => s.StartsWith("hit ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheRootsMoneyHandlerSeesThePaidChildsLinkNumber()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "child", h.Prim.OwnerID));
        SceneObjectPart child = h.Prim.ParentGroup.GetLinkNumPart(2);
        h.RezScript(@"default {
            state_entry() { llSay(0, ""armed""); }
            money(key id, integer amount) { llSay(0, ""paid "" + (string)llDetectedLinkNumber(0) + "" "" + (string)amount); }
        }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("armed") && (h.Prim.ScriptEvents & scriptEvents.money) != 0));
        var client = h.AddClient();

        // The money module's OnObjectPaid, as a payment to the child prim raises it
        MethodInfo paid = h.Engine.GetType().GetMethod("HandleObjectPaid", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(paid);
        paid.Invoke(h.Engine, new object[] { child.UUID, client.AgentId, 25 });

        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("paid ", StringComparison.Ordinal))), string.Join(" | ", h.Said));
        Assert.Equal("paid 2 25", h.Said.First(s => s.StartsWith("paid ", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnObjectScriptedOnlyInAChildPrimSensesAsScripted()
    {
        using var h = new SchedulerHarness();
        var target = SceneHelpers.AddSceneObject(h.Scene, "Target", UUID.Random());
        target.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "Target child", target.OwnerID));
        target.AbsolutePosition = h.Prim.AbsolutePosition + new Vector3(3, 0, 0);
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, target.GetLinkNumPart(2), UUID.Random(), UUID.Random(),
            "child script", "default { state_entry() { } }");
        Assert.False(target.RootPart.Inventory.ContainsScripts());

        h.RezScript(@"integer step;
        next() { if (++step == 1) llSensor(""Target"", NULL_KEY, PASSIVE, 20.0, PI); }
        default {
            state_entry() { llSensor(""Target"", NULL_KEY, SCRIPTED, 20.0, PI); }
            sensor(integer n) { llSay(0, ""step"" + (string)step + "" found "" + (string)llDetectedType(0)); next(); }
            no_sensor() { llSay(0, ""step"" + (string)step + "" none""); next(); }
        }");

        Assert.True(h.PumpUntil(() => h.Said.Count(s => s.StartsWith("step", StringComparison.Ordinal)) == 2), string.Join(" | ", h.Said));
        _out.WriteLine(string.Join(" | ", h.Said));
        Assert.StartsWith("step0 found", h.Said[0]);
        Assert.Equal("step1 none", h.Said[1]);
    }
}
