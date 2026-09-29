using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class PhacelleTests
{
    // Every tolerance below is at least 5x the largest error the same test measures at its seed.
    private const float GradientEps = 1.0e-3f;
    private const float GradientTolerance = 5.0e-3f;
    private const double ValueTolerance = 1.6e-5;
    private const double ThresholdBand = 0.02;

    private static float3 RandomPoint(System.Random random, float extent = 3.0f) => new(
        (random.NextSingle() * 2.0f - 1.0f) * extent, (random.NextSingle() * 2.0f - 1.0f) * extent, (random.NextSingle() * 2.0f - 1.0f) * extent);

    // A wave vector of length 2*pi times a stripe frequency in [0.5, 1.5], in a random direction.
    private static float3 RandomSide(System.Random random)
    {
        float3 direction;
        do
        {
            direction = new float3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f);
        }
        while (math.length(direction) < 0.1f);

        return math.normalize(direction) * (math.TAU * (0.5f + random.NextSingle()));
    }

    // ---- An independent double-precision evaluation: all 64 cells, no prune, no gradient. ----
    //
    // It shares only the hash (pcg3d over the integer cell coordinate, top 24 bits) and the constants
    // the function is specified by: weight max(0, exp(-2 d^2) - 0.01111), jitter in [-0.5, 0.5).

    private readonly record struct Reference(double Cos, double Sin, double RawLength, double SupportEdgeGap);

    private static Reference Evaluate(float3 p, float3 side, float offset, float normalization)
    {
        double fx = Math.Floor(p.x), fy = Math.Floor(p.y), fz = Math.Floor(p.z);
        double lx = p.x - fx, ly = p.y - fy, lz = p.z - fz;
        double sumW = 0.0, sumCos = 0.0, sumSin = 0.0, edgeGap = double.MaxValue;
        var supportRadius = Math.Sqrt(Math.Log(1.0 / 0.01111) / 2.0);
        for (var dz = -1; dz <= 2; dz++)
        for (var dy = -1; dy <= 2; dy++)
        for (var dx = -1; dx <= 2; dx++)
        {
            var hash = math.pcg3d(new int3((int)fx + dx, (int)fy + dy, (int)fz + dz));
            double vx = lx - dx - (math.cellular_unit(hash.x) - 0.5);
            double vy = ly - dy - (math.cellular_unit(hash.y) - 0.5);
            double vz = lz - dz - (math.cellular_unit(hash.z) - 0.5);
            var d2 = vx * vx + vy * vy + vz * vz;
            edgeGap = Math.Min(edgeGap, Math.Abs(Math.Sqrt(d2) - supportRadius));
            var w = Math.Max(0.0, Math.Exp(-2.0 * d2) - 0.01111);
            var phase = vx * side.x + vy * side.y + vz * side.z + (double)offset * math.TAU;
            sumW += w;
            sumCos += w * Math.Cos(phase);
            sumSin += w * Math.Sin(phase);
        }

        double rawCos = sumCos / sumW, rawSin = sumSin / sumW;
        var raw = Math.Sqrt(rawCos * rawCos + rawSin * rawSin);
        var length = Math.Max(1.0 - normalization, raw);
        return new Reference(rawCos / length, rawSin / length, raw, edgeGap);
    }

    private static float3 CentralDifference(Func<float3, float> f, float3 p, float eps) => new(
        (f(p + new float3(eps, 0.0f, 0.0f)) - f(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
        (f(p + new float3(0.0f, eps, 0.0f)) - f(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
        (f(p + new float3(0.0f, 0.0f, eps)) - f(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

    private static float MaxComponentError(float3 numeric, float4 analytic) => MathF.Max(
        MathF.Abs(numeric.x - analytic.x), MathF.Max(MathF.Abs(numeric.y - analytic.y), MathF.Abs(numeric.z - analytic.z)));

    // ---- Value: the function is Johansen's, on 3D cells ----

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(0.9f)]
    [InlineData(1.0f)]
    public void ValueMatchesTheIndependentReferenceAndNormalizesAboveTheThreshold(float normalization)
    {
        var random = new System.Random(0x7ACE1 + (int)(normalization * 10.0f));
        var maxError = 0.0;
        var above = 0;
        var below = 0;
        for (var i = 0; i < 3000; i++)
        {
            var p = RandomPoint(random, 40.0f);
            var side = RandomSide(random);
            var offset = random.NextSingle();
            var actual = math.phacelle(p, side, offset, normalization);
            var expected = Evaluate(p, side, offset, normalization);

            maxError = Math.Max(maxError, Math.Max(Math.Abs(actual.cos.w - expected.Cos), Math.Abs(actual.sin.w - expected.Sin)));
            if (expected.RawLength >= 1.0 - normalization + ThresholdBand)
            {
                // The output magnitude is 1 wherever the raw magnitude reaches 1 - normalization.
                above++;
                var magnitude = Math.Sqrt((double)actual.cos.w * actual.cos.w + (double)actual.sin.w * actual.sin.w);
                Assert.True(Math.Abs(magnitude - 1.0) < ValueTolerance, $"magnitude {magnitude} above the threshold at {p}");
            }
            else if (expected.RawLength <= 1.0 - normalization - ThresholdBand)
            {
                below++;
            }
        }

        Assert.True(maxError < ValueTolerance, $"max value error against the reference {maxError}");
        if (normalization > 0.0f)
            Assert.True(above >= 500, $"only {above} points above the threshold");
        if (normalization is > 0.0f and < 1.0f)
            Assert.True(below >= 50, $"only {below} points below the threshold");
    }

    [Fact]
    public void ValueIsPeriodicInOffsetAndAQuarterCycleRotatesCosIntoSin()
    {
        // offset is in cycles: a quarter turn maps cos to -sin and sin to cos, and a whole turn is the
        // identity. The weights and the normalization do not depend on the phase, so this is exact
        // up to float rounding.
        var random = new System.Random(0x7ACE2);
        for (var i = 0; i < 200; i++)
        {
            var p = RandomPoint(random);
            var side = RandomSide(random);
            var offset = random.NextSingle();
            var basePhasor = math.phacelle(p, side, offset, 0.5f);
            var quarter = math.phacelle(p, side, offset + 0.25f, 0.5f);
            var whole = math.phacelle(p, side, offset + 1.0f, 0.5f);

            Assert.True(MathF.Abs(quarter.cos.w + basePhasor.sin.w) < 2.0e-5f);
            Assert.True(MathF.Abs(quarter.sin.w - basePhasor.cos.w) < 2.0e-5f);
            Assert.True(MathF.Abs(whole.cos.w - basePhasor.cos.w) < 2.0e-5f);
            Assert.True(MathF.Abs(whole.sin.w - basePhasor.sin.w) < 2.0e-5f);
        }
    }

    // ---- Invariant 8: the gradient is exact ----

    [Fact]
    public void GradientOfCosAndSinMatchesCentralDifferencesOnBothSidesOfTheNormalizationThreshold()
    {
        // Above the threshold the output is I / |I|; below it, I / (1 - normalization). The gradient
        // is discontinuous where |I| crosses the threshold, and at the edge of each cell's weight support
        // (distance 1.49998, where max(0, ...) starts: a jump of 4 d 0.01111 in grad w), so a band
        // around each is excluded (by the
        // reference's raw length, since the output alone cannot say how close it is).
        const float normalization = 0.5f;
        var random = new System.Random(0x7ACE3);
        var above = 0;
        var below = 0;
        var maxError = 0.0f;
        for (var attempts = 0; attempts < 20000 && (above < 150 || below < 150); attempts++)
        {
            var p = RandomPoint(random);
            var side = RandomSide(random);
            var offset = random.NextSingle();
            var reference = Evaluate(p, side, offset, normalization);
            var distance = reference.RawLength - (1.0 - normalization);
            if (Math.Abs(distance) < ThresholdBand || reference.SupportEdgeGap < 3.0 * GradientEps)
                continue;
            if (distance > 0.0 ? above >= 150 : below >= 150)
                continue;
            if (distance > 0.0) above++; else below++;

            var analytic = math.phacelle(p, side, offset, normalization);
            var numericCos = CentralDifference(q => math.phacelle(q, side, offset, normalization).cos.w, p, GradientEps);
            var numericSin = CentralDifference(q => math.phacelle(q, side, offset, normalization).sin.w, p, GradientEps);
            maxError = MathF.Max(maxError, MathF.Max(MaxComponentError(numericCos, analytic.cos), MaxComponentError(numericSin, analytic.sin)));
        }

        Assert.True(above >= 150 && below >= 150, $"{above} points above and {below} below the threshold");
        Assert.True(maxError < GradientTolerance, $"max gradient error against central differences {maxError}");
    }

    // ---- Stripes run along the flow ----

    [Fact]
    public void OnTheUnitSphereStripesRunAlongTheFlow()
    {
        // side = cross(p_hat, flow) * 2*pi is tangent to the sphere and perpendicular to the flow, so the
        // cosine should change across the stripes (along side) and hardly at all along them (along
        // flow). Asura's probe measured 0.212 for this ratio, against 0.202 for the planar upstream.
        const float radiusInCells = 10.0f;
        var random = new System.Random(0x7ACE4);
        var reference = math.normalize(new float3(0.3f, 0.5f, 0.8f));
        var alongFlow = 0.0;
        var acrossFlow = 0.0;
        var used = 0;
        while (used < 2000)
        {
            var hat = math.normalize(new float3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var tangent = reference - hat * math.dot(hat, reference);
            if (math.length(tangent) < 0.2f)
                continue;

            var flow = math.normalize(tangent);
            var sideDirection = math.cross(hat, flow);
            var phasor = math.phacelle(hat * radiusInCells, sideDirection * math.TAU, 0.25f, 0.5f);
            var gradient = new float3(phasor.cos.x, phasor.cos.y, phasor.cos.z);
            alongFlow += Math.Abs(math.dot(gradient, flow));
            acrossFlow += Math.Abs(math.dot(gradient, sideDirection));
            used++;
        }

        var ratio = alongFlow / acrossFlow;
        Assert.True(ratio <= 0.25, $"mean|d cos/d flow| / mean|d cos/d side| = {ratio}");
    }

    // ---- Continuity ----

    [Fact]
    public void ValueStepDifferenceShrinksTenfoldPerTenfoldStepIncludingAcrossCellPlanes()
    {
        // Pairs straddle an integer plane on each axis (where the 4x4x4 window moves by one cell and a
        // weight that did not vanish at the window edge would jump) and also start at random points.
        // Lipschitz continuity means the largest difference over a step scales with the step.
        var random = new System.Random(0x7ACE5);
        var steps = new[] { 1.0e-3f, 1.0e-4f, 1.0e-5f };
        var maxDifference = new float[steps.Length];
        for (var i = 0; i < 3000; i++)
        {
            var p = RandomPoint(random);
            var side = RandomSide(random);
            var offset = random.NextSingle();
            var axis = i % 3;
            if (i % 2 == 0)
                p = SetAxis(p, axis, MathF.Round(GetAxis(p, axis)));
            for (var k = 0; k < steps.Length; k++)
            {
                var low = math.phacelle(SetAxis(p, axis, GetAxis(p, axis) - steps[k] * 0.5f), side, offset, 0.5f);
                var high = math.phacelle(SetAxis(p, axis, GetAxis(p, axis) + steps[k] * 0.5f), side, offset, 0.5f);
                maxDifference[k] = MathF.Max(maxDifference[k], MathF.Max(MathF.Abs(low.cos.w - high.cos.w), MathF.Abs(low.sin.w - high.sin.w)));
            }
        }

        Assert.True(maxDifference[1] * 8.0f <= maxDifference[0], $"1e-3: {maxDifference[0]}, 1e-4: {maxDifference[1]}");
        Assert.True(maxDifference[2] * 8.0f <= maxDifference[1], $"1e-4: {maxDifference[1]}, 1e-5: {maxDifference[2]}");
    }

    private static float GetAxis(float3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

    private static float3 SetAxis(float3 v, int axis, float value) =>
        axis == 0 ? new float3(value, v.y, v.z) : axis == 1 ? new float3(v.x, value, v.z) : new float3(v.x, v.y, value);

    // ---- Exact pruning ----

    [Fact]
    public void PrunedOutputIsBitEqualToTheUnprunedSum()
    {
        var random = new System.Random(0x7ACE6);
        for (var i = 0; i < 20000; i++)
        {
            var p = RandomPoint(random, 50.0f);
            var side = RandomSide(random);
            var offset = random.NextSingle();
            var normalization = random.NextSingle();
            var pruned = math.phacelle(p, side, offset, normalization);
            var unpruned = math.phacelle(p, side, offset, normalization, false);

            AssertBitEqual(unpruned.cos, pruned.cos);
            AssertBitEqual(unpruned.sin, pruned.sin);
        }
    }

    // Points next to a cell whose jitter sits at the far corner of its cube: the cell's box bound is
    // just under the 2.25 cut (2.2451 to 2.25) while its real distance is still under the support
    // radius (d^2 < 2.24995), so its weight is small but nonzero and a prune that cuts even slightly
    // low (2.245, 2.24, 2.2) drops a contribution and changes output bits. Random points meet such
    // a cell about once in 10^4 evaluations, which is why the random sweep above let those cuts
    // through. Each was found once by an offline search (cells K in [-12, 12]^3 whose jitter on one
    // axis lies within 0.0008 of the cube face; p is the feature moved 1.4996 out along that axis,
    // so |p - feature| = 1.4996 and the bound is about 1.4988). The witness check below re-derives
    // that property from the hash, so a change to pcg3d fails loudly instead of degenerating.
    // Shared with the HLSL mirror test.
    internal static readonly float3[] PruneCutPoints =
    {
        new(1.77850008f, -2.16020727f, -0.9991166f),
        new(1.09346282f, -2.37882328f, -3.00082445f),
        new(-6.39144945f, -3.99953747f, -0.610023975f),
        new(-0.99940908f, 4.16302729f, 4.07277536f),
        new(3.99912024f, 7.21890545f, 1.09053993f),
        new(5.43959713f, 1.99937367f, -6.14929914f),
    };

    internal static readonly float3 PruneCutSide = new(2.1f, -3.7f, 1.3f);
    internal const float PruneCutOffset = 0.3f;
    internal const float PruneCutNormalization = 0.5f;

    // True when some cell of the 4x4x4 window has a box bound in [2.2451, 2.25) and a positive weight.
    internal static bool HasCellJustInsideTheCut(float3 p)
    {
        double fx = Math.Floor(p.x), fy = Math.Floor(p.y), fz = Math.Floor(p.z);
        double lx = p.x - fx, ly = p.y - fy, lz = p.z - fz;
        for (var dz = -1; dz <= 2; dz++)
        for (var dy = -1; dy <= 2; dy++)
        for (var dx = -1; dx <= 2; dx++)
        {
            var gx = Math.Max(0.0, Math.Abs(lx - dx) - 0.5);
            var gy = Math.Max(0.0, Math.Abs(ly - dy) - 0.5);
            var gz = Math.Max(0.0, Math.Abs(lz - dz) - 0.5);
            var bound = gx * gx + gy * gy + gz * gz;
            if (bound < 2.2451 || bound >= 2.25)
                continue;

            var hash = math.pcg3d(new int3((int)fx + dx, (int)fy + dy, (int)fz + dz));
            double vx = lx - dx - (math.cellular_unit(hash.x) - 0.5);
            double vy = ly - dy - (math.cellular_unit(hash.y) - 0.5);
            double vz = lz - dz - (math.cellular_unit(hash.z) - 0.5);
            if (Math.Exp(-2.0 * (vx * vx + vy * vy + vz * vz)) > 0.01111)
                return true;
        }

        return false;
    }

    [Fact]
    public void PrunedOutputIsBitEqualToTheUnprunedSumNextToCellsJustInsideTheCut()
    {
        foreach (var p in PruneCutPoints)
        {
            Assert.True(HasCellJustInsideTheCut(p), $"no cell just inside the cut at {p}: the fixture no longer exercises the prune");
            var pruned = math.phacelle(p, PruneCutSide, PruneCutOffset, PruneCutNormalization);
            var unpruned = math.phacelle(p, PruneCutSide, PruneCutOffset, PruneCutNormalization, false);

            AssertBitEqual(unpruned.cos, pruned.cos);
            AssertBitEqual(unpruned.sin, pruned.sin);
        }
    }

    private static void AssertBitEqual(float4 expected, float4 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.x), BitConverter.SingleToInt32Bits(actual.x));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.y), BitConverter.SingleToInt32Bits(actual.y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.z), BitConverter.SingleToInt32Bits(actual.z));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.w), BitConverter.SingleToInt32Bits(actual.w));
    }
}
