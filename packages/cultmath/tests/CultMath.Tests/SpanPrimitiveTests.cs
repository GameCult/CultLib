using CultMath;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// The span primitives (math.Span.cs): snoise_hessian against the finite differences of snoise_grad and of
/// snoise, with each tolerance measured by Richardson's two step sizes rather than assumed; its symmetry and
/// its continuity across the simplex diagonal; and integral_clamp_linear against adaptive quadrature in
/// double. SNOISE_THIRD is measured and pinned in NoiseBoundTests (MeasureThird, ThirdConstantPinsSampled).
///
/// Hand mutations these must kill: any term of the per-corner second derivative dropped or mis-signed
/// (48 m^2 s x_i x_j, 8 m^3 p_j x_i, 8 m^3 p_i x_j, 8 m^3 s delta_ij) fails
/// SnoiseHessianMatchesCentralDifferences; the 0.3.0 cyclic tie order fails
/// SnoiseHessianContinuousOnSimplexDiagonal; a crossing of integral_clamp_linear moved, a clamp piece
/// dropped or the reversed-span sign lost fails IntegralClampLinearMatchesQuadrature.
/// </summary>
public sealed class SpanPrimitiveTests
{
    private readonly ITestOutputHelper output;

    public SpanPrimitiveTests(ITestOutputHelper output) => this.output = output;

    private const double Unit = 1.0 / 16777216.0;

    // A point of [-50, 50]^3 on the 2^-10 lattice, so that p +- h is exact in float32 for h a power of two
    // up to 2^-4 and the finite-difference quotients carry no step rounding.
    private static float3 LatticePoint(System.Random random)
    {
        float Axis() => MathF.Round((random.NextSingle() * 100.0f - 50.0f) * 1024.0f) / 1024.0f;
        return new float3(Axis(), Axis(), Axis());
    }

    // The simplex tie set of NoiseGradTests.SnoiseIsContinuousOnTheSimplexDiagonal: the line (t, t, t) and
    // the lattice diagonals v + s (1, 1, 1), v = L - sum(L) / 6.
    private static List<float3> TiePoints()
    {
        const int vertices = 160;
        const int perVertex = 8;
        var random = new System.Random(0x71E5);
        var points = new List<float3>();
        for (var k = 0; k < 400; k++)
        {
            var t = (float)(random.NextDouble() * 128.0 - 64.0);
            points.Add(new float3(t, t, t));
        }

        points.Add(new float3(0.0f, 0.0f, 0.0f));
        for (var k = 0; k < vertices; k++)
        {
            var lx = random.Next(-64, 65);
            var ly = random.Next(-64, 65);
            var lz = random.Next(-64, 65);
            var shift = (lx + ly + lz) / 6.0f;
            for (var j = 0; j < perVertex; j++)
            {
                var s = j == 0 ? 0.0f : random.NextSingle();
                points.Add(new float3(lx - shift + s, ly - shift + s, lz - shift + s));
            }
        }

        return points;
    }

    private static float3 Axis(int a, float h) => new(a == 0 ? h : 0.0f, a == 1 ? h : 0.0f, a == 2 ? h : 0.0f);

    // Column a of the Hessian by the central difference of snoise_grad at step h, in double.
    private static double[] GradientDifference(float3 p, int a, float h)
    {
        var plus = snoise_grad(p + Axis(a, h));
        var minus = snoise_grad(p - Axis(a, h));
        return new[] { (plus.x - (double)minus.x) / (2.0 * h), (plus.y - (double)minus.y) / (2.0 * h), (plus.z - (double)minus.z) / (2.0 * h) };
    }

    // Worst ratio of |H - D(h/2)| to its tolerance, the number of entries compared and the largest |H| met.
    private (double Worst, int Compared, double LargestEntry, double LargestTolerance) CompareToGradientDifferences(IEnumerable<float3> points)
    {
        const float h = 1.0f / 256.0f;
        double worst = 0.0, largest = 0.0, largestTolerance = 0.0;
        var compared = 0;
        foreach (var p in points)
        {
            var hessian = snoise_hessian(p);
            var gradient = snoise_grad(p);
            var scale = Math.Max(Math.Max(Math.Abs(gradient.x), Math.Abs(gradient.y)), Math.Max(Math.Abs(gradient.z), 1.0));
            for (var a = 0; a < 3; a++)
            {
                var coarse = GradientDifference(p, a, h);
                var fine = GradientDifference(p, a, h / 2.0f);
                for (var i = 0; i < 3; i++)
                {
                    // The truncation of the finer quotient is a third of the gap between the two, as the
                    // error is O(h^2); the roundoff is a few ulp of the gradient over the step. Both are
                    // measured per entry and tripled, so the tolerance follows the point's own roughness.
                    var tolerance = 3.0 * (Math.Abs(coarse[i] - fine[i]) + 64.0 * Unit * scale / (h / 2.0));
                    var error = Math.Abs(hessian[i][a] - fine[i]);
                    worst = Math.Max(worst, error / tolerance);
                    largest = Math.Max(largest, Math.Abs(hessian[i][a]));
                    largestTolerance = Math.Max(largestTolerance, tolerance);
                    Assert.True(error <= tolerance, $"snoise_hessian({p})[{i}][{a}] = {hessian[i][a]:R} differs from the central difference of snoise_grad {fine[i]:R} by {error:R}, over the measured tolerance {tolerance:R}");
                    compared++;
                }
            }
        }

        return (worst, compared, largest, largestTolerance);
    }

    [Fact]
    public void SnoiseHessianMatchesCentralDifferences()
    {
        var random = new System.Random(0x5A11);
        var points = Enumerable.Range(0, 10_000).Select(_ => LatticePoint(random)).ToList();
        var (worst, compared, largest, largestTolerance) = CompareToGradientDifferences(points);
        output.WriteLine($"snoise_hessian vs central differences of snoise_grad over {points.Count} seeded points: {compared} entries, worst error {worst:F3} of its Richardson tolerance, largest |H| {largest:R}, largest tolerance {largestTolerance:R}");

        // The comparison has teeth: curvature of the size the constant claims is present, and the tolerance
        // is far under it, so a dropped term of the second derivative cannot hide in it.
        Assert.True(largest > 10.0, $"the sample never meets curvature above 10 (largest {largest:R})");
        Assert.True(largestTolerance < 0.5, $"the tolerance reaches {largestTolerance:R}, too wide to see a dropped term");

        var (tieWorst, tieCompared, tieLargest, tieTolerance) = CompareToGradientDifferences(TiePoints());
        output.WriteLine($"snoise_hessian vs central differences of snoise_grad on the simplex tie set: {tieCompared} entries, worst error {tieWorst:F3} of its Richardson tolerance, largest |H| {tieLargest:R}, largest tolerance {tieTolerance:R}");
        Assert.True(tieLargest > 1.0, $"the tie set never meets curvature above 1 (largest {tieLargest:R})");
    }

    // The second difference of snoise in double, h apart, for the entry (i, j): the three-point stencil
    // for i = j, the four-point mixed stencil otherwise.
    private static double SecondDifference(float3 p, int i, int j, float h)
    {
        if (i == j)
            return (snoise(p + Axis(i, h)) - 2.0 * (double)snoise(p) + snoise(p - Axis(i, h))) / ((double)h * h);
        var pp = snoise(p + Axis(i, h) + Axis(j, h));
        var pm = snoise(p + Axis(i, h) - Axis(j, h));
        var mp = snoise(p - Axis(i, h) + Axis(j, h));
        var mm = snoise(p - Axis(i, h) - Axis(j, h));
        return ((double)pp - pm - mp + mm) / (4.0 * h * h);
    }

    [Fact]
    public void SnoiseHessianMatchesSecondDifferencesOfSnoise()
    {
        var random = new System.Random(0x5A12);
        double worst = 0.0, largest = 0.0;
        var compared = 0;
        var outside = 0;
        for (var k = 0; k < 4000; k++)
        {
            var p = LatticePoint(random);
            var hessian = snoise_hessian(p);
            for (var i = 0; i < 3; i++)
            for (var j = i; j < 3; j++)
            {
                // Richardson on the O(h^2) second difference: (4 S(h/2) - S(h)) / 3 is O(h^4). Two such
                // extrapolations, from (h, h/2) and from (h/2, h/4), differ by about fifteen times the
                // error of the finer one, so the comparison is against the finer and its tolerance is
                // four times that gap, plus the roundoff of snoise (a few ulp) over (h/4)^2. Near a
                // kernel's edge (m = 0) snoise's fourth derivative jumps and the extrapolation loses its
                // order: such an entry may exceed the tight tolerance, but never by more than the gap
                // between the last two second differences (an error that shrinks at least linearly in h
                // is at most that gap), and the tight tolerance must hold for all but a fraction.
                const float h = 1.0f / 16.0f;
                var s0 = SecondDifference(p, i, j, h);
                var s1 = SecondDifference(p, i, j, h / 2.0f);
                var s2 = SecondDifference(p, i, j, h / 4.0f);
                var coarseExtrapolation = (4.0 * s1 - s0) / 3.0;
                var extrapolated = (4.0 * s2 - s1) / 3.0;
                var tolerance = 4.0 * Math.Abs(extrapolated - coarseExtrapolation) + 64.0 * Unit / ((h / 4.0) * (h / 4.0));
                var loose = tolerance + 2.0 * Math.Abs(s1 - s2);
                var error = Math.Abs(hessian[i][j] - extrapolated);
                worst = Math.Max(worst, error / tolerance);
                largest = Math.Max(largest, Math.Abs(hessian[i][j]));
                compared++;
                if (error > tolerance)
                    outside++;
                Assert.True(error <= loose, $"snoise_hessian({p})[{i}][{j}] = {hessian[i][j]:R} differs from the extrapolated second difference of snoise {extrapolated:R} by {error:R}, over the measured tolerance {loose:R}");
            }
        }

        output.WriteLine($"snoise_hessian vs Richardson second differences of snoise over 4000 seeded points: {compared} entries, {outside} over the tight tolerance, worst error {worst:F3} of it, largest |H| {largest:R}");
        Assert.True(largest > 10.0, $"the sample never meets curvature above 10 (largest {largest:R})");
        Assert.True(outside <= compared / 200, $"{outside} of {compared} entries are over the tight Richardson tolerance, more than 0.5%");
    }

    [Fact]
    public void SnoiseHessianIsSymmetric()
    {
        var random = new System.Random(0x5A13);
        var points = Enumerable.Range(0, 20_000).Select(_ => new float3(random.NextSingle() * 512.0f - 256.0f, random.NextSingle() * 512.0f - 256.0f, random.NextSingle() * 512.0f - 256.0f))
            .Concat(TiePoints()).Append(new float3(0.0f, 0.0f, 0.0f)).ToList();
        foreach (var p in points)
        {
            var h = snoise_hessian(p);
            Assert.True(BitConverter.SingleToInt32Bits(h._m01) == BitConverter.SingleToInt32Bits(h._m10)
                && BitConverter.SingleToInt32Bits(h._m02) == BitConverter.SingleToInt32Bits(h._m20)
                && BitConverter.SingleToInt32Bits(h._m12) == BitConverter.SingleToInt32Bits(h._m21), $"snoise_hessian({p}) is not its own transpose: {h}");
        }
    }

    [Fact]
    public void SnoiseHessianContinuousOnSimplexDiagonal()
    {
        // Within 1 or 2 ulp of a tie point along each axis the Hessian moves by at most the third derivative
        // bound times the distance: SNOISE_THIRD x 2 sqrt 3 x 1e-4 covers a 1e-4 ball, and the points are
        // far closer than that. The 0.3.0 cyclic tie order is a jump of order one here.
        var bound = SNOISE_THIRD * 2.0 * Math.Sqrt(3.0) * 1.0e-4;
        var worst = 0.0;
        foreach (var p in TiePoints())
        {
            var centre = snoise_hessian(p);
            for (var axis = 0; axis < 3; axis++)
            {
                for (var direction = -1; direction <= 1; direction += 2)
                {
                    var q = p;
                    for (var ulp = 1; ulp <= 2; ulp++)
                    {
                        var component = axis == 0 ? q.x : axis == 1 ? q.y : q.z;
                        component = direction < 0 ? MathF.BitDecrement(component) : MathF.BitIncrement(component);
                        q = axis == 0 ? new float3(component, q.y, q.z) : axis == 1 ? new float3(q.x, component, q.z) : new float3(q.x, q.y, component);
                        var moved = snoise_hessian(q);
                        for (var i = 0; i < 3; i++)
                        for (var j = 0; j < 3; j++)
                        {
                            var jump = Math.Abs(moved[i][j] - (double)centre[i][j]);
                            worst = Math.Max(worst, jump);
                            Assert.True(jump <= bound, $"snoise_hessian jumps {jump:R} in entry [{i}][{j}] within {ulp} ulp of {p}, over {bound:R}");
                        }
                    }
                }
            }
        }

        output.WriteLine($"largest Hessian jump within 2 ulp of the simplex tie set: {worst:R} against the bound {bound:R}");
    }

    // Adaptive Simpson of f over [a, b] in double, to an absolute tolerance, with a depth cap.
    private static double Quadrature(Func<double, double> f, double a, double b, double tolerance)
    {
        double Simpson(double fa, double fm, double fb, double width) => width / 6.0 * (fa + 4.0 * fm + fb);

        double Recurse(double x0, double x1, double f0, double fm, double f1, double whole, double eps, int depth)
        {
            var mid = 0.5 * (x0 + x1);
            var lm = 0.5 * (x0 + mid);
            var rm = 0.5 * (mid + x1);
            var flm = f(lm);
            var frm = f(rm);
            var left = Simpson(f0, flm, fm, mid - x0);
            var right = Simpson(fm, frm, f1, x1 - mid);
            if (depth <= 0 || Math.Abs(left + right - whole) <= 15.0 * eps)
                return left + right + (left + right - whole) / 15.0;
            return Recurse(x0, mid, f0, flm, fm, left, eps / 2.0, depth - 1) + Recurse(mid, x1, fm, frm, f1, right, eps / 2.0, depth - 1);
        }

        if (a == b)
            return 0.0;
        var fa = f(a);
        var fb = f(b);
        var fmid = f(0.5 * (a + b));
        return Recurse(a, b, fa, fmid, fb, Simpson(fa, fmid, fb, b - a), tolerance, 40);
    }

    private static double ClampedLine(double a, double b, double lo, double hi, double t) => Math.Min(Math.Max(a + b * t, lo), hi);

    // The reference integral in double: the adaptive quadrature of the clamped line, sign for a reversed span.
    private static double ReferenceIntegral(float a, float b, float lo, float hi, float t0, float t1)
    {
        double low = Math.Min(lo, hi);
        var integral = t0 <= t1
            ? Quadrature(t => ClampedLine(a, b, low, hi, t), t0, t1, 1.0e-12)
            : -Quadrature(t => ClampedLine(a, b, low, hi, t), t1, t0, 1.0e-12);
        return integral;
    }

    [Fact]
    public void IntegralClampLinearMatchesQuadrature()
    {
        var random = new System.Random(0x1A7E);
        float Uniform(float lo, float hi) => lo + (hi - lo) * random.NextSingle();
        double worstRatio = 0.0;
        int zeroSlopes = 0, reversed = 0, both = 0, neither = 0, ramps = 0;
        for (var k = 0; k < 100_000; k++)
        {
            var a = Uniform(-4.0f, 4.0f);
            // One in eight slopes is zero, one in sixteen is tiny (a crossing far outside any span).
            var b = (k % 8) switch { 0 => 0.0f, 1 => 1.0e-30f * (k % 16 == 1 ? 1.0f : -1.0f), _ => Uniform(-4.0f, 4.0f) };
            var lo = Uniform(-2.0f, 0.5f);
            var hi = lo + Uniform(0.0f, 3.0f);
            var t0 = Uniform(-3.0f, 3.0f);
            var t1 = Uniform(-3.0f, 3.0f);
            var got = integral_clamp_linear(a, b, lo, hi, t0, t1);
            var want = ReferenceIntegral(a, b, lo, hi, t0, t1);
            var span = Math.Abs((double)t1 - t0);
            var error = Math.Abs(got - want);
            worstRatio = Math.Max(worstRatio, error / Math.Max(1.0e-5 * Math.Abs(want), 1.0e-6 * span));
            Assert.True(error <= 1.0e-5 * Math.Abs(want) || error <= 1.0e-6 * span, $"integral_clamp_linear({a:R}, {b:R}, {lo:R}, {hi:R}, {t0:R}, {t1:R}) = {got:R}, quadrature {want:R}");
            if (b == 0.0f)
                zeroSlopes++;
            if (t1 < t0)
                reversed++;
            if (b != 0.0f)
            {
                double atLo = (lo - (double)a) / b, atHi = (hi - (double)a) / b;
                double from = Math.Min(t0, t1), to = Math.Max(t0, t1);
                var crossesLo = atLo > from && atLo < to;
                var crossesHi = atHi > from && atHi < to;
                if (crossesLo && crossesHi)
                    both++;
                else if (!crossesLo && !crossesHi)
                    neither++;
                if (hi > lo && (crossesLo || crossesHi))
                    ramps++;
            }
        }

        output.WriteLine($"integral_clamp_linear vs adaptive quadrature over 100000 cases: {zeroSlopes} flat, {reversed} reversed, {both} crossing both bounds, {neither} crossing neither, {ramps} on a ramp; worst error {worstRatio:F3} of its tolerance");
        Assert.True(zeroSlopes > 10_000 && reversed > 40_000 && both > 2_000 && neither > 2_000, $"the sample meets a branch too rarely: {zeroSlopes} flat, {reversed} reversed, {both} both, {neither} neither");
    }

    /// <summary>
    /// An infinite bound never reached by the line contributes nothing, not infinity times zero: relu
    /// (hi = +inf) and its mirror (lo = -inf) give the finite integral, whether the line crosses the finite
    /// bound inside the span or not.
    /// </summary>
    [Fact]
    public void IntegralClampLinearIsFiniteForInfiniteBounds()
    {
        var inf = float.PositiveInfinity;
        // t over [0, 1] clamped below at 0: all ramp, 1/2.
        Assert.Equal(0.5f, integral_clamp_linear(0.0f, 1.0f, 0.0f, inf, 0.0f, 1.0f));
        // t - 1/2 over [0, 1] clamped below at 0: 0 until 1/2, then the ramp's 1/8.
        Assert.Equal(0.125f, integral_clamp_linear(-0.5f, 1.0f, 0.0f, inf, 0.0f, 1.0f));
        // The falling line 1/2 - t clamped below at 0 is the mirror: 1/8 over [0, 1].
        Assert.Equal(0.125f, integral_clamp_linear(0.5f, -1.0f, 0.0f, inf, 0.0f, 1.0f));
        // lo = -inf: clamp(t, -inf, 1/2) over [0, 1] is t up to 1/2 then 1/2 (1/8 + 1/4), and 1 - t clamped
        // above at 1/2 is 1/2 until t = 1/2 and then falls (1/4 + 1/8).
        Assert.Equal(0.375f, integral_clamp_linear(0.0f, 1.0f, -inf, 0.5f, 0.0f, 1.0f));
        Assert.Equal(0.375f, integral_clamp_linear(1.0f, -1.0f, -inf, 0.5f, 0.0f, 1.0f));
        // Both infinite: the line itself, the trapezoid, in either direction.
        Assert.Equal(7.5f, integral_clamp_linear(0.0f, 1.0f, -inf, inf, 1.0f, 4.0f));
        Assert.Equal(-7.5f, integral_clamp_linear(0.0f, 1.0f, -inf, inf, 4.0f, 1.0f));
        // A line starting on the finite bound and rising into the infinite one: all ramp, the integral of t over [2, 4].
        Assert.Equal(6.0f, integral_clamp_linear(0.0f, 1.0f, 2.0f, inf, 2.0f, 4.0f));
    }

    [Fact]
    public void IntegralClampLinearHasExactValuesOnTheBinaryFloats()
    {
        // The ramp t from 0 to 1 clamped to [0.25, 0.75] over [0, 1]: 0.25 x 0.25 below, 0.5 x 0.5 on the ramp, 0.75 x 0.25 above.
        Assert.Equal(0.0625f + 0.25f + 0.1875f, integral_clamp_linear(0.0f, 1.0f, 0.25f, 0.75f, 0.0f, 1.0f));
        // A reversed span is the negative of the span.
        Assert.Equal(-0.5f, integral_clamp_linear(0.0f, 1.0f, 0.25f, 0.75f, 1.0f, 0.0f));
        Assert.Equal(integral_clamp_linear(0.3f, -0.7f, -0.5f, 0.9f, -2.0f, 1.5f), -integral_clamp_linear(0.3f, -0.7f, -0.5f, 0.9f, 1.5f, -2.0f));
        // b = 0 is the constant, clamped.
        Assert.Equal(2.0f * 3.0f, integral_clamp_linear(5.0f, 0.0f, 0.0f, 2.0f, 1.0f, 4.0f));
        Assert.Equal(-1.0f * 3.0f, integral_clamp_linear(-5.0f, 0.0f, -1.0f, 2.0f, 1.0f, 4.0f));
        // An empty span, and a span that never leaves the ramp (the trapezoid: 3 x mean of 1 and 4).
        Assert.Equal(0.0f, integral_clamp_linear(0.0f, 1.0f, -9.0f, 9.0f, 2.0f, 2.0f));
        Assert.Equal(7.5f, integral_clamp_linear(0.0f, 1.0f, -9.0f, 9.0f, 1.0f, 4.0f));
        // A falling line starts at the upper bound: 1 for t in [0, 1], the ramp 1 to 0 over [1, 2], 0 after.
        Assert.Equal(1.0f + 0.5f, integral_clamp_linear(2.0f, -1.0f, 0.0f, 1.0f, 0.0f, 3.0f));
        // lo above hi clamps to hi everywhere.
        Assert.Equal(2.0f * 3.0f, integral_clamp_linear(0.0f, 1.0f, 5.0f, 2.0f, 0.0f, 3.0f));
    }
}
