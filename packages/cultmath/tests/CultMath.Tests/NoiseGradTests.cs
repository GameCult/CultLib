using System;
using CultMath;
using Xunit;

namespace CultMath.Tests;

public sealed class NoiseGradTests
{
    // ---- Invariant-8 property harness (shared by snoise_grad, fbm_grad, ridged_grad) ----
    //
    // Each test compares a function's analytic gradient with central differences of its own .w at
    // random points in [-3, 3]^3. eps trades curvature error (eps too large) against float32
    // rounding noise (eps too small). snoise is continuous across simplex-cell boundaries (the
    // continuity test below), so no point needs excluding for a seam; ridged_grad excludes a band
    // around its own deliberate crease. Each tolerance is at least 5x the largest error the harness
    // itself produces at its seed.
    private const float PropertyEps = 0.0001f;
    private const float SnoiseGradTolerance = 0.03f;
    private const float FbmGradTolerance = 0.05f;
    private const float RidgedGradTolerance = 0.05f;

    [Fact]
    public void SnoiseGradGradientMatchesCentralDifferencesOfItsOwnValue()
    {
        var random = new System.Random(0xA11CE);
        var tested = 0;
        var attempts = 0;
        while (tested < 200 && attempts < 20000)
        {
            attempts++;
            var p = RandomPoint(random);
            var g = math.snoise_grad(p);
            float F(float3 q) => math.snoise_grad(q).w;
            var fine = CentralDifference(F, p, PropertyEps);

            tested++;
            AssertGradientWithinTolerance(fine, g, SnoiseGradTolerance);
        }

        Assert.True(tested >= 150, $"only {tested} of {attempts} points were usable");
    }

    [Fact]
    public void FbmGradGradientMatchesCentralDifferencesOfItsOwnValue()
    {
        const int octaves = 4;
        const float lacunarity = 2.0f;
        const float gain = 0.5f;
        var random = new System.Random(0xA11CF);
        var tested = 0;
        var attempts = 0;
        while (tested < 200 && attempts < 40000)
        {
            attempts++;
            var p = RandomPoint(random);
            var g = math.fbm_grad(p, octaves, lacunarity, gain);
            float F(float3 q) => math.fbm_grad(q, octaves, lacunarity, gain).w;
            var fine = CentralDifference(F, p, PropertyEps);

            tested++;
            AssertGradientWithinTolerance(fine, g, FbmGradTolerance);
        }

        Assert.True(tested >= 150, $"only {tested} of {attempts} points were usable");
    }

    [Fact]
    public void RidgedGradGradientMatchesCentralDifferencesExcludingAnyOctavesZeroBand()
    {
        // ridged_grad's own crease (any octave's n_i = 0) is a real, deliberate discontinuity
        // (design.md, "the operator's dune term"), not a numerical artifact: exclude a band around it
        // for every octave, the same way cellular excludes its F1 = F2 seam.
        const float zeroBand = 0.1f;
        const int octaves = 3;
        const float lacunarity = 2.0f;
        const float gain = 0.5f;
        var random = new System.Random(0xA11D0);
        var tested = 0;
        var attempts = 0;
        while (tested < 200 && attempts < 80000)
        {
            attempts++;
            var p = RandomPoint(random);
            if (AnyOctaveNearZero(p, octaves, lacunarity, zeroBand))
                continue;

            var g = math.ridged_grad(p, octaves, lacunarity, gain);
            float F(float3 q) => math.ridged_grad(q, octaves, lacunarity, gain).w;
            var fine = CentralDifference(F, p, PropertyEps);

            tested++;
            AssertGradientWithinTolerance(fine, g, RidgedGradTolerance);
        }

        Assert.True(tested >= 150, $"only {tested} of {attempts} points were outside the zero band");
    }

    private static bool AnyOctaveNearZero(float3 p, int octaves, float lacunarity, float band)
    {
        var frequency = 1.0f;
        for (var i = 0; i < octaves; i++)
        {
            if (MathF.Abs(math.snoise_grad(p * frequency).w) < band)
                return true;
            frequency *= lacunarity;
        }

        return false;
    }

    private static float3 RandomPoint(System.Random random) => new(
        random.NextSingle() * 6.0f - 3.0f, random.NextSingle() * 6.0f - 3.0f, random.NextSingle() * 6.0f - 3.0f);

    private static float3 CentralDifference(Func<float3, float> f, float3 p, float eps) => new(
        (f(p + new float3(eps, 0.0f, 0.0f)) - f(p - new float3(eps, 0.0f, 0.0f))) / (2.0f * eps),
        (f(p + new float3(0.0f, eps, 0.0f)) - f(p - new float3(0.0f, eps, 0.0f))) / (2.0f * eps),
        (f(p + new float3(0.0f, 0.0f, eps)) - f(p - new float3(0.0f, 0.0f, eps))) / (2.0f * eps));

    private static void AssertGradientWithinTolerance(float3 numeric, float4 analytic, float tolerance)
    {
        AssertWithinTolerance(numeric.x, analytic.x, tolerance);
        AssertWithinTolerance(numeric.y, analytic.y, tolerance);
        AssertWithinTolerance(numeric.z, analytic.z, tolerance);
    }

    private static void AssertWithinTolerance(float expected, float actual, float tolerance) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected}, actual {actual}, tolerance {tolerance}");

    // ---- snoise is continuous across simplex-cell boundaries ----

    // The simplex corner set (i, i1, i2) summed by snoise(float3), derived the same way snoise does.
    private static (float3 i, float3 i1, float3 i2) SimplexCell(float3 p)
    {
        var i = math.floor(p + math.dot(p, new float3(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f)));
        var x0 = p - i + math.dot(i, new float3(1.0f / 6.0f, 1.0f / 6.0f, 1.0f / 6.0f));
        var g = math.step(new float3(x0.y, x0.z, x0.x), x0);
        var l = 1.0f - g;
        var lzxy = new float3(l.z, l.x, l.y);
        return (i, math.min(g, lzxy), math.max(g, lzxy));
    }

    [Fact]
    public void SnoiseAndItsGradientAreContinuousAcrossSimplexCellBoundaries()
    {
        // March random lines in steps of 1e-3 until the corner set changes, bisect the crossing to
        // float precision, and compare the two sides. With a kernel radius^2 above 0.5 a lattice
        // vertex outside the four summed corners can lie inside the kernel, which leaves a value
        // jump at the boundary (stegu/webgl-noise fixed it in 21d9fe23d7); at 0.5 the jump is float
        // noise. A value jump below 1e-5 and a gradient jump below 1e-4 is float noise on both sides.
        const int lines = 400;
        const int maxSteps = 20000;
        const float dt = 1.0e-3f;
        var random = new System.Random(0x5EA4);
        var crossings = 0;
        var maxValueJump = 0.0f;
        var maxGradientJump = 0.0f;
        for (var line = 0; line < lines; line++)
        {
            var origin = RandomPoint(random);
            var direction = math.normalize(new float3(
                random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            var start = SimplexCell(origin);
            var t = 0.0f;
            var found = false;
            for (var step = 0; step < maxSteps && !found; step++)
            {
                t += dt;
                found = !SimplexCell(origin + direction * t).Equals(start);
            }

            Assert.True(found, $"line {line} never left its simplex cell in {maxSteps} steps");

            double lo = t - dt;
            double hi = t;
            for (var bisect = 0; bisect < 60; bisect++)
            {
                var mid = 0.5 * (lo + hi);
                if (SimplexCell(origin + direction * (float)mid).Equals(start)) lo = mid; else hi = mid;
            }

            var a = math.snoise_grad(origin + direction * (float)lo);
            var b = math.snoise_grad(origin + direction * (float)hi);
            crossings++;
            maxValueJump = MathF.Max(maxValueJump, MathF.Abs(a.w - b.w));
            maxGradientJump = MathF.Max(maxGradientJump, MathF.Max(MathF.Abs(a.x - b.x), MathF.Max(MathF.Abs(a.y - b.y), MathF.Abs(a.z - b.z))));
        }

        Assert.Equal(lines, crossings);
        Assert.True(maxValueJump < 1.0e-5f, $"value jump {maxValueJump} across a simplex boundary");
        Assert.True(maxGradientJump < 1.0e-4f, $"gradient jump {maxGradientJump} across a simplex boundary");
    }

    // ---- snoise_grad.w vs snoise ----

    [Fact]
    public void SnoiseGradValueIsBitEqualToSnoise()
    {
        // Both compute 105 * dot(m4, px) via the exact same operation order (math.cs comment on
        // snoise_grad), so this asserts bit equality over 5,000 random points in [-100, 100]^3.
        var random = new System.Random(0x50F2);
        for (var i = 0; i < 5000; i++)
        {
            var p = new float3(
                random.NextSingle() * 200.0f - 100.0f, random.NextSingle() * 200.0f - 100.0f, random.NextSingle() * 200.0f - 100.0f);
            var expected = math.snoise(p);
            var actual = math.snoise_grad(p).w;
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
        }
    }

    // ---- fbm_grad / ridged_grad: per-octave gain and lacunarity ----

    [Fact]
    public void FbmGradWithOneOctaveEqualsSnoiseGrad()
    {
        var random = new System.Random(0xF6B10);
        for (var i = 0; i < 64; i++)
        {
            var p = RandomPoint(random);
            var lacunarity = 1.0f + random.NextSingle() * 3.0f;
            var gain = 0.1f + random.NextSingle() * 0.8f;

            var expected = math.snoise_grad(p);
            var actual = math.fbm_grad(p, 1, lacunarity, gain);

            Assert.Equal(expected.x, actual.x);
            Assert.Equal(expected.y, actual.y);
            Assert.Equal(expected.z, actual.z);
            Assert.Equal(expected.w, actual.w);
        }
    }

    [Fact]
    public void FbmGradCompoundsGainAndLacunarityPerOctaveNotOnce()
    {
        // Falsifies two mutant shapes at once: applying gain/lacunarity once for the whole sum
        // instead of compounding per octave, and swapping which argument drives amplitude versus
        // frequency. lacunarity != gain and both != 1 so a swap changes the result, and 3 octaves
        // gives amplitude/frequency three distinct compounded values (1, gain, gain^2 and
        // 1, lacunarity, lacunarity^2) so "applied once" mutants cannot coincidentally match.
        // The expected value is built by the same incremental multiply chain fbm_grad itself uses
        // (frequency *= lacunarity, amplitude *= gain, one step at a time, not lacunarity*lacunarity
        // or MathF.Pow), so this asserts bit-exact equality rather than needing a tolerance.
        const float lacunarity = 2.5f;
        const float gain = 0.6f;
        var random = new System.Random(0xF6B11);
        for (var i = 0; i < 32; i++)
        {
            var p = RandomPoint(random);

            var frequency = 1.0f;
            var amplitude = 1.0f;
            var n0 = math.snoise_grad(p * frequency);
            var expectedValue = amplitude * n0.w;
            var expectedGradient = amplitude * frequency * new float3(n0.x, n0.y, n0.z);
            frequency *= lacunarity;
            amplitude *= gain;

            var n1 = math.snoise_grad(p * frequency);
            expectedValue += amplitude * n1.w;
            expectedGradient += amplitude * frequency * new float3(n1.x, n1.y, n1.z);
            frequency *= lacunarity;
            amplitude *= gain;

            var n2 = math.snoise_grad(p * frequency);
            expectedValue += amplitude * n2.w;
            expectedGradient += amplitude * frequency * new float3(n2.x, n2.y, n2.z);

            var actual = math.fbm_grad(p, 3, lacunarity, gain);

            Assert.Equal(expectedValue, actual.w);
            Assert.Equal(expectedGradient.x, actual.x);
            Assert.Equal(expectedGradient.y, actual.y);
            Assert.Equal(expectedGradient.z, actual.z);
        }
    }

    [Fact]
    public void RidgedGradCompoundsGainAndLacunarityPerOctaveNotOnce()
    {
        // Same construction as FbmGradCompoundsGainAndLacunarityPerOctaveNotOnce, folded: value is
        // Sum a_i*(1-|n_i|), gradient is -Sum a_i*f_i*sign(n_i)*grad(n_i) (math.cs comment on
        // ridged_grad). p is offset away from the origin and re-tried on a near-zero octave so no
        // octave's n_i lands in the fold's own zero band, which would make the closed form's sign()
        // ambiguous between runs.
        const float lacunarity = 2.5f;
        const float gain = 0.6f;
        var random = new System.Random(0xF6B12);
        var tested = 0;
        var attempts = 0;
        while (tested < 32 && attempts < 4000)
        {
            attempts++;
            var p = RandomPoint(random);
            if (AnyOctaveNearZero(p, 3, lacunarity, 0.1f))
                continue;
            tested++;

            var frequency = 1.0f;
            var amplitude = 1.0f;
            var n0 = math.snoise_grad(p * frequency);
            var s0 = (float)MathF.Sign(n0.w);
            var expectedValue = amplitude * (1.0f - MathF.Abs(n0.w));
            var expectedGradient = -amplitude * frequency * s0 * new float3(n0.x, n0.y, n0.z);
            frequency *= lacunarity;
            amplitude *= gain;

            var n1 = math.snoise_grad(p * frequency);
            var s1 = (float)MathF.Sign(n1.w);
            expectedValue += amplitude * (1.0f - MathF.Abs(n1.w));
            expectedGradient += -amplitude * frequency * s1 * new float3(n1.x, n1.y, n1.z);
            frequency *= lacunarity;
            amplitude *= gain;

            var n2 = math.snoise_grad(p * frequency);
            var s2 = (float)MathF.Sign(n2.w);
            expectedValue += amplitude * (1.0f - MathF.Abs(n2.w));
            expectedGradient += -amplitude * frequency * s2 * new float3(n2.x, n2.y, n2.z);

            var actual = math.ridged_grad(p, 3, lacunarity, gain);

            Assert.Equal(expectedValue, actual.w);
            Assert.Equal(expectedGradient.x, actual.x);
            Assert.Equal(expectedGradient.y, actual.y);
            Assert.Equal(expectedGradient.z, actual.z);
        }

        Assert.True(tested == 32, $"only {tested} of {attempts} points were outside the zero band");
    }

    // ---- ridged_grad's deliberate crease at n = 0 ----

    [Fact]
    public void RidgedGradOneSidedGradientsAcrossAZeroCrossingDifferInSign()
    {
        // 1 octave: ridged_grad(p) = 1 - |snoise(p)|, gradient = -sign(snoise(p)) * grad(snoise)(p).
        // Bisect along a fixed direction from a random start point to find a tight straddle of a
        // snoise zero crossing, then check the two ridged_grad gradients just either side of it point
        // in near-opposite directions (their dot product is negative): the crease is real, not a
        // numerical accident (design.md, "the operator's dune term").
        var random = new System.Random(0xC2E55);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var origin = RandomPoint(random);
            var direction = new float3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f);
            if (math.length(direction) < 1.0e-3f)
                continue;

            float NoiseAt(float t) => math.snoise(origin + direction * t);

            var lo = -3.0f;
            var hi = 3.0f;
            if (Math.Sign(NoiseAt(lo)) == Math.Sign(NoiseAt(hi)))
                continue;

            for (var step = 0; step < 40; step++)
            {
                var mid = (lo + hi) * 0.5f;
                if (Math.Sign(NoiseAt(lo)) == Math.Sign(NoiseAt(mid))) lo = mid; else hi = mid;
            }

            // lo and hi now straddle the crossing within about 3 * 2^-40 of parameter t; step off by
            // a small margin either side so each sample is cleanly on one side, not exactly on it.
            const float margin = 1.0e-4f;
            var minusSide = origin + direction * (lo - margin);
            var plusSide = origin + direction * (hi + margin);
            if (MathF.Sign(NoiseAt(lo - margin)) == MathF.Sign(NoiseAt(hi + margin)))
                continue; // margin stepped back across the crossing; try another direction.

            var gradMinus = math.ridged_grad(minusSide, 1, 2.0f, 0.5f);
            var gradPlus = math.ridged_grad(plusSide, 1, 2.0f, 0.5f);
            var dot = gradMinus.x * gradPlus.x + gradMinus.y * gradPlus.y + gradMinus.z * gradPlus.z;

            Assert.True(dot < 0.0f, $"one-sided gradients did not differ in sign: minus=({gradMinus.x},{gradMinus.y},{gradMinus.z}) plus=({gradPlus.x},{gradPlus.y},{gradPlus.z}) dot={dot}");
            return;
        }

        Assert.Fail("did not find a snoise zero crossing to straddle");
    }
}
