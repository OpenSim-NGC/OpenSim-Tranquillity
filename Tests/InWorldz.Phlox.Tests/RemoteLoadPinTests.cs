/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llRemoteLoadScriptPin / iwRemoteLoadScriptPin report what the target refuses, as Halcyon's (LSLSystemAPI.cs:8977-8993,
/// over Scene.RezScript's reasons): a wrong PIN is -1 ("Script update denied - PIN mismatch." for the ll form), no PIN
/// set is -2 ("PIN not set."), a target without Modify is 0 with the reason. Core's RezScriptFromPrim refuses all three
/// silently, so Phlox checks them first. Every path sleeps 3 s (SL: "This function causes the script to sleep for 3.0
/// seconds", with no exception for a failure). llTeleportAgentHome sleeps 5 s on every path (SL; Halcyon :5749-5770).
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class RemoteLoadPinTests
{
    private const string Err = "Script error: llRemoteLoadScriptPin: ";

    private static (InventoryGivesRig R, SceneObjectPart Target) Setup(int targetPin, bool modify = true)
    {
        var r = new InventoryGivesRig();
        var target = SceneHelpers.AddSceneObject(r.H.Scene, "target", r.H.Prim.OwnerID).RootPart;
        target.ScriptAccessPin = targetPin;
        if (!modify) target.OwnerMask &= ~(uint)OpenSim.Framework.PermissionMask.Modify;
        TaskInventoryHelpers.AddScript(r.H.Scene.AssetService, r.H.Prim, "loader", "default { state_entry() { } }");
        return (r, target);
    }

    [Theory]
    [InlineData(1234, true, -1, "Script update denied - PIN mismatch.")]
    [InlineData(0, true, -2, "Script update denied - PIN not set.")]
    [InlineData(99, false, 0, "Destination lacks Modify permission.")]
    public void TheTargetsRefusalIsReportedAndCostsTheThreeSeconds(int targetPin, bool modify, int rc, string reason)
    {
        var (r, target) = Setup(targetPin, modify);
        using (r)
        {
            var (ms, ret) = r.Accounted(a => a.iwRemoteLoadScriptPin(target.UUID.ToString(), "loader", 99, 1, 0));
            Assert.Equal(rc, ret);
            Assert.True(ms >= 3000, ms + " ms");
            Assert.Null(target.Inventory.GetInventoryItem("loader"));
            if (rc == 0) Assert.Equal(new[] { Err + reason }, r.Errors);   // the iw form still says a denial that is not the PIN
            else Assert.Empty(r.Errors);

            r.Accounted(a => { a.llRemoteLoadScriptPin(target.UUID.ToString(), "loader", 99, 1, 0); return null; });
            Assert.Equal(new[] { Err + reason }, r.Errors);
        }
    }

    [Fact]
    public void TheRightPinLoadsTheScript()
    {
        var (r, target) = Setup(99);
        using (r)
        {
            Assert.Equal(1, r.Accounted(a => a.iwRemoteLoadScriptPin(target.UUID.ToString(), "loader", 99, 0, 0)).Ret);
            Assert.NotNull(target.Inventory.GetInventoryItem("loader"));
        }
    }

    [Fact]
    public void AnEarlyFailureStillSleepsThreeSeconds()
    {
        var (r, _) = Setup(99);
        using (r)
            Assert.Equal(3015, r.Accounted(a => a.iwRemoteLoadScriptPin(UUID.Random().ToString(), "loader", 99, 1, 0)).Ms);
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("absent")]
    public void TeleportAgentHomeSleepsFiveSecondsOnEveryPath(string agent)
    {
        using var r = new InventoryGivesRig();
        string key = agent == "absent" ? UUID.Random().ToString() : agent;
        Assert.Equal(5000, r.Accounted(a => { a.llTeleportAgentHome(key); return null; }).Ms);
    }
}
