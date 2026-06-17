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
        Assert.Equal(new float2(1.0f, 2.0f), math.float2(1.0f, 2.0f));
        Assert.Equal(new float3(1.0f, 1.0f, 1.0f), math.float3(1.0f));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), math.float4(new float3(1.0f, 2.0f, 3.0f), 4.0f));
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
        Assert.Equal(0.5f, math.smootherstep(0.5f), precision: 5);
    }

    [Fact]
    public void TrigIntrinsicsUseRadians()
    {
        Assert.Equal(1.0f, math.sin(math.HALF_PI), precision: 5);
        Assert.Equal(-1.0f, math.cos(math.PI), precision: 5);
        Assert.Equal(1.0f, math.tan(math.PI * 0.25f), precision: 5);
        Assert.Equal(math.HALF_PI, math.atan2(1.0f, 0.0f), precision: 5);
    }

    [Fact]
    public void AetheriaPrimitivesRemainSmallAndDeterministic()
    {
        Assert.Equal(6.0f, math.csum(new float3(1.0f, 2.0f, 3.0f)));
        Assert.Equal(MathF.Exp(-2.0f), math.decay(1.0f, 2.0f, 1.0f), precision: 5);
        Assert.Equal(1.0f - MathF.Exp(-2.0f), math.damp(0.0f, 1.0f, 2.0f, 1.0f), precision: 5);

        Assert.Equal(15.0f, math.catmullrom(0.0f, 10.0f, 20.0f, 30.0f, 0.5f), precision: 5);
        Assert.Equal(0.25f, math.quadratic_bezier(0.0f, 0.0f, 1.0f, 0.5f), precision: 5);
        Assert.Equal(0.5f, math.cubic_bezier(0.0f, 0.0f, 1.0f, 1.0f, 0.5f), precision: 5);
    }

    [Fact]
    public void SegmentDistanceReportsClosestPoint()
    {
        var distance = math.distance_to_segment(new float2(2.0f, 3.0f), new float2(0.0f, 0.0f), new float2(4.0f, 0.0f), out var closest);

        Assert.Equal(3.0f, distance, precision: 5);
        Assert.Equal(new float2(2.0f, 0.0f), closest);
    }

    [Fact]
    public void FirstOrderInterceptSolvesSimpleClosingTarget()
    {
        var time = math.first_order_intercept_time(
            2.0f,
            new float3(10.0f, 0.0f, 0.0f),
            new float3(-1.0f, 0.0f, 0.0f));

        Assert.Equal(10.0f / 3.0f, time, precision: 5);
    }

    [Fact]
    public void ValueNoiseIsDeterministicAndSmoothAtCellCorners()
    {
        var position = new float2(12.25f, -4.5f);

        Assert.Equal(math.value_noise(position), math.value_noise(position));
        Assert.Equal(math.hash(new float2(3.0f, 5.0f)), math.value_noise(new float2(3.0f, 5.0f)), precision: 5);

        var bicubic = math.value_noise_bicubic(position);
        Assert.True(bicubic > -0.25f && bicubic < 1.25f);
    }

    [Fact]
    public void VoronoiBatchProducesToneMappedColors()
    {
        var xs = new[] { 0.0f, 24.0f, 96.0f };
        var ys = new[] { 0.0f, 36.0f, 144.0f };
        var tones = new[] { CultMathTone.Background, CultMathTone.Header, CultMathTone.Body };
        var spans = new[] { 1920.0f, 8.0f, 8.0f };
        var colors = new Color32[xs.Length];

        Voronoi.SampleTones(xs, ys, tones, spans, 1080.0f, 12, colors);

        Assert.NotEqual(new Color32(), colors[0]);
        Assert.True(colors[1].r > colors[1].g);
        Assert.True(colors[2].r > 120 && colors[2].g > 120 && colors[2].b > 120);
    }

    [Fact]
    public void BatchRadialFalloffAccelerationMatchesScalarContract()
    {
        var xs = new[] { 0.0f, 5.0f, 10.0f };
        var ys = new[] { 0.0f, 0.0f, 0.0f };
        var ax = new float[xs.Length];
        var ay = new float[xs.Length];

        BatchMath.AddRadialFalloffAcceleration2D(xs, ys, 10.0f, 0.0f, 10.0f, 20.0f, ax, ay);

        Assert.True(BatchMath.LaneCount >= 4);
        Assert.Equal(5.0f, ax[0], precision: 5);
        Assert.Equal(7.5f, ax[1], precision: 5);
        Assert.Equal(0.0f, ax[2], precision: 5);
        Assert.Equal(0.0f, ay[0], precision: 5);
    }

    [Fact]
    public void BatchEulerIntegrationHonorsDynamicMask()
    {
        var dynamicMask = new[] { 1.0f, 0.0f };
        var px = new[] { 0.0f, 0.0f };
        var py = new[] { 0.0f, 0.0f };
        var vx = new[] { 1.0f, 1.0f };
        var vy = new[] { 0.0f, 0.0f };
        var ax = new[] { 2.0f, 2.0f };
        var ay = new[] { 0.0f, 4.0f };

        BatchMath.IntegrateSemiImplicitEuler2D(0.5f, dynamicMask, px, py, vx, vy, ax, ay);

        Assert.Equal(2.0f, vx[0], precision: 5);
        Assert.Equal(1.0f, px[0], precision: 5);
        Assert.Equal(1.0f, vx[1], precision: 5);
        Assert.Equal(0.0f, px[1], precision: 5);
        Assert.Equal(0.0f, py[1], precision: 5);
    }
}
