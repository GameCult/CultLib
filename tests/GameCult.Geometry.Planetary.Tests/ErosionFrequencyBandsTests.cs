using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class ErosionFrequencyBandsTests
{
    [Fact]
    public void RadiusIndependentWorldWavelengthSelectsSameBands()
    {
        var small = ErosionFrequencyBands.Select(1_000.0f, 100.0f, 8, 2.0f, 20.0f, 0.5f);
        var large = ErosionFrequencyBands.Select(1_000.0f, 100.0f, 8, 2.0f, 20.0f, 0.5f);
        Assert.Equal(small, large);
        Assert.Equal(3, small.ActiveOctaves);
    }

    [Fact]
    public void TransitionPartiallyWeightsLastResolvableOctave()
    {
        var selection = ErosionFrequencyBands.Select(1_000.0f, 50.0f, 8, 2.0f, 1.0f, 0.5f);
        Assert.Equal(4, selection.ActiveOctaves);
        Assert.InRange(selection.FinalOctaveWeight, 0.0f, 1.0f);
        Assert.True(selection.UnresolvedHeightBound > 0.0f);
    }

    [Fact]
    public void BandedFilterPreservesLowOctaveResultExactly()
    {
        var p = AdvancedErosionParameters.Default;
        var band = new ErosionBandSelection(2, 1.0f, 0.0f, 0.0f);
        var banded = AdvancedErosionFilter.Sample(new float2(0.42f, 0.31f), new float3(0.5f, 0.1f, -0.2f), 0.0f, p, band);
        var truncated = AdvancedErosionFilter.Sample(new float2(0.42f, 0.31f), new float3(0.5f, 0.1f, -0.2f), 0.0f, p with { Octaves = 2 });
        Assert.Equal(truncated, banded);
    }
}
