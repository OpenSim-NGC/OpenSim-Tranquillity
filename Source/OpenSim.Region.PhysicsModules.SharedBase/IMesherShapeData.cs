/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.PhysicsModules.SharedBase;

/// <summary>
/// What a mesher can say about a shape beyond its mesh: the rings a prim is extruded from, and the convex hulls a mesh
/// asset carries. A physics engine asks the mesher it was given (<see cref="IMesher"/>) for this interface rather than
/// casting to the mesher's own class: a region server loads each module in its own assembly load context, so the class the
/// physics module was built against and the class the running mesher is an instance of are two different types, and the
/// cast fails although the mesher is the right one. Types of this assembly are the same in every context.
/// </summary>
public interface IMesherShapeData
{
    /// <summary>
    /// The rings the mesher extrudes a prim from, for a prim that is made from its own profile and path (not a sculpt
    /// or a mesh). The vertices are scaled by <paramref name="size"/>.
    /// </summary>
    /// <param name="primName">The name used when the extrusion reports a problem.</param>
    /// <param name="primShape">The prim's shape.</param>
    /// <param name="size">The size the vertices are scaled to, as passed to CreateMesh.</param>
    /// <param name="lod">The level of detail, as passed to CreateMesh.</param>
    /// <param name="rings">The rings, or null when false is returned.</param>
    /// <returns>false for a sculpt or mesh prim, or when the extrusion fails</returns>
    bool TryGetPrimProfileRings(string primName, PrimitiveBaseShape primShape, Vector3 size, float lod, out PrimProfileRings rings);

    /// <summary>
    /// The convex hulls a mesh asset carries, each point scaled by <paramref name="size"/>.
    /// </summary>
    /// <param name="mesh">A mesh returned by CreateMesh, whether freshly made or served from the cache.</param>
    /// <param name="size">The value the hull points are scaled by.</param>
    /// <returns>a list of hulls if the asset has them and they were decoded, otherwise null</returns>
    List<List<Vector3>> GetConvexHulls(IMesh mesh, Vector3 size);
}
