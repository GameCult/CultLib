using CultMath;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// intervals-enclose for every iv_* op (design.md, "Intervals"): for each op, intervals with seeded random
/// centres and widths and the points inside them; f(x), evaluated pointwise in float32 by the same C# math
/// the op mirrors, must lie in op(I) with no tolerance.
///
/// Stryker mutates operators, not float bounds, so the mutants that matter most here are written by hand
/// and run by <see cref="EveryOneUlpShrinkIsCaught"/>: for every op, the lo bound raised by one ulp and the
/// hi bound lowered by one ulp must each fail the harness. Two pairs are equivalent mutants on this CPU and
/// are not in that list:
///   - iv_exp's one-ulp widening, both sides: MathF.Exp is monotone over every float on the Linux runtime
///     (MeasureMonotonicity prints 0 violations), so removing the widening encloses here. It is kept because
///     exp is not required to be correctly rounded, and another libm or a GPU need not be monotone.
///   - iv_smoothstep's one-ulp widening: the generic harness never meets one of smoothstep's 1-ulp
///     non-monotone steps; <see cref="SmoothstepWideningCoversItsFloatNonMonotonicity"/> constructs one on
///     each side and kills both mutants.
/// </summary>
public sealed class IntervalTests
{
    private readonly ITestOutputHelper output;

    public IntervalTests(ITestOutputHelper output) => this.output = output;

    private const int IntervalCount = 10000;
    private const int PointsPerInterval = 16;

    // One row of the shared harness: how many interval arguments the op takes, how to draw its scalar
    // arguments, the op itself and the pointwise function it encloses. A new op is one entry here.
    internal sealed record Op(
        string Name,
        int Intervals,
        Func<System.Random, float[]> Scalars,
        Func<float2[], float[], float2> Bound,
        Func<float[], float[], float> Pointwise,
        bool NonNegativeDomain = false);

    private static float[] None(System.Random random) => Array.Empty<float>();

    private static float Uniform(System.Random random, float lo, float hi) => lo + (hi - lo) * random.NextSingle();

    internal static readonly Op[] Ops =
    {
        new("iv_point", 0, r => new[] { Uniform(r, -20.0f, 20.0f) }, (i, s) => iv_point(s[0]), (x, s) => s[0]),
        new("iv_add", 2, None, (i, s) => iv_add(i[0], i[1]), (x, s) => x[0] + x[1]),
        new("iv_sub", 2, None, (i, s) => iv_sub(i[0], i[1]), (x, s) => x[0] - x[1]),
        new("iv_neg", 1, None, (i, s) => iv_neg(i[0]), (x, s) => -x[0]),
        new("iv_mul", 2, None, (i, s) => iv_mul(i[0], i[1]), (x, s) => x[0] * x[1]),
        new("iv_scale", 1, r => new[] { Uniform(r, -4.0f, 4.0f) }, (i, s) => iv_scale(i[0], s[0]), (x, s) => x[0] * s[0]),
        new("iv_abs", 1, None, (i, s) => iv_abs(i[0]), (x, s) => abs(x[0])),
        new("iv_min", 2, None, (i, s) => iv_min(i[0], i[1]), (x, s) => min(x[0], x[1])),
        new("iv_max", 2, None, (i, s) => iv_max(i[0], i[1]), (x, s) => max(x[0], x[1])),
        new("iv_sqr", 1, None, (i, s) => iv_sqr(i[0]), (x, s) => x[0] * x[0]),
        new("iv_sqrt", 1, None, (i, s) => iv_sqrt(i[0]), (x, s) => sqrt(x[0]), NonNegativeDomain: true),
        new("iv_exp", 1, None, (i, s) => iv_exp(i[0]), (x, s) => exp(x[0])),
        new("iv_clamp", 1, r => new[] { Uniform(r, -25.0f, 25.0f), Uniform(r, -25.0f, 25.0f) },
            (i, s) => iv_clamp(i[0], s[0], s[1]), (x, s) => clamp(x[0], s[0], s[1])),
        new("iv_saturate", 1, None, (i, s) => iv_saturate(i[0]), (x, s) => saturate(x[0])),
        new("iv_smoothstep", 1, r => new[] { Uniform(r, -25.0f, 25.0f), Uniform(r, -25.0f, 25.0f) },
            (i, s) => iv_smoothstep(s[0], s[1], i[0]), (x, s) => smoothstep(s[0], s[1], x[0])),
        new("iv_lerp", 2, r => new[] { Uniform(r, -0.5f, 1.5f) }, (i, s) => iv_lerp(i[0], i[1], s[0]), (x, s) => lerp(x[0], x[1], s[0])),
    };

    // Centres in [-20, 20], widths log-uniform in [1e-4, 10]. Every eighth interval is a point
    // interval: a bound that is only reached when an argument is a point (iv_lerp's, where x appears
    // twice) is still reached, so a one-ulp shrink of it is still caught.
    private static float2 RandomInterval(System.Random random, int index)
    {
        var centre = Uniform(random, -20.0f, 20.0f);
        if (index % 8 == 7)
            return iv_point(centre);
        var width = MathF.Pow(10.0f, Uniform(random, -4.0f, 1.0f));
        return new float2(centre - width * 0.5f, centre + width * 0.5f);
    }

    // The points inside one argument: both endpoints, zero when the interval holds it, and the rest
    // uniform. Endpoints and zero are where a tight bound is reached.
    private static float[] PointsInside(System.Random random, float2 interval, bool nonNegative)
    {
        var lo = nonNegative ? max(interval.x, 0.0f) : interval.x;
        var hi = nonNegative ? max(interval.y, 0.0f) : interval.y;
        var points = new float[PointsPerInterval];
        points[0] = lo;
        points[1] = hi;
        var next = 2;
        if (lo < 0.0f && hi > 0.0f)
            points[next++] = 0.0f;
        for (; next < points.Length; next++)
            points[next] = min(max(lo + (hi - lo) * random.NextSingle(), lo), hi);
        return points;
    }

    /// <summary>Runs one op over the seeded set; returns the violations, at most a few, described.</summary>
    internal static List<string> Violations(Op op, Func<float2, float2>? mutate = null, int seed = 0x1A7E)
    {
        var random = new System.Random(seed);
        var violations = new List<string>();
        var count = 0;
        for (var k = 0; k < IntervalCount; k++)
        {
            var intervals = new float2[op.Intervals];
            for (var a = 0; a < op.Intervals; a++)
                intervals[a] = RandomInterval(random, k + a);
            var scalars = op.Scalars(random);
            var bound = op.Bound(intervals, scalars);
            if (mutate is not null)
                bound = mutate(bound);

            var points = intervals.Select(i => PointsInside(random, i, op.NonNegativeDomain)).ToArray();
            for (var p = 0; p < PointsPerInterval; p++)
            {
                // Binary ops visit the four endpoint corners first (p = 0..3), then pair points by index.
                var x = new float[op.Intervals];
                for (var a = 0; a < op.Intervals; a++)
                    x[a] = op.Intervals == 2 && p < 4 ? points[a][a == 0 ? p & 1 : p >> 1] : points[a][p];
                var value = op.Pointwise(x, scalars);
                if (!(value >= bound.x && value <= bound.y))
                {
                    if (count++ < 4)
                        violations.Add($"{op.Name}({string.Join(", ", intervals)}; {string.Join(", ", scalars)}) = {bound}, f({string.Join(", ", x)}) = {value:R}");
                }
            }
        }

        if (count > violations.Count)
            violations.Add($"... {count} violations in all");
        return violations;
    }

    [Fact]
    public void EveryOpEnclosesItsPoints()
    {
        var failures = Ops.SelectMany(op => Violations(op)).ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    internal static float NextUp(float v) => v == 0.0f ? asfloat(1u) : asfloat(v > 0.0f ? asuint(v) + 1u : asuint(v) - 1u);
    private static float NextDown(float v) => -NextUp(-v);

    /// <summary>
    /// The hand-mutation list: every op's lo raised by one ulp, and its hi lowered by one ulp, must each
    /// fail the harness. The two equivalent pairs are named in the class comment.
    /// </summary>
    [Fact]
    public void EveryOneUlpShrinkIsCaught()
    {
        var survivors = new List<string>();
        foreach (var op in Ops.Where(o => o.Name is not "iv_exp" and not "iv_smoothstep"))
        {
            if (Violations(op, b => new float2(NextUp(b.x), b.y)).Count == 0)
                survivors.Add(op.Name + " lo + 1 ulp");
            if (Violations(op, b => new float2(b.x, NextDown(b.y))).Count == 0)
                survivors.Add(op.Name + " hi - 1 ulp");
        }

        Assert.True(survivors.Count == 0, "surviving one-ulp shrinks: " + string.Join(", ", survivors));
    }

    [Fact]
    public void MulCoversEverySignCase()
    {
        // Both positive, both negative, mixed both ways, and a factor straddling zero on either side.
        Assert.Equal(new float2(2.0f, 12.0f), iv_mul(new float2(1.0f, 3.0f), new float2(2.0f, 4.0f)));
        Assert.Equal(new float2(2.0f, 12.0f), iv_mul(new float2(-3.0f, -1.0f), new float2(-4.0f, -2.0f)));
        Assert.Equal(new float2(-12.0f, -2.0f), iv_mul(new float2(1.0f, 3.0f), new float2(-4.0f, -2.0f)));
        Assert.Equal(new float2(-12.0f, -2.0f), iv_mul(new float2(-3.0f, -1.0f), new float2(2.0f, 4.0f)));
        Assert.Equal(new float2(-8.0f, 12.0f), iv_mul(new float2(-2.0f, 3.0f), new float2(2.0f, 4.0f)));
        Assert.Equal(new float2(-12.0f, 8.0f), iv_mul(new float2(-2.0f, 3.0f), new float2(-4.0f, -2.0f)));
        Assert.Equal(new float2(-15.0f, 12.0f), iv_mul(new float2(-2.0f, 3.0f), new float2(-5.0f, 4.0f)));
    }

    [Fact]
    public void SqrtClampsANegativeLoAtZero()
    {
        Assert.Equal(new float2(0.0f, 3.0f), iv_sqrt(new float2(-4.0f, 9.0f)));
        Assert.Equal(new float2(0.0f, 0.0f), iv_sqrt(new float2(-4.0f, -1.0f)));
        Assert.Equal(new float2(2.0f, 3.0f), iv_sqrt(new float2(4.0f, 9.0f)));
    }

    [Fact]
    public void AbsAndSqrStraddlingZeroStartAtZero()
    {
        Assert.Equal(new float2(0.0f, 3.0f), iv_abs(new float2(-3.0f, 2.0f)));
        Assert.Equal(new float2(0.0f, 5.0f), iv_abs(new float2(-2.0f, 5.0f)));
        Assert.Equal(new float2(2.0f, 5.0f), iv_abs(new float2(-5.0f, -2.0f)));
        Assert.Equal(new float2(0.0f, 9.0f), iv_sqr(new float2(-3.0f, 2.0f)));
        Assert.Equal(new float2(4.0f, 25.0f), iv_sqr(new float2(-5.0f, -2.0f)));
    }

    [Fact]
    public void MinAndMaxAreThePointwiseMinAndMaxOfTheEnds()
    {
        var a = new float2(-1.0f, 4.0f);
        var b = new float2(2.0f, 3.0f);
        Assert.Equal(new float2(-1.0f, 3.0f), iv_min(a, b));
        Assert.Equal(new float2(2.0f, 4.0f), iv_max(a, b));
    }

    [Fact]
    public void SmoothstepBoundsAreItsOrderedEndpointsWidenedByOneUlp()
    {
        var rising = iv_smoothstep(0.0f, 1.0f, new float2(0.25f, 0.5f));
        Assert.Equal(NextDown(smoothstep(0.0f, 1.0f, 0.25f)), rising.x);
        Assert.Equal(NextUp(smoothstep(0.0f, 1.0f, 0.5f)), rising.y);

        // minimum > maximum: smoothstep falls, so the endpoints swap.
        var falling = iv_smoothstep(1.0f, 0.0f, new float2(0.25f, 0.5f));
        Assert.Equal(NextDown(smoothstep(1.0f, 0.0f, 0.5f)), falling.x);
        Assert.Equal(NextUp(smoothstep(1.0f, 0.0f, 0.25f)), falling.y);

        // The widening never leaves [0, 1].
        Assert.Equal(new float2(0.0f, 1.0f), iv_smoothstep(0.0f, 1.0f, new float2(-2.0f, 3.0f)));
    }

    /// <summary>
    /// smoothstep's float32 evaluation steps down by one ulp in places (MeasureMonotonicity). Three
    /// consecutive floats t0 &lt; t1 &lt; t2 with f(t1) below both neighbours put f(t1) outside the ordered
    /// endpoints of [t0, t2]; three with f(t1) above both put it outside on the other side. The widened
    /// bound encloses both; without the widening it does not.
    /// </summary>
    [Fact]
    public void SmoothstepWideningCoversItsFloatNonMonotonicity()
    {
        float F(float t) => smoothstep(0.0f, 1.0f, t);
        float2? dip = null, peak = null;
        for (var t = 1.0e-4f; t < 0.01f && (dip is null || peak is null); t = NextUp(t))
        {
            var t1 = NextUp(t);
            var t2 = NextUp(t1);
            if (dip is null && F(t1) < F(t) && F(t1) < F(t2))
                dip = new float2(t, t2);
            if (peak is null && F(t1) > F(t) && F(t1) > F(t2))
                peak = new float2(t, t2);
        }

        Assert.NotNull(dip);
        Assert.NotNull(peak);
        foreach (var (interval, low) in new[] { (dip!.Value, true), (peak!.Value, false) })
        {
            var mid = F(NextUp(interval.x));
            var unwidened = new float2(min(F(interval.x), F(interval.y)), max(F(interval.x), F(interval.y)));
            Assert.False(mid >= unwidened.x && mid <= unwidened.y, $"the {(low ? "dip" : "peak")} at {interval} is inside the endpoints");
            var bound = iv_smoothstep(0.0f, 1.0f, interval);
            Assert.True(mid >= bound.x && mid <= bound.y, $"iv_smoothstep{interval} = {bound} misses {mid:R}");
        }
    }

    [Fact]
    public void LerpIsTheNaturalExtensionAndTightForPointArguments()
    {
        Assert.Equal(new float2(lerp(1.0f, 3.0f, 0.25f), lerp(1.0f, 7.0f, 0.25f)), iv_lerp(iv_point(1.0f), new float2(3.0f, 7.0f), 0.25f));
        Assert.Equal(new float2(-1.0f + (2.0f - 1.0f) * 0.5f, 1.0f + (4.0f + 1.0f) * 0.5f), iv_lerp(new float2(-1.0f, 1.0f), new float2(2.0f, 4.0f), 0.5f));
    }

    [Fact]
    public void ExpIsWidenedByOneUlpOnEachSide()
    {
        var bound = iv_exp(new float2(-1.0f, 2.0f));
        Assert.Equal(NextDown(exp(-1.0f)), bound.x);
        Assert.Equal(NextUp(exp(2.0f)), bound.y);
        Assert.Equal(0.0f, iv_exp(new float2(-200.0f, 0.0f)).x);
    }

    /// <summary>
    /// The monotonicity the widening rules rest on, measured over every float: exp over [-87, 88.7], the
    /// range where it neither underflows to a subnormal nor overflows, and smoothstep(0, 1, t) over [0, 1],
    /// which covers every smoothstep because t = saturate((x - minimum) / (maximum - minimum)) is itself
    /// monotone in x. Prints the number of steps down and the largest, in ulps. Slow: about a minute.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Slow")]
    public void MeasureMonotonicity()
    {
        long expDown = 0, expWorst = 0;
        var previous = exp(-87.0f);
        for (var x = NextUp(-87.0f); x <= 88.7f; x = NextUp(x))
        {
            var e = exp(x);
            if (e < previous)
            {
                expDown++;
                expWorst = Math.Max(expWorst, (long)asuint(previous) - asuint(e));
            }

            previous = e;
        }

        long smoothDown = 0, smoothWorst = 0;
        var runningMax = 0.0f;
        for (var bits = 0u; bits <= asuint(1.0f); bits++)
        {
            var s = smoothstep(0.0f, 1.0f, asfloat(bits));
            if (s < runningMax)
            {
                smoothDown++;
                smoothWorst = Math.Max(smoothWorst, (long)asuint(runningMax) - asuint(s));
            }
            else
            {
                runningMax = s;
            }
        }

        output.WriteLine($"IV-REPORT monotonicity: exp steps down {expDown} times, worst {expWorst} ulp; smoothstep steps down {smoothDown} times, worst {smoothWorst} ulp");
        Assert.True(expWorst <= 1, $"exp steps down by {expWorst} ulp; one ulp of widening is not enough");
        Assert.True(smoothWorst <= 1, $"smoothstep steps down by {smoothWorst} ulp; one ulp of widening is not enough");
    }
}
