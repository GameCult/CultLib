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
float3 cultmath_snoise_permute(float3 value) { return cultmath_snoise_mod289(((value * 34.0) + 1.0) * value); }
float4 cultmath_snoise_permute(float4 value) { return cultmath_snoise_mod289(((value * 34.0) + 1.0) * value); }

// Ashima Arts / Ian McEwan 3D simplex noise (MIT), same float32 evaluation
// order as the C# math.snoise(float3) mirror.
float cultmath_snoise(float3 value)
{
    float2 c = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(value + dot(value, c.yyy));
    float3 x0 = value - i + dot(i, c.xxx);
    float3 g = step(x0.yzx, x0.xyz);
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
    float4 sh = -step(h, 0.0);
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

    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
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
    return cultmath_lerp(start, end, 1.0 - exp(-lambda_value * dt));
}

float3 cultmath_damp(float3 start, float3 end, float lambda_value, float dt)
{
    return cultmath_lerp(start, end, 1.0 - exp(-lambda_value * dt));
}

float4 cultmath_damp(float4 start, float4 end, float lambda_value, float dt)
{
    return cultmath_lerp(start, end, 1.0 - exp(-lambda_value * dt));
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
    uint word = ((state >> (int)((state >> 28) + 4)) ^ state) * 277803737u;
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

int3 cultmath_pcg3d(float2 value) { return cultmath_pcg3d(float3(value, 0.0)); }
int3 cultmath_pcg3d(float3 value) { return cultmath_pcg3d(int3((int)asuint(value.x), (int)asuint(value.y), (int)asuint(value.z))); }

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
