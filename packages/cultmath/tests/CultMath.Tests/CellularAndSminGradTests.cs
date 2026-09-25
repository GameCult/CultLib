using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class CellularAndSminGradTests
{
    // Shared central-difference tolerance for smin_grad and cellular (F5). Soul measured float noise
    // at the committed eps values (0.001f here, 0.0005f in cellular) at about 6e-5; this leaves
    // roughly 5x headroom for legitimate finite-difference truncation while still killing a
    // 1%-scale gradient mutant (about 1e-2 error against these fields).
    private const float GradientTolerance = 3.0e-4f;

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

            // Measured float noise at this eps is about 6e-5 (Soul, cut 2a-i); 3e-4 leaves roughly
            // 5x headroom while still killing a 1%-scale gradient mutant (about 1e-2 error here).
            AssertWithinTolerance(numeric.x, analytic.x, GradientTolerance);
            AssertWithinTolerance(numeric.y, analytic.y, GradientTolerance);
            AssertWithinTolerance(numeric.z, analytic.z, GradientTolerance);
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
        // bound already exceeds the running F2. Offsets with any |component| = 2 are provably never
        // the true nearest (their minimum possible distance, sqrt(3), sits right at the maximum
        // possible distance of the query's own cell, per the exactness proof in math.cs, so it takes
        // a measure-zero coincidence for one to win — confirmed empirically at 0 wins in 3,000,000
        // samples), which only leaves the 3x3x3 corners as ever-reachable "last" cases. (dx, dy, dz)
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
            var (f1, f2) = FindF1F2(p);

            // Exclude a band around the F1 = F2 set: the identity of the nearest and second-nearest
            // feature swap there, a genuine kink in F1 and F2 alike, not something central differences
            // can approximate. Also skip the exact feature point itself (F1 = 0), covered separately.
            if (f2 - f1 < 0.05f || f1 < 0.01f)
                continue;

            tested++;
            var result = math.cellular(p);
            const float eps = 0.0005f;

            // Differentiate cellular's own returned .w values (F1): calling cellular again at p +/-
            // eps and reading result.nearest.w / result.edge.w, not a duplicate FindF1F2 search. A
            // mutant that corrupts only the returned gradient, leaving the returned value alone,
            // would survive a check built on an independent value oracle; it cannot survive this one.
            float NearestValueAt(float3 q) => math.cellular(q).nearest.w;
            float EdgeValueAt(float3 q) => math.cellular(q).edge.w;

            AssertGradientMatchesCentralDifference(NearestValueAt, p, eps, result.nearest);
            AssertGradientMatchesCentralDifference(EdgeValueAt, p, eps, result.edge);
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
            var hash = math.pcg3d(cell);
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
    public void PrunedSearchIsBitIdenticalToTheUnprunedFiveCubedSearch()
    {
        // F6, part 2: the lower-bound prune inside math.cellular's loop must never change the
        // result. Compares against an independent, unpruned 5x5x5 reference that visits every cell
        // in the same fixed order, over a large seeded sweep.
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
        var idHash = math.pcg4d(new float4(cellId, 0.0f));
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

    private static float3 FeaturePoint(float3 cell)
    {
        var hash = math.pcg3d(cell);
        var jitter = new float3(Unit(hash.x), Unit(hash.y), Unit(hash.z));
        return cell + jitter;
    }

    // Mirrors math.cellular_unit's top-24-bit [0, 1) mapping (F7) exactly, so this fixture never
    // drifts from production's own jitter/id mapping.
    private static float Unit(int bits) => ((uint)bits >> 8) * (1.0f / 16777216.0f);

    private static (float f1, float f2) FindF1F2(float3 p)
    {
        var cell = new float3(MathF.Floor(p.x), MathF.Floor(p.y), MathF.Floor(p.z));
        var f1 = float.PositiveInfinity;
        var f2 = float.PositiveInfinity;
        for (var dz = -3; dz <= 3; dz++)
        for (var dy = -3; dy <= 3; dy++)
        for (var dx = -3; dx <= 3; dx++)
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

    private static void AssertGradientMatchesCentralDifference(Func<float3, float> scalarField, float3 p, float eps, float4 valueAndGradient)
    {
        var numeric = new float3(
            (scalarField(p + new float3(eps, 0.0f, 0.0f)) - scalarField(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
            (scalarField(p + new float3(0.0f, eps, 0.0f)) - scalarField(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
            (scalarField(p + new float3(0.0f, 0.0f, eps)) - scalarField(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

        AssertWithinTolerance(numeric.x, valueAndGradient.x, GradientTolerance);
        AssertWithinTolerance(numeric.y, valueAndGradient.y, GradientTolerance);
        AssertWithinTolerance(numeric.z, valueAndGradient.z, GradientTolerance);
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
