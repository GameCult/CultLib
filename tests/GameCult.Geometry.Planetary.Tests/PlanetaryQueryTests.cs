using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryQueryTests
{
    private static readonly PlanetaryFieldDefinition Field = new(12, 10, 0, AdvancedErosionParameters.Default with { Strength = 0, Octaves = 0 });
    private static readonly PlanetaryQueryScale Scale = PlanetaryQueryScale.AtFootprint(0.1f, 0);

    [Fact]
    public void RayIntersectionAndClearanceUseTheCpuSurfaceContract()
    {
        var source = new BulgedSphere();
        Assert.True(PlanetaryQueries.TryIntersectRay(
            Field, new double3(0, 0, 20), new double3(0, 0, -1), 30,
            Scale, new PlanetaryRadialBounds(-1, 1), source, out var hit));
        Assert.InRange(hit.Distance, 8.99999, 9.00001);
        Assert.InRange(hit.PlanetLocalPosition.z, 10.99999, 11.00001);
        var clearance = PlanetaryQueries.SegmentClearance(
            Field, new double3(-2, 0, 12), new double3(2, 0, 12), 0.1, Scale, source);
        Assert.True(clearance > 0.8);
    }

    [Fact]
    public void GreatCircleAndRegionQueriesReturnVersionedSamples()
    {
        var source = new BulgedSphere();
        var path = PlanetaryQueries.SampleGreatCircle(Field, new float3(1, 0, 0), new float3(0, 1, 0), 8, Scale, source);
        Assert.Equal(9, path.Length);
        Assert.Equal(MathF.PI * 5, path[^1].Distance, 4);
        Assert.All(path, point => Assert.Equal(Field.FieldVersion, point.Surface.FieldVersion));
        var summary = PlanetaryQueries.Summarize(Field, path.Select(point => point.Surface.UnitDirection).ToArray(), Scale, source);
        Assert.True(summary.MaximumDisplacement >= summary.MinimumDisplacement);
        Assert.True(summary.MaximumSlope >= 0);
    }

    private readonly struct BulgedSphere : IPlanetaryBaseField
    {
        public PlanetaryBaseFieldSample Sample(float3 direction)
        {
            var displacement = direction.z;
            var gradient = new float3(0, 0, 1) - direction * direction.z;
            return new(displacement, gradient, 0, float3.zero, 0);
        }
    }
}
