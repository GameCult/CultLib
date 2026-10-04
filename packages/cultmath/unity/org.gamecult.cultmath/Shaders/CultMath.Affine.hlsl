#ifndef CULTMATH_AFFINE_HLSL
#define CULTMATH_AFFINE_HLSL

// Reduced affine forms with one shared symbol over float3(x0, a, e): the set { x0 + a eps + e delta :
// delta in [-1, 1] } at the region's shared eps in [-1, 1], e >= 0. Every cultmath_af_* function mirrors
// math.Affine.cs bit for bit; see that file and docs/design.md ("Affine forms") for each op's enclosure
// and the float32 rounding allowance it folds into e. Included by CultMath.hlsl after
// CultMath.Interval.hlsl, whose CULTMATH_SNOISE_LIPSCHITZ it shares, and after its own
// definition of cultmath_snoise_grad.

// An upper bound on the spectral norm of the Hessian of cultmath_snoise(float3); provenance in
// math.Affine.cs (SNOISE_HESSIAN).
static const float CULTMATH_SNOISE_HESSIAN = 56.050385;

float3 cultmath_af_point(float value) { return float3(value, 0.0, 0.0); }
float3 cultmath_af_symbol(float x0, float a) { return float3(x0, a, 0.0); }

float3 cultmath_af_from_iv(float2 interval)
{
    float x0 = interval.x * 0.5 + interval.y * 0.5;
    float e = interval.y * 0.5 - interval.x * 0.5;
    return float3(x0, 0.0, e + (abs(x0) + e) * 9.5367431640625e-7 + 1.17549435e-38);
}

// An endpoint that would be NaN is the infinity on its side.
float2 cultmath_af_range(float3 x)
{
    float r = abs(x.y) + x.z;
    float w = abs(x.x) * 9.5367431640625e-7 + r * 9.5367431640625e-7;
    float lo = x.x - r - w;
    float hi = x.x + r + w;
    return float2(lo == lo ? lo : asfloat(0xff800000u), hi == hi ? hi : asfloat(0x7f800000u));
}

float3 cultmath_af_add(float3 x, float3 y)
{
    float x0 = x.x + y.x;
    float a = x.y + y.y;
    float e = x.z + y.z;
    return float3(x0, a, e + (abs(x0) + abs(a) + e) * 9.5367431640625e-7);
}

float3 cultmath_af_sub(float3 x, float3 y)
{
    float x0 = x.x - y.x;
    float a = x.y - y.y;
    float e = x.z + y.z;
    return float3(x0, a, e + (abs(x0) + abs(a) + e) * 9.5367431640625e-7);
}

float3 cultmath_af_neg(float3 x) { return float3(-x.x, -x.y, x.z); }

float3 cultmath_af_scale(float3 x, float s)
{
    float x0 = x.x * s;
    float a = x.y * s;
    float e = x.z * abs(s);
    return float3(x0, a, e + (abs(x0) + abs(a) + e) * 9.5367431640625e-7 + 1.17549435e-38);
}

float3 cultmath_af_add_iv(float3 x, float2 interval) { return cultmath_af_add(x, cultmath_af_from_iv(interval)); }

float3 cultmath_af_mul(float3 x, float3 y)
{
    float x0 = x.x * y.x;
    float p = x.x * y.y;
    float q = y.x * x.y;
    float a = p + q;
    float e = abs(x.x) * y.z + abs(y.x) * x.z + (abs(x.y) + x.z) * (abs(y.y) + y.z);
    return float3(x0, a, e + (abs(x0) + abs(p) + abs(q) + e) * 9.5367431640625e-7 + 1.17549435e-38);
}

// The narrower of the Lipschitz form and the centred (Taylor) form over every p = centre + axis eps + w,
// |w| <= radius, from one cultmath_snoise_grad at the centre; the allowance for the float32 evaluation is
// in math.Affine.cs.
float3 cultmath_af_snoise(float3 centre, float3 axis, float radius)
{
    float4 g = cultmath_snoise_grad(centre);
    float3 gradient = float3(g.x, g.y, g.z);
    float reach = length(axis) + radius;
    float lipschitz = CULTMATH_SNOISE_LIPSCHITZ * reach;
    float a = dot(gradient, axis);
    float remainder = length(gradient) * radius + 0.5 * CULTMATH_SNOISE_HESSIAN * reach * reach;
    float allowance = 6.103515625e-5 + 2.44140625e-4 * reach
        + CULTMATH_SNOISE_LIPSCHITZ * 9.5367431640625e-7 * (abs(centre.x) + abs(centre.y) + abs(centre.z) + reach);
    bool centred = abs(a) + remainder < lipschitz;
    float slope = centred ? a : 0.0;
    float e = (centred ? remainder : lipschitz) + allowance;
    return float3(g.w, slope, e + (abs(g.w) + abs(slope) + e) * 9.5367431640625e-7);
}

// Octave sum of cultmath_af_snoise, compounding frequency and amplitude as the value-and-gradient fBm does.
float3 cultmath_af_fbm(float3 centre, float3 axis, float radius, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    float3 sum = float3(0.0, 0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        sum = cultmath_af_add(sum, cultmath_af_scale(cultmath_af_snoise(centre * frequency, axis * frequency, radius * abs(frequency)), amplitude));
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return sum;
}

// The axis of a screen tile's slice over the depth segment [z0, z1]; camera frame of
// cultmath_iv_frustum_ball.
float3 cultmath_af_frustum_axis(float2 centreSlope, float z0, float z1)
{
    float h = (z1 - z0) * 0.5;
    return float3(centreSlope.x * h, centreSlope.y * h, h);
}

// The affine slice of a screen tile over [z0, z1]: centre xyz, radius w. Derivation in math.Affine.cs.
float4 cultmath_af_frustum_ball(float2 centreSlope, float z0, float z1, float footprintPerDepth, float warpVariation)
{
    float zm = (z0 + z1) * 0.5;
    float h = (z1 - z0) * 0.5;
    float cx = centreSlope.x * zm;
    float cy = centreSlope.y * zm;
    float spread = (abs(centreSlope.x) + abs(centreSlope.y) + 1.0) * h;
    float radius = z1 * footprintPerDepth + warpVariation;
    radius += (abs(cx) + abs(cy) + abs(zm) + spread + radius) * 9.5367431640625e-7;
    return float4(cx, cy, zm, radius);
}

#endif
