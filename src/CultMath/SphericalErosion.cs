namespace CultMath;

public readonly record struct SphericalErosionParameters(
    float Frequency,
    float Amplitude,
    float Lacunarity,
    float Gain,
    float SlopeStrength,
    float Detail,
    float Seed,
    int Octaves)
{
    public static SphericalErosionParameters Default => new(18.0f, 0.035f, 2.03f, 0.52f, 6.0f, 1.35f, 0.0f, 5);
}

public readonly record struct SphericalErosionSample(float HeightOffset, float Ridge, float Gully, float3 Gradient);

/// <summary>
/// Stateless, point-evaluable erosion-like synthesis on a unit sphere.
/// The matching shader body is cultmath_spherical_erosion in CultMath.hlsl.
/// Position and gradient are planet-space; the gradient is projected tangent
/// internally so cube faces and tile request order never enter the result.
/// </summary>
public static class SphericalErosion
{
    public static SphericalErosionSample Sample(
        float3 unitPosition,
        float baseHeight,
        float3 baseGradient,
        SphericalErosionParameters parameters)
    {
        var position = math.normalize(unitPosition);
        var gradient = baseGradient - position * math.dot(baseGradient, position);
        var slope = math.length(gradient);
        var flow = slope > 1.0e-6f ? -gradient / slope : StableTangent(position);
        var frequency = math.max(parameters.Frequency, 0.001f);
        var amplitude = parameters.Amplitude;
        var combiMask = EaseOut(math.saturate(slope * parameters.SlopeStrength));
        var fadeTarget = math.saturate(baseHeight * 0.5f + 0.5f) * 2.0f - 1.0f;
        var heightOffset = 0.0f;
        var ridge = 0.0f;
        var gully = 0.0f;

        var octaveCount = math.min(math.max(parameters.Octaves, 0), 8);
        for (var octave = 0; octave < octaveCount; octave++)
        {
            var phase = DirectionalCellWave(position * frequency, flow, parameters.Seed + octave * 19.19f);
            var wave = math.cos(phase);
            var waveSlope = math.sin(phase);
            var faded = math.lerp(fadeTarget, wave, combiMask);
            heightOffset += faded * amplitude;
            ridge = math.max(ridge, math.saturate(faded));
            gully = math.max(gully, math.saturate(-faded));

            var across = math.normalize(math.cross(position, flow));
            gradient += across * (math.sign(waveSlope) * amplitude * frequency * combiMask);
            gradient -= position * math.dot(gradient, position);
            slope = math.length(gradient);
            flow = slope > 1.0e-6f ? -gradient / slope : StableTangent(position);

            var newMask = EaseOut(math.saturate(math.abs(waveSlope) * 1.35f));
            combiMask = PowInverse(combiMask, parameters.Detail) * newMask;
            fadeTarget = faded;
            frequency *= math.max(parameters.Lacunarity, 1.01f);
            amplitude *= math.saturate(parameters.Gain);
        }

        return new SphericalErosionSample(heightOffset, ridge, gully, gradient);
    }

    private static float DirectionalCellWave(float3 position, float3 flow, float seed)
    {
        var cell = math.floor(position);
        var local = math.frac(position);
        var weights = local * local * (3.0f - 2.0f * local);
        var x00 = CellWave(position, flow, cell + new float3(0, 0, 0), seed);
        var x10 = CellWave(position, flow, cell + new float3(1, 0, 0), seed);
        var x01 = CellWave(position, flow, cell + new float3(0, 1, 0), seed);
        var x11 = CellWave(position, flow, cell + new float3(1, 1, 0), seed);
        var y00 = math.lerp(x00, x10, weights.x);
        var y10 = math.lerp(x01, x11, weights.x);
        var z0 = math.lerp(y00, y10, weights.y);
        var x00z = CellWave(position, flow, cell + new float3(0, 0, 1), seed);
        var x10z = CellWave(position, flow, cell + new float3(1, 0, 1), seed);
        var x01z = CellWave(position, flow, cell + new float3(0, 1, 1), seed);
        var x11z = CellWave(position, flow, cell + new float3(1, 1, 1), seed);
        var z1 = math.lerp(math.lerp(x00z, x10z, weights.x), math.lerp(x01z, x11z, weights.x), weights.y);
        return math.lerp(z0, z1, weights.z);
    }

    private static float CellWave(float3 position, float3 flow, float3 cell, float seed)
    {
        var pivot = cell + new float3(
            math.hash(cell + seed + 11.7f),
            math.hash(cell.yzx + seed + 37.1f),
            math.hash(cell.zyx + seed + 73.9f));
        return math.dot(position - pivot, flow) * math.TAU + math.hash(cell + seed + 101.3f) * math.TAU;
    }

    private static float3 StableTangent(float3 position)
    {
        var axis = math.abs(position.z) < 0.8f ? new float3(0, 0, 1) : new float3(0, 1, 0);
        return math.normalize(math.cross(axis, position));
    }

    private static float EaseOut(float value) => 1.0f - (1.0f - value) * (1.0f - value);
    private static float PowInverse(float value, float power) => 1.0f - math.pow(1.0f - math.saturate(value), math.max(power, 0.001f));
}
