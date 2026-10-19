/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.SharedBase;

/// <summary>
/// The rings a prim is extruded from: a profile (the outline of the prim's cross-section) copied once for every
/// node of the prim's path. Read-only. It describes the vertices the mesher builds the prim's mesh from, so a
/// caller can work with the profile and path of a prim (for instance to split a hollow or twisted prim into convex
/// pieces) without repeating the mesher's parameter mapping.
///
/// <see cref="Vertices"/> holds <see cref="LayerCount"/> layers of <see cref="LayerVertexCount"/> vertices each, in
/// path order. Within one layer the vertices are, in this order: the centre vertex when
/// <see cref="HasCentreVertex"/> is set, then <see cref="OuterVertexCount"/> vertices of the outer ring, then
/// <see cref="HollowVertexCount"/> vertices of the hollow ring (none when the prim is not hollow). The outer ring runs
/// counter-clockwise around the profile; the layer stores the hollow ring in the opposite direction. A ring that is
/// not cut starts and ends on the same point, so that point appears twice.
/// </summary>
public sealed class PrimProfileRings
{
    private readonly float[] m_profileAngles;
    private readonly float[] m_layerTurns;
    private readonly float[] m_layerTwists;
    private readonly Vector3[] m_vertices;

    public PrimProfileRings(
        int layerCount, int layerVertexCount, bool hasCentreVertex, int outerVertexCount, int hollowVertexCount,
        IReadOnlyList<float> profileAngles, IReadOnlyList<float> layerTurns, IReadOnlyList<float> layerTwists,
        Vector3[] vertices)
    {
        LayerCount = layerCount;
        LayerVertexCount = layerVertexCount;
        HasCentreVertex = hasCentreVertex;
        OuterVertexCount = outerVertexCount;
        HollowVertexCount = hollowVertexCount;
        m_profileAngles = profileAngles.ToArray();
        m_layerTurns = layerTurns.ToArray();
        m_layerTwists = layerTwists.ToArray();
        m_vertices = vertices;
    }

    /// <summary>The number of layers, one per node of the path.</summary>
    public int LayerCount { get; }

    /// <summary>The number of vertices in each layer.</summary>
    public int LayerVertexCount { get; }

    /// <summary>True when each layer starts with one vertex at the centre of the profile.</summary>
    public bool HasCentreVertex { get; }

    /// <summary>The number of vertices of the outer ring in each layer.</summary>
    public int OuterVertexCount { get; }

    /// <summary>The number of vertices of the hollow ring in each layer; 0 when the prim is not hollow.</summary>
    public int HollowVertexCount { get; }

    /// <summary>
    /// The angle in radians of each vertex of a layer, as seen from the centre of the profile, indexed like a layer.
    /// The outer ring's angles start at 0 and increase along the ring. The hollow ring is measured from the same zero,
    /// so a vertex of either ring at the same angle is on the same ray from the centre; since the layer stores the hollow
    /// ring in the opposite direction its angles decrease along the index. The centre vertex has angle 0.
    /// </summary>
    public IReadOnlyList<float> ProfileAngles => m_profileAngles;

    /// <summary>
    /// For each layer, the angle in radians its rotation turns about the path's axis (0 on a straight path). The
    /// difference between two layers is how far the path turns between them.
    /// </summary>
    public IReadOnlyList<float> LayerTurns => m_layerTurns;

    /// <summary>For each layer, the twist in radians applied to it about the path's direction.</summary>
    public IReadOnlyList<float> LayerTwists => m_layerTwists;

    /// <summary>All the vertices, scaled to the size the rings were asked for: layer 0 first.</summary>
    public IReadOnlyList<Vector3> Vertices => m_vertices;

    /// <summary>The vertex at <paramref name="index"/> of layer <paramref name="layer"/>.</summary>
    public Vector3 GetVertex(int layer, int index) => m_vertices[layer * LayerVertexCount + index];
}
