// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.
// Mathematical procedure adapted from Rune Skovbo Johansen's Advanced Terrain
// Erosion Filter (2025): https://www.shadertoy.com/view/wXcfWn

#ifndef CULTMATH_ADVANCED_EROSION_FILTER_HLSL
#define CULTMATH_ADVANCED_EROSION_FILTER_HLSL

struct CultMathAdvancedErosionParameters
{
    float scale; float strength; float gully_weight; float detail;
    float4 rounding; float4 onset; float2 assumed_slope;
    float cell_scale; float normalization; int octaves; float lacunarity; float gain;
};

struct CultMathAdvancedErosionResult
{
    float3 delta; float magnitude; float ridge_map; float fade_target;
};

float cultmath_erosion_hash_unit(int2 cell, uint salt)
{
    uint value = (uint)cell.x * 0x8da6b343u ^ (uint)cell.y * 0xd8163841u ^ salt;
    value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu; value ^= value >> 16;
    return (value & 0x00ffffffu) * (1.0 / 16777216.0);
}
float2 cultmath_erosion_hash2(float2 input)
{
    int2 cell = (int2)input;
    return float2(cultmath_erosion_hash_unit(cell, 0x68bc21ebu), cultmath_erosion_hash_unit(cell, 0x02e5be93u)) * 2.0 - 1.0;
}

float4 cultmath_phacelle_noise(float2 position, float2 normal_direction, float frequency, float offset_cycles, float normalization)
{
    float2 side_direction = float2(-normal_direction.y, normal_direction.x) * frequency * 6.28318530718;
    float offset = offset_cycles * 6.28318530718;
    float2 cell = floor(position);
    float2 local = frac(position);
    float2 phase = 0.0;
    float weight_sum = 0.0;
    [unroll] for (int x = -1; x <= 2; x++)
    [unroll] for (int y = -1; y <= 2; y++)
    {
        float2 grid_offset = float2(x, y);
        float2 grid_point = cell + grid_offset;
        float2 random_offset = cultmath_erosion_hash2(grid_point) * 0.5;
        float2 delta = local - grid_offset - random_offset;
        float weight = max(0.0, exp(-dot(delta, delta) * 2.0) - 0.01111);
        weight_sum += weight;
        float wave = dot(delta, side_direction) + offset;
        phase += float2(cos(wave), sin(wave)) * weight;
    }
    float2 interpolated = phase / max(weight_sum, 1.0e-20);
    float magnitude = max(1.0 - saturate(normalization), length(interpolated));
    return float4(interpolated / max(magnitude, 1.0e-20), side_direction);
}

float cultmath_erosion_pow_inverse(float value, float power) { return 1.0 - pow(1.0 - saturate(value), power); }
float cultmath_erosion_ease_out(float value) { float inverse = 1.0 - saturate(value); return 1.0 - inverse * inverse; }
float cultmath_erosion_smooth_start(float value, float smoothing)
{
    return smoothing <= 1.0e-10 || value >= smoothing ? value - 0.5 * smoothing : 0.5 * value * value / smoothing;
}
float2 cultmath_erosion_safe_normalize(float2 value)
{
    float value_length = length(value);
    return abs(value_length) > 1.0e-10 ? value / value_length : value;
}

CultMathAdvancedErosionResult cultmath_advanced_erosion_filter(float2 position, float3 base_height_and_slope, float fade_target, CultMathAdvancedErosionParameters p)
{
    float strength = p.strength * p.scale;
    fade_target = clamp(fade_target, -1.0, 1.0);
    float3 input_value = base_height_and_slope;
    float3 height_and_slope = input_value;
    float frequency = 1.0 / max(p.scale * p.cell_scale, 1.0e-10);
    float2 slope = height_and_slope.yz;
    float slope_length = max(length(slope), 1.0e-10);
    float magnitude = 0.0;
    float rounding_multiplier = 1.0;
    float input_rounding = lerp(p.rounding.y, p.rounding.x, saturate(fade_target + 0.5)) * p.rounding.z;
    float combined_mask = cultmath_erosion_ease_out(cultmath_erosion_smooth_start(slope_length * p.onset.x, input_rounding * p.onset.x));
    float ridge_mask = cultmath_erosion_ease_out(slope_length * p.onset.z);
    float ridge_fade = fade_target;
    float2 gully_slope = lerp(slope, slope / slope_length * p.assumed_slope.x, p.assumed_slope.y);

    [loop] for (int octave = 0; octave < min(max(p.octaves, 0), 8); octave++)
    {
        float4 phacelle = cultmath_phacelle_noise(position * frequency, cultmath_erosion_safe_normalize(gully_slope), p.cell_scale, 0.25, p.normalization);
        float2 derivative_direction = phacelle.zw * -frequency;
        float sloping = abs(phacelle.y);
        gully_slope += sign(phacelle.y) * derivative_direction * strength * p.gully_weight;
        float3 gullies = float3(phacelle.x, phacelle.y * derivative_direction.x, phacelle.y * derivative_direction.y);
        float3 faded = lerp(float3(fade_target, 0.0, 0.0), gullies * p.gully_weight, combined_mask);
        height_and_slope += faded * strength;
        magnitude += strength;
        fade_target = faded.x;
        float octave_rounding = lerp(p.rounding.y, p.rounding.x, saturate(phacelle.x + 0.5)) * rounding_multiplier;
        float new_mask = cultmath_erosion_ease_out(cultmath_erosion_smooth_start(sloping * p.onset.y, octave_rounding * p.onset.y));
        combined_mask = cultmath_erosion_pow_inverse(combined_mask, p.detail) * new_mask;
        ridge_fade = lerp(ridge_fade, gullies.x, ridge_mask);
        ridge_mask *= cultmath_erosion_ease_out(sloping * p.onset.w);
        strength *= p.gain;
        frequency *= p.lacunarity;
        rounding_multiplier *= p.rounding.w;
    }

    CultMathAdvancedErosionResult result;
    result.delta = height_and_slope - input_value;
    result.magnitude = magnitude;
    result.ridge_map = ridge_fade * (1.0 - ridge_mask);
    result.fade_target = fade_target;
    return result;
}

#endif
