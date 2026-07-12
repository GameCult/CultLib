using Xunit;

namespace CultMath.Tests;

public sealed class SphericalErosionTests
{
    [Fact]
    public void SampleIsDeterministicAndScaleInvariant()
    {
        var parameters = SphericalErosionParameters.Default with { Seed = 42.0f };
        var a = SphericalErosion.Sample(new float3(0.31f, -0.74f, 0.59f), 0.2f, new float3(0.4f, -0.1f, 0.2f), parameters);
        var repeated = SphericalErosion.Sample(new float3(0.31f, -0.74f, 0.59f), 0.2f, new float3(0.4f, -0.1f, 0.2f), parameters);
        var b = SphericalErosion.Sample(new float3(3.1f, -7.4f, 5.9f), 0.2f, new float3(0.4f, -0.1f, 0.2f), parameters);

        Assert.Equal(a, repeated);
        Assert.InRange(math.abs(a.HeightOffset-b.HeightOffset),0.0f,1.0e-6f);
        Assert.InRange(math.abs(a.Ridge-b.Ridge),0.0f,1.0e-6f);
        Assert.InRange(math.abs(a.Gully-b.Gully),0.0f,5.0e-5f);
        Assert.InRange(math.length(a.Gradient-b.Gradient),0.0f,5.0e-6f);
        Assert.InRange(a.HeightOffset, -0.08f, 0.08f);
        Assert.InRange(a.Ridge, 0.0f, 1.0f);
        Assert.InRange(a.Gully, 0.0f, 1.0f);
    }

    [Fact]
    public void NearbySamplesRemainContinuousAcrossAChartBoundary()
    {
        var parameters = SphericalErosionParameters.Default;
        var left = SphericalErosion.Sample(math.normalize(new float3(1.0f, 0.99999f, 0.2f)), 0.1f, new float3(0.2f, -0.1f, 0.05f), parameters);
        var right = SphericalErosion.Sample(math.normalize(new float3(0.99999f, 1.0f, 0.2f)), 0.1f, new float3(0.2f, -0.1f, 0.05f), parameters);

        Assert.InRange(math.abs(left.HeightOffset - right.HeightOffset), 0.0f, 0.0002f);
    }
}
