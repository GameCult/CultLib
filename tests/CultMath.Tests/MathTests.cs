using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class MathTests
{
    [Fact]
    public void FracMatchesShaderStylePositiveAndNegativeValues()
    {
        Assert.Equal(0.25f, math.frac(1.25f));
        Assert.Equal(0.75f, math.frac(-1.25f));
    }

    [Fact]
    public void VectorOperationsAreComponentWise()
    {
        var value = new float3(1.0f, 2.0f, 3.0f) * new float3(4.0f, 5.0f, 6.0f);

        Assert.Equal(new float3(4.0f, 10.0f, 18.0f), value);
    }

    [Fact]
    public void VectorConstructorsAcceptLowerDimensionVectors()
    {
        Assert.Equal(new float3(1.0f, 2.0f, 3.0f), new float3(new float2(1.0f, 2.0f), 3.0f));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), new float4(new float2(1.0f, 2.0f), 3.0f, 4.0f));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), new float4(new float3(1.0f, 2.0f, 3.0f), 4.0f));
    }

    [Fact]
    public void NormalizeProducesUnitLength()
    {
        var value = math.normalize(new float3(3.0f, 4.0f, 0.0f));

        Assert.Equal(1.0f, math.length(value), precision: 5);
    }

    [Fact]
    public void SmoothstepUsesHermiteRamp()
    {
        Assert.Equal(0.0f, math.smoothstep(0.0f, 1.0f, -1.0f));
        Assert.Equal(0.5f, math.smoothstep(0.0f, 1.0f, 0.5f), precision: 5);
        Assert.Equal(1.0f, math.smoothstep(0.0f, 1.0f, 2.0f));
    }

    [Fact]
    public void TrigIntrinsicsUseRadians()
    {
        Assert.Equal(1.0f, math.sin(math.HALF_PI), precision: 5);
        Assert.Equal(-1.0f, math.cos(math.PI), precision: 5);
        Assert.Equal(1.0f, math.tan(math.PI * 0.25f), precision: 5);
        Assert.Equal(math.HALF_PI, math.atan2(1.0f, 0.0f), precision: 5);
    }
}
