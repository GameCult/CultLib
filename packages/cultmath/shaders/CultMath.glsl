// Generated from shaders/CultMath.hlsl and CultMath.Interval.hlsl by GlslLowering (packages/cultmath/tests/CultMath.Tests/GlslLowering.cs). Do not edit; regenerate with CULTMATH_WRITE_GLSL=1 dotnet test --filter GlslMirrorTests. MIT. cultmath_phacelle is in CultMath.Phacelle.glsl, to be concatenated after this file by consumers that call it.
#ifndef CULTMATH_GLSL
#define CULTMATH_GLSL
// CultMath HLSL mirror.
//
// HLSL already owns the vec2/vec3/vec4 type names and most primitive
// intrinsics. This include names the CultMath semantic surface explicitly so
// shader code, C# wrappers, and Rust/native parity fixtures can share the same
// small vocabulary without local helper drift.

const float CULTMATH_PI = 3.14159265358979323846;
const float CULTMATH_TAU = 6.28318530717958647692;
const float CULTMATH_HALF_PI = 1.57079632679489661923;

float cultmath_radians(float degrees_value) { return degrees_value * (CULTMATH_PI / 180.0); }
float cultmath_degrees(float radians_value) { return radians_value * (180.0 / CULTMATH_PI); }

float cultmath_frac(float value) { return value - floor(value); }
vec2 cultmath_frac(vec2 value) { return value - floor(value); }
vec3 cultmath_frac(vec3 value) { return value - floor(value); }
vec4 cultmath_frac(vec4 value) { return value - floor(value); }

float cultmath_clamp(float value, float minimum, float maximum) { return min(max(value, minimum), maximum); }
vec2 cultmath_clamp(vec2 value, vec2 minimum, vec2 maximum) { return min(max(value, minimum), maximum); }
vec3 cultmath_clamp(vec3 value, vec3 minimum, vec3 maximum) { return min(max(value, minimum), maximum); }
vec4 cultmath_clamp(vec4 value, vec4 minimum, vec4 maximum) { return min(max(value, minimum), maximum); }

// DXIL Saturate is FMin(1, FMax(0, x)); dxc lowers min/max to the same FMin/FMax, NaN rules included.
float cultmath_saturate(float value) { return min(1.0, max(0.0, value)); }
vec2 cultmath_saturate(vec2 value) { return min(vec2(1.0, 1.0), max(vec2(0.0, 0.0), value)); }
vec3 cultmath_saturate(vec3 value) { return min(vec3(1.0, 1.0, 1.0), max(vec3(0.0, 0.0, 0.0), value)); }
vec4 cultmath_saturate(vec4 value) { return min(vec4(1.0, 1.0, 1.0, 1.0), max(vec4(0.0, 0.0, 0.0, 0.0), value)); }

float cultmath_lerp(float start, float end, float amount) { return start + (end - start) * amount; }
vec2 cultmath_lerp(vec2 start, vec2 end, vec2 amount) { return start + (end - start) * amount; }
vec3 cultmath_lerp(vec3 start, vec3 end, vec3 amount) { return start + (end - start) * amount; }
vec4 cultmath_lerp(vec4 start, vec4 end, vec4 amount) { return start + (end - start) * amount; }

float cultmath_step(float edge, float value) { return value < edge ? 0.0 : 1.0; }
vec2 cultmath_step(vec2 edge, vec2 value) { return vec2(cultmath_step(edge.x, value.x), cultmath_step(edge.y, value.y)); }
vec3 cultmath_step(vec3 edge, vec3 value) { return vec3(cultmath_step(edge.x, value.x), cultmath_step(edge.y, value.y), cultmath_step(edge.z, value.z)); }
vec4 cultmath_step(vec4 edge, vec4 value) { return vec4(cultmath_step(edge.x, value.x), cultmath_step(edge.y, value.y), cultmath_step(edge.z, value.z), cultmath_step(edge.w, value.w)); }

float cultmath_smoothstep(float minimum, float maximum, float value)
{
    float t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (3.0 - 2.0 * t);
}

vec2 cultmath_smoothstep(vec2 minimum, vec2 maximum, vec2 value)
{
    vec2 t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (vec2(3.0, 3.0) - vec2(2.0, 2.0) * t);
}

vec3 cultmath_smoothstep(vec3 minimum, vec3 maximum, vec3 value)
{
    vec3 t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (vec3(3.0, 3.0, 3.0) - vec3(2.0, 2.0, 2.0) * t);
}

vec4 cultmath_smoothstep(vec4 minimum, vec4 maximum, vec4 value)
{
    vec4 t = cultmath_saturate((value - minimum) / (maximum - minimum));
    return t * t * (vec4(3.0, 3.0, 3.0, 3.0) - vec4(2.0, 2.0, 2.0, 2.0) * t);
}

float cultmath_smoothstep01(float value) { return cultmath_smoothstep(0.0, 1.0, value); }

float cultmath_smootherstep(float value)
{
    value = cultmath_saturate(value);
    return value * value * value * (value * (value * 6.0 - 15.0) + 10.0);
}

vec2 cultmath_snoise_mod289(vec2 value) { return value - floor(value * (1.0 / 289.0)) * 289.0; }
vec3 cultmath_snoise_mod289(vec3 value) { return value - floor(value * (1.0 / 289.0)) * 289.0; }
vec4 cultmath_snoise_mod289(vec4 value) { return value - floor(value * (1.0 / 289.0)) * 289.0; }
vec3 cultmath_snoise_permute(vec3 value) { return cultmath_snoise_mod289(((value * 34.0) + 10.0) * value); }
vec4 cultmath_snoise_permute(vec4 value) { return cultmath_snoise_mod289(((value * 34.0) + 10.0) * value); }

// Ashima Arts / Ian McEwan 3D simplex noise (MIT), same float32 evaluation
// order as the C# math.snoise(vec3) mirror.
float cultmath_snoise(vec3 value)
{
    vec2 c = vec2(1.0 / 6.0, 1.0 / 3.0);
    vec3 i = floor(value + dot(value, c.yyy));
    vec3 x0 = value - i + dot(i, c.xxx);
    vec3 g = step(x0.yzx, x0.xyz);
    vec3 l = 1.0 - g;
    vec3 i1 = min(g.xyz, l.zxy);
    vec3 i2 = max(g.xyz, l.zxy);
    vec3 x1 = x0 - i1 + c.xxx;
    vec3 x2 = x0 - i2 + c.yyy;
    vec3 x3 = x0 - 0.5;

    i = cultmath_snoise_mod289(i);
    vec4 p = cultmath_snoise_permute(cultmath_snoise_permute(cultmath_snoise_permute(
        i.z + vec4(0.0, i1.z, i2.z, 1.0)) + i.y + vec4(0.0, i1.y, i2.y, 1.0)) + i.x + vec4(0.0, i1.x, i2.x, 1.0));

    const float n_ = 0.142857142857;
    vec3 ns = vec3(2.0 * n_, 0.5 * n_ - 1.0, n_);
    vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
    vec4 x_ = floor(j * ns.z);
    vec4 y_ = floor(j - 7.0 * x_);
    vec4 x = x_ * ns.x + ns.yyyy;
    vec4 y = y_ * ns.x + ns.yyyy;
    vec4 h = 1.0 - abs(x) - abs(y);

    vec4 b0 = vec4(x.xy, y.xy);
    vec4 b1 = vec4(x.zw, y.zw);
    vec4 s0 = floor(b0) * 2.0 + 1.0;
    vec4 s1 = floor(b1) * 2.0 + 1.0;
    vec4 sh = -step(h, vec4(0.0, 0.0, 0.0, 0.0));
    vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    vec3 p0 = vec3(a0.xy, h.x);
    vec3 p1 = vec3(a0.zw, h.y);
    vec3 p2 = vec3(a1.xy, h.z);
    vec3 p3 = vec3(a1.zw, h.w);
    vec4 norm = 1.79284291400159 - 0.85373472095314 * vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;

    vec4 m = max(0.5 - vec4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 105.0 * dot(m * m, vec4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

float cultmath_snoise(vec2 value)
{
    vec4 c = vec4(
        0.211324865405187,
        0.366025403784439,
        -0.577350269189626,
        0.024390243902439);
    vec2 i = floor(value + dot(value, c.yy));
    vec2 x0 = value - i + dot(i, c.xx);
    vec2 i1 = x0.x > x0.y ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
    vec4 x12 = vec4(x0.x + c.x - i1.x, x0.y + c.x - i1.y, x0.x + c.z, x0.y + c.z);

    i = cultmath_snoise_mod289(i);
    vec3 p = cultmath_snoise_permute(
        cultmath_snoise_permute(i.y + vec3(0.0, i1.y, 1.0)) + i.x + vec3(0.0, i1.x, 1.0));
    vec3 m = max(0.5 - vec3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m *= m;
    m *= m;

    vec3 x = 2.0 * fract(p * c.w) - 1.0;
    vec3 h = abs(x) - 0.5;
    vec3 ox = floor(x + 0.5);
    vec3 a0 = x - ox;
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);

    vec3 g = vec3(
        a0.x * x0.x + h.x * x0.y,
        a0.y * x12.x + h.y * x12.y,
        a0.z * x12.z + h.z * x12.w);
    return 130.0 * dot(m, g);
}

// Analytic gradient of cultmath_snoise(vec3) (invariant 8), following webgl-noise
// src/noise3Dgrad.glsl (Ashima Arts / McEwan, MIT); same kernel, permutation and scale as
// cultmath_snoise(vec3) (stegu/webgl-noise 22434e04d7). See the C# math.snoise_grad comment for the
// derivation. Every local up through p0..p3 and x0..x3 is cultmath_snoise(vec3)'s own derivation
// verbatim.
vec4 cultmath_snoise_grad(vec3 value)
{
    vec2 c = vec2(1.0 / 6.0, 1.0 / 3.0);
    vec3 i = floor(value + dot(value, c.yyy));
    vec3 x0 = value - i + dot(i, c.xxx);
    vec3 g = step(x0.yzx, x0.xyz);
    vec3 l = 1.0 - g;
    vec3 i1 = min(g.xyz, l.zxy);
    vec3 i2 = max(g.xyz, l.zxy);
    vec3 x1 = x0 - i1 + c.xxx;
    vec3 x2 = x0 - i2 + c.yyy;
    vec3 x3 = x0 - 0.5;

    i = cultmath_snoise_mod289(i);
    vec4 p = cultmath_snoise_permute(cultmath_snoise_permute(cultmath_snoise_permute(
        i.z + vec4(0.0, i1.z, i2.z, 1.0)) + i.y + vec4(0.0, i1.y, i2.y, 1.0)) + i.x + vec4(0.0, i1.x, i2.x, 1.0));

    const float n_ = 0.142857142857;
    vec3 ns = vec3(2.0 * n_, 0.5 * n_ - 1.0, n_);
    vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
    vec4 x_ = floor(j * ns.z);
    vec4 y_ = floor(j - 7.0 * x_);
    vec4 x = x_ * ns.x + ns.yyyy;
    vec4 y = y_ * ns.x + ns.yyyy;
    vec4 h = 1.0 - abs(x) - abs(y);

    vec4 b0 = vec4(x.xy, y.xy);
    vec4 b1 = vec4(x.zw, y.zw);
    vec4 s0 = floor(b0) * 2.0 + 1.0;
    vec4 s1 = floor(b1) * 2.0 + 1.0;
    vec4 sh = -step(h, vec4(0.0, 0.0, 0.0, 0.0));
    vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    vec3 p0 = vec3(a0.xy, h.x);
    vec3 p1 = vec3(a0.zw, h.y);
    vec3 p2 = vec3(a1.xy, h.z);
    vec3 p3 = vec3(a1.zw, h.w);
    vec4 norm = 1.79284291400159 - 0.85373472095314 * vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;

    vec4 m0 = max(0.5 - vec4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    vec4 m2 = m0 * m0;
    vec4 m3 = m2 * m0;
    vec4 m4 = m2 * m2;

    vec4 px = vec4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3));
    float value2 = 105.0 * dot(m4, px);
    vec3 grad = -8.0 * m3.x * x0 * px.x + m4.x * p0
        + -8.0 * m3.y * x1 * px.y + m4.y * p1
        + -8.0 * m3.z * x2 * px.z + m4.z * p2
        + -8.0 * m3.w * x3 * px.w + m4.w * p3;
    grad *= 105.0;

    return vec4(grad, value2);
}

// Octave sum of cultmath_snoise_grad (invariant 8); see the C# math.fbm_grad comment for the
// per-octave frequency/amplitude derivation and why octaves is clamped.
vec4 cultmath_fbm_grad(vec3 p, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    float value = 0.0;
    vec3 gradient = vec3(0.0, 0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        vec4 n = cultmath_snoise_grad(p * frequency);
        value += amplitude * n.w;
        gradient += amplitude * frequency * n.xyz;
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return vec4(gradient, value);
}

// Ridged multifractal noise (Musgrave, Texturing and Modeling, ch. 16); see the C# math.ridged_grad
// comment for the fold/sign derivation, the deliberate crease at n = 0, and why octaves is clamped.
vec4 cultmath_ridged_grad(vec3 p, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    float value = 0.0;
    vec3 gradient = vec3(0.0, 0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        vec4 n = cultmath_snoise_grad(p * frequency);
        float s = sign(n.w);
        value += amplitude * (1.0 - abs(n.w));
        gradient += -amplitude * frequency * s * n.xyz;
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return vec4(gradient, value);
}

float cultmath_lengthsq(vec2 value) { return dot(value, value); }
float cultmath_lengthsq(vec3 value) { return dot(value, value); }
float cultmath_lengthsq(vec4 value) { return dot(value, value); }

float cultmath_distance(vec2 left, vec2 right) { return length(left - right); }
float cultmath_distance(vec3 left, vec3 right) { return length(left - right); }
float cultmath_distance(vec4 left, vec4 right) { return length(left - right); }

vec2 cultmath_reflect(vec2 incident, vec2 normal_value)
{
    return incident - 2.0 * dot(normal_value, incident) * normal_value;
}

vec3 cultmath_reflect(vec3 incident, vec3 normal_value)
{
    return incident - 2.0 * dot(normal_value, incident) * normal_value;
}

vec2 cultmath_rotate(vec2 value, float radians_value)
{
    float s = sin(radians_value);
    float c = cos(radians_value);
    return vec2(value.x * c - value.y * s, value.x * s + value.y * c);
}

vec2 cultmath_rotate_degrees(vec2 value, float degrees_value)
{
    return cultmath_rotate(value, cultmath_radians(degrees_value));
}

float cultmath_csum(vec2 value) { return value.x + value.y; }
float cultmath_csum(vec3 value) { return value.x + value.y + value.z; }
float cultmath_csum(vec4 value) { return value.x + value.y + value.z + value.w; }

float cultmath_decay(float source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }
vec2 cultmath_decay(vec2 source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }
vec3 cultmath_decay(vec3 source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }
vec4 cultmath_decay(vec4 source, float lambda_value, float dt) { return source * exp(-lambda_value * dt); }

float cultmath_damp(float start, float end, float lambda_value, float dt)
{
    return cultmath_lerp(start, end, 1.0 - exp(-lambda_value * dt));
}

vec2 cultmath_damp(vec2 start, vec2 end, float lambda_value, float dt)
{
    float amount = 1.0 - exp(-lambda_value * dt);
    return cultmath_lerp(start, end, vec2(amount, amount));
}

vec3 cultmath_damp(vec3 start, vec3 end, float lambda_value, float dt)
{
    float amount = 1.0 - exp(-lambda_value * dt);
    return cultmath_lerp(start, end, vec3(amount, amount, amount));
}

vec4 cultmath_damp(vec4 start, vec4 end, float lambda_value, float dt)
{
    float amount = 1.0 - exp(-lambda_value * dt);
    return cultmath_lerp(start, end, vec4(amount, amount, amount, amount));
}

float cultmath_catmullrom(float p0, float p1, float p2, float p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

vec2 cultmath_catmullrom(vec2 p0, vec2 p1, vec2 p2, vec2 p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

vec3 cultmath_catmullrom(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
}

vec4 cultmath_catmullrom(vec4 p0, vec4 p1, vec4 p2, vec4 p3, float t)
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

vec3 cultmath_quadratic_bezier(vec3 p0, vec3 p1, vec3 p2, float t)
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

vec3 cultmath_cubic_bezier(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float t)
{
    t = cultmath_saturate(t);
    float inv = 1.0 - t;
    return inv * inv * inv * p0 + 3.0 * inv * inv * t * p1 + 3.0 * inv * t * t * p2 + t * t * t * p3;
}

float cultmath_hash(float value) { return cultmath_frac(sin(value) * 43758.5453); }
float cultmath_hash(vec2 value) { return cultmath_hash(dot(value, vec2(127.1, 311.7))); }
float cultmath_hash(vec3 value) { return cultmath_hash(dot(value, vec3(127.1, 311.7, 74.7))); }

// PCG hashes (Jarzynski and Olano, JCGT 2020), written over scalar uints so CultMath's ivec3/ivec4 carry the bits.
uint cultmath_pcg(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> int((state >> 28) + 4u)) ^ state) * 277803737u;
    return (word >> 22) ^ word;
}

ivec3 cultmath_pcg3d(ivec3 value)
{
    uint x = uint(value.x) * 1664525u + 1013904223u, y = uint(value.y) * 1664525u + 1013904223u, z = uint(value.z) * 1664525u + 1013904223u;
    x += y * z; y += z * x; z += x * y;
    x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
    x += y * z; y += z * x; z += x * y;
    return ivec3(int(x), int(y), int(z));
}

ivec3 cultmath_pcg3d(vec3 value) { return cultmath_pcg3d(ivec3(int(floatBitsToUint(value.x)), int(floatBitsToUint(value.y)), int(floatBitsToUint(value.z)))); }
ivec3 cultmath_pcg3d(vec2 value) { return cultmath_pcg3d(vec3(value, 0.0)); }

ivec4 cultmath_pcg4d(ivec4 value)
{
    uint x = uint(value.x) * 1664525u + 1013904223u, y = uint(value.y) * 1664525u + 1013904223u;
    uint z = uint(value.z) * 1664525u + 1013904223u, w = uint(value.w) * 1664525u + 1013904223u;
    x += y * w; y += z * x; z += x * y; w += y * z;
    x ^= x >> 16; y ^= y >> 16; z ^= z >> 16; w ^= w >> 16;
    x += y * w; y += z * x; z += x * y; w += y * z;
    return ivec4(int(x), int(y), int(z), int(w));
}

ivec4 cultmath_pcg4d(vec4 value) { return cultmath_pcg4d(ivec4(int(floatBitsToUint(value.x)), int(floatBitsToUint(value.y)), int(floatBitsToUint(value.z)), int(floatBitsToUint(value.w)))); }

// Inigo Quilez, "smooth minimum": quadratic-polynomial smin carried to value-and-gradient form. See
// math.smin_grad's comment (math.cs) for why the gradient blend is exact, not an approximation.
vec4 cultmath_smin_grad(vec4 a, vec4 b, float k)
{
    float h = clamp(0.5 + 0.5 * (b.w - a.w) / k, 0.0, 1.0);
    float value = mix(b.w, a.w, h) - k * h * (1.0 - h);
    vec3 gradient = mix(b.xyz, a.xyz, h);
    return vec4(gradient, value);
}

// Invariant 8's one struct return shape (design.md, "HLSL Target: Source Transformations"):
// compared field by field and bit for bit against CultMath.CultCellular, same field order.
struct CultCellular
{
    vec4 nearest;
    vec4 edge;
    float id;
};

// Worley, "A Cellular Texture Basis Function" (SIGGRAPH 1996). See math.cellular's comment
// (math.cs) for the hashing, gradient, search-radius exactness proof, id source and
// degenerate-point conventions this mirrors exactly, including the 5x5x5 lower-bound prune.
CultCellular cultmath_cellular(vec3 p)
{
    vec3 cell = floor(p);
    float f1 = 1.0e30;
    float f2 = 1.0e30;
    vec3 c1 = vec3(0.0, 0.0, 0.0);
    vec3 c2 = vec3(0.0, 0.0, 0.0);
    vec3 cellId = vec3(0.0, 0.0, 0.0);

    for (int dz = -2; dz <= 2; dz++)
    {
        float lz = max(0.0, abs(float(dz)) - 1.0);
        for (int dy = -2; dy <= 2; dy++)
        {
            float ly = max(0.0, abs(float(dy)) - 1.0);
            for (int dx = -2; dx <= 2; dx++)
            {
                float lx = max(0.0, abs(float(dx)) - 1.0);
                float lowerBound = sqrt(lx * lx + ly * ly + lz * lz);
                if (lowerBound >= f2)
                    continue;

                vec3 neighbor = cell + vec3(dx, dy, dz);
                ivec3 hash = cultmath_pcg3d(ivec3(neighbor));
                vec3 jitter = vec3(
                    float(uint(hash.x) >> 8) * (1.0 / 16777216.0),
                    float(uint(hash.y) >> 8) * (1.0 / 16777216.0),
                    float(uint(hash.z) >> 8) * (1.0 / 16777216.0));
                vec3 feature = neighbor + jitter;
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
    vec3 grad1 = f1 > 0.0 ? (p - c1) / f1 : vec3(0.0, 0.0, 0.0);
    vec3 grad2 = f2 > 0.0 ? (p - c2) / f2 : vec3(0.0, 0.0, 0.0);

    // id comes from pcg4d over the winning cell's integer coordinate alone (same int-hashing rule
    // as the jitter above), never from the pcg3d hash that produced its jitter (F4).
    ivec4 idHash = cultmath_pcg4d(ivec4(ivec3(cellId), 0));

    CultCellular result;
    result.nearest = vec4(grad1, f1);
    result.edge = vec4(grad2 - grad1, f2 - f1);
    result.id = float(uint(idHash.w) >> 8) * (1.0 / 16777216.0);
    return result;
}




// Interval arithmetic over vec2(lo, hi), lo <= hi, both finite. Every cultmath_iv_* function returns
// an interval containing f(x) for every x in its input intervals, mirroring math.Interval.cs bit for
// bit; see that file and docs/design.md ("Intervals") for each op's enclosure and its ulp widening.
// Included by CultMath.hlsl after its own definitions of cultmath_smoothstep and cultmath_snoise.

// Lipschitz constant of cultmath_snoise(vec3); provenance in math.Interval.cs (SNOISE_LIPSCHITZ).
const float CULTMATH_SNOISE_LIPSCHITZ = 10.099261;

vec2 cultmath_iv_point(float value) { return vec2(value, value); }
vec2 cultmath_iv_add(vec2 a, vec2 b) { return vec2(a.x + b.x, a.y + b.y); }
vec2 cultmath_iv_sub(vec2 a, vec2 b) { return vec2(a.x - b.y, a.y - b.x); }
vec2 cultmath_iv_neg(vec2 a) { return vec2(-a.y, -a.x); }

vec2 cultmath_iv_mul(vec2 a, vec2 b)
{
    float p0 = a.x * b.x;
    float p1 = a.x * b.y;
    float p2 = a.y * b.x;
    float p3 = a.y * b.y;
    return vec2(min(min(p0, p1), min(p2, p3)), max(max(p0, p1), max(p2, p3)));
}

vec2 cultmath_iv_scale(vec2 a, float s) { return s >= 0.0 ? vec2(a.x * s, a.y * s) : vec2(a.y * s, a.x * s); }

vec2 cultmath_iv_abs(vec2 a)
{
    if (a.x >= 0.0)
        return a;
    if (a.y <= 0.0)
        return vec2(-a.y, -a.x);
    return vec2(0.0, max(-a.x, a.y));
}

vec2 cultmath_iv_min(vec2 a, vec2 b) { return vec2(min(a.x, b.x), min(a.y, b.y)); }
vec2 cultmath_iv_max(vec2 a, vec2 b) { return vec2(max(a.x, b.x), max(a.y, b.y)); }

vec2 cultmath_iv_sqr(vec2 a)
{
    float l = a.x * a.x;
    float h = a.y * a.y;
    if (a.x >= 0.0)
        return vec2(l, h);
    if (a.y <= 0.0)
        return vec2(h, l);
    return vec2(0.0, max(l, h));
}

vec2 cultmath_iv_sqrt(vec2 a) { return vec2(sqrt(max(a.x, 0.0)), sqrt(max(a.y, 0.0))); }

// Widened by one ulp on each side: exp is not required to be correctly rounded.
vec2 cultmath_iv_exp(vec2 a)
{
    float lo = exp(a.x);
    float hi = exp(a.y);
    return vec2(lo > 0.0 ? uintBitsToFloat(floatBitsToUint(lo) - 1u) : 0.0, uintBitsToFloat(floatBitsToUint(hi) + 1u));
}

vec2 cultmath_iv_clamp(vec2 a, float minimum, float maximum)
{
    return vec2(cultmath_clamp(a.x, minimum, maximum), cultmath_clamp(a.y, minimum, maximum));
}

vec2 cultmath_iv_saturate(vec2 a) { return vec2(cultmath_saturate(a.x), cultmath_saturate(a.y)); }

// The endpoints, ordered, widened by one ulp inside [0, 1]: the float32 evaluation of smoothstep is
// monotone only to within one ulp.
vec2 cultmath_iv_smoothstep(float minimum, float maximum, vec2 a)
{
    float s0 = cultmath_smoothstep(minimum, maximum, a.x);
    float s1 = cultmath_smoothstep(minimum, maximum, a.y);
    float lo = min(s0, s1);
    float hi = max(s0, s1);
    return vec2(lo > 0.0 ? uintBitsToFloat(floatBitsToUint(lo) - 1u) : 0.0, hi < 1.0 ? uintBitsToFloat(floatBitsToUint(hi) + 1u) : 1.0);
}

// The natural interval extension of x + (y - x) * amount, which is how cultmath_lerp evaluates.
vec2 cultmath_iv_lerp(vec2 a, vec2 b, float amount) { return cultmath_iv_add(a, cultmath_iv_scale(cultmath_iv_sub(b, a), amount)); }

// [n - L r, n + L r] intersected with [-1, 1], n = cultmath_snoise(centre). One snoise, value only.
vec2 cultmath_iv_snoise_ball(vec3 centre, float radius)
{
    float n = cultmath_snoise(centre);
    float e = CULTMATH_SNOISE_LIPSCHITZ * radius;
    return vec2(max(n - e, -1.0), min(n + e, 1.0));
}

// Octave sum of cultmath_iv_snoise_ball, compounding frequency and amplitude as the value-and-gradient fBm does.
vec2 cultmath_iv_fbm_ball(vec3 centre, float radius, int octaves, float lacunarity, float gain)
{
    octaves = clamp(octaves, 0, 16);
    float amplitude = 1.0;
    float frequency = 1.0;
    vec2 sum = vec2(0.0, 0.0);
    for (int i = 0; i < octaves; i++)
    {
        sum = cultmath_iv_add(sum, cultmath_iv_scale(cultmath_iv_snoise_ball(centre * frequency, radius * abs(frequency)), amplitude));
        frequency *= lacunarity;
        amplitude *= gain;
    }

    return sum;
}

// A ball (xyz centre, w radius) enclosing every point of every ray of a screen tile over the depth segment
// [z0, z1], warped by at most warp. Camera frame: pinhole at the origin looking down +z, ray (m z, z).
// footprintPerDepth = N / (sqrt(2) f) for an N x N tile at focal length f. The radius is widened by
// (|c|_1 + radius) 2^-20, which covers the ball's own float32 rounding. Derivation in math.Interval.cs.
vec4 cultmath_iv_frustum_ball(vec2 centreSlope, float z0, float z1, float footprintPerDepth, float warp)
{
    float zm = (z0 + z1) * 0.5;
    float axis = sqrt(centreSlope.x * centreSlope.x + centreSlope.y * centreSlope.y + 1.0);
    float cx = centreSlope.x * zm;
    float cy = centreSlope.y * zm;
    float radius = (z1 - z0) * 0.5 * axis + z1 * footprintPerDepth + warp;
    radius += (abs(cx) + abs(cy) + abs(zm) + radius) * 9.5367431640625e-7;
    return vec4(cx, cy, zm, radius);
}




float cultmath_value_noise(vec2 position)
{
    vec2 cell = floor(position);
    vec2 local = cultmath_frac(position);
    vec2 u = local * local * (vec2(3.0, 3.0) - vec2(2.0, 2.0) * local);

    float a = cultmath_hash(cell);
    float b = cultmath_hash(cell + vec2(1.0, 0.0));
    float c = cultmath_hash(cell + vec2(0.0, 1.0));
    float d = cultmath_hash(cell + vec2(1.0, 1.0));

    return cultmath_lerp(cultmath_lerp(a, b, u.x), cultmath_lerp(c, d, u.x), u.y);
}

float cultmath_value_noise_bicubic(vec2 position)
{
    vec2 cell = floor(position);
    vec2 local = cultmath_frac(position);

    float y0 = cultmath_catmullrom(cultmath_hash(cell + vec2(-1.0, -1.0)), cultmath_hash(cell + vec2(0.0, -1.0)), cultmath_hash(cell + vec2(1.0, -1.0)), cultmath_hash(cell + vec2(2.0, -1.0)), local.x);
    float y1 = cultmath_catmullrom(cultmath_hash(cell + vec2(-1.0, 0.0)), cultmath_hash(cell + vec2(0.0, 0.0)), cultmath_hash(cell + vec2(1.0, 0.0)), cultmath_hash(cell + vec2(2.0, 0.0)), local.x);
    float y2 = cultmath_catmullrom(cultmath_hash(cell + vec2(-1.0, 1.0)), cultmath_hash(cell + vec2(0.0, 1.0)), cultmath_hash(cell + vec2(1.0, 1.0)), cultmath_hash(cell + vec2(2.0, 1.0)), local.x);
    float y3 = cultmath_catmullrom(cultmath_hash(cell + vec2(-1.0, 2.0)), cultmath_hash(cell + vec2(0.0, 2.0)), cultmath_hash(cell + vec2(1.0, 2.0)), cultmath_hash(cell + vec2(2.0, 2.0)), local.x);

    return cultmath_catmullrom(y0, y1, y2, y3, local.y);
}

#endif
