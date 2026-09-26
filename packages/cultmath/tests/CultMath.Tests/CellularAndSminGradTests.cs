using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class CellularAndSminGradTests
{
    // Central-difference tolerances for smin_grad and cellular (F5), sized separately: the two
    // functions have different curvature, so the same eps does not carry the same truncation
    // error, and an earlier shared 3e-4 tolerance was fragile against both (Soul, cut 2a-i: measured
    // max error smin 2.74e-4, cellular nearest 1.25e-4, cellular edge 1.88e-4 on a small sample —
    // and re-measured here at 5.3e-4, 2.0e-4, 2.6e-4 on a larger one — against that 3e-4 tolerance,
    // sometimes under 1.1x headroom, not the "~6e-5 noise" an earlier version of this comment
    // claimed). Each tolerance below is set from its own measured worst case with real headroom.

    // smin_grad's value is exactly quadratic in position within a smoothing band (math.cs comment on
    // smin_grad: h is affine in b.w - a.w, which is itself affine in position, so value(q) has zero
    // third derivative there): central differences have no truncation error from curvature, only
    // float32 rounding noise, so a larger eps directly dilutes that noise with no accuracy cost.
    // Measured max error at eps=0.01f over 10,000 samples: 4.37e-5 (up from eps=0.001f's 5.3e-4,
    // confirming the noise is eps-limited, not curvature-limited); this tolerance keeps about 5.7x
    // headroom over that. A uniform-scale mutant on the returned gradient (a 1.01x M9/M16-style
    // mutant applied here) still dies down to a 1.0005x scale (0.05%); it survives at 1.0002x.
    private const float SminGradientTolerance = 2.5e-4f;

    // cellular's F1/F2 are Euclidean distance fields: real curvature away from a feature point means
    // central differences DO carry O(eps^2) truncation error, so (unlike smin_grad) a larger eps
    // makes things worse, not better; a sweep at eps in {0.0002f, 0.0003f, 0.0005f, 0.0007f, 0.001f}
    // over 8,000 samples found the committed eps=0.0005f already close to the sweet spot. Measured
    // max error at that eps: nearest 2.04e-4, edge 2.57e-4; this tolerance keeps just over 5x
    // headroom over the larger of the two. M9 (grad1 * 1.01) and M16 (grad2 * 1.01) both still die
    // down to a 1.002x scale (0.2%); both survive at 1.001x.
    private const float CellularGradientTolerance = 1.3e-3f;

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
            // band around its own F1 = F2 set). The band has an absolute floor, not just a
            // fraction of k: eps grew to 0.01f below (SminGradientTolerance's comment), and a
            // fraction-of-k-only band could be narrower than the diff can shift by eps*|cf-cg|
            // (up to about 0.035 here) when k is small, letting p +/- eps step across the seam even
            // though p itself was clear of it.
            var diff = F(p) - G(p);
            if (Math.Abs(Math.Abs(diff) - k) < Math.Max(0.05f * k, 0.05f))
                continue;

            tested++;
            var analytic = math.smin_grad(new float4(cf, F(p)), new float4(cg, G(p)), k);

            // eps=0.01f, not 0.001f: smin is exactly quadratic within a band (no truncation error
            // from curvature; see SminGradientTolerance's comment), so the larger eps only dilutes
            // float32 rounding noise, at no accuracy cost, while still comfortably inside the band
            // exclusion above.
            const float eps = 0.01f;
            var numeric = new float3(
                (Smin(p + new float3(eps, 0.0f, 0.0f)) - Smin(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
                (Smin(p + new float3(0.0f, eps, 0.0f)) - Smin(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
                (Smin(p + new float3(0.0f, 0.0f, eps)) - Smin(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

            AssertWithinTolerance(numeric.x, analytic.x, SminGradientTolerance);
            AssertWithinTolerance(numeric.y, analytic.y, SminGradientTolerance);
            AssertWithinTolerance(numeric.z, analytic.z, SminGradientTolerance);
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

            // Both returned .w values, checked against the independent 7x7x7 brute-force oracle (F1,
            // F6), not a value the test derived some other way.
            var (f1, f2) = FindF1F2(p);
            Assert.Equal(f1, result.nearest.w, precision: 3);
            Assert.Equal(f2 - f1, result.edge.w, precision: 3);
        }
    }

    [Fact]
    public void NearestFeatureWinsEvenWhenItIsInTheLastVisitedNeighbourCell()
    {
        // math.cellular's own search order visits neighbour offsets with dz outermost and dx
        // innermost over a 5x5x5 block (F6), and it also prunes cells whose position-only lower
        // bound already exceeds the running F2. Offsets with any |component| = 2 are not provably
        // unreachable as F1: their minimum possible distance is 1 (e.g. (2, 0, 0), whose lower bound
        // per the exactness proof in math.cs is max(0, 2-1) = 1), well inside the query's own cell's
        // sqrt(3) ceiling, so a |component| = 2 offset winning F1 is rare rather than impossible
        // (Soul, cut 2a-i: measured 3 F1 wins in 2,000,000 samples). It is still rare enough that the
        // 3x3x3 corners remain the practically-reachable "last" cases for this test. (dx, dy, dz)
        // = (1, 1, 1) is the last of those the fixed iteration order visits, and if pruning or search
        // order were broken, this is the offset most likely to be skipped. Rather than hand-picking a
        // hash value, search random points for one whose true nearest feature (by independent brute
        // force over the same 5x5x5 block) happens to live in exactly that cell, at a comfortable
        // margin from the F1 = F2 seam, then check math.cellular agrees.
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
            var (f1, f2, f3) = FindF1F2F3(p);

            // Exclude a band around the F1 = F2 set: the identity of the nearest and second-nearest
            // feature swap there, a genuine kink in F1 and F2 alike, not something central differences
            // can approximate. Also exclude a band around F2 = F3: edge (F2 - F1) kinks there too,
            // since the second-nearest feature's identity swaps (Soul, cut 2a-i: an unfiltered sample
            // hit a 0.78 edge error this way). Also skip the exact feature point itself (F1 = 0),
            // covered separately.
            if (f2 - f1 < 0.05f || f1 < 0.01f || f3 - f2 < 0.05f)
                continue;

            tested++;
            var result = math.cellular(p);

            // eps=0.0005f: unlike smin_grad, cellular's F1/F2 are Euclidean distance fields with real
            // curvature, so central differences carry O(eps^2) truncation error and a larger eps is
            // worse, not better (see CellularGradientTolerance's comment for the measured sweep).
            const float eps = 0.0005f;

            // Differentiate cellular's own returned .w values (F1): calling cellular again at p +/-
            // eps and reading result.nearest.w / result.edge.w, not a duplicate FindF1F2 search. A
            // mutant that corrupts only the returned gradient, leaving the returned value alone,
            // would survive a check built on an independent value oracle; it cannot survive this one.
            float NearestValueAt(float3 q) => math.cellular(q).nearest.w;
            float EdgeValueAt(float3 q) => math.cellular(q).edge.w;

            AssertGradientMatchesCentralDifference(NearestValueAt, p, eps, result.nearest, CellularGradientTolerance);
            AssertGradientMatchesCentralDifference(EdgeValueAt, p, eps, result.edge, CellularGradientTolerance);
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

    [Fact]
    public void IdIsNotCorrelatedWithAnyJitterComponent()
    {
        // F4: id must come from hash bits the jitter never uses. A mutant that takes id from
        // hash.x (or hash.y) makes id literally equal to that jitter component, which this catches
        // as a correlation near 1.0, far past the 0.05 threshold. n = 20000 keeps sampling noise
        // (measured under 0.01 at this n) well clear of that threshold.
        const int n = 20000;
        var random = new System.Random(0xCE15);
        var ids = new double[n];
        var jitterX = new double[n];
        var jitterY = new double[n];
        var jitterZ = new double[n];

        for (var i = 0; i < n; i++)
        {
            var cell = new float3(random.Next(-2000, 2000), random.Next(-2000, 2000), random.Next(-2000, 2000));
            var hash = math.pcg3d(math.int3(cell));
            var jitter = new float3(Unit(hash.x), Unit(hash.y), Unit(hash.z));

            ids[i] = math.cellular(cell + jitter).id;
            jitterX[i] = jitter.x;
            jitterY[i] = jitter.y;
            jitterZ[i] = jitter.z;
        }

        Assert.True(Math.Abs(Correlation(ids, jitterX)) < 0.05, "id correlates with jitter.x");
        Assert.True(Math.Abs(Correlation(ids, jitterY)) < 0.05, "id correlates with jitter.y");
        Assert.True(Math.Abs(Correlation(ids, jitterZ)) < 0.05, "id correlates with jitter.z");
    }

    [Fact]
    public void JitterAndIdPassAChiSquareUniformityTest()
    {
        // F4/jitter uniformity: cellular hashes the winning cell's INTEGER coordinate (design.md,
        // "Integer hashing"), never that coordinate's float32 bit pattern. Hashing the bit pattern of
        // an integer-valued float shares more structure between adjacent cells (same exponent, a
        // mantissa one increment apart) than the integers themselves do going into pcg3d's own
        // mixing, and that structure survived into the jitter: Soul measured jitter.x's marginal
        // chi-square statistic at 203.8 over 1,000,000 cells this way (15 degrees of freedom;
        // critical value 37.7 at alpha = 0.001), soundly rejecting uniformity. 16 equal-width bins
        // over [0, 1) give those same 15 degrees of freedom for jitter.x/y/z and id alike.
        const int n = 200_000;
        const int bins = 16;
        const double criticalValue = 37.7; // chi-square, 15 df, alpha = 0.001

        var jitterXCounts = new int[bins];
        var jitterYCounts = new int[bins];
        var jitterZCounts = new int[bins];
        var idCounts = new int[bins];
        var random = new System.Random(0xCE19);

        for (var i = 0; i < n; i++)
        {
            var cell = new float3(random.Next(-100000, 100000), random.Next(-100000, 100000), random.Next(-100000, 100000));
            var hash = math.pcg3d(math.int3(cell));
            jitterXCounts[Bin(Unit(hash.x), bins)]++;
            jitterYCounts[Bin(Unit(hash.y), bins)]++;
            jitterZCounts[Bin(Unit(hash.z), bins)]++;

            var idHash = math.pcg4d(new int4(math.int3(cell), 0));
            idCounts[Bin(Unit(idHash.w), bins)]++;
        }

        var expected = (double)n / bins;
        AssertUniform(jitterXCounts, expected, criticalValue, "jitter.x");
        AssertUniform(jitterYCounts, expected, criticalValue, "jitter.y");
        AssertUniform(jitterZCounts, expected, criticalValue, "jitter.z");
        AssertUniform(idCounts, expected, criticalValue, "id");
    }

    private static int Bin(float value, int binCount) => Math.Clamp((int)(value * binCount), 0, binCount - 1);

    private static void AssertUniform(int[] counts, double expected, double criticalValue, string name)
    {
        var chiSquare = 0.0;
        foreach (var count in counts)
        {
            var delta = count - expected;
            chiSquare += delta * delta / expected;
        }

        Assert.True(chiSquare < criticalValue, $"{name} chi-square {chiSquare:G6} exceeds critical value {criticalValue} ({counts.Length - 1} df)");
    }

    [Fact]
    public void NoNaNAtIntegerPointsNear2To24()
    {
        // F3: past |p| of about 2^23, distinct cells' jittered features can round to the same
        // float32 value, making F2 = 0 reachable. Soul measured 4096 of 4096 integer points at 2^24
        // producing NaN before the guard; this pins the fix over the same sample.
        const float scale = 16777216.0f; // 2^24
        for (var ix = 0; ix < 16; ix++)
        for (var iy = 0; iy < 16; iy++)
        for (var iz = 0; iz < 16; iz++)
        {
            var p = new float3(scale + ix, scale + iy, scale + iz);
            var result = math.cellular(p);

            Assert.True(
                float.IsFinite(result.nearest.x) && float.IsFinite(result.nearest.y) &&
                float.IsFinite(result.nearest.z) && float.IsFinite(result.nearest.w),
                $"nearest not finite at {p.x},{p.y},{p.z}: {result.nearest.x},{result.nearest.y},{result.nearest.z},{result.nearest.w}");
            Assert.True(
                float.IsFinite(result.edge.x) && float.IsFinite(result.edge.y) &&
                float.IsFinite(result.edge.z) && float.IsFinite(result.edge.w),
                $"edge not finite at {p.x},{p.y},{p.z}: {result.edge.x},{result.edge.y},{result.edge.z},{result.edge.w}");
            Assert.True(float.IsFinite(result.id), $"id not finite at {p.x},{p.y},{p.z}");
        }
    }

    [Fact]
    public void CellularUnitMapsAllOnesBitsBelowOne()
    {
        // F7: cellular_unit(0xFFFFFFFF) must land in [0, 1), not round up to exactly 1.0f.
        var mapped = math.cellular_unit(-1); // -1's bit pattern is 0xFFFFFFFF.
        Assert.True(mapped >= 0.0f && mapped < 1.0f, $"cellular_unit(0xFFFFFFFF) = {mapped}");
    }

    [Fact]
    public void ProductionSearchMatchesTheBruteForceOracleExactly()
    {
        // F6, part 2: the production 5x5x5 search must equal the independent 7x7x7 oracle exactly
        // (to the oracle's own float32 precision), over a large seeded sweep in the domain Soul
        // measured ([-50, 50]^3, where 3x3x3 missed 9 of 400,000 points).
        var random = new System.Random(0xCE16);
        for (var i = 0; i < 20000; i++)
        {
            var p = new float3(random.NextSingle() * 100.0f - 50.0f, random.NextSingle() * 100.0f - 50.0f, random.NextSingle() * 100.0f - 50.0f);
            var result = math.cellular(p);
            var (f1, f2) = FindF1F2(p);

            Assert.Equal(f1, result.nearest.w, precision: 4);
            Assert.Equal(f2 - f1, result.edge.w, precision: 4);
        }
    }

    [Fact]
    public void SearchReachesEveryAxisAlignedRadiusTwoSlice()
    {
        // F6, part 2 (Soul finding): a single-slice loop-bound mutant on one axis (e.g. dz < 2
        // instead of dz <= 2, silently dropping the +2 layer on that axis alone) still passed the
        // random 20,000-point sweeps above. That is not those sweeps being weak; it is the
        // exactness proof's own margin (F1, F2 always < sqrt(3), a radius-2 cell's lower bound is
        // max(0, 2-1) = 1, not "exactly 2" as an earlier version of this comment claimed) making a
        // genuine win from any single |offset component| = 2 cell rare over uniformly random points
        // (Soul, cut 2a-i: measured about 1 hit in 28,000 samples, not the "zero in 3,000,000" an
        // earlier version of this comment claimed).
        // Hunting for one by choosing candidate query POINTS is the wrong end of the search; this
        // instead scans candidate integer CELLS and, for each, builds the query point most likely to
        // realize that cell's neighbour as the winner: the closest point of the query cell's own unit
        // cube to the target neighbour's actual jittered feature. That construction finds a real
        // example for every one of the six axis-aligned radius-2 offsets within a couple thousand
        // candidate cells, deterministically (pcg3d has no randomness to get lucky or unlucky with).
        foreach (var offset in AxisAlignedRadiusTwoOffsets)
        {
            var p = FindPointRealizingOffset(offset);
            var (f1, f2) = FindF1F2(p); // independent 7x7x7 oracle
            var result = math.cellular(p);
            Assert.Equal(f1, result.nearest.w, precision: 3);
            Assert.Equal(f2 - f1, result.edge.w, precision: 3);
        }
    }

    // The six axis-aligned radius-2 offsets (F6, part 2): shared by
    // SearchReachesEveryAxisAlignedRadiusTwoSlice and, via FindPointRealizingOffset, by
    // HlslSourceCompatibilityTests' mirror-comparison inputs, so both pin the same construction.
    internal static readonly float3[] AxisAlignedRadiusTwoOffsets =
    {
        new(2.0f, 0.0f, 0.0f), new(-2.0f, 0.0f, 0.0f),
        new(0.0f, 2.0f, 0.0f), new(0.0f, -2.0f, 0.0f),
        new(0.0f, 0.0f, 2.0f), new(0.0f, 0.0f, -2.0f),
    };

    // Hunting for a query point where a radius-2 offset wins F1 or F2 by choosing candidate query
    // POINTS is the wrong end of the search (Soul, cut 2a-i: about 1 hit in 28,000 uniformly random
    // points); this instead scans candidate integer CELLS and, for each, builds the query point most
    // likely to realize that cell's neighbour as the winner: the closest point of the query cell's
    // own unit cube to the target neighbour's actual jittered feature. That construction finds a
    // real example for every one of the six axis-aligned radius-2 offsets within a couple thousand
    // candidate cells, deterministically (pcg3d has no randomness to get lucky or unlucky with).
    internal static float3 FindPointRealizingOffset(float3 offset)
    {
        for (var qz = -80; qz <= 80; qz++)
        for (var qy = -80; qy <= 80; qy++)
        for (var qx = -80; qx <= 80; qx++)
        {
            var q = new float3(qx, qy, qz);
            var targetFeature = FeaturePoint(q + offset);
            var p = new float3(
                Math.Clamp(targetFeature.x, q.x, q.x + 0.999f),
                Math.Clamp(targetFeature.y, q.y, q.y + 0.999f),
                Math.Clamp(targetFeature.z, q.z, q.z + 0.999f));

            var (f1, f2) = FindF1F2(p); // independent 7x7x7 oracle
            var d = math.distance(p, targetFeature);
            if (MathF.Abs(d - f1) > 1.0e-4f && MathF.Abs(d - f2) > 1.0e-4f)
                continue; // this cell didn't realize the target offset as F1 or F2; try another.

            return p;
        }

        throw new InvalidOperationException($"could not construct a query point where offset {offset.x},{offset.y},{offset.z} ever wins F1 or F2");
    }

    [Fact]
    public void PrunedSearchIsBitIdenticalToTheUnprunedFiveCubedSearchBelowTwoToTheTwentyFifth()
    {
        // F6, part 2: the lower-bound prune inside math.cellular's loop must never change the
        // result. Compares against an independent, unpruned 5x5x5 reference that visits every cell
        // in the same fixed order, over a large seeded sweep in [-50, 50]^3.
        //
        // This bit-identity has a domain: it holds for |p| < 2^25 (design.md, "Precision domain").
        // Above that, cell + offset starts to round in float32, so the pruned loop's recomputed
        // neighbour and the unpruned loop's neighbour at the same nominal (dx, dy, dz) can land on
        // different actual cells, and the two searches diverge (Soul, cut 2a-i: 180 of 1,100,000
        // points differ at |p| ~= 3.4e7). [-50, 50]^3 is comfortably inside the proven domain.
        var random = new System.Random(0xCE18);
        for (var i = 0; i < 20000; i++)
        {
            var p = new float3(random.NextSingle() * 100.0f - 50.0f, random.NextSingle() * 100.0f - 50.0f, random.NextSingle() * 100.0f - 50.0f);
            var pruned = math.cellular(p);
            var unpruned = UnprunedCellular5x5x5(p);

            Assert.Equal(unpruned.nearest.x, pruned.nearest.x);
            Assert.Equal(unpruned.nearest.y, pruned.nearest.y);
            Assert.Equal(unpruned.nearest.z, pruned.nearest.z);
            Assert.Equal(unpruned.nearest.w, pruned.nearest.w);
            Assert.Equal(unpruned.edge.x, pruned.edge.x);
            Assert.Equal(unpruned.edge.y, pruned.edge.y);
            Assert.Equal(unpruned.edge.z, pruned.edge.z);
            Assert.Equal(unpruned.edge.w, pruned.edge.w);
            Assert.Equal(unpruned.id, pruned.id);
        }
    }

    // Independent of math.cellular's own pruning: visits every cell of the 5x5x5 block, in the same
    // fixed order, unconditionally.
    private static CultCellular UnprunedCellular5x5x5(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var f1 = 1.0e30f;
        var f2 = 1.0e30f;
        var c1 = float3.zero;
        var c2 = float3.zero;
        var cellId = float3.zero;

        for (var dz = -2; dz <= 2; dz++)
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var neighbor = cell + new float3(dx, dy, dz);
            var feature = FeaturePoint(neighbor);
            var d = math.distance(p, feature);
            if (d < f1) { f2 = f1; c2 = c1; f1 = d; c1 = feature; cellId = neighbor; }
            else if (d < f2) { f2 = d; c2 = feature; }
        }

        var grad1 = f1 > 0.0f ? (p - c1) / f1 : float3.zero;
        var grad2 = f2 > 0.0f ? (p - c2) / f2 : float3.zero;
        var idHash = math.pcg4d(new int4(math.int3(cellId), 0));
        return new CultCellular(new float4(grad1, f1), new float4(grad2 - grad1, f2 - f1), Unit(idHash.w));
    }

    private static double Correlation(double[] a, double[] b)
    {
        var n = a.Length;
        var meanA = 0.0;
        var meanB = 0.0;
        for (var i = 0; i < n; i++) { meanA += a[i]; meanB += b[i]; }
        meanA /= n;
        meanB /= n;

        var cov = 0.0;
        var varA = 0.0;
        var varB = 0.0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        return cov / Math.Sqrt(varA * varB);
    }

    // ---- shared fixtures: an independent brute-force Worley search over the same public pcg3d
    // hash cellular itself is documented to use (design.md, "Integer hashing"), so these tests do
    // not anchor to cellular's own control flow or iteration order. FindF1F2 searches 7x7x7 (radius
    // 3), strictly wider than production's own proven-sufficient 5x5x5 (F6), so it is a true
    // independent oracle rather than a second copy of the algorithm under test. ----

    internal static float3 FeaturePoint(float3 cell)
    {
        var hash = math.pcg3d(math.int3(cell));
        var jitter = new float3(Unit(hash.x), Unit(hash.y), Unit(hash.z));
        return cell + jitter;
    }

    // Mirrors math.cellular_unit's top-24-bit [0, 1) mapping (F7) exactly, so this fixture never
    // drifts from production's own jitter/id mapping.
    private static float Unit(int bits) => ((uint)bits >> 8) * (1.0f / 16777216.0f);

    private static (float f1, float f2) FindF1F2(float3 p)
    {
        var (f1, f2, _) = FindF1F2F3(p);
        return (f1, f2);
    }

    // Same independent 7x7x7 oracle as FindF1F2, plus the third-nearest distance. Central-difference
    // tests need this: excluding only the F1=F2 gap is not enough, because a small F2=F3 gap is also
    // a kink (the identity of the second-nearest feature swaps there), and hitting one produces a
    // large, curvature-driven finite-difference error unrelated to eps or float noise (Soul, cut
    // 2a-i: an unfiltered large sample hit an edge error of 0.78 this way, versus ~2e-4 once F2=F3
    // is also excluded).
    private static (float f1, float f2, float f3) FindF1F2F3(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var f1 = float.PositiveInfinity;
        var f2 = float.PositiveInfinity;
        var f3 = float.PositiveInfinity;
        for (var dz = -3; dz <= 3; dz++)
        for (var dy = -3; dy <= 3; dy++)
        for (var dx = -3; dx <= 3; dx++)
        {
            var d = math.distance(p, FeaturePoint(cell + new float3(dx, dy, dz)));
            if (d < f1) { f3 = f2; f2 = f1; f1 = d; }
            else if (d < f2) { f3 = f2; f2 = d; }
            else if (d < f3) { f3 = d; }
        }

        return (f1, f2, f3);
    }

    private static (float3 offset, float f1, float f2) FindNearestOffset(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var f1 = float.PositiveInfinity;
        var f2 = float.PositiveInfinity;
        var bestOffset = float3.zero;
        for (var dz = -2; dz <= 2; dz++)
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
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

    private static void AssertGradientMatchesCentralDifference(Func<float3, float> scalarField, float3 p, float eps, float4 valueAndGradient, float tolerance)
    {
        var numeric = new float3(
            (scalarField(p + new float3(eps, 0.0f, 0.0f)) - scalarField(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
            (scalarField(p + new float3(0.0f, eps, 0.0f)) - scalarField(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
            (scalarField(p + new float3(0.0f, 0.0f, eps)) - scalarField(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

        AssertWithinTolerance(numeric.x, valueAndGradient.x, tolerance);
        AssertWithinTolerance(numeric.y, valueAndGradient.y, tolerance);
        AssertWithinTolerance(numeric.z, valueAndGradient.z, tolerance);
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
