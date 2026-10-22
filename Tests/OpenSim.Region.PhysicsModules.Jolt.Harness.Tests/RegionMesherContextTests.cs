/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.Loader;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The mesher a running region hands the physics module is not an instance of the class the module was built against: the
/// region server loads each module in an assembly load context of its own, so its Meshmerizer is a second copy of the type
/// (the test scenes in the other classes make theirs with <c>new Meshmerizer()</c>, which is the same type as the module's
/// and hides the difference). These tests give the scene a Meshmerizer loaded the way a region loads it, and check what the
/// module does with it: a hollow physical prim is still split into convex cells, and a physical mesh still collides as the
/// hull list its asset carries. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class RegionMesherContextTests
{
    private readonly ITestOutputHelper _out;
    public RegionMesherContextTests(ITestOutputHelper output) { _out = output; }

    private const byte Prim = (byte)PhysShapeType.prim;

    private static uint _id = 52000;

    // A load context that holds its own copy of the mesher's assembly, as the region's plugin loader makes one, and hands
    // every other assembly to the default context.
    private sealed class MesherContext : AssemblyLoadContext
    {
        private readonly string _path;
        public MesherContext(string path) : base("OpenSim.Region.PhysicsModules.Meshing", false) { _path = path; }
        protected override Assembly Load(AssemblyName name)
            => name.Name == "OpenSim.Region.PhysicsModules.Meshing" ? LoadFromAssemblyPath(_path) : null;
    }

    // A scene whose mesher is a Meshmerizer of a second load context.
    private static JoltScene NewRegionScene(string scratch)
    {
        JoltScene scene = NewScene(scratch);
        string path = typeof(Meshing.Meshmerizer).Assembly.Location;
        Assembly assembly = new MesherContext(path).LoadFromAssemblyPath(path);
        object mesher = Activator.CreateInstance(assembly.GetType("OpenSim.Region.PhysicsModules.Meshing.Meshmerizer", true));
        Assert.NotSame(typeof(Meshing.Meshmerizer), mesher.GetType());   // the point of the class: not the module's own type
        Assert.IsAssignableFrom<IMesher>(mesher);
        Assert.IsAssignableFrom<IMesherShapeData>(mesher);
        var config = new IniConfigSource();
        config.AddConfig("Startup").Set("meshing", "Meshmerizer");
        config.Configs["Startup"].Set("DecodedSculptMapPath", scratch);
        ((INonSharedRegionModule)mesher).Initialise(config);
        typeof(JoltScene).GetField("m_mesher", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(scene, mesher);
        return scene;
    }

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    [Fact]
    public void A_hollow_physical_prim_is_split_into_cells_when_the_mesher_comes_from_another_load_context()
    {
        JoltScene scene = NewRegionScene(Scratch());
        try
        {
            // A 1 m hollow box with a 0.5 m hole up and down through it, on the ground.
            PhysicsActor box = scene.AddPrimShape("prim", PrimHullProbeTests.Shapes().Single(s => s.Name == "hollow box 0.5").Build(),
                new Vector3(CentreX, CentreY, Ground + 0.505f), Vector3.One, Quaternion.Identity, true, false, Prim, _id++);
            Step(scene, 1f);
            Assert.StartsWith(JoltScene.PrimCellsKind, KindOf(box), StringComparison.Ordinal);
            Assert.True(((JoltPrim)box).ComplexityCount >= 4, $"hull count {((JoltPrim)box).ComplexityCount}");

            // A 0.2 m cube let go above the hole falls into it and rests on the ground inside.
            Vector3 at = box.Position;
            PhysicsActor cube = scene.AddPrimShape("cube", PrimitiveBaseShape.CreateBox(), new Vector3(at.X + 0.04f, at.Y - 0.03f, Ground + 1f + 0.15f + 0.1f),
                new Vector3(0.2f), Quaternion.Identity, true, false, Prim, _id++);
            Step(scene, 3f);
            float rest = cube.Position.Z - Ground;
            _out.WriteLine($"REGION-MESHER cube over hollow box: kind={KindOf(box)} cube rests {rest:0.000} m up");
            Assert.InRange(rest, 0.08f, 0.13f);   // in the hole, not on top of the box (1.1 m)
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_physical_mesh_with_a_hull_list_cooks_as_its_hulls_when_the_mesher_comes_from_another_load_context()
    {
        JoltScene scene = NewRegionScene(Scratch());
        try
        {
            PhysicsActor a = AddMesh(scene, Asset(Soup(Trough), Trough, false), TroughHeight, true, Prim, out _);
            Step(scene, 2f);
            _out.WriteLine($"REGION-MESHER trough: kind={KindOf(a)}");
            Assert.Equal("hulls(asset x3)", KindOf(a));
        }
        finally { scene.Dispose(); }
    }
}
