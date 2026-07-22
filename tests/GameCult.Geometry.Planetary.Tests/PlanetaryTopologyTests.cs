using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryTopologyTests
{
    [Fact]
    public void FaceDirectionRoundTripsAcrossEveryFace()
    {
        foreach (var face in Enum.GetValues<PlanetaryCubeFace>())
        foreach (var u in new[] { -0.9, -0.25, 0.0, 0.4, 0.9 })
        foreach (var v in new[] { -0.85, -0.1, 0.3, 0.88 })
        {
            var direction = PlanetaryTopology.Direction(new(face, u, v));
            var roundTrip = PlanetaryTopology.FaceCoordinate(direction);
            Assert.Equal(face, roundTrip.Face);
            Assert.Equal(u, roundTrip.U, 5);
            Assert.Equal(v, roundTrip.V, 5);
        }
    }

    [Fact]
    public void SiblingPagesShareBitStableBoundaryDirections()
    {
        var left = new PlanetaryPageLayout(new(PlanetaryCubeFace.PositiveZ, 1, 0, 0), 17, 2);
        var right = new PlanetaryPageLayout(new(PlanetaryCubeFace.PositiveZ, 1, 1, 0), 17, 2);
        for (var y = 0; y < left.InteriorSize; y++)
        {
            var a = PlanetaryPageSampling.Direction(left, left.BorderSize + left.InteriorSize - 1, left.BorderSize + y);
            var b = PlanetaryPageSampling.Direction(right, right.BorderSize, right.BorderSize + y);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void DirectionSelectsContainingTileAndLocalCoordinate()
    {
        var expected = new PlanetaryTileAddress(PlanetaryCubeFace.NegativeY, 5, 19, 7);
        var direction = PlanetaryPageSampling.DirectionAtLocal(new(expected, 17, 2), 0.37, 0.61);
        Assert.Equal(expected, PlanetaryTopology.TileAt(direction, expected.Level));
        Assert.True(PlanetaryTopology.TryLocalCoordinate(direction, expected, out var local));
        Assert.Equal(0.37f, local.x, 4);
        Assert.Equal(0.61f, local.y, 4);
    }

    [Fact]
    public void BorderSamplesDoNotRequireNeighborResidency()
    {
        var page = new PlanetaryPageLayout(new(PlanetaryCubeFace.PositiveX, 0, 0, 0), 17, 3);
        var edge = PlanetaryPageSampling.Direction(page, page.BorderSize + page.InteriorSize - 1, page.BorderSize + 8);
        var border = PlanetaryPageSampling.Direction(page, page.StorageSize - 1, page.BorderSize + 8);
        Assert.NotEqual(edge, border);
        Assert.Equal(1, math.length(border), 5);
    }

    [Fact]
    public void NormalizedCubeBaselineSharesFaceEdges()
    {
        var a = PlanetaryTopology.NormalizedCubeDirection(new(PlanetaryCubeFace.PositiveZ, 1, -0.4));
        var b = PlanetaryTopology.NormalizedCubeDirection(new(PlanetaryCubeFace.PositiveX, -1, -0.4));
        Assert.InRange(math.distance(a, b), 0, 1.0e-6f);
    }
}
