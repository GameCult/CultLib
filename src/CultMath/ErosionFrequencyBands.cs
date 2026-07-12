namespace CultMath;

public readonly record struct ErosionBandSelection(
    int ActiveOctaves,
    float FinalOctaveWeight,
    float FinestIncludedWavelength,
    float UnresolvedHeightBound)
{
    public bool HasSamples => ActiveOctaves > 0;
}

public static class ErosionFrequencyBands
{
    public static ErosionBandSelection Select(
        float baseWavelength,
        float sampleSpacing,
        int maximumOctaves,
        float lacunarity,
        float baseAmplitude,
        float gain,
        float transitionRatio = 2.0f)
    {
        baseWavelength = math.max(baseWavelength, 1.0e-6f);
        sampleSpacing = math.max(sampleSpacing, 0.0f);
        lacunarity = math.max(lacunarity, 1.01f);
        transitionRatio = math.max(transitionRatio, 1.01f);
        maximumOctaves = math.min(math.max(maximumOctaves, 0), 16);
        var nyquistWavelength = sampleSpacing * 2.0f;
        var wavelength = baseWavelength;
        var active = 0;
        var finalWeight = 0.0f;
        var finest = 0.0f;

        for (var octave = 0; octave < maximumOctaves; octave++)
        {
            var weight = nyquistWavelength <= 0.0f
                ? 1.0f
                : math.smoothstep(nyquistWavelength, nyquistWavelength * transitionRatio, wavelength);
            if (weight <= 0.0f) break;
            active++;
            finalWeight = weight;
            finest = wavelength;
            wavelength /= lacunarity;
        }

        var omittedAmplitude = 0.0f;
        var amplitude = math.abs(baseAmplitude);
        for (var octave = 0; octave < maximumOctaves; octave++)
        {
            if (octave >= active) omittedAmplitude += amplitude;
            else if (octave == active - 1) omittedAmplitude += amplitude * (1.0f - finalWeight);
            amplitude *= math.abs(gain);
        }

        return new ErosionBandSelection(active, active > 0 ? finalWeight : 0.0f, finest, omittedAmplitude);
    }
}
