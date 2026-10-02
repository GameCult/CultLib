#ifndef CULTMATH_INTERVAL_HLSL
#define CULTMATH_INTERVAL_HLSL

// Interval arithmetic over float2(lo, hi), lo <= hi, both finite. Every cultmath_iv_* function returns
// an interval containing f(x) for every x in its input intervals, mirroring math.Interval.cs bit for
// bit; see that file and docs/design.md ("Intervals") for each op's enclosure and its ulp widening.
// Included by CultMath.hlsl after its own definitions of cultmath_smoothstep and cultmath_snoise.

// Lipschitz constant of cultmath_snoise(float3); provenance in math.Interval.cs (SNOISE_LIPSCHITZ).
static const float CULTMATH_SNOISE_LIPSCHITZ = 10.099261;

float2 cultmath_iv_point(float value) { return float2(value, value); }
float2 cultmath_iv_add(float2 a, float2 b) { return float2(a.x + b.x, a.y + b.y); }
float2 cultmath_iv_sub(float2 a, float2 b) { return float2(a.x - b.y, a.y - b.x); }
float2 cultmath_iv_neg(float2 a) { return float2(-a.y, -a.x); }

float2 cultmath_iv_mul(float2 a, float2 b)
{
    float p0 = a.x * b.x;
    float p1 = a.x * b.y;
    float p2 = a.y * b.x;
    float p3 = a.y * b.y;
    return float2(min(min(p0, p1), min(p2, p3)), max(max(p0, p1), max(p2, p3)));
}

float2 cultmath_iv_scale(float2 a, float s) { return s >= 0.0 ? float2(a.x * s, a.y * s) : float2(a.y * s, a.x * s); }

float2 cultmath_iv_abs(float2 a)
{
    if (a.x >= 0.0)
        return a;
    if (a.y <= 0.0)
        return float2(-a.y, -a.x);
    return float2(0.0, max(-a.x, a.y));
}

float2 cultmath_iv_min(float2 a, float2 b) { return float2(min(a.x, b.x), min(a.y, b.y)); }
float2 cultmath_iv_max(float2 a, float2 b) { return float2(max(a.x, b.x), max(a.y, b.y)); }

float2 cultmath_iv_sqr(float2 a)
{
    float l = a.x * a.x;
    float h = a.y * a.y;
    if (a.x >= 0.0)
        return float2(l, h);
    if (a.y <= 0.0)
        return float2(h, l);
    return float2(0.0, max(l, h));
}

float2 cultmath_iv_sqrt(float2 a) { return float2(sqrt(max(a.x, 0.0)), sqrt(max(a.y, 0.0))); }

// Widened by one ulp on each side: exp is not required to be correctly rounded.
float2 cultmath_iv_exp(float2 a)
{
    float lo = exp(a.x);
    float hi = exp(a.y);
    return float2(lo > 0.0 ? asfloat(asuint(lo) - 1u) : 0.0, asfloat(asuint(hi) + 1u));
}

float2 cultmath_iv_clamp(float2 a, float minimum, float maximum)
{
    return float2(cultmath_clamp(a.x, minimum, maximum), cultmath_clamp(a.y, minimum, maximum));
}

float2 cultmath_iv_saturate(float2 a) { return float2(cultmath_saturate(a.x), cultmath_saturate(a.y)); }

// The endpoints, ordered, widened by one ulp inside [0, 1]: the float32 evaluation of smoothstep is
// monotone only to within one ulp.
float2 cultmath_iv_smoothstep(float minimum, float maximum, float2 a)
{
    float s0 = cultmath_smoothstep(minimum, maximum, a.x);
    float s1 = cultmath_smoothstep(minimum, maximum, a.y);
    float lo = min(s0, s1);
    float hi = max(s0, s1);
    return float2(lo > 0.0 ? asfloat(asuint(lo) - 1u) : 0.0, hi < 1.0 ? asfloat(asuint(hi) + 1u) : 1.0);
}

// The natural interval extension of x + (y - x) * amount, which is how cultmath_lerp evaluates.
float2 cultmath_iv_lerp(float2 a, float2 b, float amount) { return cultmath_iv_add(a, cultmath_iv_scale(cultmath_iv_sub(b, a), amount)); }

// [n - L r, n + L r] intersected with [-1, 1], n = cultmath_snoise(centre). One snoise, value only.
float2 cultmath_iv_snoise_ball(float3 centre, float radius)
{
    float n = cultmath_snoise(centre);
    float e = CULTMATH_SNOISE_LIPSCHITZ * radius;
    return float2(max(n - e, -1.0), min(n + e, 1.0));
}

// Octave sum of cultmath_iv_snoise_ball, compounding frequency and amplitude as cultmath_fbm_grad does.
float2 cultmath_iv_fbm_ball(float3 centre, float radius, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    float2 sum = float2(0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        sum = cultmath_iv_add(sum, cultmath_iv_scale(cultmath_iv_snoise_ball(centre * frequency, radius * abs(frequency)), amplitude));
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return sum;
}

#endif
