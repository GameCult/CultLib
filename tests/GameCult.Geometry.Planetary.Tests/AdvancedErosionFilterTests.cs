using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class AdvancedErosionFilterTests
{
    [Fact]
    public void DefaultReferenceFixtureIsStable()
    {
        var result = AdvancedErosionFilter.Sample(
            new float2(0.42f, 0.31f),
            new float3(0.5f, 0.1f, -0.2f),
            0.0f,
            AdvancedErosionParameters.Default);

        Assert.Equal(0.0639375f, result.Magnitude, precision: 7);
        Assert.True(float.IsFinite(result.Delta.x));
        Assert.True(float.IsFinite(result.Delta.y));
        Assert.True(float.IsFinite(result.Delta.z));
        Assert.InRange(result.RidgeMap, -1.0f, 1.0f);
    }

    [Fact]
    public void FlatInputAndZeroRoundingRemainFinite()
    {
        var parameters = AdvancedErosionParameters.Default with { Rounding = float4.zero };
        var result = AdvancedErosionFilter.Sample(new float2(0.25f, 0.75f), float3.zero, 0.0f, parameters);

        Assert.True(float.IsFinite(result.Delta.x));
        Assert.True(float.IsFinite(result.RidgeMap));
        Assert.True(float.IsFinite(result.FadeTarget));
    }

    [Fact]
    public void PhacellePartialNormalizationIsFiniteAtEndpoints()
    {
        foreach (var normalization in new[] { 0.0f, 0.5f, 1.0f })
        {
            var sample = AdvancedErosionFilter.Phacelle(new float2(1.7f, -3.2f), new float2(0.6f, -0.8f), 0.7f, 0.25f, normalization);
            Assert.True(float.IsFinite(sample.x));
            Assert.True(float.IsFinite(sample.y));
        }
    }
}
