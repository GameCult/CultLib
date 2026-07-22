// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Mathematical procedure adapted from Rune Skovbo Johansen's Advanced Terrain
// Erosion Filter (2025): https://www.shadertoy.com/view/wXcfWn

using CultMath;

namespace GameCult.Geometry;

public readonly record struct AdvancedErosionParameters(
    float Scale,
    float Strength,
    float GullyWeight,
    float Detail,
    float4 Rounding,
    float4 Onset,
    float2 AssumedSlope,
    float CellScale,
    float Normalization,
    int Octaves,
    float Lacunarity,
    float Gain)
{
    public static AdvancedErosionParameters Default => new(
        0.15f, 0.22f, 0.5f, 1.5f,
        new float4(0.1f, 0.0f, 0.1f, 2.0f),
        new float4(1.25f, 1.25f, 2.8f, 1.5f),
        new float2(0.7f, 1.0f),
        0.7f, 0.5f, 5, 2.0f, 0.5f);
}

public readonly record struct AdvancedErosionResult(float3 Delta, float Magnitude, float RidgeMap, float FadeTarget);

public static class AdvancedErosionFilter
{
    public static AdvancedErosionResult Sample(float2 position, float3 baseHeightAndSlope, float fadeTarget, AdvancedErosionParameters p)
        => Sample(position, baseHeightAndSlope, fadeTarget, p, new ErosionBandSelection(p.Octaves, 1.0f, 0.0f, 0.0f));

    public static AdvancedErosionResult Sample(float2 position, float3 baseHeightAndSlope, float fadeTarget, AdvancedErosionParameters p, ErosionBandSelection band)
    {
        var strength = p.Strength * p.Scale;
        fadeTarget = math.clamp(fadeTarget, -1.0f, 1.0f);
        var input = baseHeightAndSlope;
        var heightAndSlope = input;
        var frequency = 1.0f / math.max(p.Scale * p.CellScale, 1.0e-10f);
        var slope = new float2(heightAndSlope.y, heightAndSlope.z);
        var slopeLength = math.max(math.length(slope), 1.0e-10f);
        var magnitude = 0.0f;
        var roundingMultiplier = 1.0f;

        var inputRounding = math.lerp(p.Rounding.y, p.Rounding.x, math.saturate(fadeTarget + 0.5f)) * p.Rounding.z;
        var combinedMask = EaseOut(SmoothStart(slopeLength * p.Onset.x, inputRounding * p.Onset.x));
        var ridgeMask = EaseOut(slopeLength * p.Onset.z);
        var ridgeFade = fadeTarget;
        var gullySlope = math.lerp(slope, slope / slopeLength * p.AssumedSlope.x, p.AssumedSlope.y);

        var octaves = math.min(math.min(math.max(p.Octaves, 0), 8), math.max(band.ActiveOctaves, 0));
        for (var octave = 0; octave < octaves; octave++)
        {
            var phacelle = Phacelle(position * frequency, SafeNormalize(gullySlope), p.CellScale, 0.25f, p.Normalization);
            var derivativeDirection = new float2(phacelle.z, phacelle.w) * -frequency;
            var sloping = math.abs(phacelle.y);
            gullySlope += math.sign(phacelle.y) * derivativeDirection * strength * p.GullyWeight;

            var gullies = new float3(phacelle.x, phacelle.y * derivativeDirection.x, phacelle.y * derivativeDirection.y);
            var faded = math.lerp(new float3(fadeTarget, 0.0f, 0.0f), gullies * p.GullyWeight, combinedMask);
            var octaveWeight = octave == octaves - 1 ? math.saturate(band.FinalOctaveWeight) : 1.0f;
            heightAndSlope += faded * strength * octaveWeight;
            magnitude += strength * octaveWeight;
            fadeTarget = faded.x;

            var octaveRounding = math.lerp(p.Rounding.y, p.Rounding.x, math.saturate(phacelle.x + 0.5f)) * roundingMultiplier;
            var newMask = EaseOut(SmoothStart(sloping * p.Onset.y, octaveRounding * p.Onset.y));
            combinedMask = PowInverse(combinedMask, p.Detail) * newMask;

            ridgeFade = math.lerp(ridgeFade, math.lerp(ridgeFade, gullies.x, ridgeMask), octaveWeight);
            ridgeMask *= math.lerp(1.0f, EaseOut(sloping * p.Onset.w), octaveWeight);
            strength *= p.Gain;
            frequency *= p.Lacunarity;
            roundingMultiplier *= p.Rounding.w;
        }

        return new AdvancedErosionResult(heightAndSlope - input, magnitude, ridgeFade * (1.0f - ridgeMask), fadeTarget);
    }

    public static float4 Phacelle(float2 position, float2 normalDirection, float frequency, float offsetCycles, float normalization)
    {
        var sideDirection = new float2(-normalDirection.y, normalDirection.x) * frequency * math.TAU;
        var offset = offsetCycles * math.TAU;
        var cell = math.floor(position);
        var local = math.frac(position);
        var phase = float2.zero;
        var weightSum = 0.0f;

        for (var x = -1; x <= 2; x++)
        for (var y = -1; y <= 2; y++)
        {
            var gridOffset = new float2(x, y);
            var gridPoint = cell + gridOffset;
            var randomOffset = Hash2(gridPoint) * 0.5f;
            var delta = local - gridOffset - randomOffset;
            var weight = math.max(0.0f, math.exp(-math.dot(delta, delta) * 2.0f) - 0.01111f);
            weightSum += weight;
            var wave = math.dot(delta, sideDirection) + offset;
            phase += new float2(math.cos(wave), math.sin(wave)) * weight;
        }

        var interpolated = phase / math.max(weightSum, 1.0e-20f);
        var magnitude = math.max(1.0f - math.saturate(normalization), math.length(interpolated));
        var normalized = interpolated / math.max(magnitude, 1.0e-20f);
        return new float4(normalized, sideDirection.x, sideDirection.y);
    }

    private static float2 Hash2(float2 input)
    {
        var x = (int)input.x;
        var y = (int)input.y;
        return new float2(HashUnit(x, y, 0x68bc21ebu), HashUnit(x, y, 0x02e5be93u)) * 2.0f - float2.one;
    }

    private static float HashUnit(int x, int y, uint salt)
    {
        var value = unchecked((uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ salt);
        value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu; value ^= value >> 16;
        return (value & 0x00ffffffu) * (1.0f / 16777216.0f);
    }

    private static float2 SafeNormalize(float2 value)
    {
        var length = math.length(value);
        return math.abs(length) > 1.0e-10f ? value / length : value;
    }

    private static float PowInverse(float value, float power) => 1.0f - math.pow(1.0f - math.saturate(value), power);
    private static float EaseOut(float value) { var inverse = 1.0f - math.saturate(value); return 1.0f - inverse * inverse; }
    private static float SmoothStart(float value, float smoothing) => smoothing <= 1.0e-10f || value >= smoothing
        ? value - 0.5f * smoothing
        : 0.5f * value * value / smoothing;
}
