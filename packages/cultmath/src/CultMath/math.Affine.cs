namespace CultMath;

public static partial class math
{
    // Reduced affine forms with one shared symbol (design.md, "Affine forms"): float3(x0, a, e) is the
    // set { x0 + a eps + e delta : delta in [-1, 1] } at the region's shared eps in [-1, 1], with
    // e >= 0. Every form over one region shares that eps (for a screen-tile slice, the depth
    // parameter), and delta is private to the form. This is Messine's AF1 as Gamito and Maddock use it
    // for gradient noise (t0 + t1 e1 + t2 e2), in one register, as an interval is a float2.
    //
    // Every af_* op returns a form whose set, at each eps, contains f evaluated pointwise in float32 at
    // every point of its operands' sets at that same eps. Rounding rule: each op folds its own float32
    // rounding into e as (S) 2^-20, where S is the sum of the magnitudes the op's roundings are
    // relative to (named in each comment). With u = 2^-24, every op below needs at most 8u S (its
    // component roundings, the pointwise result's own rounding, and the roundings of computing e), so
    // 2^-20 = 16u covers it twice over; the factor of two is the allowance for a device whose + and *
    // are faithful (within one ulp) rather than correctly rounded, and fma contraction only removes
    // roundings. Products also add 2^-126, the most any underflow to a subnormal can lose. The
    // enclosure tests evaluate the form's set in double and f in float32 with no tolerance
    // (AffineTests.EveryOpEnclosesItsPoints). No op takes a compound type or a reference parameter: each is a
    // plain function of float values, so it mirrors into the HLSL common subset.

    // An upper bound on the spectral norm of the Hessian of snoise(float3). Provenance:
    // NoiseBoundTests.MeasureHessian (slow, explicit) takes the largest Hessian norm (central
    // differences of snoise_grad, largest absolute eigenvalue) over 1e6 seeded points, refines the 1e4
    // largest by ascent, and multiplies the refined maximum by 1.10;
    // NoiseBoundTests.HessianConstantPinsSampledCurvature pins this value against a fast re-measure and
    // the ascent from MeasureHessian's witness. Empirical with a margin, not a proof. Random balls
    // cannot see a constant 20% low, so the defence is those two curvature tests plus
    // NoiseBoundTests.SnoiseFormEnclosesTheCurvatureWitness, where af_snoise misses at the point the
    // curvature is realised if the constant is under it. The margin over the function: a scan of the
    // whole 289^3 hash period found a realised maximum of 50.58, and bounding each corner's gradient
    // independently over every hash configuration gives 52.22 (Soul pass s1). A change to the snoise
    // kernel must re-run MeasureHessian and re-pin it.
    public const float SNOISE_HESSIAN = 56.050385f;

    /// <summary>The point form (x, 0, 0): the value x at every eps. Exact.</summary>
    public static float3 af_point(float value) => new(value, 0.0f, 0.0f);

    /// <summary>
    /// The shared symbol itself, (x0, a, 0): the value x0 + a eps. It is how a region's parameter enters
    /// (for a tile slice, depth z = z_m + h eps), so it is exact by definition: eps is defined through it.
    /// </summary>
    public static float3 af_symbol(float x0, float a) => new(x0, a, 0.0f);

    /// <summary>
    /// Every value of the interval [lo, hi], uncorrelated with eps: (lo / 2 + hi / 2, 0, hi / 2 - lo / 2),
    /// the halves taken before the sum and the difference so that endpoints near the float maximum do not
    /// overflow in them. Rounding: x0 and e each round once, and a value of the interval is within e of x0
    /// only up to those; S = |x0| + e. Halving a subnormal endpoint loses at most 2^-150, and the sum and
    /// difference of subnormals are exact, so 2^-126 covers the underflow (the e of an interval spanning
    /// one subnormal step would otherwise round to 0). If e + its absorption exceeds the float maximum
    /// it is infinity, which still encloses.
    /// </summary>
    public static float3 af_from_iv(float2 interval)
    {
        var x0 = interval.x * 0.5f + interval.y * 0.5f;
        var e = interval.y * 0.5f - interval.x * 0.5f;
        return new float3(x0, 0.0f, e + (abs(x0) + e) * 9.5367431640625e-7f + 1.17549435e-38f);
    }

    /// <summary>
    /// The interval of every value of the form over every eps: x0 -+ (|a| + e), the bridge to iv_*. The
    /// endpoints round inward by at most u (|x0| + |a| + e) each, so each is moved outward by
    /// (|x0| + |a| + e) 2^-20. A form with an infinite component (an overflowed op) gives infinite
    /// endpoints, and an endpoint that would be NaN (infinity minus infinity, or a NaN component) is
    /// the infinity on its side, so the result is never NaN and still encloses.
    /// </summary>
    public static float2 af_range(float3 x)
    {
        var r = abs(x.y) + x.z;
        var w = (abs(x.x) + r) * 9.5367431640625e-7f;
        var lo = x.x - r - w;
        var hi = x.x + r + w;
        return new float2(lo == lo ? lo : float.NegativeInfinity, hi == hi ? hi : float.PositiveInfinity);
    }

    /// <summary>
    /// Encloses x + y: (x0 + y0, a_x + a_y, e_x + e_y). The shared symbol keeps the two forms
    /// correlated, so af_sub(x, x) has a = 0. Rounding: S = |x0| + |a| + e of the result.
    /// </summary>
    public static float3 af_add(float3 x, float3 y)
    {
        var x0 = x.x + y.x;
        var a = x.y + y.y;
        var e = x.z + y.z;
        return new float3(x0, a, e + (abs(x0) + abs(a) + e) * 9.5367431640625e-7f);
    }

    /// <summary>Encloses x - y: (x0 - y0, a_x - a_y, e_x + e_y). Rounding as af_add.</summary>
    public static float3 af_sub(float3 x, float3 y)
    {
        var x0 = x.x - y.x;
        var a = x.y - y.y;
        var e = x.z + y.z;
        return new float3(x0, a, e + (abs(x0) + abs(a) + e) * 9.5367431640625e-7f);
    }

    /// <summary>Encloses -x: (-x0, -a, e). Exact.</summary>
    public static float3 af_neg(float3 x) => new(-x.x, -x.y, x.z);

    /// <summary>
    /// Encloses x * s: (x0 s, a s, e |s|). Rounding: S = |x0| + |a| + e of the result, plus 2^-126
    /// for underflow.
    /// </summary>
    public static float3 af_scale(float3 x, float s)
    {
        var x0 = x.x * s;
        var a = x.y * s;
        var e = x.z * abs(s);
        return new float3(x0, a, e + (abs(x0) + abs(a) + e) * 9.5367431640625e-7f + 1.17549435e-38f);
    }

    /// <summary>Encloses x + v for v in the interval, uncorrelated with eps: af_add(x, af_from_iv(interval)).</summary>
    public static float3 af_add_iv(float3 x, float2 interval) => af_add(x, af_from_iv(interval));

    /// <summary>
    /// Encloses x * y, AF1's product: (x0 y0, x0 a_y + y0 a_x, |x0| e_y + |y0| e_x + (|a_x| + e_x)(|a_y| + e_y)).
    /// The last term condenses the eps^2 term a_x a_y and every cross term with an error symbol into e,
    /// so a form multiplied by itself is wider than its square: there is no af_sqr until a consumer
    /// names one. Rounding: a can cancel, so S = |x0| + |x0 a_y| + |y0 a_x| + e, plus 2^-126 for
    /// underflow.
    /// </summary>
    public static float3 af_mul(float3 x, float3 y)
    {
        var x0 = x.x * y.x;
        var p = x.x * y.y;
        var q = y.x * x.y;
        var a = p + q;
        var e = abs(x.x) * y.z + abs(y.x) * x.z + (abs(x.y) + x.z) * (abs(y.y) + y.z);
        return new float3(x0, a, e + (abs(x0) + abs(p) + abs(q) + e) * 9.5367431640625e-7f + 1.17549435e-38f);
    }

    /// <summary>
    /// Encloses snoise(p) for every p = centre + axis eps + w with |w| &lt;= radius, from one snoise_grad at
    /// the centre (value n, gradient g). With R = |axis| + radius it returns the narrower of two sound
    /// forms: the Lipschitz form (n, 0, L R), L = SNOISE_LIPSCHITZ, and the centred form
    /// (n, g . axis, |g| radius + M R^2 / 2), M = SNOISE_HESSIAN. The centred form is Taylor's theorem:
    /// snoise(c + d) = n + g . d + r with |r| &lt;= M |d|^2 / 2, d = axis eps + w, g . w &lt;= |g| radius.
    /// radius must be at least 0.
    ///
    /// Both forms carry an allowance for the float32 evaluation of snoise, which is not the exact
    /// function: 2^-14 + 2^-12 R + L 2^-20 (|centre|_1 + R). The skew and the corner offsets round the
    /// input point by at most about 2u |p|_1 before any kernel term is formed (the hashing is exact on
    /// integers below 2^24), so a float32 snoise of p is the exact one at a point within 2u |p|_1, which
    /// moves the value by at most L 2u |p|_1; for both p and the centre, with the caller's scaling of the
    /// point by a frequency (one rounding) and a factor of two for a GPU, 16u = 2^-20. The kernel's
    /// absolute rounding is at most 105 x four corners x terms of magnitude below 0.5^4 x 1.8 over about
    /// 30 operations, under 2^-15, doubled to 2^-14; the gradient's error times |d| &lt;= R is under 2^-13 R,
    /// doubled to 2^-12 R. AffineTests.SnoiseFormEnclosesBall checks the result with no tolerance on the
    /// CPU, and the fixture's enclosure check measures it on a GPU. Rounding of the form itself: S =
    /// |n| + |a| + e. The Lipschitz form is chosen when its half-width L R is at most the centred form's
    /// |a| + |g| radius + M R^2 / 2, so the total width is never more than the Lipschitz form's.
    /// </summary>
    public static float3 af_snoise(float3 centre, float3 axis, float radius)
    {
        var g = snoise_grad(centre);
        var gradient = new float3(g.x, g.y, g.z);
        var reach = length(axis) + radius;
        var lipschitz = SNOISE_LIPSCHITZ * reach;
        var a = dot(gradient, axis);
        var remainder = length(gradient) * radius + 0.5f * SNOISE_HESSIAN * reach * reach;
        var allowance = 6.103515625e-5f + 2.44140625e-4f * reach
            + SNOISE_LIPSCHITZ * 9.5367431640625e-7f * (abs(centre.x) + abs(centre.y) + abs(centre.z) + reach);
        var centred = abs(a) + remainder < lipschitz;
        var slope = centred ? a : 0.0f;
        var e = (centred ? remainder : lipschitz) + allowance;
        return new float3(g.w, slope, e + (abs(g.w) + abs(slope) + e) * 9.5367431640625e-7f);
    }

    /// <summary>
    /// Encloses the fBm value, sum of a_i snoise(p f_i) over the octaves, for every p = centre + axis eps
    /// + w with |w| &lt;= radius: the octave sum of af_snoise(centre f_i, axis f_i, radius |f_i|) scaled by
    /// a_i, with frequency f_i and amplitude a_i compounded exactly as fbm_grad compounds them (its .w is
    /// what AffineTests.SnoiseFormEnclosesBall encloses). The rounding of p f_i, inside fbm_grad, and of
    /// the scaled centre and axis here, moves each point by at most 3u (|centre f_i|_1 + R f_i), inside
    /// af_snoise's allowance for its input point. octaves is clamped to [0, 16] as there.
    /// </summary>
    public static float3 af_fbm(float3 centre, float3 axis, float radius, int octaves, float lacunarity, float gain)
    {
        octaves = clamp(octaves, 0, 16);
        var amplitude = 1.0f;
        var frequency = 1.0f;
        var sum = new float3(0.0f, 0.0f, 0.0f);
        for (var i = 0; i < octaves; i++)
        {
            sum = af_add(sum, af_scale(af_snoise(centre * frequency, axis * frequency, radius * abs(frequency)), amplitude));
            frequency *= lacunarity;
            amplitude *= gain;
        }

        return sum;
    }

    /// <summary>
    /// The axis of a screen tile's slice over the depth segment [z0, z1], for af_frustum_ball's centre:
    /// (m_c, 1) h with h = (z1 - z0) / 2, in the camera frame of iv_frustum_ball (a pinhole at the
    /// origin looking down +z, the ray of slope m the points (m z, z)). Its z component is h itself, the
    /// symbol's coefficient for depth: z = z_m + h eps. Its rounding is af_frustum_ball's.
    /// </summary>
    public static float3 af_frustum_axis(float2 centreSlope, float z0, float z1)
    {
        var h = (z1 - z0) * 0.5f;
        return new float3(centreSlope.x * h, centreSlope.y * h, h);
    }

    /// <summary>
    /// The affine slice of a screen tile over [z0, z1]: a centre (xyz) and radius (w) such that every point
    /// of every ray of the tile at depth z in [z0, z1] is centre + axis eps(z) + w', |w'| &lt;= radius, with
    /// axis = af_frustum_axis(centreSlope, z0, z1), z_m = (z0 + z1) / 2, h = (z1 - z0) / 2 (both float32,
    /// as computed here) and eps(z) = clamp((z - z_m) / h, -1, 1) (0 where h is 0). The centre is
    /// (m_c z_m, z_m) and the radius z1 footprintPerDepth + warpVariation, where footprintPerDepth is
    /// iv_frustum_ball's and warpVariation is what the caller's warp can add once its value at the centre
    /// is applied to the centre (design.md, "Affine forms", the centred warp). Derivation: a point of
    /// slope m_c + e (|e| &lt;= footprintPerDepth) at depth z is (m_c z_m, z_m) + (m_c, 1)(z - z_m) +
    /// (e z, 0), and (m_c, 1)(z - z_m) = axis eps(z) + (m_c, 1) r(z), where the clamp residual
    /// |r(z)| = |z - z_m - h eps(z)| is at most u (|z_m| + h), one rounding each of z_m and h. A caller
    /// taking depth as af_symbol(z_m, h) adds the same residual, 2^-23 (|z_m| + h) covers it, to that
    /// form's e. Rounding: the centre's products and the axis's products round once each (u |c|_1 and
    /// u |axis|_1 against |eps| &lt;= 1), the residual is at most u (|c|_1 + |axis|_1) since
    /// |(m_c, 1)| &lt;= |m_c|_1 + 1, and the radius rounds at most four times; 2^-20 (|c|_1 + |axis|_1 +
    /// radius) covers it twice over (AffineTests.FrustumSliceEnclosesEveryRayPoint, extreme families
    /// included). The caller rotates and translates the centre into world space, rotates the axis, and
    /// scales all three by its noise frequency; the rounding of that transform is the caller's.
    /// </summary>
    public static float4 af_frustum_ball(float2 centreSlope, float z0, float z1, float footprintPerDepth, float warpVariation)
    {
        var zm = (z0 + z1) * 0.5f;
        var h = (z1 - z0) * 0.5f;
        var cx = centreSlope.x * zm;
        var cy = centreSlope.y * zm;
        var spread = (abs(centreSlope.x) + abs(centreSlope.y) + 1.0f) * h;
        var radius = z1 * footprintPerDepth + warpVariation;
        radius += (abs(cx) + abs(cy) + abs(zm) + spread + radius) * 9.5367431640625e-7f;
        return new float4(cx, cy, zm, radius);
    }
}
