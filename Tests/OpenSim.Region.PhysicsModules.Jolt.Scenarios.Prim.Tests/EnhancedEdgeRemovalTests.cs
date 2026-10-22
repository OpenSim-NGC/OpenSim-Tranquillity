/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// [Jolt] BodyEnhancedEdgeRemoval and AvatarEnhancedEdgeRemoval turn on Jolt's enhanced internal edge removal for bodies that
/// move under physics and for avatars; both are off by default, which is what the engine does without them. The seam
/// scenarios of the harness (a ball, a sled and an avatar crossing a flat mesh floor of many triangles, a rippled one,
/// and rolling terrain) count the vertical hitches with each off and on, at one physics step per heartbeat and at 45 Hz.
/// Each figure is a line "SEAM ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class EnhancedEdgeRemovalTests
{
    private readonly ITestOutputHelper _out;
    public EnhancedEdgeRemovalTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    // ------------------------------------------------------------------ the settings

    [Fact]
    public void Both_settings_are_off_by_default_and_documented_keys_turn_them_on()
    {
        JoltScene off = NewScene(Scratch());
        try
        {
            Assert.False(Body(off));
            Assert.False(Avatar(off));
        }
        finally { off.Dispose(); }

        JoltScene bodies = NewScene(Scratch(), ("BodyEnhancedEdgeRemoval", "true"));
        try
        {
            Assert.True(Body(bodies));
            Assert.False(Avatar(bodies));
        }
        finally { bodies.Dispose(); }

        JoltScene avatars = NewScene(Scratch(), ("AvatarEnhancedEdgeRemoval", "true"));
        try
        {
            Assert.False(Body(avatars));
            Assert.True(Avatar(avatars));
        }
        finally { avatars.Dispose(); }
    }

    // A physical box has the flag and a fixed one does not need it (the flag of either body of a pair acts); an avatar's
    // character has it after its capsule is replaced for a support radius too.
    private static bool Body(JoltScene scene)
    {
        PhysicsActor box = scene.AddPrimShape("box", OpenSim.Framework.PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY, Ground + 5f),
                                              Vector3.One, Quaternion.Identity, true, _id++);
        return scene.Backend.GetBodyEnhancedEdgeRemoval(((JoltPrim)box).BodyHandle);
    }

    private static bool Avatar(JoltScene scene)
    {
        PhysicsActor av = scene.AddAvatar(_id++, "Test User", new Vector3(CentreX + 20f, CentreY, Ground + 1.2f), Run.AvatarSize, 0f, false);
        for (int i = 0; i < 3; i++)
            scene.Simulate(Heartbeat);
        return scene.Backend.GetCharacterEnhancedEdgeRemoval(((JoltCharacter)av).CharacterHandle);
    }

    [Fact]
    public void A_fixed_prim_does_not_take_the_body_setting()
    {
        JoltScene scene = NewScene(Scratch(), ("BodyEnhancedEdgeRemoval", "true"));
        try
        {
            PhysicsActor box = scene.AddPrimShape("fixed", OpenSim.Framework.PrimitiveBaseShape.CreateBox(), new Vector3(CentreX, CentreY, Ground + 5f),
                                                  Vector3.One, Quaternion.Identity, false, _id++);
            Assert.False(scene.Backend.GetBodyEnhancedEdgeRemoval(((JoltPrim)box).BodyHandle));
            box.IsPhysical = true;
            Assert.True(scene.Backend.GetBodyEnhancedEdgeRemoval(((JoltPrim)box).BodyHandle));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ the hitch figures

    private static readonly string[] SeamRuns =
    {
        "seam-ball", "seam-sled", "seam-walker", "seam-ripple-ball", "seam-ripple-sled", "seam-ripple-walker",
        "seam-terrain-ball", "seam-terrain-sled", "seam-terrain-walker",
    };

    private static RunResult RunSeam(string scenario, double physicsRate, bool bodies, bool avatars)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate };
        if (bodies) o.Jolt["BodyEnhancedEdgeRemoval"] = "true";
        if (avatars) o.Jolt["AvatarEnhancedEdgeRemoval"] = "true";
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    [Fact]
    public void Count_the_hitches_of_every_seam_scenario_with_each_setting_off_and_on()
    {
        var lines = new List<string>();
        foreach (string scenario in SeamRuns)
            foreach (double rate in new[] { 0.0, 45.0 })
            {
                bool walker = scenario.EndsWith("walker", StringComparison.Ordinal);
                RunResult off = RunSeam(scenario, rate, false, false);
                RunResult on = RunSeam(scenario, rate, !walker, walker);
                (int hOff, float lOff) = SeamScenarios_Count(off);
                (int hOn, float lOn) = SeamScenarios_Count(on);
                string setting = walker ? "AvatarEnhancedEdgeRemoval" : "BodyEnhancedEdgeRemoval";
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"SEAM {scenario} physics_rate={rate:0.#} {setting}: hitches off={hOff} on={hOn}; largest departure off={lOff:0.000} on={lOn:0.000} m/s; " +
                    $"end_x off={off.Summary.End.X:0.00} on={on.Summary.End.X:0.00}"));
                Assert.Equal(0, off.Summary.NonFinite);
                Assert.Equal(0, on.Summary.NonFinite);
                Assert.True(off.Samples.Count > 40 && on.Samples.Count > 40);
            }
        foreach (string line in lines)
            Say(line);
    }

    private static (int, float) SeamScenarios_Count(RunResult r) => Harness.SeamScenarios.HitchCount(r.Samples);
}
