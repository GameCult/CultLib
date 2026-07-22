using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryPatchTests
{
    [Fact]
    public void CubeSpherePatchMeshUsesCanonicalTopology()
    {
        var meshes = PlanetaryPatch.CreateCubeSphere(8);
        Assert.Equal(6, meshes.Length);
        foreach (var mesh in meshes)
        {
            Assert.Equal(81, mesh.Vertices.Length);
            Assert.Equal(8 * 8 * 6, mesh.Indices.Length);
            Assert.All(mesh.Vertices, vertex => Assert.Equal(1, math.length(vertex.UnitDirection), 5));
            foreach (var index in mesh.Indices) Assert.InRange(index, 0, mesh.Vertices.Length - 1);
        }
    }

    [Fact]
    public void NeighborFaceCornersAreSingleValued()
    {
        var directions = PlanetaryPatch.CreateCubeSphere(1)
            .SelectMany(mesh => mesh.Vertices.Select(vertex => vertex.UnitDirection))
            .ToArray();
        var groups = directions.GroupBy(direction => new
        {
            X = Math.Sign(direction.x),
            Y = Math.Sign(direction.y),
            Z = Math.Sign(direction.z),
        });
        Assert.Equal(8, groups.Count());
        Assert.All(groups, group =>
        {
            var first = group.First();
            Assert.All(group, direction => Assert.InRange(math.distance(first, direction), 0, 1.0e-6f));
        });
    }
}
