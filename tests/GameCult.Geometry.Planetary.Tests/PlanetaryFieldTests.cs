using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryFieldTests
{
    private static readonly PlanetaryFieldDefinition Definition = new(
        0x7f59_6d11_02ab_9c31UL,
        8.4f,
        42,
        AdvancedErosionParameters.Default with { Scale = 0.63f, Strength = 0.12f, Octaves = 7 });

    [Fact]
    public void SampleIsDeterministicScaleAwareAndTangent()
    {
        var direction = math.normalize(new float3(0.31f, -0.72f, 0.61f));
        var source = new FixtureField();
        var coarse = PlanetaryField.Sample(Definition, direction, source.Sample(direction), PlanetaryQueryScale.AtFootprint(0.1f));
        var fine = PlanetaryField.Sample(Definition, direction, source.Sample(direction), PlanetaryQueryScale.AtFootprint(0.01f));
        var repeated = PlanetaryField.Sample(Definition, direction, source.Sample(direction), PlanetaryQueryScale.AtFootprint(0.01f));

        Assert.Equal(fine, repeated);
        Assert.Equal(Definition.FieldVersion, fine.FieldVersion);
        Assert.True(fine.UnresolvedHeightBound <= coarse.UnresolvedHeightBound);
        Assert.True(fine.FinestResolvedWavelength <= coarse.FinestResolvedWavelength);
        Assert.InRange(MathF.Abs(math.dot(fine.UnitDirection, fine.TangentGradient)), 0, 2.0e-5f);
        Assert.Equal(1, math.length(fine.SurfaceNormal), 5);
    }

    [Fact]
    public void BatchAndDoublePositionUseThePointContract()
    {
        var directions = new[]
        {
            math.normalize(new float3(1, 2, 3)),
            math.normalize(new float3(-2, 0.5f, 1)),
            math.normalize(new float3(0.1f, -0.2f, -1)),
        };
        var output = new PlanetarySurfaceSample[directions.Length];
        var source = new FixtureField();
        var scale = PlanetaryQueryScale.AtFootprint(0.05f);
        PlanetaryField.SampleBatch(Definition, directions, scale, source, output);

        for (var i = 0; i < directions.Length; i++)
        {
            var normalized = math.normalize(directions[i]);
            var expected = PlanetaryField.Sample(Definition, normalized, source.Sample(normalized), scale);
            Assert.InRange(MathF.Abs(expected.RadialDisplacement - output[i].RadialDisplacement), 0, 1.0e-6f);
            Assert.InRange(math.distance(expected.TangentGradient, output[i].TangentGradient), 0, 1.0e-6f);
        }

        var d = directions[0];
        var position = new double3(d.x * 8.0e11, d.y * 8.0e11, d.z * 8.0e11);
        var positioned = PlanetaryField.SamplePosition(Definition, position, scale, source);
        Assert.InRange(math.distance(positioned.UnitDirection, d), 0, 2.0e-6f);
    }

    [Fact]
    public void FieldIdentityChangesWithEveryLoadBearingInput()
    {
        var erosion = AdvancedErosionParameters.Default;
        var baseline = PlanetaryFieldDefinition.Create(3, 8.4f, 7, erosion);
        Assert.Equal(baseline.FieldVersion, PlanetaryFieldDefinition.Create(3, 8.4f, 7, erosion).FieldVersion);
        Assert.NotEqual(baseline.FieldVersion, PlanetaryFieldDefinition.Create(4, 8.4f, 7, erosion).FieldVersion);
        Assert.NotEqual(baseline.FieldVersion, PlanetaryFieldDefinition.Create(3, 8.5f, 7, erosion).FieldVersion);
        Assert.NotEqual(baseline.FieldVersion, PlanetaryFieldDefinition.Create(3, 8.4f, 8, erosion).FieldVersion);
        Assert.NotEqual(baseline.FieldVersion, PlanetaryFieldDefinition.Create(3, 8.4f, 7, erosion with { Gain = 0.49f }).FieldVersion);
    }

    private readonly struct FixtureField : IPlanetaryBaseField
    {
        public PlanetaryBaseFieldSample Sample(float3 direction)
        {
            var value = direction.x * 0.6f + direction.y * direction.z * 0.3f;
            var fieldGradient = new float3(0.6f, direction.z * 0.3f, direction.y * 0.3f);
            fieldGradient -= direction * math.dot(fieldGradient, direction);
            return new(value * 0.1f, fieldGradient * 0.1f, value, fieldGradient, math.clamp(value, -1, 1));
        }
    }
}
