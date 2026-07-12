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
static const float CULTMATH_NORMALIZE_EPSILON = 1.0e-20;

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

float cultmath_saturate(float value) { return cultmath_clamp(value, 0.0, 1.0); }
float2 cultmath_saturate(float2 value) { return cultmath_clamp(value, float2(0.0, 0.0), float2(1.0, 1.0)); }
float3 cultmath_saturate(float3 value) { return cultmath_clamp(value, float3(0.0, 0.0, 0.0), float3(1.0, 1.0, 1.0)); }
float4 cultmath_saturate(float4 value) { return cultmath_clamp(value, float4(0.0, 0.0, 0.0, 0.0), float4(1.0, 1.0, 1.0, 1.0)); }

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

float cultmath_lengthsq(float2 value) { return dot(value, value); }
float cultmath_lengthsq(float3 value) { return dot(value, value); }
float cultmath_lengthsq(float4 value) { return dot(value, value); }

float cultmath_distance(float2 left, float2 right) { return length(left - right); }
float cultmath_distance(float3 left, float3 right) { return length(left - right); }
float cultmath_distance(float4 left, float4 right) { return length(left - right); }

float2 cultmath_normalize(float2 value) { return value / max(length(value), CULTMATH_NORMALIZE_EPSILON); }
float3 cultmath_normalize(float3 value) { return value / max(length(value), CULTMATH_NORMALIZE_EPSILON); }
float4 cultmath_normalize(float4 value) { return value / max(length(value), CULTMATH_NORMALIZE_EPSILON); }

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

struct CultMathSphericalErosionParameters
{
    float frequency; float amplitude; float lacunarity; float gain;
    float slope_strength; float detail; float seed; int octaves;
};

struct CultMathSphericalErosionSample
{
    float height_offset; float ridge; float gully; float3 gradient;
};

float cultmath_spherical_cell_wave(float3 position, float3 flow, float3 cell, float seed)
{
    float3 pivot = cell + float3(cultmath_hash(cell + seed + 11.7), cultmath_hash(cell.yzx + seed + 37.1), cultmath_hash(cell.zyx + seed + 73.9));
    return dot(position - pivot, flow) * 6.28318530718 + cultmath_hash(cell + seed + 101.3) * 6.28318530718;
}

float cultmath_spherical_directional_wave(float3 position, float3 flow, float seed)
{
    float3 cell = floor(position);
    float3 local = cultmath_frac(position);
    float3 weights = local * local * (3.0 - 2.0 * local);
    float z0 = cultmath_lerp(
        cultmath_lerp(cultmath_spherical_cell_wave(position, flow, cell + float3(0,0,0), seed), cultmath_spherical_cell_wave(position, flow, cell + float3(1,0,0), seed), weights.x),
        cultmath_lerp(cultmath_spherical_cell_wave(position, flow, cell + float3(0,1,0), seed), cultmath_spherical_cell_wave(position, flow, cell + float3(1,1,0), seed), weights.x), weights.y);
    float z1 = cultmath_lerp(
        cultmath_lerp(cultmath_spherical_cell_wave(position, flow, cell + float3(0,0,1), seed), cultmath_spherical_cell_wave(position, flow, cell + float3(1,0,1), seed), weights.x),
        cultmath_lerp(cultmath_spherical_cell_wave(position, flow, cell + float3(0,1,1), seed), cultmath_spherical_cell_wave(position, flow, cell + float3(1,1,1), seed), weights.x), weights.y);
    return cultmath_lerp(z0, z1, weights.z);
}

float3 cultmath_spherical_stable_tangent(float3 position)
{
    float3 axis = abs(position.z) < 0.8 ? float3(0,0,1) : float3(0,1,0);
    return cultmath_normalize(cross(axis, position));
}

CultMathSphericalErosionSample cultmath_spherical_erosion(float3 unit_position, float base_height, float3 base_gradient, CultMathSphericalErosionParameters p)
{
    float3 position = cultmath_normalize(unit_position);
    float3 gradient = base_gradient - position * dot(base_gradient, position);
    float slope = length(gradient);
    float3 flow = slope > 1.0e-6 ? -gradient / slope : cultmath_spherical_stable_tangent(position);
    float frequency = max(p.frequency, 0.001);
    float amplitude = p.amplitude;
    float combi_mask = 1.0 - pow(1.0 - cultmath_saturate(slope * p.slope_strength), 2.0);
    float fade_target = cultmath_saturate(base_height * 0.5 + 0.5) * 2.0 - 1.0;
    CultMathSphericalErosionSample result = (CultMathSphericalErosionSample)0;
    [loop] for (int octave = 0; octave < min(max(p.octaves, 0), 8); octave++)
    {
        float phase = cultmath_spherical_directional_wave(position * frequency, flow, p.seed + octave * 19.19);
        float wave = cos(phase);
        float wave_slope = sin(phase);
        float faded = cultmath_lerp(fade_target, wave, combi_mask);
        result.height_offset += faded * amplitude;
        result.ridge = max(result.ridge, cultmath_saturate(faded));
        result.gully = max(result.gully, cultmath_saturate(-faded));
        float3 across = cultmath_normalize(cross(position, flow));
        gradient += across * (sign(wave_slope) * amplitude * frequency * combi_mask);
        gradient -= position * dot(gradient, position);
        slope = length(gradient);
        flow = slope > 1.0e-6 ? -gradient / slope : cultmath_spherical_stable_tangent(position);
        float new_mask = 1.0 - pow(1.0 - cultmath_saturate(abs(wave_slope) * 1.35), 2.0);
        combi_mask = (1.0 - pow(1.0 - cultmath_saturate(combi_mask), max(p.detail, 0.001))) * new_mask;
        fade_target = faded;
        frequency *= max(p.lacunarity, 1.01);
        amplitude *= cultmath_saturate(p.gain);
    }
    result.gradient = gradient;
    return result;
}

#endif
