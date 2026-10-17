#ifndef CULTMATH_HLSL
#define CULTMATH_HLSL

// CultMath HLSL mirror.
//
// HLSL already owns the float2/float3/float4 type names and most primitive
// intrinsics. This include names the CultMath semantic surface explicitly so
// shader code, C# wrappers, and Rust/native parity fixtures can share the same
// small vocabulary without local helper drift.

static const float CULTMATH_PI = 3.14159265358979323846;
static const float CULTMATH_TAU = 6.28318530717958647692;
static const float CULTMATH_HALF_PI = 1.57079632679489661923;

float cultmath_radians(float degrees_value) { return degrees_value * (CULTMATH_PI / 180.0); }
float cultmath_degrees(float radians_value) { return radians_value * (180.0 / CULTMATH_PI); }

float cultmath_frac(float value) { return value - floor(value); }
float2 cultmath_frac(float2 value) { return value - floor(value); }
float3 cultmath_frac(float3 value) { return value - floor(value); }
float4 cultmath_frac(float4 value) { return value - floor(value); }

float cultmath_clamp(float value, float minimum, float maximum) { return min(max(value, minimum), maximum); }
float2 cultmath_clamp(float2 value, float2 minimum, float2 maximum) { return min(max(value, minimum), maximum); }
float3 cultmath_clamp(float3 value, float3 minimum, float3 maximum) { return min(max(value, minimum), maximum); }
float4 cultmath_clamp(float4 value, float4 minimum, float4 maximum) { return min(max(value, minimum), maximum); }

// DXIL Saturate is FMin(1, FMax(0, x)); dxc lowers min/max to the same FMin/FMax, NaN rules included.
float cultmath_saturate(float value) { return min(1.0, max(0.0, value)); }
float2 cultmath_saturate(float2 value) { return min(float2(1.0, 1.0), max(float2(0.0, 0.0), value)); }
float3 cultmath_saturate(float3 value) { return min(float3(1.0, 1.0, 1.0), max(float3(0.0, 0.0, 0.0), value)); }
float4 cultmath_saturate(float4 value) { return min(float4(1.0, 1.0, 1.0, 1.0), max(float4(0.0, 0.0, 0.0, 0.0), value)); }

float cultmath_lerp(float start, float end, float amount) { return start + (end - start) * amount; }
float2 cultmath_lerp(float2 start, float2 end, float2 amount) { return start + (end - start) * amount; }
float3 cultmath_lerp(float3 start, float3 end, float3 amount) { return start + (end - start) * amount; }
float4 cultmath_lerp(float4 start, float4 end, float4 amount) { return start + (end - start) * amount; }

float cultmath_step(float edge, float value) { return value < edge ? 0.0 : 1.0; }
float2 cultmath_step(float2 edge, float2 value) { return float2(cultmath_step(edge.x, value.x), cultmath_step(edge.y, value.y)); }
float3 cultmath_step(float3 edge, float3 value) { return float3(cultmath_step(edge.x, value.x), cultmath_step(edge.y, value.y), cultmath_step(edge.z, value.z)); }
float4 cultmath_step(float4 edge, float4 value) { return float4(cultmath_step(edge.x, value.x), cultmath_step(edge.y, value.y), cultmath_step(edge.z, value.z), cultmath_step(edge.w, value.w)); }

float cultmath_smoothstep(float minimum, float maximum, float value)
{
    float t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (3.0 - 2.0 * t);
}

float2 cultmath_smoothstep(float2 minimum, float2 maximum, float2 value)
{
    float2 t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (float2(3.0, 3.0) - float2(2.0, 2.0) * t);
}

float3 cultmath_smoothstep(float3 minimum, float3 maximum, float3 value)
{
    float3 t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (float3(3.0, 3.0, 3.0) - float3(2.0, 2.0, 2.0) * t);
}

float4 cultmath_smoothstep(float4 minimum, float4 maximum, float4 value)
{
    float4 t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (float4(3.0, 3.0, 3.0, 3.0) - float4(2.0, 2.0, 2.0, 2.0) * t);
}

float cultmath_smoothstep01(float value) { return cultmath_smoothstep(0.0, 1.0, value); }

float cultmath_smootherstep(float value)
{
    value = cultmath_saturate(value);
    return value * value * value * (value * (value * 6.0 - 15.0) + 10.0);
}

float2 cultmath_snoise_mod289(float2 value) { return value - floor(value * (1.0 / 289.0)) * 289.0; }
float3 cultmath_snoise_mod289(float3 value) { return value - floor(value * (1.0 / 289.0)) * 289.0; }
float4 cultmath_snoise_mod289(float4 value) { return value - floor(value * (1.0 / 289.0)) * 289.0; }
float3 cultmath_snoise_permute(float3 value) { return cultmath_snoise_mod289(((value * 34.0) + 10.0) * value); }
float4 cultmath_snoise_permute(float4 value) { return cultmath_snoise_mod289(((value * 34.0) + 10.0) * value); }

// Ashima Arts / Ian McEwan 3D simplex noise (MIT), same float32 evaluation
// order as the C# math.snoise(float3) mirror. Ties resolve in simplexnoise1234's
// total order; see math.snoise.
float cultmath_snoise(float3 value)
{
    float2 c = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(value + dot(value, c.yyy));
    float3 x0 = value - i + dot(i, c.xxx);
    float3 g = float3(step(x0.y, x0.x), step(x0.z, x0.y), 1.0 - step(x0.z, x0.x));
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + c.xxx;
    float3 x2 = x0 - i2 + c.yyy;
    float3 x3 = x0 - 0.5;

    i = cultmath_snoise_mod289(i);
    float4 p = cultmath_snoise_permute(cultmath_snoise_permute(cultmath_snoise_permute(
        i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));

    const float n_ = 0.142857142857;
    float3 ns = float3(2.0 * n_, 0.5 * n_ - 1.0, n_);
    float4 j = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);

    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    float3 p0 = float3(a0.xy, h.x);
    float3 p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z);
    float3 p3 = float3(a1.zw, h.w);
    float4 norm = 1.79284291400159 - 0.85373472095314 * float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;

    float4 m = max(0.5 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 105.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

float cultmath_snoise(float2 value)
{
    float4 c = float4(
        0.211324865405187,
        0.366025403784439,
        -0.577350269189626,
        0.024390243902439);
    float2 i = floor(value + dot(value, c.yy));
    float2 x0 = value - i + dot(i, c.xx);
    float2 i1 = x0.x > x0.y ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float4 x12 = float4(x0.x + c.x - i1.x, x0.y + c.x - i1.y, x0.x + c.z, x0.y + c.z);

    i = cultmath_snoise_mod289(i);
    float3 p = cultmath_snoise_permute(
        cultmath_snoise_permute(i.y + float3(0.0, i1.y, 1.0)) + i.x + float3(0.0, i1.x, 1.0));
    float3 m = max(0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m *= m;
    m *= m;

    float3 x = 2.0 * frac(p * c.w) - 1.0;
    float3 h = abs(x) - 0.5;
    float3 ox = floor(x + 0.5);
    float3 a0 = x - ox;
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);

    float3 g = float3(
        a0.x * x0.x + h.x * x0.y,
        a0.y * x12.x + h.y * x12.y,
        a0.z * x12.z + h.z * x12.w);
    return 130.0 * dot(m, g);
}

// Analytic gradient of cultmath_snoise(float3) (invariant 8), following webgl-noise
// src/noise3Dgrad.glsl (Ashima Arts / McEwan, MIT); same kernel, permutation and scale as
// cultmath_snoise(float3) (stegu/webgl-noise 22434e04d7). See the C# math.snoise_grad comment for the
// derivation. Every local up through p0..p3 and x0..x3 is cultmath_snoise(float3)'s own derivation
// verbatim.
float4 cultmath_snoise_grad(float3 value)
{
    float2 c = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(value + dot(value, c.yyy));
    float3 x0 = value - i + dot(i, c.xxx);
    float3 g = float3(step(x0.y, x0.x), step(x0.z, x0.y), 1.0 - step(x0.z, x0.x));
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + c.xxx;
    float3 x2 = x0 - i2 + c.yyy;
    float3 x3 = x0 - 0.5;

    i = cultmath_snoise_mod289(i);
    float4 p = cultmath_snoise_permute(cultmath_snoise_permute(cultmath_snoise_permute(
        i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));

    const float n_ = 0.142857142857;
    float3 ns = float3(2.0 * n_, 0.5 * n_ - 1.0, n_);
    float4 j = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);

    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    float3 p0 = float3(a0.xy, h.x);
    float3 p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z);
    float3 p3 = float3(a1.zw, h.w);
    float4 norm = 1.79284291400159 - 0.85373472095314 * float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;

    float4 m0 = max(0.5 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    float4 m2 = m0 * m0;
    float4 m3 = m2 * m0;
    float4 m4 = m2 * m2;

    float4 px = float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3));
    float value2 = 105.0 * dot(m4, px);
    float3 grad = -8.0 * m3.x * x0 * px.x + m4.x * p0
        + -8.0 * m3.y * x1 * px.y + m4.y * p1
        + -8.0 * m3.z * x2 * px.z + m4.z * p2
        + -8.0 * m3.w * x3 * px.w + m4.w * p3;
    grad *= 105.0;

    return float4(grad, value2);
}

// Analytic Hessian of cultmath_snoise(float3); see the C# math.snoise_hessian comment for the
// derivation. Every local up through p0..p3 and x0..x3 is cultmath_snoise_grad's own. The matrix is
// built from nine scalars, which read as rows in HLSL and as columns in GLSL; it is symmetric, so the
// two agree.
float3x3 cultmath_snoise_hessian(float3 value)
{
    float2 c = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(value + dot(value, c.yyy));
    float3 x0 = value - i + dot(i, c.xxx);
    float3 g = float3(step(x0.y, x0.x), step(x0.z, x0.y), 1.0 - step(x0.z, x0.x));
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + c.xxx;
    float3 x2 = x0 - i2 + c.yyy;
    float3 x3 = x0 - 0.5;

    i = cultmath_snoise_mod289(i);
    float4 p = cultmath_snoise_permute(cultmath_snoise_permute(cultmath_snoise_permute(
        i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));

    const float n_ = 0.142857142857;
    float3 ns = float3(2.0 * n_, 0.5 * n_ - 1.0, n_);
    float4 j = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);

    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    float3 p0 = float3(a0.xy, h.x);
    float3 p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z);
    float3 p3 = float3(a1.zw, h.w);
    float4 norm = 1.79284291400159 - 0.85373472095314 * float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;

    float4 m0 = max(0.5 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    float4 m2 = m0 * m0;
    float4 m3 = m2 * m0;
    float4 px = float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3));
    float4 wide = 48.0 * m2 * px;
    float4 narrow = 8.0 * m3;

    float4 cxx = float4(x0.x, x1.x, x2.x, x3.x);
    float4 cxy = float4(x0.y, x1.y, x2.y, x3.y);
    float4 cxz = float4(x0.z, x1.z, x2.z, x3.z);
    float4 cpx = float4(p0.x, p1.x, p2.x, p3.x);
    float4 cpy = float4(p0.y, p1.y, p2.y, p3.y);
    float4 cpz = float4(p0.z, p1.z, p2.z, p3.z);

    float hxx = 105.0 * (dot(wide * cxx, cxx) - dot(narrow, 2.0 * cpx * cxx + px));
    float hyy = 105.0 * (dot(wide * cxy, cxy) - dot(narrow, 2.0 * cpy * cxy + px));
    float hzz = 105.0 * (dot(wide * cxz, cxz) - dot(narrow, 2.0 * cpz * cxz + px));
    float hxy = 105.0 * (dot(wide * cxx, cxy) - dot(narrow, cpx * cxy + cpy * cxx));
    float hxz = 105.0 * (dot(wide * cxx, cxz) - dot(narrow, cpx * cxz + cpz * cxx));
    float hyz = 105.0 * (dot(wide * cxy, cxz) - dot(narrow, cpy * cxz + cpz * cxy));
    return float3x3(hxx, hxy, hxz, hxy, hyy, hyz, hxz, hyz, hzz);
}

// The integral over [t0, t1] of clamp(a + b t, lo, hi); see the C# math.integral_clamp_linear comment.
float cultmath_integral_clamp_linear(float a, float b, float lo, float hi, float t0, float t1)
{
    lo = min(lo, hi);
    if (b == 0.0)
        return t1 != t0 ? clamp(a, lo, hi) * (t1 - t0) : 0.0;

    float direction = t1 < t0 ? -1.0 : 1.0;
    float start = min(t0, t1);
    float stop = max(t0, t1);
    float atLo = (lo - a) / b;
    float atHi = (hi - a) / b;
    float enter = clamp(min(atLo, atHi), start, stop);
    float leave = clamp(max(atLo, atHi), start, stop);
    float before = b > 0.0 ? lo : hi;
    float after = b > 0.0 ? hi : lo;
    float held = enter > start ? before * (enter - start) : 0.0;
    float kept = stop > leave ? after * (stop - leave) : 0.0;
    float total = held + (a + b * (0.5 * (enter + leave))) * (leave - enter) + kept;
    return direction * total;
}

// Octave sum of cultmath_snoise_grad (invariant 8); see the C# math.fbm_grad comment for the
// per-octave frequency/amplitude derivation and why octaves is clamped.
float4 cultmath_fbm_grad(float3 p, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    float value = 0.0;
    float3 gradient = float3(0.0, 0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        float4 n = cultmath_snoise_grad(p * frequency);
        value += amplitude * n.w;
        gradient += amplitude * frequency * n.xyz;
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return float4(gradient, value);
}

// Ridged multifractal noise (Musgrave, Texturing and Modeling, ch. 16); see the C# math.ridged_grad
// comment for the fold/sign derivation, the deliberate crease at n = 0, and why octaves is clamped.
float4 cultmath_ridged_grad(float3 p, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    float value = 0.0;
    float3 gradient = float3(0.0, 0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        float4 n = cultmath_snoise_grad(p * frequency);
        float s = sign(n.w);
        value += amplitude * (1.0 - abs(n.w));
        gradient += -amplitude * frequency * s * n.xyz;
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return float4(gradient, value);
}

float cultmath_lengthsq(float2 value) { return dot(value, value); }
float cultmath_lengthsq(float3 value) { return dot(value, value); }
float cultmath_lengthsq(float4 value) { return dot(value, value); }

float cultmath_distance(float2 left, float2 right) { return length(left - right); }
float cultmath_distance(float3 left, float3 right) { return length(left - right); }
float cultmath_distance(float4 left, float4 right) { return length(left - right); }

float2 cultmath_reflect(float2 incident, float2 normal_value)
{
    return incident - 2.0 * dot(normal_value, incident) * normal_value;
}

float3 cultmath_reflect(float3 incident, float3 normal_value)
{
    return incident - 2.0 * dot(normal_value, incident) * normal_value;
}

float2 cultmath_rotate(float2 value, float radians_value)
{
    float s = sin(radians_value);
    float c = cos(radians_value);
    return float2(value.x * c - value.y * s, value.x * s + value.y * c);
}

float2 cultmath_rotate_degrees(float2 value, float degrees_value)
{
    return cultmath_rotate(value, cultmath_radians(degrees_value));
}

float cultmath_csum(float2 value) { return value.x + value.y; }
float cultmath_csum(float3 value) { return value.x + value.y + value.z; }
float cultmath_csum(float4 value) { return value.x + value.y + value.z + value.w; }

float cultmath_decay(float source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }
float2 cultmath_decay(float2 source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }
float3 cultmath_decay(float3 source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }
float4 cultmath_decay(float4 source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }

float cultmath_damp(float start, float end, float lambda_value, float dt)
{
    return cultmath_lerp(start, end, 1.0 - exp(-lambda_value * dt));
}

float2 cultmath_damp(float2 start, float2 end, float lambda_value, float dt)
{
    float amount = 1.0 - exp(-lambda_value * dt);
    return cultmath_lerp(start, end, float2(amount, amount));
}

float3 cultmath_damp(float3 start, float3 end, float lambda_value, float dt)
{
    float amount = 1.0 - exp(-lambda_value * dt);
    return cultmath_lerp(start, end, float3(amount, amount, amount));
}

float4 cultmath_damp(float4 start, float4 end, float lambda_value, float dt)
{
    float amount = 1.0 - exp(-lambda_value * dt);
    return cultmath_lerp(start, end, float4(amount, amount, amount, amount));
}

float cultmath_catmullrom(float p0, float p1, float p2, float p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

float2 cultmath_catmullrom(float2 p0, float2 p1, float2 p2, float2 p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

float3 cultmath_catmullrom(float3 p0, float3 p1, float3 p2, float3 p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

float4 cultmath_catmullrom(float4 p0, float4 p1, float4 p2, float4 p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

float cultmath_quadratic_bezier(float p0, float p1, float p2, float t)
{
    t = cultmath_saturate(t);
    float inv = 1.0 - t;
    return inv * inv * p0 + 2.0 * inv * t * p1 + t * t * p2;
}

float3 cultmath_quadratic_bezier(float3 p0, float3 p1, float3 p2, float t)
{
    t = cultmath_saturate(t);
    float inv = 1.0 - t;
    return inv * inv * p0 + 2.0 * inv * t * p1 + t * t * p2;
}

float cultmath_cubic_bezier(float p0, float p1, float p2, float p3, float t)
{
    t = cultmath_saturate(t);
    float inv = 1.0 - t;
    return inv * inv * inv * p0 + 3.0 * inv * inv * t * p1 + 3.0 * inv * t * t * p2 + t * t * t * p3;
}

float3 cultmath_cubic_bezier(float3 p0, float3 p1, float3 p2, float3 p3, float t)
{
    t = cultmath_saturate(t);
    float inv = 1.0 - t;
    return inv * inv * inv * p0 + 3.0 * inv * inv * t * p1 + 3.0 * inv * t * t * p2 + t * t * t * p3;
}

float cultmath_hash(float value) { return cultmath_frac(sin(value) * 43758.5453); }
float cultmath_hash(float2 value) { return cultmath_hash(dot(value, float2(127.1, 311.7))); }
float cultmath_hash(float3 value) { return cultmath_hash(dot(value, float3(127.1, 311.7, 74.7))); }

// PCG hashes (Jarzynski and Olano, JCGT 2020), written over scalar uints so CultMath's int3/int4 carry the bits.
uint cultmath_pcg(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
    return (word >> 22) ^ word;
}

int3 cultmath_pcg3d(int3 value)
{
    uint x = (uint)value.x * 1664525u + 1013904223u, y = (uint)value.y * 1664525u + 1013904223u, z = (uint)value.z * 1664525u + 1013904223u;
    x += y * z; y += z * x; z += x * y;
    x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
    x += y * z; y += z * x; z += x * y;
    return int3((int)x, (int)y, (int)z);
}

int3 cultmath_pcg3d(float3 value) { return cultmath_pcg3d(int3((int)asuint(value.x), (int)asuint(value.y), (int)asuint(value.z))); }
int3 cultmath_pcg3d(float2 value) { return cultmath_pcg3d(float3(value, 0.0)); }

int4 cultmath_pcg4d(int4 value)
{
    uint x = (uint)value.x * 1664525u + 1013904223u, y = (uint)value.y * 1664525u + 1013904223u;
    uint z = (uint)value.z * 1664525u + 1013904223u, w = (uint)value.w * 1664525u + 1013904223u;
    x += y * w; y += z * x; z += x * y; w += y * z;
    x ^= x >> 16; y ^= y >> 16; z ^= z >> 16; w ^= w >> 16;
    x += y * w; y += z * x; z += x * y; w += y * z;
    return int4((int)x, (int)y, (int)z, (int)w);
}

int4 cultmath_pcg4d(float4 value) { return cultmath_pcg4d(int4((int)asuint(value.x), (int)asuint(value.y), (int)asuint(value.z), (int)asuint(value.w))); }

// Inigo Quilez, "smooth minimum": quadratic-polynomial smin carried to value-and-gradient form. See
// math.smin_grad's comment (math.cs) for why the gradient blend is exact, not an approximation.
float4 cultmath_smin_grad(float4 a, float4 b, float k)
{
    float h = saturate(0.5 + 0.5 * (b.w - a.w) / k);
    float value = lerp(b.w, a.w, h) - k * h * (1.0 - h);
    float3 gradient = lerp(b.xyz, a.xyz, h);
    return float4(gradient, value);
}

// Invariant 8's one struct return shape (design.md, "HLSL Target: Source Transformations"):
// compared field by field and bit for bit against CultMath.CultCellular, same field order.
struct CultCellular
{
    float4 nearest;
    float4 edge;
    float id;
};

// Worley, "A Cellular Texture Basis Function" (SIGGRAPH 1996). See math.cellular's comment
// (math.cs) for the hashing, gradient, search-radius exactness proof, id source and
// degenerate-point conventions this mirrors exactly, including the 5x5x5 lower-bound prune.
CultCellular cultmath_cellular(float3 p)
{
    float3 cell = floor(p);
    float f1 = 1.0e30;
    float f2 = 1.0e30;
    float3 c1 = float3(0.0, 0.0, 0.0);
    float3 c2 = float3(0.0, 0.0, 0.0);
    float3 cellId = float3(0.0, 0.0, 0.0);

    for (int dz = -2; dz <= 2; dz++)
    {
        float lz = max(0.0, abs((float)dz) - 1.0);
        for (int dy = -2; dy <= 2; dy++)
        {
            float ly = max(0.0, abs((float)dy) - 1.0);
            for (int dx = -2; dx <= 2; dx++)
            {
                float lx = max(0.0, abs((float)dx) - 1.0);
                float lowerBound = sqrt(lx * lx + ly * ly + lz * lz);
                if (lowerBound >= f2)
                    continue;

                float3 neighbor = cell + float3(dx, dy, dz);
                int3 hash = cultmath_pcg3d(int3(neighbor));
                float3 jitter = float3(
                    (float)((uint)hash.x >> 8) * (1.0 / 16777216.0),
                    (float)((uint)hash.y >> 8) * (1.0 / 16777216.0),
                    (float)((uint)hash.z >> 8) * (1.0 / 16777216.0));
                float3 feature = neighbor + jitter;
                float d = cultmath_distance(p, feature);

                if (d < f1)
                {
                    f2 = f1; c2 = c1;
                    f1 = d; c1 = feature; cellId = neighbor;
                }
                else if (d < f2)
                {
                    f2 = d; c2 = feature;
                }
            }
        }
    }

    // F1 = 0 and F2 = 0 share the same degenerate-gradient guard; see math.cellular's comment
    // (math.cs) for why F2 = 0 is reachable past roughly |p| = 2^23.
    float3 grad1 = f1 > 0.0 ? (p - c1) / f1 : float3(0.0, 0.0, 0.0);
    float3 grad2 = f2 > 0.0 ? (p - c2) / f2 : float3(0.0, 0.0, 0.0);

    // id comes from pcg4d over the winning cell's integer coordinate alone (same int-hashing rule
    // as the jitter above), never from the pcg3d hash that produced its jitter (F4).
    int4 idHash = cultmath_pcg4d(int4(int3(cellId), 0));

    CultCellular result;
    result.nearest = float4(grad1, f1);
    result.edge = float4(grad2 - grad1, f2 - f1);
    result.id = (float)((uint)idHash.w >> 8) * (1.0 / 16777216.0);
    return result;
}

// Sites: a site is float4(position.xyz, weight). A span is a loop over a per-site step, because a shader
// function cannot take a span in the mirror (design.md, "Sites"). The consumer's loop, over its own buffer:
//   CultSiteCells cells = cultmath_site_cells_empty();
//   for (int i = 0; i < count; i++)
//       cells = cultmath_site_cells_insert(cells, cultmath_power_score(p, sites[i]), i);
// The exact power cell border is a second loop over the buffer, skipping the owner cells.nearestSite:
//   float4 border = float4(0.0, 0.0, 0.0, 1.0e30); int owner = (int)cells.nearestSite;
//   for (int k = 0; k < count; k++) if (k != owner) border = cultmath_power_border_step(p, sites[owner], sites[k], border);
// The step trusts the owner: a p outside the owner's cell reports distance 0 with that plane's normal, not an error.
// Statement for statement with math.Sites.cs (power_score, multiplicative_score, site_cells_*, gabriel_margin).
struct CultSiteCells
{
    float4 nearest;
    float4 second;
    float nearestSite;
    float secondSite;
};

float4 cultmath_power_score(float3 p, float4 site)
{
    float3 offset = p - site.xyz;
    return float4(offset * 2.0, dot(offset, offset) - site.w * site.w);
}

float4 cultmath_multiplicative_score(float3 p, float4 site)
{
    float3 offset = p - site.xyz;
    float r = length(offset);
    float3 gradient = r > 0.0 ? offset / (r * site.w) : float3(0.0, 0.0, 0.0);
    return float4(gradient, r / site.w);
}

CultSiteCells cultmath_site_cells_empty()
{
    CultSiteCells result;
    result.nearest = float4(0.0, 0.0, 0.0, 1.0e30);
    result.second = float4(0.0, 0.0, 0.0, 1.0e30);
    result.nearestSite = -1.0;
    result.secondSite = -1.0;
    return result;
}

CultSiteCells cultmath_site_cells_insert(CultSiteCells cells, float4 score, int index)
{
    CultSiteCells result = cells;
    if (score.w < cells.nearest.w)
    {
        result.second = cells.nearest;
        result.secondSite = cells.nearestSite;
        result.nearest = score;
        result.nearestSite = (float)index;
    }
    else if (score.w < cells.second.w)
    {
        result.second = score;
        result.secondSite = (float)index;
    }

    return result;
}

float4 cultmath_site_cells_border(CultSiteCells cells)
{
    float3 n = cells.second.xyz - cells.nearest.xyz;
    float len = length(n);
    if (cells.secondSite < 0.0 || len <= 0.0)
        return float4(0.0, 0.0, 0.0, 1.0e30);
    return float4(n / len, (cells.second.w - cells.nearest.w) / len);
}

float4 cultmath_power_border_step(float3 p, float4 owner, float4 other, float4 border)
{
    float3 d = other.xyz - owner.xyz;
    float len = length(d);
    if (len <= 0.0)
        return border;
    float plane = max((dot(owner.xyz - p, d) + 0.5 * (dot(d, d) + (owner.w - other.w) * (owner.w + other.w))) / len, 0.0);
    return plane < border.w ? float4(-d / len, plane) : border;
}

float cultmath_gabriel_margin(float3 a, float3 b, float3 other)
{
    float r = cultmath_distance(a, b) * 0.5;
    if (r <= 0.0)
        return -1.0;
    return (cultmath_distance(other, (a + b) * 0.5) - r) / r;
}

// Weighted lattice cells: the four per-cell steps of math.Lattice.cs, statement for statement. A shader function cannot take a
// span (design.md, "Sites"), so weighted_lattice_cells is the consumer's loop over its own body buffer, in two passes:
//   float2 q = p / cellSize; int2 c0 = (int2)floor(q); float f1 = 1.0e30, f2 = 1.0e30; int reach = 0;
//   for (int k = 0; ; k++) {   // ring pass: Chebyshev rings 0, 1, 2, ... around c0, exact exit
//       if (k > 1 && 0.5 * (k - 1) >= max(f2, f1 + 2.0 * smoothing)) break;   // k never reaches 38 in the accepted range
//       for each cell on ring k: d = cultmath_lattice_distance(q, cultmath_lattice_site(cell, time, drift).xy, w, normal, stretch),
//           w = cultmath_lattice_weight(site.z, border, boost, falloff), border = the .w of cultmath_power_border_step
//           folded over the bodies (owner = the nearest body of the cell centre (cell + 0.5) * cellSize, z = 0);
//           if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) f2 = d;   store d for the box pass
//       reach = k; }
//   float m = f1 + smoothing;   // box pass: every stored cell, rows dz = -reach..reach, then dx = -reach..reach
//   for each (dz, dx): m = cultmath_lattice_smooth_step(m, d, f1, smoothing);   // strict < for the winning id (earlier cell wins ties)
// The preconditions the C# function throws for are the caller's in a shader: finite arguments, cellSize > 0, boost in [0, 4],
// falloff > 0, smoothing in (0, 1], stretch in [1, 2], p within 2^23 cells. The normal is normalised as C# does: divide by its
// largest component, then by its length (a zero normal means (1, 0) and needs stretch == 1). The ring bound is 37.
float3 cultmath_lattice_site(float2 cell, float time, float drift)
{
    int3 h = cultmath_pcg3d(int3((int)cell.x, (int)cell.y, 0));
    float2 phase = float2((float)((uint)h.x >> 8) * (1.0 / 16777216.0), (float)((uint)h.y >> 8) * (1.0 / 16777216.0));
    float2 site = cell + 0.5 + 0.5 * float2(sin(drift * time + CULTMATH_TAU * phase.x), sin(drift * time + CULTMATH_TAU * phase.y));
    return float3(site, 0.5 + 0.5 * ((float)((uint)h.z >> 8) * (1.0 / 16777216.0)));
}

float cultmath_lattice_weight(float base_weight, float border, float boost, float falloff)
{
    float t = border / falloff;
    return base_weight * (1.0 + boost * exp(-(t * t)));
}

float cultmath_lattice_distance(float2 q, float2 site, float weight, float2 normal, float stretch)
{
    float2 v = site - q;
    float a = dot(v, normal);
    float c = v.x * normal.y - v.y * normal.x;
    return weight * length(float2(stretch * a, c));
}

float cultmath_lattice_smooth_step(float m, float d, float f1, float smoothing)
{
    if (d >= f1 + 2.0 * smoothing)
        return m;
    float h = cultmath_smoothstep(0.0, 1.0, 0.5 + 0.5 * (m - d) / smoothing);
    float next = m + (d - m) * h - h * (1.0 - h) * smoothing / (1.0 + 3.0 * smoothing);
    return min(next, f1 + smoothing);
}

float2 cultmath_segment_closest(float2 point_value, float2 start, float2 end)
{
    float2 segment = end - start;
    float segmentLengthSq = cultmath_lengthsq(segment);
    if (segmentLengthSq <= 0.0)
        return start;

    float t = cultmath_saturate(dot(point_value - start, segment) / segmentLengthSq);
    return start + segment * t;
}

float3 cultmath_segment_closest(float3 point_value, float3 start, float3 end)
{
    float3 segment = end - start;
    float segmentLengthSq = cultmath_lengthsq(segment);
    if (segmentLengthSq <= 0.0)
        return start;

    float t = cultmath_saturate(dot(point_value - start, segment) / segmentLengthSq);
    return start + segment * t;
}

float cultmath_distance_to_segment(float2 point_value, float2 start, float2 end)
{
    return cultmath_distance(point_value, cultmath_segment_closest(point_value, start, end));
}

float cultmath_distance_to_segment(float3 point_value, float3 start, float3 end)
{
    return cultmath_distance(point_value, cultmath_segment_closest(point_value, start, end));
}

#include "CultMath.Phacelle.hlsl"
#include "CultMath.Interval.hlsl"
#include "CultMath.Cycles.hlsl"
#include "CultMath.Affine.hlsl"

// Span stages (invariant 8): the SpanPath, SpanStage and SpanNoise of math.Span.cs, field for field in
// C#'s order, and cultmath_span_stage, its octave stage, statement for statement so the text is a bit-for-bit
// mirror. They follow the includes because the bounds read CULTMATH_SNOISE_LIPSCHITZ, _HESSIAN and _THIRD.
// A shader caller builds a SpanStage itself (SpanStage(1.0, 1.0, vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0))
// is the identity of math.SpanStage.Identity, which has no shader form); see math.span_stage's comments for
// the span, the transform, the bounds and the allowances. cultmath_span_rounding and cultmath_span_slack
// are math.span_rounding and math.span_slack, the owners of S_o and of the three allowances.
struct SpanPath
{
    float3 midpoint;
    float3 velocity;
    float speed;
    float acceleration;
    float halfLength;
};

struct SpanStage
{
    float frequency;
    float stretch;
    float3 axis;
    float3 shift;
    float3 offset;
    float3 octaveStep;
};

struct SpanNoise
{
    float4 field;
    float curvature;
    float fold;
};

float cultmath_span_rounding(float scale, float3 mq, SpanStage stage, float3 octaveShift, float nu, float halfLength)
{
    return 9.5367431640625e-7 * (abs(scale) * (cultmath_csum(abs(mq)) + cultmath_csum(abs(stage.shift))) + cultmath_csum(abs(stage.offset)) + cultmath_csum(abs(octaveShift)) + 1.7320508 * nu * halfLength);
}

float3 cultmath_span_slack(float rounding)
{
    return float3(CULTMATH_SNOISE_READ_VALUE + CULTMATH_SNOISE_LIPSCHITZ * rounding,
        CULTMATH_SNOISE_READ_GRADIENT + CULTMATH_SNOISE_HESSIAN * rounding,
        9.765625e-4 + CULTMATH_SNOISE_THIRD * rounding);
}

// The octave stage behind cultmath_span_fbm and cultmath_span_ridged, as math.span_stage is behind span_fbm
// and span_ridged. The Frobenius norm of the Hessian is written out in C#'s order (GLSL has no _mRC member
// access); the C# local named step is octaveShift here (it would shadow GLSL's step()).
SpanNoise cultmath_span_stage(bool ridged, SpanPath path, SpanStage stage, int octaves, float lacunarity, float gain)
{
    float3 q = path.midpoint;
    float halfLength = path.halfLength;
    octaves = clamp(octaves, 0, 16);
    bool stretched = dot(stage.axis, stage.axis) > 0.0 && stage.stretch != 1.0;
    float3 direction = stretched ? normalize(stage.axis) : float3(0.0, 0.0, 0.0);
    float squeeze = stretched ? 1.0 / stage.stretch - 1.0 : 0.0;
    float norm = abs(stage.frequency) * (stretched ? max(1.0, 1.0 / stage.stretch) : 1.0);
    float3 mq = stretched ? stage.frequency * (q + squeeze * dot(direction, q) * direction) : stage.frequency * q;
    float3 mv = stretched ? stage.frequency * (path.velocity + squeeze * dot(direction, path.velocity) * direction) : stage.frequency * path.velocity;
    float amplitude = 1.0;
    float scale = 1.0;
    float value = 0.0;
    float3 gradient = float3(0.0, 0.0, 0.0);
    float curvature = 0.0;
    float fold = 0.0;
    for (int i = 0; i < octaves; i++)
    {
        float3 octaveShift = (float)i * stage.octaveStep;
        float3 x = scale * (mq + stage.shift) + (stage.offset + octaveShift);
        float4 n = cultmath_snoise_grad(x);
        float3 g = float3(n.x, n.y, n.z);
        float3 mg = stretched ? stage.frequency * (g + squeeze * dot(direction, g) * direction) : stage.frequency * g;
        if (ridged)
        {
            float s = sign(n.w);
            value += amplitude * (1.0 - abs(n.w));
            gradient += -amplitude * scale * s * mg;
        }
        else
        {
            value += amplitude * n.w;
            gradient += amplitude * scale * mg;
        }

        float weight = abs(amplitude);
        float factor = abs(scale) * norm;
        float nu = factor * path.speed;
        float rounding = cultmath_span_rounding(scale, mq, stage, octaveShift, nu, halfLength);
        float3 slack = cultmath_span_slack(rounding);
        float valueSlack = slack.x;
        float gradientSlack = slack.y;
        float hessianSlack = slack.z;
        float3x3 hm = cultmath_snoise_hessian(x);
        float frobenius = sqrt(hm[0][0] * hm[0][0] + hm[0][1] * hm[0][1] + hm[0][2] * hm[0][2] + hm[1][0] * hm[1][0] + hm[1][1] * hm[1][1] + hm[1][2] * hm[1][2]
            + hm[2][0] * hm[2][0] + hm[2][1] * hm[2][1] + hm[2][2] * hm[2][2]);
        float hessianBound = min(CULTMATH_SNOISE_HESSIAN, frobenius + hessianSlack + CULTMATH_SNOISE_THIRD * nu * halfLength);
        curvature += weight * factor * factor * hessianBound;
        if (ridged)
        {
            float3 w = scale * mv;
            float reach = hessianBound * nu * halfLength;
            float gl = min(CULTMATH_SNOISE_LIPSCHITZ, length(g) + gradientSlack + reach);
            float travel = (abs(dot(g, w)) + gradientSlack * length(w)) * halfLength + (hessianBound * nu * length(w) + gl * factor * path.acceleration) * (0.5 * halfLength * halfLength) + 2.0 * valueSlack;
            fold += 2.0 * weight * max(travel - abs(n.w), 0.0);
        }

        scale *= lacunarity;
        amplitude *= gain;
    }

    SpanNoise result;
    result.field = float4(gradient, value);
    result.curvature = curvature;
    result.fold = fold;
    return result;
}

SpanNoise cultmath_span_fbm(SpanPath path, SpanStage stage, int octaves, float lacunarity, float gain)
{
    return cultmath_span_stage(false, path, stage, octaves, lacunarity, gain);
}

SpanNoise cultmath_span_ridged(SpanPath path, SpanStage stage, int octaves, float lacunarity, float gain)
{
    return cultmath_span_stage(true, path, stage, octaves, lacunarity, gain);
}

float cultmath_value_noise(float2 position)
{
    float2 cell = floor(position);
    float2 local = cultmath_frac(position);
    float2 u = local * local * (float2(3.0, 3.0) - float2(2.0, 2.0) * local);

    float a = cultmath_hash(cell);
    float b = cultmath_hash(cell + float2(1.0, 0.0));
    float c = cultmath_hash(cell + float2(0.0, 1.0));
    float d = cultmath_hash(cell + float2(1.0, 1.0));

    return cultmath_lerp(cultmath_lerp(a, b, u.x), cultmath_lerp(c, d, u.x), u.y);
}

float cultmath_value_noise_bicubic(float2 position)
{
    float2 cell = floor(position);
    float2 local = cultmath_frac(position);

    float y0 = cultmath_catmullrom(cultmath_hash(cell + float2(-1.0, -1.0)), cultmath_hash(cell + float2(0.0, -1.0)), cultmath_hash(cell + float2(1.0, -1.0)), cultmath_hash(cell + float2(2.0, -1.0)), local.x);
    float y1 = cultmath_catmullrom(cultmath_hash(cell + float2(-1.0, 0.0)), cultmath_hash(cell + float2(0.0, 0.0)), cultmath_hash(cell + float2(1.0, 0.0)), cultmath_hash(cell + float2(2.0, 0.0)), local.x);
    float y2 = cultmath_catmullrom(cultmath_hash(cell + float2(-1.0, 1.0)), cultmath_hash(cell + float2(0.0, 1.0)), cultmath_hash(cell + float2(1.0, 1.0)), cultmath_hash(cell + float2(2.0, 1.0)), local.x);
    float y3 = cultmath_catmullrom(cultmath_hash(cell + float2(-1.0, 2.0)), cultmath_hash(cell + float2(0.0, 2.0)), cultmath_hash(cell + float2(1.0, 2.0)), cultmath_hash(cell + float2(2.0, 2.0)), local.x);

    return cultmath_catmullrom(y0, y1, y2, y3, local.y);
}

float cultmath_value_noise_texture(Texture2D<float> noise_texture, SamplerState noise_sampler, float2 uv, float scale)
{
    return noise_texture.SampleLevel(noise_sampler, uv * scale, 0.0).r;
}

float cultmath_value_noise_texture_bicubic(Texture2D<float> noise_texture, SamplerState noise_sampler, float2 uv, float scale, float2 texel_size)
{
    float2 position = uv * scale;
    float2 cell = floor(position);
    float2 local = cultmath_frac(position);
    float2 base_uv = cell / scale;

    float row0 = cultmath_catmullrom(
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(-1.0, -1.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(0.0, -1.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(1.0, -1.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(2.0, -1.0), 0.0).r,
        local.x);
    float row1 = cultmath_catmullrom(
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(-1.0, 0.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(0.0, 0.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(1.0, 0.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(2.0, 0.0), 0.0).r,
        local.x);
    float row2 = cultmath_catmullrom(
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(-1.0, 1.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(0.0, 1.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(1.0, 1.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(2.0, 1.0), 0.0).r,
        local.x);
    float row3 = cultmath_catmullrom(
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(-1.0, 2.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(0.0, 2.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(1.0, 2.0), 0.0).r,
        noise_texture.SampleLevel(noise_sampler, base_uv + texel_size * float2(2.0, 2.0), 0.0).r,
        local.x);

    return cultmath_catmullrom(row0, row1, row2, row3, local.y);
}

#endif
