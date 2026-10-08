using OpenMetaverse;
using Xunit;
using Mesher = OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using UbMesher = OpenSim.Region.PhysicsModules.ubODEMeshing.PrimMesher;

namespace OpenSim.Region.CoreModules.Tests.Physics;

public class PrimMesherIdentityTests
{
    [Fact]
    public void LocalMeshersDoNotExportUpstreamTypeNames()
    {
        var upstreamNames = typeof(global::PrimMesher.PrimMesh).Assembly.GetExportedTypes()
            .Select(type => type.FullName).ToHashSet();
        var localAssemblies = new[]
        {
            typeof(Mesher.PrimMesh).Assembly,
            typeof(UbMesher.PrimMesh).Assembly
        };

        foreach (var assembly in localAssemblies)
            foreach (var type in assembly.GetExportedTypes())
                Assert.DoesNotContain(type.FullName, upstreamNames);

        Assert.Equal(typeof(OpenSim.Region.PhysicsModules.Meshing.Meshmerizer).Assembly,
            typeof(Mesher.PrimMesh).Assembly);
        Assert.Equal(typeof(OpenSim.Region.PhysicsModules.ubODEMeshing.ubODEMeshmerizer).Assembly,
            typeof(UbMesher.PrimMesh).Assembly);
    }

    [Fact]
    public void MeshingExtrudesAUnitBoxWithValidFaces()
    {
        var mesh = new Mesher.PrimMesh(4, 0, 1, 0, 4);
        mesh.Extrude(Mesher.PathType.Linear);

        Assert.Null(mesh.errorMessage);
        AssertUnitBox(mesh.coords.Select(coord => new Vector3(coord.X, coord.Y, coord.Z)).ToArray(),
            mesh.faces.Select(face => (face.v1, face.v2, face.v3)).ToArray());
    }

    [Fact]
    public void UbODEMeshingExtrudesAUnitBoxWithValidFaces()
    {
        var mesh = new UbMesher.PrimMesh(4, 0, 1, 0, 4);
        mesh.Extrude(UbMesher.PathType.Linear);

        Assert.Null(mesh.errorMessage);
        AssertUnitBox(mesh.coords.ToArray(),
            mesh.faces.Select(face => (face.v1, face.v2, face.v3)).ToArray());
    }

    private static void AssertUnitBox(Vector3[] vertices, (int V1, int V2, int V3)[] faces)
    {
        Assert.NotEmpty(vertices);
        Assert.NotEmpty(faces);
        Assert.Equal(-0.5f, vertices.Min(vertex => vertex.X), 5);
        Assert.Equal(0.5f, vertices.Max(vertex => vertex.X), 5);
        Assert.Equal(-0.5f, vertices.Min(vertex => vertex.Y), 5);
        Assert.Equal(0.5f, vertices.Max(vertex => vertex.Y), 5);
        Assert.Equal(-0.5f, vertices.Min(vertex => vertex.Z), 5);
        Assert.Equal(0.5f, vertices.Max(vertex => vertex.Z), 5);
        foreach (var face in faces)
        {
            Assert.InRange(face.V1, 0, vertices.Length - 1);
            Assert.InRange(face.V2, 0, vertices.Length - 1);
            Assert.InRange(face.V3, 0, vertices.Length - 1);
        }
    }
}
