using Xunit;

namespace CultMath.Tests;

public sealed class PlanetaryProjectionTests
{
    [Theory]
    [InlineData(PlanetaryProjectionKind.Equirectangular)]
    [InlineData(PlanetaryProjectionKind.WebMercator)]
    [InlineData(PlanetaryProjectionKind.EqualEarth)]
    [InlineData(PlanetaryProjectionKind.Orthographic)]
    [InlineData(PlanetaryProjectionKind.AzimuthalEquidistant)]
    [InlineData(PlanetaryProjectionKind.AzimuthalEqualArea)]
    [InlineData(PlanetaryProjectionKind.LocalTangent)]
    public void ProjectionRoundTripsVisibleDirections(PlanetaryProjectionKind kind)
    {
        var parameters = new PlanetaryProjectionParameters(kind, 0.31, -0.22);
        foreach (var longitude in new[] { -0.4, -0.1, 0.2, 0.65 })
        foreach (var latitude in new[] { -0.6, -0.15, 0.3, 0.7 })
        {
            var direction = Direction(longitude + parameters.CenterLongitude, latitude);
            if (!PlanetaryProjection.TryForward(direction, parameters, out var map)) continue;
            Assert.True(PlanetaryProjection.TryInverse(map, parameters, out var roundTrip));
            Assert.InRange(math.distance(direction, roundTrip), 0, 3.0e-5f);
        }
    }

    [Fact]
    public void CubeAtlasRoundTripsEveryFaceInterior()
    {
        var projection = new PlanetaryProjectionParameters(PlanetaryProjectionKind.CubeAtlas);
        foreach (var face in Enum.GetValues<PlanetaryCubeFace>())
        {
            var direction = PlanetaryTopology.Direction(new(face, 0.23, -0.41));
            Assert.True(PlanetaryProjection.TryForward(direction, projection, out var map));
            Assert.True(PlanetaryProjection.TryInverse(map, projection, out var roundTrip));
            Assert.InRange(math.distance(direction, roundTrip), 0, 2.0e-6f);
        }
    }

    [Fact]
    public void MercatorRejectsPolarSingularity()
    {
        var northPole = new float3(0, 0, 1);
        Assert.False(PlanetaryProjection.TryForward(northPole, new(PlanetaryProjectionKind.WebMercator), out _));
    }

    [Fact]
    public void MapTileBakerCarriesFieldIdentityAndProjectionValidity()
    {
        var field = new PlanetaryFieldDefinition(99, 10, 0, AdvancedErosionParameters.Default);
        var layout = new PlanetaryMapTileLayout(
            new(PlanetaryProjectionKind.Orthographic), 0, 0, 0, 9, 1);
        var key = new PlanetaryMapTileKey(field.FieldVersion, 1, 7, layout, PlanetaryQueryScale.AtFootprint(0.1f));
        var tile = PlanetaryMapTileBaker.Bake(field, key, new FlatField());
        Assert.Equal(key, tile.Key);
        Assert.Equal(layout.StorageSize * layout.StorageSize, tile.Samples.Length);
        Assert.Contains(tile.Validity, valid => valid);
        Assert.Contains(tile.Validity, valid => !valid);
        Assert.All(tile.Samples.Where((_, index) => tile.Validity[index]), sample => Assert.Equal(field.FieldVersion, sample.FieldVersion));
    }

    private static float3 Direction(double longitude, double latitude)
    {
        var cos = Math.Cos(latitude);
        return new((float)(cos * Math.Cos(longitude)), (float)(cos * Math.Sin(longitude)), (float)Math.Sin(latitude));
    }

    private readonly struct FlatField : IPlanetaryBaseField
    {
        public PlanetaryBaseFieldSample Sample(float3 unitDirection) => new(0, float3.zero, 0, float3.zero, 0);
    }
}
