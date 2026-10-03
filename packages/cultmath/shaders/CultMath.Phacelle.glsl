// Generated from shaders/CultMath.Phacelle.hlsl by GlslLowering (packages/cultmath/tests/CultMath.Tests/GlslLowering.cs). Do not edit; regenerate with CULTMATH_WRITE_GLSL=1 dotnet test --filter GlslMirrorTests. MPL-2.0; requires CultMath.glsl before it.
#ifndef CULTMATH_PHACELLE_GLSL
#define CULTMATH_PHACELLE_GLSL
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
    vec4 cos;
    vec4 sin;
};

// Johansen's Phacelle noise on 3D cells. See math.phacelle's comment (math.Phacelle.cs) for the stripe wave
// vector, the weight, the normalization, the exact gradient and why the prune is exact. Mirrors
// the C# body operation for operation; the C#-only internal overload that switches the prune off
// has no shader twin.
CultPhasor cultmath_phacelle(vec3 p, vec3 side, float offset, float normalization)
{
    vec3 cell = floor(p);
    vec3 local = p - cell;
    float phaseOffset = offset * CULTMATH_TAU;
    float sumW = 0.0;
    float sumCos = 0.0;
    float sumSin = 0.0;
    vec3 gradW = vec3(0.0, 0.0, 0.0);
    vec3 gradCos = vec3(0.0, 0.0, 0.0);
    vec3 gradSin = vec3(0.0, 0.0, 0.0);

    for (int dz = -1; dz <= 2; dz++)
    {
        float gz = max(0.0, abs(local.z - float(dz)) - 0.5);
        for (int dy = -1; dy <= 2; dy++)
        {
            float gy = max(0.0, abs(local.y - float(dy)) - 0.5);
            for (int dx = -1; dx <= 2; dx++)
            {
                float gx = max(0.0, abs(local.x - float(dx)) - 0.5);
                if (gx * gx + gy * gy + gz * gz >= 2.25)
                    continue;

                vec3 gridStep = vec3(dx, dy, dz);
                ivec3 hash = cultmath_pcg3d(ivec3(cell + gridStep));
                vec3 jitter = vec3(
                    float(uint(hash.x) >> 8) * (1.0 / 16777216.0) - 0.5,
                    float(uint(hash.y) >> 8) * (1.0 / 16777216.0) - 0.5,
                    float(uint(hash.z) >> 8) * (1.0 / 16777216.0) - 0.5);
                vec3 v = local - gridStep - jitter;
                float falloff = exp(-2.0 * dot(v, v));
                float w = max(0.0, falloff - 0.01111);
                vec3 dw = w > 0.0 ? (-4.0 * falloff) * v : vec3(0.0, 0.0, 0.0);
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
    vec3 gradRawCos = (gradCos - rawCos * gradW) * invW;
    vec3 gradRawSin = (gradSin - rawSin * gradW) * invW;
    float rawLength = sqrt(rawCos * rawCos + rawSin * rawSin);
    float floorLength = 1.0 - normalization;

    CultPhasor result;
    if (rawLength > floorLength)
    {
        float outCos = rawCos / rawLength;
        float outSin = rawSin / rawLength;
        vec3 gradLength = (rawCos * gradRawCos + rawSin * gradRawSin) / rawLength;
        result.cos = vec4((gradRawCos - outCos * gradLength) / rawLength, outCos);
        result.sin = vec4((gradRawSin - outSin * gradLength) / rawLength, outSin);
    }
    else
    {
        result.cos = vec4(gradRawCos / floorLength, rawCos / floorLength);
        result.sin = vec4(gradRawSin / floorLength, rawSin / floorLength);
    }

    return result;
}

#endif
