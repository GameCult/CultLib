#ifndef CULTMATH_PHACELLE_HLSL
#define CULTMATH_PHACELLE_HLSL

// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of
// the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Phacelle noise. Copyright (c) 2025 Rune Skovbo Johansen (Shadertoy t3dyWl; MPL-2.0), read from the
// C# port PhacelleNoise in lpmitchell/AdvancedTerrainErosion, converted to burstable C# by Luke
// Mitchell (2026). Modified by GameCult: 3D cells, a caller-supplied stripe wave vector, an exact
// gradient and exact pruning. This file is an MPL-2.0 file; CultMath.hlsl, which includes it after
// its own definitions of CULTMATH_TAU and cultmath_pcg3d, is MIT (see THIRD-PARTY-NOTICES.md).

// Invariant 8's struct return shape for cultmath_phacelle, compared field by field and bit for bit
// against CultMath.CultPhasor, same field order.
struct CultPhasor
{
    float4 cos;
    float4 sin;
};

// Johansen's Phacelle noise on 3D cells. See math.phacelle's comment (math.Phacelle.cs) for the stripe wave
// vector, the weight, the normalization, the exact gradient and why the prune is exact. Mirrors
// the C# body operation for operation; the C#-only internal overload that switches the prune off
// has no shader twin.
CultPhasor cultmath_phacelle(float3 p, float3 side, float offset, float normalization)
{
    float3 cell = floor(p);
    float3 local = p - cell;
    float phaseOffset = offset * CULTMATH_TAU;
    float sumW = 0.0;
    float sumCos = 0.0;
    float sumSin = 0.0;
    float3 gradW = float3(0.0, 0.0, 0.0);
    float3 gradCos = float3(0.0, 0.0, 0.0);
    float3 gradSin = float3(0.0, 0.0, 0.0);

    for (int dz = -1; dz <= 2; dz++)
    {
        float gz = max(0.0, abs(local.z - (float)dz) - 0.5);
        for (int dy = -1; dy <= 2; dy++)
        {
            float gy = max(0.0, abs(local.y - (float)dy) - 0.5);
            for (int dx = -1; dx <= 2; dx++)
            {
                float gx = max(0.0, abs(local.x - (float)dx) - 0.5);
                if (gx * gx + gy * gy + gz * gz >= 2.25)
                    continue;

                float3 gridStep = float3(dx, dy, dz);
                int3 hash = cultmath_pcg3d(int3(cell + gridStep));
                float3 jitter = float3(
                    ((uint)hash.x >> 8) * (1.0 / 16777216.0) - 0.5,
                    ((uint)hash.y >> 8) * (1.0 / 16777216.0) - 0.5,
                    ((uint)hash.z >> 8) * (1.0 / 16777216.0) - 0.5);
                float3 v = local - gridStep - jitter;
                float falloff = exp(-2.0 * dot(v, v));
                float w = max(0.0, falloff - 0.01111);
                float3 dw = w > 0.0 ? (-4.0 * falloff) * v : float3(0.0, 0.0, 0.0);
                float phase = dot(v, side) + phaseOffset;
                float c = cos(phase);
                float s = sin(phase);

                sumW += w;
                sumCos += w * c;
                sumSin += w * s;
                gradW += dw;
                gradCos += dw * c - (w * s) * side;
                gradSin += dw * s + (w * c) * side;
            }
        }
    }

    float invW = 1.0 / sumW;
    float rawCos = sumCos * invW;
    float rawSin = sumSin * invW;
    float3 gradRawCos = (gradCos - rawCos * gradW) * invW;
    float3 gradRawSin = (gradSin - rawSin * gradW) * invW;
    float rawLength = sqrt(rawCos * rawCos + rawSin * rawSin);
    float floorLength = 1.0 - normalization;

    CultPhasor result;
    if (rawLength > floorLength)
    {
        float outCos = rawCos / rawLength;
        float outSin = rawSin / rawLength;
        float3 gradLength = (rawCos * gradRawCos + rawSin * gradRawSin) / rawLength;
        result.cos = float4((gradRawCos - outCos * gradLength) / rawLength, outCos);
        result.sin = float4((gradRawSin - outSin * gradLength) / rawLength, outSin);
    }
    else
    {
        result.cos = float4(gradRawCos / floorLength, rawCos / floorLength);
        result.sin = float4(gradRawSin / floorLength, rawSin / floorLength);
    }

    return result;
}

#endif
