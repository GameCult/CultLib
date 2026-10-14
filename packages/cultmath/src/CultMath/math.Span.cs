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
    // clamps to hi everywhere, as clamp does. A crossing that overflows (b tiny) lies past the span and
    // cuts nothing.
    public static float integral_clamp_linear(float a, float b, float lo, float hi, float t0, float t1)
    {
        if (b == 0.0f)
            return clamp(a, lo, hi) * (t1 - t0);

        lo = min(lo, hi);
        var direction = t1 < t0 ? -1.0f : 1.0f;
        var start = min(t0, t1);
        var stop = max(t0, t1);
        var atLo = (lo - a) / b;
        var atHi = (hi - a) / b;
        var enter = clamp(min(atLo, atHi), start, stop);
        var leave = clamp(max(atLo, atHi), start, stop);
        var before = b > 0.0f ? lo : hi;
        var after = b > 0.0f ? hi : lo;
        var total = before * (enter - start) + (a + b * (0.5f * (enter + leave))) * (leave - enter) + after * (stop - leave);
        return direction * total;
    }
}
