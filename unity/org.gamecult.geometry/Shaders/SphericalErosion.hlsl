// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

#ifndef GAMECULT_GEOMETRY_SPHERICAL_EROSION_HLSL
#define GAMECULT_GEOMETRY_SPHERICAL_EROSION_HLSL

struct GameCultGeometrySphericalErosionParameters
{
    float frequency; float amplitude; float lacunarity; float gain;
    float slope_strength; float detail; float seed; int octaves;
};

struct GameCultGeometrySphericalErosionSample
{
    float height_offset; float ridge; float gully; float3 gradient;
};

float gamecult_geometry_spherical_cell_wave(float3 position, float3 flow, float3 cell, float seed)
{
    float3 pivot = cell + float3(cultmath_hash(cell + seed + 11.7), cultmath_hash(cell.yzx + seed + 37.1), cultmath_hash(cell.zyx + seed + 73.9));
    return dot(position - pivot, flow) * 6.28318530718 + cultmath_hash(cell + seed + 101.3) * 6.28318530718;
}

float gamecult_geometry_spherical_directional_wave(float3 position, float3 flow, float seed)
{
    float3 cell = floor(position);
    float3 local = cultmath_frac(position);
    float3 weights = local * local * (3.0 - 2.0 * local);
    float z0 = cultmath_lerp(
        cultmath_lerp(gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(0,0,0), seed), gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(1,0,0), seed), weights.x),
        cultmath_lerp(gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(0,1,0), seed), gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(1,1,0), seed), weights.x), weights.y);
    float z1 = cultmath_lerp(
        cultmath_lerp(gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(0,0,1), seed), gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(1,0,1), seed), weights.x),
        cultmath_lerp(gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(0,1,1), seed), gamecult_geometry_spherical_cell_wave(position, flow, cell + float3(1,1,1), seed), weights.x), weights.y);
    return cultmath_lerp(z0, z1, weights.z);
}

float3 gamecult_geometry_spherical_stable_tangent(float3 position)
{
    float3 axis = abs(position.z) < 0.8 ? float3(0,0,1) : float3(0,1,0);
    return cultmath_normalize(cross(axis, position));
}

GameCultGeometrySphericalErosionSample gamecult_geometry_spherical_erosion(float3 unit_position, float base_height, float3 base_gradient, GameCultGeometrySphericalErosionParameters p)
{
    float3 position = cultmath_normalize(unit_position);
    float3 gradient = base_gradient - position * dot(base_gradient, position);
    float slope = length(gradient);
    float3 flow = slope > 1.0e-6 ? -gradient / slope : gamecult_geometry_spherical_stable_tangent(position);
    float frequency = max(p.frequency, 0.001);
    float amplitude = p.amplitude;
    float combi_mask = 1.0 - pow(1.0 - cultmath_saturate(slope * p.slope_strength), 2.0);
    float fade_target = cultmath_saturate(base_height * 0.5 + 0.5) * 2.0 - 1.0;
    GameCultGeometrySphericalErosionSample result = (GameCultGeometrySphericalErosionSample)0;
    [loop] for (int octave = 0; octave < min(max(p.octaves, 0), 8); octave++)
    {
        float phase = gamecult_geometry_spherical_directional_wave(position * frequency, flow, p.seed + octave * 19.19);
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
        flow = slope > 1.0e-6 ? -gradient / slope : gamecult_geometry_spherical_stable_tangent(position);
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
