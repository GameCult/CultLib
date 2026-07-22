using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryPageTests
{
    [Fact]
    public void BilinearSamplingAndSummaryUseOnePageContract()
    {
        var layout = new PlanetaryPageLayout(new(PlanetaryCubeFace.PositiveZ, 0, 0, 0), 3, 1);
        var samples = new PlanetaryPageSample[layout.StorageSize * layout.StorageSize];
        for (var y = 0; y < layout.StorageSize; y++)
        for (var x = 0; x < layout.StorageSize; x++)
            samples[y * layout.StorageSize + x] = new(x + y * 10, new float3(x, y, 0), x / 10.0f, y / 10.0f);

        var sample = PlanetaryPageSampling.Bilinear(samples, layout, new float2(0.25f, 0.75f));
        Assert.Equal(26.5f, sample.Height, 4);
        Assert.Equal(new float3(1.5f, 2.5f, 0), sample.TangentGradient);
        var summary = PlanetaryPageSampling.Summarize(samples, layout, 0.125f);
        Assert.Equal(0, summary.MinimumHeight);
        Assert.Equal(44, summary.MaximumHeight);
        Assert.Equal(0.125f, summary.UnresolvedHeightBound);
        Assert.True(summary.MaximumSlope > 0);
    }

    [Fact]
    public void ChildPageStoresOnlyResidualAndComposesBackToDirectField()
    {
        var field = new PlanetaryFieldDefinition(7, 8.4f, 3, AdvancedErosionParameters.Default with
        {
            Scale = 0.63f,
            Strength = 0.12f,
            Octaves = 7,
        });
        var source = new FixtureField();
        var root = PlanetaryPageBaker.Bake(field, new(new(PlanetaryCubeFace.PositiveZ, 0, 0, 0), 17, 2), source);
        var child = PlanetaryPageBaker.Bake(field, new(new(PlanetaryCubeFace.PositiveZ, 1, 1, 0), 17, 2), source);
        var direction = PlanetaryPageSampling.DirectionAtLocal(child.Layout, 0.5, 0.5);
        var pages = new[] { new PlanetaryResidentPage(root, 1), new PlanetaryResidentPage(child, 1) };
        Assert.True(PlanetaryPageSetSampling.TrySample(pages, direction, out var composed));
        var spacing = PlanetaryPageSampling.NominalAngularTexelSize(child.Layout) * field.Radius;
        var direct = PlanetaryField.Sample(field, direction, source.Sample(direction), PlanetaryQueryScale.AtFootprint(spacing));
        Assert.InRange(MathF.Abs(composed.Height - direct.RadialDisplacement), 0, 2.0e-5f);
        Assert.InRange(math.distance(composed.TangentGradient, direct.TangentGradient), 0, 3.0e-5f);
    }

    private readonly struct FixtureField : IPlanetaryBaseField
    {
        public PlanetaryBaseFieldSample Sample(float3 direction)
        {
            var value = direction.x * 0.4f - direction.y * 0.2f + direction.z * 0.5f;
            var gradient = new float3(0.4f, -0.2f, 0.5f);
            gradient -= direction * math.dot(gradient, direction);
            return new(value * 0.08f, gradient * 0.08f, value, gradient, value);
        }
    }
}
