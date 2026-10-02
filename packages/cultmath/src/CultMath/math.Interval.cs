namespace CultMath;

public static partial class math
{
    // Interval arithmetic over float2(lo, hi), lo <= hi, both finite (design.md, "Intervals"). Every
    // iv_* function returns an interval that contains f(x) for every x in its input intervals, where
    // f(x) is the same function evaluated pointwise in float32, mirrored by cultmath_iv_* in
    // shaders/CultMath.Interval.hlsl. Each bound is computed with the pointwise function's own float32
    // operations; because IEEE round-to-nearest is monotone, an op that is monotone on its region
    // encloses its float32 evaluation with no widening. Where float32 evaluation is not monotone, or
    // the libm is not required to be, the bound is widened by exactly one ulp, and the doc comment
    // says so. There is no empty interval: a caller that needs "provably empty" tests hi < cutoff.

    // Lipschitz constant of snoise(float3) in its input: an upper bound on the length of its analytic
    // gradient. Provenance: NoiseBoundTests.MeasureLipschitz (slow, explicit) takes the largest gradient
    // length over 1e6 seeded points, refines the 1e4 largest by gradient ascent, and multiplies the
    // refined maximum by 1.10; NoiseBoundTests.LipschitzConstantPinsSampledGradients pins this value
    // against a fast re-measure and the ascent from MeasureLipschitz's witness.
    // Empirical with a margin, not a proof; the enclosure tests are the defence. A change to the
    // snoise kernel must re-run MeasureLipschitz and re-pin this constant.
    public const float SNOISE_LIPSCHITZ = 10.099261f;

    /// <summary>The point interval [x, x].</summary>
    public static float2 iv_point(float value) => new(value, value);

    /// <summary>Encloses x + y for x in a, y in b. Monotone: no widening.</summary>
    public static float2 iv_add(float2 a, float2 b) => new(a.x + b.x, a.y + b.y);

    /// <summary>Encloses x - y for x in a, y in b. Monotone: no widening.</summary>
    public static float2 iv_sub(float2 a, float2 b) => new(a.x - b.y, a.y - b.x);

    /// <summary>Encloses -x for x in a. Exact.</summary>
    public static float2 iv_neg(float2 a) => new(-a.y, -a.x);

    /// <summary>
    /// Encloses x * y for x in a, y in b: the least and greatest of the four corner products. The
    /// product is monotone in each factor for a fixed sign of the other, so no widening.
    /// </summary>
    public static float2 iv_mul(float2 a, float2 b)
    {
        var p0 = a.x * b.x;
        var p1 = a.x * b.y;
        var p2 = a.y * b.x;
        var p3 = a.y * b.y;
        return new(min(min(p0, p1), min(p2, p3)), max(max(p0, p1), max(p2, p3)));
    }

    /// <summary>Encloses x * s for x in a. Monotone: no widening.</summary>
    public static float2 iv_scale(float2 a, float s) => s >= 0.0f ? new(a.x * s, a.y * s) : new(a.y * s, a.x * s);

    /// <summary>Encloses abs(x) for x in a; an interval straddling zero gives [0, max(-lo, hi)]. Exact.</summary>
    public static float2 iv_abs(float2 a)
    {
        if (a.x >= 0.0f)
            return a;
        if (a.y <= 0.0f)
            return new(-a.y, -a.x);
        return new(0.0f, max(-a.x, a.y));
    }

    /// <summary>Encloses min(x, y) for x in a, y in b. Monotone: no widening.</summary>
    public static float2 iv_min(float2 a, float2 b) => new(min(a.x, b.x), min(a.y, b.y));

    /// <summary>Encloses max(x, y) for x in a, y in b. Monotone: no widening.</summary>
    public static float2 iv_max(float2 a, float2 b) => new(max(a.x, b.x), max(a.y, b.y));

    /// <summary>
    /// Encloses x * x for x in a; an interval straddling zero gives [0, max(lo * lo, hi * hi)]. Tighter
    /// than iv_mul(a, a), which cannot know both factors are the same x. Monotone on each side of zero:
    /// no widening.
    /// </summary>
    public static float2 iv_sqr(float2 a)
    {
        var l = a.x * a.x;
        var h = a.y * a.y;
        if (a.x >= 0.0f)
            return new(l, h);
        if (a.y <= 0.0f)
            return new(h, l);
        return new(0.0f, max(l, h));
    }

    /// <summary>
    /// Encloses sqrt(x) for x in a with x >= 0 (sqrt's domain): lo is clamped at 0, so [-4, 9] gives [0, 3]
    /// and an interval wholly below zero gives [0, 0]. sqrt is correctly rounded, hence monotone: no
    /// widening.
    /// </summary>
    public static float2 iv_sqrt(float2 a) => new(sqrt(max(a.x, 0.0f)), sqrt(max(a.y, 0.0f)));

    /// <summary>
    /// Encloses exp(x) for x in a. Widened by one ulp on each side, because exp is not required to be
    /// correctly rounded, so a libm need not be monotone. Where exp(hi) overflows to infinity, hi is NaN,
    /// which a cull test hi &lt; cutoff reads as "not provably below", the safe side.
    /// </summary>
    public static float2 iv_exp(float2 a)
    {
        var lo = exp(a.x);
        var hi = exp(a.y);
        return new(lo > 0.0f ? asfloat(asuint(lo) - 1u) : 0.0f, asfloat(asuint(hi) + 1u));
    }

    /// <summary>Encloses clamp(x, minimum, maximum) for x in a. Monotone: no widening.</summary>
    public static float2 iv_clamp(float2 a, float minimum, float maximum) =>
        new(clamp(a.x, minimum, maximum), clamp(a.y, minimum, maximum));

    /// <summary>Encloses saturate(x) for x in a. Monotone: no widening.</summary>
    public static float2 iv_saturate(float2 a) => new(saturate(a.x), saturate(a.y));

    /// <summary>
    /// Encloses smoothstep(minimum, maximum, x) for x in a. smoothstep is monotone in x (increasing when
    /// minimum &lt; maximum, decreasing when minimum &gt; maximum), so the bound is its value at the two
    /// endpoints, ordered. Its float32 evaluation t * t * (3 - 2t) is not monotone, by at most one ulp
    /// (IntervalTests.MeasureMonotonicity), so the bound is widened by one ulp inside [0, 1].
    /// </summary>
    public static float2 iv_smoothstep(float minimum, float maximum, float2 a)
    {
        var s0 = smoothstep(minimum, maximum, a.x);
        var s1 = smoothstep(minimum, maximum, a.y);
        var lo = min(s0, s1);
        var hi = max(s0, s1);
        return new(lo > 0.0f ? asfloat(asuint(lo) - 1u) : 0.0f, hi < 1.0f ? asfloat(asuint(hi) + 1u) : 1.0f);
    }

    /// <summary>
    /// Encloses lerp(x, y, amount) = x + (y - x) * amount for x in a, y in b, by evaluating that expression
    /// with iv_sub, iv_scale and iv_add. Each step is monotone, so the result encloses the float32 lerp
    /// with no widening, for any amount. It is the natural interval extension: x appears twice, so for
    /// non-point a the width is (width(a) + width(b)) * |amount| + width(a), wider than the true range.
    /// </summary>
    public static float2 iv_lerp(float2 a, float2 b, float amount) => iv_add(a, iv_scale(iv_sub(b, a), amount));

    /// <summary>
    /// Encloses snoise(x) for every x within distance radius of centre: [n - L r, n + L r] intersected
    /// with [-1, 1], where n = snoise(centre) and L = SNOISE_LIPSCHITZ. radius must be at least 0. One
    /// snoise evaluation, value only. A domain warp that moves x by at most D is enclosed by passing
    /// radius + D (NoiseBoundTests.WarpedPointsStayEnclosed).
    /// </summary>
    public static float2 iv_snoise_ball(float3 centre, float radius)
    {
        var n = snoise(centre);
        var e = SNOISE_LIPSCHITZ * radius;
        return new(max(n - e, -1.0f), min(n + e, 1.0f));
    }

    /// <summary>
    /// Encloses the fBm value, sum of a_i * snoise(x * f_i) over the octaves, for every x within distance
    /// radius of centre: the octave sum of iv_snoise_ball(centre * f_i, radius * |f_i|) scaled by a_i,
    /// with frequency f_i and amplitude a_i compounded exactly as the value-and-gradient fBm compounds
    /// them (its .w is what NoiseBoundTests.FbmBallEnclosesPoints encloses). octaves is clamped to [0, 16]
    /// as there.
    /// </summary>
    public static float2 iv_fbm_ball(float3 centre, float radius, int octaves, float lacunarity, float gain)
    {
        octaves = clamp(octaves, 0, 16);
        var amplitude = 1.0f;
        var frequency = 1.0f;
        var sum = new float2(0.0f, 0.0f);
        for (var i = 0; i < octaves; i++)
        {
            sum = iv_add(sum, iv_scale(iv_snoise_ball(centre * frequency, radius * abs(frequency)), amplitude));
            frequency *= lacunarity;
            amplitude *= gain;
        }

        return sum;
    }
}
