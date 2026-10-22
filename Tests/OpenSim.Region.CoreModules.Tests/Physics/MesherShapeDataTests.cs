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
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.CoreModules.Physics.Tests;

/// <summary>
/// A region server loads each module in an assembly load context of its own, so the Meshmerizer a running physics module is
/// handed is a second copy of the type: a cast to the concrete class fails although the mesher is the right one. Types of
/// OpenSim.Region.PhysicsModules.SharedBase are the same in every context, so a physics module asks the mesher for
/// <see cref="IMesherShapeData"/> instead. These tests load the mesher the way a region does and use it through the interface.
/// </summary>
public class MesherShapeDataTests
{
    // A load context that holds its own copy of the mesher's assembly and hands every other assembly to the default context.
    private sealed class MesherContext : AssemblyLoadContext
    {
        private readonly string _path;
        public MesherContext(string path) : base("OpenSim.Region.PhysicsModules.Meshing", false) { _path = path; }
        protected override Assembly? Load(AssemblyName name)
            => name.Name == "OpenSim.Region.PhysicsModules.Meshing" ? LoadFromAssemblyPath(_path) : null;
    }

    private static object NewMesherInSecondContext()
    {
        string path = typeof(Meshmerizer).Assembly.Location;
        Assembly assembly = new MesherContext(path).LoadFromAssemblyPath(path);
        object? mesher = Activator.CreateInstance(assembly.GetType("OpenSim.Region.PhysicsModules.Meshing.Meshmerizer", true)!);
        Assert.NotNull(mesher);
        var config = new IniConfigSource();
        config.AddConfig("Startup").Set("meshing", "Meshmerizer");
        ((INonSharedRegionModule)mesher!).Initialise(config);
        return mesher;
    }

    [Fact]
    public void ACastToTheConcreteMesherFailsInAnotherLoadContextButTheInterfaceWorks()
    {
        object mesher = NewMesherInSecondContext();

        Assert.False(mesher is Meshmerizer);                   // the second copy of the class is another type
        Assert.True(mesher is IMesher);                        // the interfaces come from assemblies every context shares
        Assert.True(mesher is IMesherShapeData);
    }

    [Fact]
    public void TheRingsOfAPrimAreReadThroughTheInterfaceFromAnotherLoadContext()
    {
        var shape = PrimitiveBaseShape.CreateBox();
        var size = new Vector3(2f, 3f, 4f);

        var local = new Meshmerizer();
        var other = (IMesherShapeData)NewMesherInSecondContext();

        Assert.True(local.TryGetPrimProfileRings("local", shape, size, 32f, out PrimProfileRings? expected));
        Assert.True(other.TryGetPrimProfileRings("other", shape, size, 32f, out PrimProfileRings? rings));
        Assert.NotNull(expected);
        Assert.NotNull(rings);
        Assert.Equal(expected!.LayerCount, rings!.LayerCount);
        Assert.Equal(expected.LayerVertexCount, rings.LayerVertexCount);
        Assert.Equal(expected.Vertices, rings.Vertices);
    }

    [Fact]
    public void ASculptHasNoRingsThroughTheInterface()
    {
        var other = (IMesherShapeData)NewMesherInSecondContext();
        var sculpt = PrimitiveBaseShape.CreateSphere();
        sculpt.SculptEntry = true;
        sculpt.SculptType = (byte)SculptType.Sphere;

        Assert.False(other.TryGetPrimProfileRings("sculpt", sculpt, Vector3.One, 32f, out PrimProfileRings? rings));
        Assert.Null(rings);
    }
}
