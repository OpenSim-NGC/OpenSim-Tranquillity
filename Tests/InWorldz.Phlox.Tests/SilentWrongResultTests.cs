/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Calls that gave a script a wrong answer without an error:
/// - llGetVehicleFlags returned the vehicle type, not its flags;
/// - llRezObjectWithParams read REZ_POS's relative and at_root the wrong way round, and a rule it did not act on
///   (or a value of the wrong type) left its values to be read as the next rules;
/// - llSetAgentEnvironment and llReplaceAgentEnvironment changed nothing and returned 0, and their errors used codes
///   that are not SL's ENV_* codes.
/// </summary>
// Touches no process-wide state, so it runs in parallel.
public class SilentWrongResultTests
{
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private readonly ITestOutputHelper _out;
    public SilentWrongResultTests(ITestOutputHelper o) => _out = o;

    private static SchedulerHarness WithChild()
    {
        var h = new SchedulerHarness();
        TaskInventoryHelpers.AddSceneObject(h.Scene.AssetService, h.Prim, "child", UUID.Random(), h.Prim.OwnerID);
        // away from the region's origin, where a relative and an absolute position are the same point
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(100, 110, 30);
        return h;
    }

    private void Run(SchedulerHarness h, string body)
    {
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("done")), "the script never finished: " + string.Join(" | ", h.Said));
        _out.WriteLine(string.Join("\n", h.SaidOn.Select(m => m.Channel + ": " + m.Message)));
    }

    private static string Line(SchedulerHarness h, string tag)
        => h.Said.Single(s => s.StartsWith(tag + "=")).Substring(tag.Length + 1);

    private static SceneObjectGroup[] Rezzed(SchedulerHarness h)
        => h.Scene.GetSceneObjectGroups().Where(g => g.UUID != h.Prim.ParentGroup.UUID).ToArray();

    // ── llGetVehicleFlags ──────────────────────────────────────────────────────

    [Fact]
    public void VehicleFlagsAreTheFlagsNotTheType()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSetVehicleType(VEHICLE_TYPE_CAR); llRemoveVehicleFlags(-1); " +
               "llSetVehicleFlags(VEHICLE_FLAG_HOVER_WATER_ONLY | VEHICLE_FLAG_LIMIT_MOTOR_UP); " +
               "llSay(0, \"flags=\" + (string)llGetVehicleFlags()); " +
               "llSay(0, \"want=\" + (string)(VEHICLE_FLAG_HOVER_WATER_ONLY | VEHICLE_FLAG_LIMIT_MOTOR_UP));");

        int stored = (int)h.Prim.ParentGroup.RootPart.VehicleParams.vd.m_flags;
        Assert.Equal(Line(h, "want"), Line(h, "flags"));
        Assert.Equal(stored.ToString(), Line(h, "flags"));
    }

    [Fact]
    public void NoVehicleHasNoFlags()
    {
        using var h = new SchedulerHarness();

        Run(h, "llSay(0, \"flags=\" + (string)llGetVehicleFlags());");

        Assert.Equal("0", Line(h, "flags"));
    }

    // ── llRezObjectWithParams ──────────────────────────────────────────────────

    [Fact]
    public void RelativeRezPositionIsRelative()
    {
        using var h = WithChild();
        Vector3 at = h.Prim.AbsolutePosition;

        Run(h, "llRezObjectWithParams(\"child\", [REZ_POS, <0, 0, 2>, TRUE, FALSE]);");
        Assert.True(h.PumpUntil(() => Rezzed(h).Length == 1), "nothing was rezzed");

        Vector3 pos = Rezzed(h)[0].AbsolutePosition;
        Assert.InRange(pos.Z - at.Z, 1.9f, 2.1f);
        Assert.InRange(Math.Abs(pos.X - at.X) + Math.Abs(pos.Y - at.Y), 0f, 0.1f);
    }

    [Fact]
    public void ARuleNotActedOnDoesNotShiftTheRulesAfterIt()
    {
        using var h = WithChild();
        Vector3 at = h.Prim.AbsolutePosition;

        // REZ_ACCEL's values used to be read as rules: TRUE as REZ_FLAGS, which took REZ_POS as its value
        Run(h, "llRezObjectWithParams(\"child\", [REZ_ACCEL, <0, 0, 1>, TRUE, REZ_SOUND, \"\", 1.0, FALSE, " +
               "REZ_POS, <0, 0, 3>, TRUE, FALSE]);");
        Assert.True(h.PumpUntil(() => Rezzed(h).Length == 1), "nothing was rezzed");

        Assert.InRange(Rezzed(h)[0].AbsolutePosition.Z - at.Z, 2.9f, 3.1f);
    }

    [Theory]
    [InlineData("[6, <0, 0, 1>, FALSE]", "Unknown parameter")]                  // REZ_TORQUE: no published arguments
    [InlineData("[REZ_POS, <0, 0, 1>, TRUE]", "needs 3 value")]                  // one value short
    [InlineData("[REZ_POS, \"up\", TRUE, FALSE]", "wrong type")]
    public void ABadRuleShoutsAndRezzesNothing(string rules, string error)
    {
        using var h = WithChild();

        Run(h, "llSay(0, \"k=[\" + (string)llRezObjectWithParams(\"child\", " + rules + ") + \"]\");");
        h.PumpUntilIdle(TimeSpan.FromSeconds(1));

        Assert.Equal("[]", Line(h, "k"));
        Assert.Empty(Rezzed(h));
        Assert.Contains(h.SaidOn, m => m.Channel == DEBUG_CHANNEL && m.Message.Contains(error));
    }

    // ── llSetAgentEnvironment / llReplaceAgentEnvironment ──────────────────────

    [Fact]
    public void OutsideAnExperienceBothReturnEnvNotExperience()
    {
        using var h = new SchedulerHarness();
        UUID agent = UUID.Random();
        SceneHelpers.AddScenePresence(h.Scene, agent);

        Run(h, "llSay(0, \"set=\" + (string)llSetAgentEnvironment(\"" + agent + "\", 1.0, [])); " +
               "llSay(0, \"replace=\" + (string)llReplaceAgentEnvironment(\"" + agent + "\", 1.0, NULL_KEY)); " +
               "llSay(0, \"code=\" + (string)ENV_NOT_EXPERIENCE);");

        Assert.Equal("-1", Line(h, "code"));
        Assert.Equal("-1", Line(h, "set"));
        Assert.Equal("-1", Line(h, "replace"));
    }
}
