namespace CultMath;

public static partial class math
{
    // An upper bound on the operator norm of the third-derivative tensor of snoise(float3):
    // max over unit u of |D^3 snoise(p)[u, u, u]|, the next order after SNOISE_LIPSCHITZ (gradient)
    // and SNOISE_HESSIAN (curvature). Provenance: NoiseBoundTests.MeasureThird (slow, explicit) takes
    // the largest such norm over 1e6 seeded points of [-256, 256]^3 of the closed-form tensor (snoise_hessian's
    // per-corner derivation differentiated once more, in double, with the kernel's lattice, tie order and
    // hash; NoiseBoundTests.ThirdTensorMatchesDifferencesOfTheHessian pins it against differences of the
    // double Hessian), then an ascent over unit vectors for the tensor's operator norm, refines the 1e4
    // largest by ascent over the point, and multiplies the maximum by 1.10; it prints the witness start.
    // NoiseBoundTests.ThirdConstantPinsSampled pins the value against a fast re-measure and the ascent
    // from that witness. Measured on the tied kernel (snoise_grad's order), not on any pre-tie splice:
    // refined maximum 487.0917, a dense sweep of 20,000 directions at the witness's ascent end found
    // 99.9987% of the tensor norm there. The inner maximisation is a local ascent and the outer a sampled
    // search refined by ascent: empirical with a margin, not a proof. A change to the snoise kernel must
    // re-run MeasureThird and re-pin it.
    public const float SNOISE_THIRD = 535.8009f;

    // Analytic Hessian of snoise(float3): the symmetric matrix of second partial derivatives, same
    // lattice, hash and tie order as snoise_grad, whose kernel it differentiates once more. Every local
    // up through p0..p3 and x0..x3 is snoise_grad's own derivation verbatim. Per corner, with
    // m = max(0.5 - dot(x,x), 0) and s = dot(p,x), the corner's gradient is -8 m^3 s x + m^4 p, so
    // d/dx_j of its i-th component is 48 m^2 s x_i x_j - 8 m^3 (p_j x_i + p_i x_j + s delta_ij) (the
    // derivative of m^3 is -6 m^2 x_j, of s is p_j); summed over the four corners and scaled by the
    // same 105. A clamped corner (m = 0) contributes nothing, and the m^2 and m^3 factors make the
    // contribution vanish continuously as it clamps. Each off-diagonal entry is computed once and
    // placed twice, so the matrix is its own transpose bit for bit. A float3x3 is built from its nine
    // scalars, which the GLSL lowering reads as columns and HLSL as rows: the matrix is symmetric.
    public static float3x3 snoise_hessian(float3 value)
    {
        const float cx = 1.0f / 6.0f;
        const float cy = 1.0f / 3.0f;
        var i = floor(value + dot(value, new float3(cy, cy, cy)));
        var x0 = value - i + dot(i, new float3(cx, cx, cx));
        var g = new float3(step(x0.y, x0.x), step(x0.z, x0.y), 1.0f - step(x0.z, x0.x));
        var l = 1.0f - g;
        var lzxy = new float3(l.z, l.x, l.y);
        var i1 = min(g, lzxy);
        var i2 = max(g, lzxy);
        var x1 = x0 - i1 + cx;
        var x2 = x0 - i2 + cy;
        var x3 = x0 - 0.5f;

        i = snoise_mod289(i);
        var p = snoise_permute(snoise_permute(snoise_permute(
            i.z + new float4(0.0f, i1.z, i2.z, 1.0f)) + i.y + new float4(0.0f, i1.y, i2.y, 1.0f)) + i.x + new float4(0.0f, i1.x, i2.x, 1.0f));

        const float n = 0.142857142857f;
        var ns = new float3(2.0f * n, 0.5f * n - 1.0f, n);
        var j = p - 49.0f * floor(p * ns.z * ns.z);
        var xs = floor(j * ns.z);
        var ys = floor(j - 7.0f * xs);
        var x = xs * ns.x + ns.y;
        var y = ys * ns.x + ns.y;
        var h = 1.0f - abs(x) - abs(y);

        var b0 = new float4(x.x, x.y, y.x, y.y);
        var b1 = new float4(x.z, x.w, y.z, y.w);
        var s0 = floor(b0) * 2.0f + 1.0f;
        var s1 = floor(b1) * 2.0f + 1.0f;
        var sh = -step(h, 0.0f);
        var a0 = new float4(b0.x, b0.z, b0.y, b0.w) + new float4(s0.x, s0.z, s0.y, s0.w) * new float4(sh.x, sh.x, sh.y, sh.y);
        var a1 = new float4(b1.x, b1.z, b1.y, b1.w) + new float4(s1.x, s1.z, s1.y, s1.w) * new float4(sh.z, sh.z, sh.w, sh.w);

        var p0 = new float3(a0.x, a0.y, h.x);
        var p1 = new float3(a0.z, a0.w, h.y);
        var p2 = new float3(a1.x, a1.y, h.z);
        var p3 = new float3(a1.z, a1.w, h.w);
        var norm = 1.79284291400159f - 0.85373472095314f * new float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
        p0 *= norm.x;
        p1 *= norm.y;
        p2 *= norm.z;
        p3 *= norm.w;

        var m0 = max(0.5f - new float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0f);
        var m2 = m0 * m0;
        var m3 = m2 * m0;
        var px = new float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3));
        var wide = 48.0f * m2 * px;
        var narrow = 8.0f * m3;

        // One component of every corner's x and p per register.
        var cxx = new float4(x0.x, x1.x, x2.x, x3.x);
        var cxy = new float4(x0.y, x1.y, x2.y, x3.y);
        var cxz = new float4(x0.z, x1.z, x2.z, x3.z);
        var cpx = new float4(p0.x, p1.x, p2.x, p3.x);
        var cpy = new float4(p0.y, p1.y, p2.y, p3.y);
        var cpz = new float4(p0.z, p1.z, p2.z, p3.z);

        var hxx = 105.0f * (dot(wide * cxx, cxx) - dot(narrow, 2.0f * cpx * cxx + px));
        var hyy = 105.0f * (dot(wide * cxy, cxy) - dot(narrow, 2.0f * cpy * cxy + px));
        var hzz = 105.0f * (dot(wide * cxz, cxz) - dot(narrow, 2.0f * cpz * cxz + px));
        var hxy = 105.0f * (dot(wide * cxx, cxy) - dot(narrow, cpx * cxy + cpy * cxx));
        var hxz = 105.0f * (dot(wide * cxx, cxz) - dot(narrow, cpx * cxz + cpz * cxx));
        var hyz = 105.0f * (dot(wide * cxy, cxz) - dot(narrow, cpy * cxz + cpz * cxy));
        return new float3x3(hxx, hxy, hxz, hxy, hyy, hyz, hxz, hyz, hzz);
    }

    // The integral over [t0, t1] of clamp(a + b t, lo, hi) dt, in closed form. The line meets lo at
    // (lo - a) / b and hi at (hi - a) / b; the span is cut at those two crossings into the stretch held
    // at the bound the line starts beyond, the stretch on the ramp (a trapezoid: its length times the
    // line's value at the stretch's midpoint), and the stretch held at the far bound. b = 0 is the
    // constant clamp(a, lo, hi); t1 < t0 is the negative of the integral over [t1, t0]; lo above hi
    // clamps to hi everywhere, as clamp does, and is normalised (lo = min(lo, hi)) before anything reads it,
    // so no clamp ever sees lo above hi (GLSL leaves that undefined). A crossing that overflows (b tiny) lies past the span and
    // cuts nothing. A held stretch contributes only when its length is positive, so an infinite bound
    // (relu has hi = +inf) times a zero length never forms: the integral of a ramp clamped on one side
    // only is finite.
    public static float integral_clamp_linear(float a, float b, float lo, float hi, float t0, float t1)
    {
        lo = min(lo, hi);
        if (b == 0.0f)
            return t1 != t0 ? clamp(a, lo, hi) * (t1 - t0) : 0.0f;

        var direction = t1 < t0 ? -1.0f : 1.0f;
        var start = min(t0, t1);
        var stop = max(t0, t1);
        var atLo = (lo - a) / b;
        var atHi = (hi - a) / b;
        var enter = clamp(min(atLo, atHi), start, stop);
        var leave = clamp(max(atLo, atHi), start, stop);
        var before = b > 0.0f ? lo : hi;
        var after = b > 0.0f ? hi : lo;
        var held = enter > start ? before * (enter - start) : 0.0f;
        var kept = stop > leave ? after * (stop - leave) : 0.0f;
        var total = held + (a + b * (0.5f * (enter + leave))) * (leave - enter) + kept;
        return direction * total;
    }

    private static float span_frobenius(float3x3 m) =>
        sqrt(m._m00 * m._m00 + m._m01 * m._m01 + m._m02 * m._m02 + m._m10 * m._m10 + m._m11 * m._m11 + m._m12 * m._m12
            + m._m20 * m._m20 + m._m21 * m._m21 + m._m22 * m._m22);

    // Plain fbm of snoise over a span: the octave stage of a warp chain, its value and gradient at the
    // span's midpoint and the bounds that hold over the whole span. See span_stage for the span, the
    // transform and the bound rules. At SpanStage.Identity the field equals fbm_grad(path.midpoint,
    // octaves, lacunarity, gain) bit for bit.
    public static SpanNoise span_fbm(SpanPath path, SpanStage stage, int octaves, float lacunarity, float gain) =>
        span_stage(false, path, stage, octaves, lacunarity, gain);

    // Ridged fbm over a span: octave o contributes gain^o (1 - |n_o|), ridged_grad's convention. fold
    // bounds how far the sum can leave the branch it is on at the midpoint. At SpanStage.Identity the field
    // equals ridged_grad bit for bit.
    //
    // The Span Lab's stage is 1 - 2|n| per octave. With G the sum of gain^o over the octaves (signed: a
    // negative gain gives its alternating sum), the lab's value is 2 value - G, and its gradient,
    // curvature and fold are twice this one's. A consumer in the lab form applies that mapping and does
    // not derive its own bounds.
    public static SpanNoise span_ridged(SpanPath path, SpanStage stage, int octaves, float lacunarity, float gain) =>
        span_stage(true, path, stage, octaves, lacunarity, gain);

    // 2^-20 S_o: the rounding one octave's read of the noise carries, S_o being the sum of the magnitudes the
    // octave's coordinate is formed from and the span's reach (the allowance paragraph above). The single
    // owner of S_o; span_slack turns it into the three allowances.
    internal static float span_rounding(float scale, float3 mq, SpanStage stage, float3 step, float nu, float halfLength) =>
        9.5367431640625e-7f * (abs(scale) * (csum(abs(mq)) + csum(abs(stage.shift))) + csum(abs(stage.offset)) + csum(abs(step)) + 1.7320508f * nu * halfLength);

    // The allowances (e_n, e_g, e_h) of the value, the gradient's length and the Hessian's Frobenius norm at
    // a rounding of 2^-20 S_o: the absolute term of each plus its derivative's pin times the rounding. The
    // single owner of the three allowances of a span; af_snoise (math.Affine.cs) forms its own allowance
    // from the same SNOISE_READ_VALUE and SNOISE_READ_GRADIENT, the owners of the first two absolute terms,
    // so widening either moves every reader of the kernel at once.
    internal static float3 span_slack(float rounding) => new(
        SNOISE_READ_VALUE + SNOISE_LIPSCHITZ * rounding,
        SNOISE_READ_GRADIENT + SNOISE_HESSIAN * rounding,
        9.765625e-4f + SNOISE_THIRD * rounding);

    // The span is SpanPath: q(t), |t| <= halfLength, through midpoint with velocity at t = 0, |q'| <= speed
    // and |q''| <= acceleration along it (speed at least |velocity|; all three and halfLength non-negative).
    // The transform is SpanStage: octave o samples snoise_grad at x_o(q) = f_o (M q + shift) + offset +
    // o octaveStep with f_o = lacunarity^o, amplitude gain^o and M = frequency (I + (1/stretch - 1) a a^T),
    // a = normalize(axis); a zero axis, or stretch 1, means no stretch. stretch must be positive and
    // finite, and nothing guards it, as nothing guards lacunarity. octaves clamps to [0, 16] as fbm_grad's
    // does. The field is the octave sum in q: gradient f_o M grad n_o (M is symmetric), the same octave
    // loop as fbm_grad and ridged_grad.
    //
    // Bounds, per octave, from SNOISE_LIPSCHITZ (L), SNOISE_HESSIAN (H) and SNOISE_THIRD (T) only. With
    // |M| = |frequency| max(1, 1/stretch) (1 without stretch) and nu = f_o |M| speed, the noise-space speed:
    //  * |x(t) - x_mid| <= nu halfLength, so the Hessian of n at x(t) has operator norm at most
    //    Hl = min(H, ||hessian(x_mid)||_F + e_h + T nu halfLength) (the Hessian is T-Lipschitz in operator
    //    norm, and the Frobenius norm bounds the operator norm), and |grad n(x(t))| at most
    //    Gl = min(L, |grad n_mid| + e_g + Hl nu halfLength).
    //  * curvature = sum gain^o f_o^2 |M|^2 Hl: the Hessian in q of n(x_o(q)) is f_o^2 M H M.
    //  * fold, ridged only. Over the span the octave's value departs from its midpoint branch
    //    gain^o (1 - s n) (s the sign of n at the midpoint) only where n has changed sign, and there
    //    |n(t)| <= V - |n_mid|, V bounding |n(t) - n_mid|. Writing n(t) - n_mid as the integral of
    //    grad n . x', x' = w + rho with w = f_o M velocity and |rho| <= f_o |M| acceleration |t|, and
    //    grad n = grad n_mid + delta with |delta| <= Hl nu |t|:
    //    V = (|grad n_mid . w| + e_g |w|) halfLength + (Hl nu |w| + Gl f_o |M| acceleration) halfLength^2 / 2
    //        + 2 e_n,
    //    and the octave's fold is 2 gain^o max(V - |n_mid|, 0), zero when it cannot reach zero.
    //
    // The float32 snoise, snoise_grad and snoise_hessian are not the exact functions, and the field a
    // consumer reads is the float one, so what is read at the midpoint carries an allowance, by the
    // derivation af_snoise's comment gives (math.Affine.cs): the skew and the corner offsets round the
    // coordinate by about 2u of the magnitudes it is formed from, which moves a result by its derivative
    // times that, and the caller's scaling of the point and a GPU's evaluation take 2u to 16u = 2^-20.
    // S_o is the sum of those magnitudes, |f_o| (|M q|_1 + |shift|_1) + |offset|_1 + |o octaveStep|_1, plus
    // the span's reach sqrt(3) nu halfLength (the coordinate of a path point); span_rounding is its owner. The
    // allowances are the value's e_n = 2^-14 + L 2^-20 S_o, the gradient length's e_g = 2^-12 + H 2^-20 S_o
    // (H is the gradient's own derivative's pin) and the Hessian's Frobenius norm's e_h = 2^-10 + T 2^-20 S_o
    // (T is the Hessian's derivative's pin): SNOISE_READ_VALUE and SNOISE_READ_GRADIENT (math.Affine.cs) are the
    // absolute terms of the first two, 2^-10 the Hessian's; span_slack is the owner of the three. They are observed where they are decided: the coordinate-band
    // snoise_grad and snoise_hessian families of glsl-parity.json and SpanAllowanceCoversReadsAlongTheSpan
    // check every read against them. Those observations are samples, not bounds: a new seed moves them. What
    // they say is that the allowances enclose the error with a margin of about 15.
    // No new pin enters.
    //
    // Domain: no coordinate range is checked, clamped or refused. The allowances grow with S_o, so
    // curvature saturates at H (true everywhere) by S_o of about 1.1e5, and fold turns positive for every
    // octave whose V + 2 e_n reaches |n_mid|: the bounds stay sound at every magnitude and stop being
    // informative where float32 noise is (design.md, Precision domain).
    private static SpanNoise span_stage(bool ridged, SpanPath path, SpanStage stage, int octaves, float lacunarity, float gain)
    {
        var q = path.midpoint;
        var halfLength = path.halfLength;
        octaves = clamp(octaves, 0, 16);
        var stretched = dot(stage.axis, stage.axis) > 0.0f && stage.stretch != 1.0f;
        var direction = stretched ? normalize(stage.axis) : new float3(0.0f, 0.0f, 0.0f);
        var squeeze = stretched ? 1.0f / stage.stretch - 1.0f : 0.0f;
        var norm = abs(stage.frequency) * (stretched ? max(1.0f, 1.0f / stage.stretch) : 1.0f);
        var mq = stretched ? stage.frequency * (q + squeeze * dot(direction, q) * direction) : stage.frequency * q;
        var mv = stretched ? stage.frequency * (path.velocity + squeeze * dot(direction, path.velocity) * direction) : stage.frequency * path.velocity;
        var amplitude = 1.0f;
        var scale = 1.0f;
        var value = 0.0f;
        var gradient = new float3(0.0f, 0.0f, 0.0f);
        var curvature = 0.0f;
        var fold = 0.0f;
        for (var i = 0; i < octaves; i++)
        {
            var step = (float)i * stage.octaveStep;
            var x = scale * (mq + stage.shift) + (stage.offset + step);
            var n = snoise_grad(x);
            var g = new float3(n.x, n.y, n.z);
            var mg = stretched ? stage.frequency * (g + squeeze * dot(direction, g) * direction) : stage.frequency * g;
            if (ridged)
            {
                var s = (float)sign(n.w);
                value += amplitude * (1.0f - abs(n.w));
                gradient += -amplitude * scale * s * mg;
            }
            else
            {
                value += amplitude * n.w;
                gradient += amplitude * scale * mg;
            }

            var weight = abs(amplitude);
            var factor = abs(scale) * norm;
            var nu = factor * path.speed;
            var rounding = span_rounding(scale, mq, stage, step, nu, halfLength);
            var slack = span_slack(rounding);
            var valueSlack = slack.x;
            var gradientSlack = slack.y;
            var hessianSlack = slack.z;
            var local = min(SNOISE_HESSIAN, span_frobenius(snoise_hessian(x)) + hessianSlack + SNOISE_THIRD * nu * halfLength);
            curvature += weight * factor * factor * local;
            if (ridged)
            {
                var w = scale * mv;
                var reach = local * nu * halfLength;
                var gl = min(SNOISE_LIPSCHITZ, length(g) + gradientSlack + reach);
                var travel = (abs(dot(g, w)) + gradientSlack * length(w)) * halfLength + (local * nu * length(w) + gl * factor * path.acceleration) * (0.5f * halfLength * halfLength) + 2.0f * valueSlack;
                fold += 2.0f * weight * max(travel - abs(n.w), 0.0f);
            }

            scale *= lacunarity;
            amplitude *= gain;
        }

        return new SpanNoise(new float4(gradient, value), curvature, fold);
    }
}
