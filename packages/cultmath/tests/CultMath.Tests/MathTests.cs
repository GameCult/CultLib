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

    // Expected values come from a Python transcription of the paper's pcg3d/pcg4d (JCGT 9(3) 2020, p. 32) and
    // O'Neill's pcg_output_rxs_m_xs_32_32 with PCG_DEFAULT_MULTIPLIER_32/INCREMENT_32, independent of this code.
    [Fact]
    public void PcgHashesMatchReferenceTranscription()
    {
        static int3 U3(uint x, uint y, uint z) => new((int)x, (int)y, (int)z);
        static int4 U4(uint x, uint y, uint z, uint w) => new((int)x, (int)y, (int)z, (int)w);

        Assert.Equal(0x07BB2FE2u, math.pcg(0u));
        Assert.Equal(0xA8BEEA3Cu, math.pcg(1u));
        Assert.Equal(0x995312E1u, math.pcg(0x12345678u));
        Assert.Equal(0xE62A4902u, math.pcg(0xFFFFFFFFu));
        Assert.Equal(U3(0x9BAFD7C6u, 0xA8E88A6Bu, 0x3F15482Cu), math.pcg3d(new int3(0, 0, 0)));
        Assert.Equal(U3(0xFA9F79A6u, 0x48F2F44Cu, 0x596F5AB1u), math.pcg3d(new int3(1, 2, 3)));
        Assert.Equal(U3(0x67DCF282u, 0x49E87E17u, 0x10018917u), math.pcg3d(U3(0xFFFFFFFFu, 0x80000000u, 0x7FFFFFFFu)));
        Assert.Equal(U4(0x0F02F829u, 0x2D568769u, 0x32B0C43Bu, 0xD32548EAu), math.pcg4d(new int4(0, 0, 0, 0)));
        Assert.Equal(U4(0x3622CD16u, 0xF11471D8u, 0xE1109B3Fu, 0x02B94C2Fu), math.pcg4d(new int4(1, 2, 3, 4)));
        Assert.Equal(U4(0x72180037u, 0x81D493E6u, 0xD9BA7226u, 0x14C9ABA7u), math.pcg4d(U4(0xFFFFFFFFu, 0x80000000u, 0x7FFFFFFFu, 0xDEADBEEFu)));
        Assert.Equal(U3(0xA7B40FEEu, 0x9B70DFC0u, 0x3106C2C5u), math.pcg3d(new float3(1.5f, -2.25f, 1e7f)));
        Assert.Equal(U3(0x5A09E5C6u, 0x53EC0DABu, 0x4BE4A1ECu), math.pcg3d(new float2(3.0f, 4.0f)));
        Assert.Equal(U4(0x1849E380u, 0xF199DEE0u, 0x6C6B4934u, 0x34AF16DFu),
            math.pcg4d(new float4(-0.0f, BitConverter.Int32BitsToSingle(0x7FC00000), 0.5f, 1e-7f)));
    }

    [Fact]
    public void Pcg3dSeparatesAPositionGrid()
    {
        // A single 32-bit component is subject to birthday collisions (x has one here); the full output is distinct.
        var hashes = new HashSet<int3>();
        for (var i = 0; i < 100; i++)
        for (var j = 0; j < 100; j++)
            hashes.Add(math.pcg3d(new float2(i * 0.5f - 25.0f, j * 0.5f - 25.0f)));
        Assert.Equal(10000, hashes.Count);
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
        Assert.Equal(new int2(1, 2), math.int2(1, 2));
        Assert.Equal(new float2(1.0f, 2.0f), math.float2(1.0f, 2.0f));
        Assert.Equal(new float3(1.0f, 1.0f, 1.0f), math.float3(1.0f));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), math.float4(new float3(1.0f, 2.0f, 3.0f), 4.0f));
        Assert.Equal(new float3(1.0f, 2.0f, 3.0f), new float3(new float2(1.0f, 2.0f), 3.0f));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), new float4(new float2(1.0f, 2.0f), 3.0f, 4.0f));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), new float4(new float3(1.0f, 2.0f, 3.0f), 4.0f));
    }

    [Fact]
    public void VectorsExposeCommonSwizzles()
    {
        Assert.Equal(new float3(2.0f, 3.0f, 1.0f), new float3(1.0f, 2.0f, 3.0f).yzx);
        Assert.Equal(new float3(3.0f, 2.0f, 1.0f), new float3(1.0f, 2.0f, 3.0f).zyx);
        Assert.Equal(new float2(3.0f, 2.0f), new float3(1.0f, 2.0f, 3.0f).zy);
        Assert.Equal(new float3(1.0f, 2.0f, 4.0f), new float4(1.0f, 2.0f, 3.0f, 4.0f).xyw);
        Assert.Equal(new float3(2.0f, 3.0f, 1.0f), new float4(1.0f, 2.0f, 3.0f, 4.0f).yzx);
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), new float4(1.0f, new float3(2.0f, 3.0f, 4.0f)));
        Assert.Equal(new float4(1.0f, 2.0f, 3.0f, 4.0f), math.float4(1.0f, new float3(2.0f, 3.0f, 4.0f)));
    }

    [Fact]
    public void VectorsSupportUnityStyleFieldAndSwizzleMutation()
    {
        var position = new float3(1.0f, 2.0f, 3.0f);
        position.y = 5.0f;
        position.xz = new float2(8.0f, 13.0f);

        Assert.Equal(new float3(8.0f, 5.0f, 13.0f), position);

        var bounds = new float4(1.0f, 2.0f, 3.0f, 4.0f);
        bounds.xy = new float2(-1.0f, -2.0f);

        Assert.Equal(new float4(-1.0f, -2.0f, 3.0f, 4.0f), bounds);
    }

    [Fact]
    public void DoubleVectorsSupportDampedMath()
    {
        var value = new double3(1.0, 2.0, 3.0);
        value.xy = new double2(5.0, 8.0);

        Assert.Equal(new double3(5.0, 8.0, 3.0), value);
        Assert.Equal(new double2(Math.Exp(-2.0), Math.Exp(-4.0)), math.exp(new double2(-2.0, -4.0)));
        Assert.Equal(new double3(2.0, 4.0, 6.0), math.lerp(new double3(0.0, 0.0, 0.0), new double3(4.0, 8.0, 12.0), new double3(0.5, 0.5, 0.5)));
    }

    [Fact]
    public void Int2SupportsGridArithmeticAndFloatPromotion()
    {
        var cell = new int2(2, 3);

        Assert.Equal(new int2(3, 5), cell + new int2(1, 2));
        Assert.Equal(new int2(4, 6), cell * new int2(2, 2));
        Assert.Equal(3, cell[1]);

        float2 promoted = cell;
        Assert.Equal(new float2(2.0f, 3.0f), promoted);
    }

    [Fact]
    public void Bool2SupportsHullGridStyleStorage()
    {
        bool2 conductivity = true;

        conductivity.y = false;

        Assert.True(conductivity.x);
        Assert.False(conductivity[1]);
        Assert.Equal(new bool2(false, true), math.bool2(false, true));
    }

    [Fact]
    public void QuaternionLookRotationProducesUnitOrientation()
    {
        var rotation = quaternion.LookRotation(new float3(0.0f, 0.0f, 1.0f), new float3(0.0f, 1.0f, 0.0f));

        Assert.Equal(quaternion.identity, rotation);
        Assert.Equal(1.0f, math.length((float4)quaternion.LookRotation(new float3(1.0f, 0.0f, 0.0f), new float3(0.0f, 1.0f, 0.0f))), precision: 5);
    }

    [Fact]
    public void RandomUsesStableXorShift32Sequence()
    {
        var a = new Random(1234);
        var b = new Random(1234);

        Assert.Equal(332584831u, a.NextUInt());
        Assert.Equal(1855942593u, a.NextUInt());
        Assert.Equal(4018585650u, a.NextUInt());
        Assert.Equal(3348358578u, a.NextUInt());
        Assert.Equal(332584831u, b.NextUInt());
    }

    [Fact]
    public void RandomProvidesDeterministicPortableSampling()
    {
        var a = new Random(1234);
        var b = new Random(1234);

        Assert.Equal(a.NextUInt(), b.NextUInt());
        Assert.Equal(a.NextFloat(), b.NextFloat());

        var bounded = a.NextFloat(-2.0f, 4.0f);
        Assert.True(bounded >= -2.0f && bounded < 4.0f);

        var direction = a.NextFloat2Direction();
        Assert.Equal(1.0f, math.length(direction), precision: 5);

        var point = a.NextFloat3(new float3(-1.0f, -2.0f, -3.0f), new float3(1.0f, 2.0f, 3.0f));
        Assert.True(point.x >= -1.0f && point.x < 1.0f);
        Assert.True(point.y >= -2.0f && point.y < 2.0f);
        Assert.True(point.z >= -3.0f && point.z < 3.0f);
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
        Assert.Equal(2, math.min(2, 5));
        Assert.Equal(5, math.max(2, 5));
        Assert.True(math.isinf(float.PositiveInfinity));
    }

    [Fact]
    public void SignIsComponentWiseForSimulationVectors()
    {
        Assert.Equal(-1, math.sign(-12.0f));
        Assert.Equal(0, math.sign(0.0f));
        Assert.Equal(1, math.sign(12.0f));
        Assert.Equal(new int3(-1, 0, 1), math.sign(new float3(-2.0f, 0.0f, 4.0f)));
    }

    [Fact]
    public void RotateTurnsFloat2CounterClockwiseInRadians()
    {
        var rotated = math.rotate(new float2(1.0f, 0.0f), math.HALF_PI);

        Assert.Equal(0.0f, rotated.x, precision: 5);
        Assert.Equal(1.0f, rotated.y, precision: 5);

        var rotatedDegrees = math.rotate_degrees(new float2(0.0f, 1.0f), 90.0f);
        Assert.Equal(-1.0f, rotatedDegrees.x, precision: 5);
        Assert.Equal(0.0f, rotatedDegrees.y, precision: 5);
    }

    [Fact]
    public void AetheriaPrimitivesRemainSmallAndDeterministic()
    {
        Assert.Equal(6.0f, math.csum(new float3(1.0f, 2.0f, 3.0f)));
        Assert.Equal(0.25f, math.unlerp(10.0f, 30.0f, 15.0f));
        Assert.Equal(new float2(0.25f, 0.75f), math.unlerp(new float2(10.0f, 10.0f), new float2(30.0f, 30.0f), new float2(15.0f, 25.0f)));
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
    public void RectNormalizesAndDerivesFromMinMax()
    {
        var bounds = new rect(8.0f, 6.0f, 2.0f, -2.0f);

        Assert.Equal(new float2(2.0f, -2.0f), bounds.min);
        Assert.Equal(new float2(8.0f, 6.0f), bounds.max);
        Assert.Equal(new float2(6.0f, 8.0f), bounds.size);
        Assert.Equal(new float2(5.0f, 2.0f), bounds.center);
        Assert.Equal(48.0f, bounds.area);
    }

    [Fact]
    public void RectContainmentIncludesBoundary()
    {
        var bounds = new rect(-2.0f, -1.0f, 4.0f, 3.0f);

        Assert.True(bounds.Contains(new float2(-2.0f, -1.0f)));
        Assert.True(bounds.Contains(new float2(4.0f, 3.0f)));
        Assert.True(bounds.Contains(new rect(-1.0f, 0.0f, 1.0f, 2.0f)));
        Assert.False(bounds.Contains(new float2(4.001f, 3.0f)));
    }

    [Fact]
    public void RectIntersectionTreatsTouchingEdgesAsContact()
    {
        var left = new rect(0.0f, 0.0f, 4.0f, 4.0f);
        var touching = new rect(4.0f, 1.0f, 8.0f, 3.0f);
        var separate = new rect(4.001f, 1.0f, 8.0f, 3.0f);

        Assert.True(left.Intersects(touching));
        Assert.Equal(0.0f, left.Intersection(touching).area);
        Assert.False(left.Intersects(separate));
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

    [Theory]
    [InlineData(0.0f, 0.0f, 0.0f)]
    [InlineData(0.25f, -0.5f, -0.425866783f)]
    [InlineData(12.25f, -4.5f, 0.258312f)]
    [InlineData(536.5106f, 536.5106f, -0.6901103f)]
    public void SimplexNoiseMatchesUnityMathematics(float x, float y, float expected)
    {
        Assert.Equal(expected, math.snoise(new float2(x, y)), precision: 6);
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

    // Tabulated to 16 significant figures (Abramowitz & Stegun table 7.1 lineage / standard erf tables).
    [Theory]
    [InlineData(0.0f, 0.0)]
    [InlineData(0.5f, 0.5204998778130465)]
    [InlineData(-0.5f, -0.5204998778130465)]
    [InlineData(1.0f, 0.8427007929497149)]
    [InlineData(-1.0f, -0.8427007929497149)]
    [InlineData(2.0f, 0.9953222650189527)]
    [InlineData(-2.0f, -0.9953222650189527)]
    [InlineData(3.0f, 0.9999779095030014)]
    [InlineData(-3.0f, -0.9999779095030014)]
    public void ErfMatchesReferenceValues(float x, double expected)
    {
        Assert.True(
            Math.Abs(math.erf(x) - expected) <= 2e-6,
            $"erf({x}) = {math.erf(x)}, expected {expected}");
    }

    [Fact]
    public void ErfIsOdd()
    {
        for (var i = -30; i <= 30; i++)
        {
            var x = i / 10.0f;
            Assert.Equal(-math.erf(x), math.erf(-x));
        }
    }

    [Fact]
    public void ErfApproachesItsLimitsOfPlusMinusOne()
    {
        Assert.Equal(1.0f, math.erf(6.0f));
        Assert.Equal(1.0f, math.erf(20.0f));
        Assert.Equal(-1.0f, math.erf(-6.0f));
        Assert.Equal(-1.0f, math.erf(-20.0f));
    }

    [Fact]
    public void ErfinvInvertsErf()
    {
        for (var i = -999; i <= 999; i++)
        {
            var y = i / 1000.0f;
            var x = math.erfinv(y);
            var roundTrip = math.erf(x);
            Assert.True(
                MathF.Abs(roundTrip - y) < 1e-5f,
                $"erf(erfinv({y})) = {roundTrip}");
        }
    }

    // Reference values from bisecting the platform's double-precision erf (Python's math.erf,
    // glibc-backed), not this package's own erf, to 200 iterations over [-6, 6] -- effectively
    // exact at float precision. This is what ErfinvInvertsErf cannot be: a check against this
    // package's own erf only proves the two functions agree with each other, not that either is
    // numerically correct. Soul measured the round-trip test alone to be blind to a coefficient
    // change that degraded erfinv's worst error 300-400x (5.0e-7 to 2.0e-4).
    [Theory]
    // Expected values are computed for the actual float32-rounded input (e.g. the literal
    // 0.999f is really 0.9990000128746033 as a double), not the decimal literal, because
    // erfinv's derivative diverges near +-1: at y = 0.999f that rounding alone moves the
    // true answer by ~2.6e-6, larger than the test's own error bar, if ignored.
    [InlineData(0.0f, 0.0)]
    [InlineData(0.1f, 0.08885599182530649)]
    [InlineData(-0.1f, -0.08885599182530649)]
    [InlineData(0.3f, 0.2724627261055238)]
    [InlineData(-0.3f, -0.2724627261055238)]
    [InlineData(0.5f, 0.4769362762044699)]
    [InlineData(-0.5f, -0.4769362762044699)]
    [InlineData(0.7f, 0.7328690598827587)]
    [InlineData(-0.7f, -0.732869059882759)]
    [InlineData(0.9f, 1.1630870719457724)]
    [InlineData(-0.9f, -1.1630870719457729)]
    [InlineData(0.95f, 1.3859037522360764)]
    [InlineData(-0.95f, -1.3859037522360773)]
    [InlineData(0.99f, 1.8213866009002766)]
    [InlineData(-0.99f, -1.8213866009002793)]
    [InlineData(0.999f, 2.3267563267961435)]
    [InlineData(-0.999f, -2.3267563267961657)]
    [InlineData(0.9999f, 2.751035437903192)]
    [InlineData(-0.9999f, -2.751035437903383)]
    public void ErfinvMatchesReferenceValues(float y, double expected)
    {
        Assert.True(
            Math.Abs(math.erfinv(y) - expected) <= 2e-6,
            $"erfinv({y}) = {math.erfinv(y)}, expected {expected}");
    }

    [Fact]
    public void ErfinvAtDomainEdgesAndBeyond()
    {
        // Domain is [-1, 1]. The edges are the correctly-signed infinite limits; anything
        // outside, including +-infinity itself, has no real inverse and is NaN.
        Assert.Equal(float.PositiveInfinity, math.erfinv(1.0f));
        Assert.Equal(float.NegativeInfinity, math.erfinv(-1.0f));
        Assert.True(float.IsNaN(math.erfinv(1.0001f)));
        Assert.True(float.IsNaN(math.erfinv(-1.0001f)));
        Assert.True(float.IsNaN(math.erfinv(2.0f)));
        Assert.True(float.IsNaN(math.erfinv(-2.0f)));
        Assert.True(float.IsNaN(math.erfinv(float.PositiveInfinity)));
        Assert.True(float.IsNaN(math.erfinv(float.NegativeInfinity)));
        Assert.True(float.IsNaN(math.erfinv(float.NaN)));
    }

    // The deleted ErfinvIsMonotonicallyIncreasing asserted a property erfinv does not have:
    // Soul measured 9662 backward steps scanning every representable float32 in [0, 0.999999],
    // first at y = 0.00022214651, worst drop 5.960464e-8 (one ULP at that scale) -- invisible
    // only because that fixture stepped by 0.001. erf has the same shape: 100 backward steps
    // over [0, 4.5], worst drop 1.192093e-7. Pin what is actually true: monotone to within a
    // tolerance comfortably above that measured noise floor.
    [Fact]
    public void ErfinvIsMonotonicWithinFloatingPointTolerance()
    {
        const float tolerance = 1e-6f;
        var previous = float.NegativeInfinity;
        for (var i = -999; i <= 999; i++)
        {
            var y = i / 1000.0f;
            var x = math.erfinv(y);
            Assert.True(
                x >= previous - tolerance,
                $"erfinv({y}) = {x} dropped more than {tolerance} below previous value {previous}");
            previous = x;
        }
    }

    // The consumer-level property that actually matters: fire control turns adjacent erf
    // values into a cell's probability mass, so a backward step in erf would show up as a
    // negative mass. Soul checked these geometries clean: 16/64/256/1024/4096 cells over
    // +-4 sigma, plus a tight 1 sigma spread and a very tight 0.25 sigma spread.
    [Theory]
    [InlineData(16, 4f)]
    [InlineData(64, 4f)]
    [InlineData(256, 4f)]
    [InlineData(1024, 4f)]
    [InlineData(4096, 4f)]
    [InlineData(256, 1f)]
    [InlineData(64, 0.25f)]
    public void ErfCellMassesStayNonNegativeAcrossRealisticGeometries(int cells, float sigmaRange)
    {
        var lo = -sigmaRange;
        var hi = sigmaRange;
        var step = (hi - lo) / cells;
        var previous = math.erf(lo / MathF.Sqrt(2f));
        for (var i = 1; i <= cells; i++)
        {
            var edge = lo + step * i;
            var current = math.erf(edge / MathF.Sqrt(2f));
            var mass = current - previous;
            Assert.True(mass >= 0f, $"cell {i} of {cells} over +-{sigmaRange} sigma has negative mass {mass}");
            previous = current;
        }
    }
}
