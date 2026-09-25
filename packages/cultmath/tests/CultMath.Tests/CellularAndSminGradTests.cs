using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class CellularAndSminGradTests
{
    // ---- smin_grad ----

    [Fact]
    public void SminValueNeverExceedsTheOrdinaryMinimum()
    {
        var random = new System.Random(0xACE1);
        for (var i = 0; i < 256; i++)
        {
            var a = new float4(RandomUnit(random), RandomScalar(random));
            var b = new float4(RandomUnit(random), RandomScalar(random));
            var k = 0.05f + random.NextSingle() * 5.0f;

            var result = math.smin_grad(a, b, k);

            Assert.True(result.w <= math.min(a.w, b.w) + 1.0e-4f, $"a.w={a.w} b.w={b.w} k={k} smin={result.w}");
        }
    }

    [Fact]
    public void SminMatchesTheHardMinimumAndItsGradientOutsideTheBand()
    {
        var random = new System.Random(0xACE2);
        for (var i = 0; i < 64; i++)
        {
            var gradA = RandomUnit(random);
            var gradB = RandomUnit(random);
            var k = 0.1f + random.NextSingle() * 2.0f;

            // Push b comfortably above a, past the smoothing band, so a is the ordinary minimum.
            var aw = random.NextSingle() * 10.0f - 5.0f;
            var bw = aw + k * (1.5f + random.NextSingle());
            var a = new float4(gradA, aw);
            var b = new float4(gradB, bw);

            var lower = math.smin_grad(a, b, k);
            Assert.Equal(aw, lower.w, precision: 4);
            Assert.Equal(gradA.x, lower.x, precision: 4);
            Assert.Equal(gradA.y, lower.y, precision: 4);
            Assert.Equal(gradA.z, lower.z, precision: 4);

            // Argument order must not matter: b below a is symmetric.
            var higher = math.smin_grad(b, a, k);
            Assert.Equal(aw, higher.w, precision: 4);
            Assert.Equal(gradA.x, higher.x, precision: 4);
            Assert.Equal(gradA.y, higher.y, precision: 4);
            Assert.Equal(gradA.z, higher.z, precision: 4);
        }
    }

    [Fact]
    public void SminGradientMatchesCentralDifferencesOfItsOwnValue()
    {
        var random = new System.Random(0xACE3);
        var tested = 0;
        while (tested < 200)
        {
            var cf = RandomUnit(random);
            var cg = RandomUnit(random);
            var offF = random.NextSingle() * 4.0f - 2.0f;
            var offG = random.NextSingle() * 4.0f - 2.0f;
            var k = 0.2f + random.NextSingle() * 2.0f;
            var p = new float3(random.NextSingle() * 4.0f - 2.0f, random.NextSingle() * 4.0f - 2.0f, random.NextSingle() * 4.0f - 2.0f);

            float F(float3 q) => math.dot(q, cf) + offF;
            float G(float3 q) => math.dot(q, cg) + offG;
            float Smin(float3 q) => math.smin_grad(new float4(cf, F(q)), new float4(cg, G(q)), k).w;

            // Stay clear of the |a.w - b.w| = k seam: central differences straddling a kink would
            // disagree with either side's analytic gradient (the same reason cellular excludes a
            // band around its own F1 = F2 set).
            var diff = F(p) - G(p);
            if (Math.Abs(Math.Abs(diff) - k) < 0.05f * k)
                continue;

            tested++;
            var analytic = math.smin_grad(new float4(cf, F(p)), new float4(cg, G(p)), k);
            const float eps = 0.001f;
            var numeric = new float3(
                (Smin(p + new float3(eps, 0.0f, 0.0f)) - Smin(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
                (Smin(p + new float3(0.0f, eps, 0.0f)) - Smin(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
                (Smin(p + new float3(0.0f, 0.0f, eps)) - Smin(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

            AssertWithinTolerance(numeric.x, analytic.x, 0.02f);
            AssertWithinTolerance(numeric.y, analytic.y, 0.02f);
            AssertWithinTolerance(numeric.z, analytic.z, 0.02f);
        }
    }

    [Fact]
    public void SminGradientIsNotAConstantBlendOrTheSwappedFactor()
    {
        // Falsifies two named mutants directly: blending the gradient with a constant 0.5 instead of
        // h, or with (1 - h) instead of h. Here a is comfortably the minimum (h saturates to 1 in the
        // correct formula), so the correct gradient is exactly a's, unmixed. A constant-0.5 blend or a
        // swapped (1 - h) blend both pull in b's gradient, which differs from a's here.
        var a = new float4(1.0f, 0.0f, 0.0f, -3.0f);
        var b = new float4(0.0f, 1.0f, 0.0f, 5.0f);

        var result = math.smin_grad(a, b, 0.25f);

        Assert.Equal(1.0f, result.x, precision: 5);
        Assert.Equal(0.0f, result.y, precision: 5);
        Assert.Equal(0.0f, result.z, precision: 5);
        Assert.Equal(-3.0f, result.w, precision: 5);
    }

    // ---- cellular ----

    [Fact]
    public void F1NeverExceedsF2()
    {
        var random = new System.Random(0xCE11);
        for (var i = 0; i < 256; i++)
        {
            var p = new float3(random.NextSingle() * 20.0f - 10.0f, random.NextSingle() * 20.0f - 10.0f, random.NextSingle() * 20.0f - 10.0f);
            var result = math.cellular(p);
            Assert.True(result.edge.w >= -1.0e-4f, $"F2 - F1 = {result.edge.w} at {p.x},{p.y},{p.z}");
            var (f1, _) = FindF1F2(p);
            Assert.Equal(f1, result.nearest.w, precision: 3);
        }
    }

    [Fact]
    public void NearestFeatureWinsEvenWhenItIsInTheLastVisitedNeighbourCell()
    {
        // math.cellular's own search order visits neighbour offsets with dz outermost and dx
        // innermost, so (dx, dy, dz) = (1, 1, 1) relative to floor(p) is the very last candidate it
        // looks at. A "first found within some radius" mutant, or one that stops early, would miss
        // it. Rather than hand-picking a hash value, search random points for one whose true nearest
        // feature (by independent brute force) happens to live in exactly that last-visited cell, at
        // a comfortable margin from the F1 = F2 seam, then check math.cellular agrees.
        var random = new System.Random(0xCE14);
        var lastOffset = new float3(1.0f, 1.0f, 1.0f);
        for (var attempt = 0; attempt < 5000; attempt++)
        {
            var p = new float3(random.NextSingle() * 8.0f - 4.0f, random.NextSingle() * 8.0f - 4.0f, random.NextSingle() * 8.0f - 4.0f);
            var (offset, f1, f2) = FindNearestOffset(p);
            if (!offset.Equals(lastOffset) || f1 < 0.05f || f2 - f1 < 0.05f)
                continue;

            var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
            var feature = FeaturePoint(cell + lastOffset);
            var expectedGradient = (p - feature) / f1;

            var result = math.cellular(p);
            Assert.Equal(f1, result.nearest.w, precision: 4);
            Assert.Equal(expectedGradient.x, result.nearest.x, precision: 4);
            Assert.Equal(expectedGradient.y, result.nearest.y, precision: 4);
            Assert.Equal(expectedGradient.z, result.nearest.z, precision: 4);
            return;
        }

        Assert.Fail("did not find a random point whose nearest feature lives in the last-visited neighbour cell");
    }

    [Fact]
    public void NearestGradientIsFiniteZeroAtAFeaturePointInsteadOfNaN()
    {
        var feature = FeaturePoint(new float3(4.0f, -2.0f, 9.0f));
        var result = math.cellular(feature);

        Assert.Equal(0.0f, result.nearest.w, precision: 4);
        Assert.True(float.IsFinite(result.nearest.x) && float.IsFinite(result.nearest.y) && float.IsFinite(result.nearest.z));
        Assert.Equal(0.0f, result.nearest.x, precision: 5);
        Assert.Equal(0.0f, result.nearest.y, precision: 5);
        Assert.Equal(0.0f, result.nearest.z, precision: 5);
    }

    [Fact]
    public void GradientMatchesCentralDifferencesAwayFromTheF1EqualsF2Seam()
    {
        var random = new System.Random(0xCE12);
        var tested = 0;
        var attempts = 0;
        while (tested < 120 && attempts < 20000)
        {
            attempts++;
            var p = new float3(random.NextSingle() * 6.0f - 3.0f, random.NextSingle() * 6.0f - 3.0f, random.NextSingle() * 6.0f - 3.0f);
            var (f1, f2) = FindF1F2(p);

            // Exclude a band around the F1 = F2 set: the identity of the nearest and second-nearest
            // feature swap there, a genuine kink in F1 and F2 alike, not something central differences
            // can approximate. Also skip the exact feature point itself (F1 = 0), covered separately.
            if (f2 - f1 < 0.05f || f1 < 0.01f)
                continue;

            tested++;
            var result = math.cellular(p);
            const float eps = 0.0005f;
            float F1At(float3 q) => FindF1F2(q).f1;

            AssertGradientMatchesCentralDifference(F1At, p, eps, result.nearest);
            var edgeValueFn = new Func<float3, float>(q => { var (a, b) = FindF1F2(q); return b - a; });
            AssertGradientMatchesCentralDifference(edgeValueFn, p, eps, result.edge);
        }

        Assert.True(tested >= 100, $"only {tested} of {attempts} random points landed away from the F1=F2 band");
    }

    [Fact]
    public void IdIsConstantAcrossAFeaturesWholeRegionAndVariesBetweenCells()
    {
        var random = new System.Random(0xCE13);
        var baseCell = new float3(2.0f, 2.0f, 2.0f);
        var feature = FeaturePoint(baseCell);
        var idAtFeature = math.cellular(feature).id;

        for (var i = 0; i < 32; i++)
        {
            // Small offsets that stay inside the same Voronoi region (well short of any neighbour).
            var jitter = new float3(random.NextSingle() * 0.2f - 0.1f, random.NextSingle() * 0.2f - 0.1f, random.NextSingle() * 0.2f - 0.1f);
            var nearby = feature + jitter;
            if (!FindNearestCell(nearby).Equals(baseCell))
                continue;

            Assert.Equal(idAtFeature, math.cellular(nearby).id, precision: 6);
        }

        var otherCellId = math.cellular(FeaturePoint(baseCell + new float3(5.0f, 0.0f, 0.0f))).id;
        Assert.NotEqual(idAtFeature, otherCellId);
    }

    // ---- shared fixtures: an independent brute-force Worley search over the same public pcg3d
    // hash cellular itself is documented to use (design.md, "Integer hashing"), so these tests do
    // not anchor to cellular's own control flow or iteration order. ----

    private static float3 FeaturePoint(float3 cell)
    {
        var hash = math.pcg3d(cell);
        var jitter = new float3(Unit(hash.x), Unit(hash.y), Unit(hash.z));
        return cell + jitter;
    }

    private static float Unit(int bits) => (uint)bits * (1.0f / 4294967296.0f);

    private static (float f1, float f2) FindF1F2(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var f1 = float.PositiveInfinity;
        var f2 = float.PositiveInfinity;
        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var d = math.distance(p, FeaturePoint(cell + new float3(dx, dy, dz)));
            if (d < f1) { f2 = f1; f1 = d; }
            else if (d < f2) { f2 = d; }
        }

        return (f1, f2);
    }

    private static (float3 offset, float f1, float f2) FindNearestOffset(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var f1 = float.PositiveInfinity;
        var f2 = float.PositiveInfinity;
        var bestOffset = float3.zero;
        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var offset = new float3(dx, dy, dz);
            var d = math.distance(p, FeaturePoint(cell + offset));
            if (d < f1) { f2 = f1; f1 = d; bestOffset = offset; }
            else if (d < f2) { f2 = d; }
        }

        return (bestOffset, f1, f2);
    }

    private static float3 FindNearestCell(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var best = float.PositiveInfinity;
        var bestCell = cell;
        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var neighbour = cell + new float3(dx, dy, dz);
            var d = math.distance(p, FeaturePoint(neighbour));
            if (d < best) { best = d; bestCell = neighbour; }
        }

        return bestCell;
    }

    private static void AssertGradientMatchesCentralDifference(Func<float3, float> scalarField, float3 p, float eps, float4 valueAndGradient)
    {
        var numeric = new float3(
            (scalarField(p + new float3(eps, 0.0f, 0.0f)) - scalarField(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
            (scalarField(p + new float3(0.0f, eps, 0.0f)) - scalarField(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
            (scalarField(p + new float3(0.0f, 0.0f, eps)) - scalarField(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

        AssertWithinTolerance(numeric.x, valueAndGradient.x, 0.02f);
        AssertWithinTolerance(numeric.y, valueAndGradient.y, 0.02f);
        AssertWithinTolerance(numeric.z, valueAndGradient.z, 0.02f);
    }

    // A fixed decimal-place Assert.Equal snaps two numbers that agree to within a few times 1e-5 to
    // different rounded digits whenever their true difference straddles a rounding boundary (seen:
    // 0.824987829 vs 0.825006127, agreeing to 2e-5, rounding to 0.82 and 0.83). An absolute-tolerance
    // check states the actual claim (central differences agree with the analytic gradient within the
    // finite-difference step's own truncation error) without that artifact.
    private static void AssertWithinTolerance(float expected, float actual, float tolerance) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected}, actual {actual}, tolerance {tolerance}");

    private static float3 RandomUnit(System.Random random) =>
        new(random.NextSingle() * 2.0f - 1.0f, random.NextSingle() * 2.0f - 1.0f, random.NextSingle() * 2.0f - 1.0f);

    private static float RandomScalar(System.Random random) => random.NextSingle() * 20.0f - 10.0f;
}
