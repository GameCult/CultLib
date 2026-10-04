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
    public const float SNOISE_LIPSCHITZ = 7.9640074f;

    // Lipschitz constant of snoise(float2) in its input, with SNOISE_LIPSCHITZ's provenance rule:
    // NoiseBoundTests.MeasureLipschitz2 (slow, explicit) takes the largest gradient length (central
    // differences over exact float steps) over 1e6 seeded points, refines the 1e4 largest by ascent and
    // multiplies by 1.10; NoiseBoundTests.Lipschitz2ConstantPins pins it against a fast re-measure and
    // the ascent from MeasureLipschitz2's witness. A change to the 2D snoise kernel must re-run
    // MeasureLipschitz2 and re-pin this constant.
    public const float SNOISE2_LIPSCHITZ = 8.117002f;

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
    /// Encloses snoise(x) for every 2D x within distance radius of centre: [n - L2 r, n + L2 r] intersected
    /// with [-1, 1], n = snoise(centre), L2 = SNOISE2_LIPSCHITZ. radius must be at least 0. One snoise
    /// evaluation, value only. Like iv_snoise_ball(float3) it is exact for the point it is handed and
    /// carries no allowance for the float32 evaluation: a consumer whose 2D flow is evaluated at points
    /// it rounds itself (a shifted, scaled or projected point) adds 2^-14 + L2 2^-20 (|centre|_1 + radius)
    /// to the width, the convention af_snoise carries for the 3D point
    /// (NoiseBoundTests.FlowVariationCarriesItsAllowance, CentredWarpStaysEnclosed).
    /// </summary>
    public static float2 iv_snoise_ball(float2 centre, float radius)
    {
        var n = snoise(centre);
        var e = SNOISE2_LIPSCHITZ * radius;
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

    /// <summary>
    /// A ball (xyz centre, w radius) enclosing every point of every ray of a screen tile over the depth
    /// segment [z0, z1], each point then moved by a warp of length at most warp: the ball to hand
    /// iv_snoise_ball (scaled by the noise frequency) when one probe serves the whole tile. Camera frame:
    /// a pinhole at the origin looking down +z, where the ray of slope m is the points (m z, z).
    /// centreSlope is the tile's central slope m_c; footprintPerDepth is the largest distance of any
    /// ray's slope from m_c, N / (sqrt(2) f) for an N x N pixel tile at focal length f pixels (the full
    /// pixel footprint, so sub-pixel jitter is covered). The centre is (m_c z_m, z_m), z_m = (z0 + z1) / 2;
    /// the radius is ((z1 - z0) / 2) |(m_c, 1)| + z1 footprintPerDepth + warp. Derivation: a point of slope
    /// m_c + e (|e| &lt;= footprintPerDepth) at depth z is c + (z - z_m)(m_c, 1) + z (e, 0); the first term is
    /// at most the first radius term, the second at most the second, and the warp adds at most warp.
    /// The ball also carries its own float32 rounding: the radius is widened by (|c|_1 + radius) 2^-20.
    /// With u = 2^-24, each centre component carries at most two roundings (z_m, then the product), so the
    /// float centre is within 2u |c|_1 of the exact one; the radius carries at most seven, so the exact
    /// radius is at most (1 + 7u) times the float one; the widening rounds once more. 2^-20 = 16u covers
    /// 8u (|c|_1 + radius) twice over, and fma contraction only removes roundings. So every exact point of
    /// every ray, at any depth and slope, lies in the returned float ball
    /// (NoiseBoundTests.TileBallEnclosesEveryRaySegment, degenerate and far segments included). The caller
    /// rotates and translates the centre into world space (the radius is unchanged) and scales both by its
    /// noise frequency; the rounding of that transform is the caller's.
    /// </summary>
    public static float4 iv_frustum_ball(float2 centreSlope, float z0, float z1, float footprintPerDepth, float warp)
    {
        var zm = (z0 + z1) * 0.5f;
        var axis = sqrt(centreSlope.x * centreSlope.x + centreSlope.y * centreSlope.y + 1.0f);
        var cx = centreSlope.x * zm;
        var cy = centreSlope.y * zm;
        var radius = (z1 - z0) * 0.5f * axis + z1 * footprintPerDepth + warp;
        radius += (abs(cx) + abs(cy) + abs(zm) + radius) * 9.5367431640625e-7f;
        return new float4(cx, cy, zm, radius);
    }
}
