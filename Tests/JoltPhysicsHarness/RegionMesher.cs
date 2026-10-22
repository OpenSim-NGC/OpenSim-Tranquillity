/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.Loader;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>
/// The mesher a running region hands the physics module. The region server loads each module in an assembly load
/// context of its own, so the Meshmerizer the physics module gets is a second copy of the class the module was built
/// against: a cast to that class is false in a region and true in a test that makes its mesher with
/// <c>new Meshmerizer()</c>. The harness builds its mesher the region's way: the Meshmerizer assembly is loaded in a
/// load context of its own, and every other assembly (SharedBase among them, where IMesher and IMesherShapeData live)
/// comes from the default context.
/// </summary>
public static class RegionMesher
{
    private const string AssemblyName = "OpenSim.Region.PhysicsModules.Meshing";
    private const string TypeName = "OpenSim.Region.PhysicsModules.Meshing.Meshmerizer";

    private sealed class MesherContext : AssemblyLoadContext
    {
        private readonly string _path;
        public MesherContext(string path) : base(AssemblyName, false) { _path = path; }
        protected override Assembly Load(AssemblyName name)
            => name.Name == RegionMesher.AssemblyName ? LoadFromAssemblyPath(_path) : null;
    }

    // One load context for the process: a load context cannot be given back, and a region has exactly one copy of the
    // mesher's assembly however many regions run.
    private static readonly Lazy<Type> MesherType = new(() =>
    {
        string path = typeof(Meshing.Meshmerizer).Assembly.Location;
        return new MesherContext(path).LoadFromAssemblyPath(path).GetType(TypeName, true);
    });

    /// <summary>The Meshmerizer class of the region's load context: not <c>typeof(Meshing.Meshmerizer)</c>.</summary>
    public static Type Type => MesherType.Value;

    /// <summary>A new Meshmerizer of the region's load context, initialised as a region does with [Startup] meshing
    /// naming it. Its decoded sculpt maps go to <paramref name="cacheFolder"/>.</summary>
    public static IMesher Create(string cacheFolder)
    {
        object mesher = Activator.CreateInstance(Type);
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("meshing", "Meshmerizer");
        startup.Set("DecodedSculptMapPath", cacheFolder);
        ((INonSharedRegionModule)mesher).Initialise(config);
        return (IMesher)mesher;
    }
}
