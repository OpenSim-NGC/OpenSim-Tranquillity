/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using Omv = OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Hover (llSetHoverHeight) and ground repel (llGroundRepel) of an ordinary physical prim over the sea, over land and across a
/// shoreline, under <c>[Jolt] BasicForcesBehaviour = InWorldz</c>, with each of the three kinds of height the actor can be given:
/// above the ground (the script's water flag FALSE), above the higher of ground and water (TRUE) and above the water alone.
/// Halcyon measures the height above the ground, the water, or the higher of the two, at the object's own position
/// (PhysxPrim.pid.cs GetHeightAbove) and drives the object to the height asked: a hover pulls it there from either side, a
/// repel only pushes it up (DoGroundRepel). An object whose height would put it under the terrain rests on the terrain.
/// The land is at 30 m, the sea floor at 10 m, the water at 20 m, with a ramp 8 m wide between them: the ground crosses the
/// water at x = 104. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class HoverOverWaterTests
{
    private readonly ITestOutputHelper _out;
    public HoverOverWaterTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Water = 20f, Sea = 10f, Land = 30f, Height = 3f, Half = 0.5f;

    public static IEnumerable<object[]> Cells()
    {
        yield return new object[] { 45.0, 2 };
        yield return new object[] { 11.0, 6 };
    }

    // The ground under x: the sea floor, the ramp from x = 100 to 108, then the land.
    private static float Ground(float x) => x <= 100f ? Sea : x >= 108f ? Land : Sea + (x - 100f) * (Land - Sea) / 8f;

    private static float[] Terrain() => SpikeProbe.Terrain((x, y) => Ground(x));

    private static void InWorldz(IConfig jolt)
    {
        jolt.Set("BasicForcesBehaviour", "InWorldz");
        jolt.Set("PrimLinearDamping", "0");
        jolt.Set("PrimAngularDamping", "0");
    }

    private static float BaseZ(PIDHoverType type, float x)
    {
        float g = Ground(x);
        return type switch { PIDHoverType.GroundAndWater => Math.Max(g, Water), PIDHoverType.Water => Water, _ => g };
    }

    // The centre the object ends at: the height above the base, or, when that is under the terrain, resting on it.
    private static double Expected(PIDHoverType type, float x) => Math.Max(BaseZ(type, x) + Height, Ground(x) + Half);

    private static (SpikeProbe, JoltPrim) Make(double rate, int n, PIDHoverType type, bool repel, float x, float z0)
    {
        var p = new SpikeProbe(rate, n, InWorldz, Terrain());
        p.Scene.SetWaterLevel(Water);
        p.RunTo(p.Time + 0.2);
        JoltPrim box = p.Rig.AddBox(new Omv.Vector3(1f, 1f, 1f), new Omv.Vector3(x, 128f, z0), false);
        box.Buoyancy = 0f;   // gravity on
        box.PIDHoverHeight = Height;
        box.PIDHoverType = type;
        box.PIDHoverTau = 1f;
        box.PIDHoverRepel = repel;
        box.PIDHoverActive = true;
        return (p, box);
    }

    private static readonly (string Name, float X)[] Places =
    {
        ("over the sea", 60f), ("on the ramp under the water (ground 15)", 102f), ("at the shoreline (ground 20)", 104f),
        ("on the ramp above the water (ground 25)", 106f), ("over land", 140f),
    };

    // Water alone is only asked of the flat places: on the ramp the object would rest on a slope.
    private static bool Skip(PIDHoverType type, float x) => type == PIDHoverType.Water && x > 100f && x < 108f;

    private double Settle(double rate, int n, PIDHoverType type, bool repel, float x, float fromTarget)
    {
        float z0 = Math.Max(BaseZ(type, x) + Height + fromTarget, Ground(x) + Half + 0.05f);
        var (p, box) = Make(rate, n, type, repel, x, z0);
        using (p)
        {
            p.RunTo(p.Time + 12.0);
            return box.Position.Z;
        }
    }

    // A hover at 3 m, the object starting 1.5 m under its height, then 1.5 m over it.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_hover_holds_the_height_over_sea_shoreline_and_land_with_each_kind_of_height(double rate, int n)
    {
        var failures = new List<string>();
        foreach (PIDHoverType type in new[] { PIDHoverType.Ground, PIDHoverType.GroundAndWater, PIDHoverType.Water })
            foreach (var (name, x) in Places)
            {
                if (Skip(type, x)) continue;
                foreach (float from in new[] { -1.5f, 1.5f })
                {
                    double z = Settle(rate, n, type, false, x, from);
                    double want = Expected(type, x);
                    Say($"[water] hover {type} {name} | {rate} Hz x {n} | from {from:+0.0;-0.0} m | ends {z:0.000}, Halcyon {want:0.000}");
                    if (Math.Abs(z - want) > 0.2) failures.Add($"{type} {name} from {from}: ends {z:0.000}, expected {want:0.000}");
                }
            }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // A repel pushes up from under its height: from under it, it comes up to the height.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_repel_pushes_up_to_the_height_over_sea_shoreline_and_land_with_each_kind_of_height(double rate, int n)
    {
        var failures = new List<string>();
        foreach (PIDHoverType type in new[] { PIDHoverType.Ground, PIDHoverType.GroundAndWater, PIDHoverType.Water })
            foreach (var (name, x) in Places)
            {
                if (Skip(type, x)) continue;
                double z = Settle(rate, n, type, true, x, -1.5f);
                double want = Expected(type, x);
                Say($"[water] repel {type} {name} | {rate} Hz x {n} | from under | ends {z:0.000}, Halcyon {want:0.000}");
                if (Math.Abs(z - want) > 0.2) failures.Add($"{type} {name} from under: ends {z:0.000}, expected {want:0.000}");
            }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // A repel never pulls an object down to its height: ten metres over it, the object only falls under gravity.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_repel_does_not_pull_an_object_down_to_its_height_over_water_or_land(double rate, int n)
    {
        var failures = new List<string>();
        foreach (PIDHoverType type in new[] { PIDHoverType.Ground, PIDHoverType.GroundAndWater })
            foreach (var (name, x) in new[] { Places[0], Places[4] })
            {
                float z0 = BaseZ(type, x) + Height + 10f;
                var (p, box) = Make(rate, n, type, true, x, z0);
                using (p)
                {
                    double t0 = p.Time;
                    p.RunTo(t0 + 0.5);
                    double fell = z0 - box.Position.Z;
                    double free = 0.5 * 9.80665 * (p.Time - t0) * (p.Time - t0);
                    Say($"[water] repel from 10 m over {type} {name} | {rate} Hz x {n} | fell {fell:0.000} m in {p.Time - t0:0.00} s, free fall {free:0.000}");
                    if (Math.Abs(fell - free) > 0.1) failures.Add($"{type} {name}: fell {fell:0.000}, free fall {free:0.000}");
                }
            }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
