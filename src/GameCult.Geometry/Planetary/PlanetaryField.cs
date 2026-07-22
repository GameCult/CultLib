using System;
using CultMath;

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

namespace GameCult.Geometry;

public readonly record struct PlanetaryQueryScale(float FootprintMeters, float MaximumUnresolvedHeight)
{
    public static PlanetaryQueryScale AtFootprint(float footprintMeters, float maximumUnresolvedHeight = float.PositiveInfinity)
        => new PlanetaryQueryScale(footprintMeters, maximumUnresolvedHeight).Validate();

    public PlanetaryQueryScale Validate()
    {
        if (!float.IsFinite(FootprintMeters) || FootprintMeters < 0) throw new ArgumentOutOfRangeException(nameof(FootprintMeters));
        if (float.IsNaN(MaximumUnresolvedHeight) || MaximumUnresolvedHeight < 0) throw new ArgumentOutOfRangeException(nameof(MaximumUnresolvedHeight));
        return this;
    }
}

public readonly record struct PlanetaryFieldDefinition(
    ulong FieldVersion,
    float Radius,
    int Seed,
    AdvancedErosionParameters Erosion)
{
    public static PlanetaryFieldDefinition Create(
        ulong baseFieldVersion,
        float radius,
        int seed,
        AdvancedErosionParameters erosion)
        => new PlanetaryFieldDefinition(PlanetaryFieldIdentity.Compute(baseFieldVersion, radius, seed, erosion), radius, seed, erosion).Validate();

    public PlanetaryFieldDefinition Validate()
    {
        if (FieldVersion == 0) throw new ArgumentOutOfRangeException(nameof(FieldVersion));
        if (!float.IsFinite(Radius) || Radius <= 0) throw new ArgumentOutOfRangeException(nameof(Radius));
        if (!float.IsFinite(Erosion.Scale) || Erosion.Scale <= 0) throw new ArgumentOutOfRangeException(nameof(Erosion));
        return this;
    }
}

public static class PlanetaryFieldIdentity
{
    public static ulong Compute(ulong baseFieldVersion, float radius, int seed, in AdvancedErosionParameters erosion)
    {
        if (baseFieldVersion == 0) throw new ArgumentOutOfRangeException(nameof(baseFieldVersion));
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        Add(baseFieldVersion);
        Add((uint)BitConverter.SingleToInt32Bits(radius)); Add((uint)seed);
        Add((uint)BitConverter.SingleToInt32Bits(erosion.Scale)); Add((uint)BitConverter.SingleToInt32Bits(erosion.Strength));
        Add((uint)BitConverter.SingleToInt32Bits(erosion.GullyWeight)); Add((uint)BitConverter.SingleToInt32Bits(erosion.Detail));
        AddFloat4(erosion.Rounding); AddFloat4(erosion.Onset);
        Add((uint)BitConverter.SingleToInt32Bits(erosion.AssumedSlope.x)); Add((uint)BitConverter.SingleToInt32Bits(erosion.AssumedSlope.y));
        Add((uint)BitConverter.SingleToInt32Bits(erosion.CellScale)); Add((uint)BitConverter.SingleToInt32Bits(erosion.Normalization));
        Add((uint)erosion.Octaves); Add((uint)BitConverter.SingleToInt32Bits(erosion.Lacunarity)); Add((uint)BitConverter.SingleToInt32Bits(erosion.Gain));
        return hash == 0 ? 1 : hash;

        void Add(ulong value) => hash = unchecked((hash ^ value) * prime);
        void AddFloat4(float4 value)
        {
            Add((uint)BitConverter.SingleToInt32Bits(value.x)); Add((uint)BitConverter.SingleToInt32Bits(value.y));
            Add((uint)BitConverter.SingleToInt32Bits(value.z)); Add((uint)BitConverter.SingleToInt32Bits(value.w));
        }
    }
}

/// <summary>
/// Application-supplied base evidence at one unit spherical direction.
/// FieldValue and FieldGradient drive erosion. RadialDisplacement and
/// RadialGradient describe the uneroded surface in metres.
/// </summary>
public readonly record struct PlanetaryBaseFieldSample(
    float RadialDisplacement,
    float3 RadialGradient,
    float FieldValue,
    float3 FieldGradient,
    float FadeTarget);

public readonly record struct PlanetarySurfaceSample(
    ulong FieldVersion,
    float3 UnitDirection,
    float Radius,
    float RadialDisplacement,
    float3 TangentGradient,
    float3 SurfaceNormal,
    float Slope,
    float Ridge,
    float Gully,
    float FinestResolvedWavelength,
    float UnresolvedHeightBound)
{
    public float SurfaceRadius => Radius + RadialDisplacement;
    public float3 PlanetLocalPosition => UnitDirection * SurfaceRadius;
    public bool Meets(PlanetaryQueryScale scale) => UnresolvedHeightBound <= scale.MaximumUnresolvedHeight;
}

public static class PlanetaryField
{
    public static PlanetarySurfaceSample Sample(
        in PlanetaryFieldDefinition definition,
        float3 unitDirection,
        in PlanetaryBaseFieldSample baseSample,
        in PlanetaryQueryScale scale)
    {
        definition.Validate();
        scale.Validate();
        PlanetaryTopology.ValidateDirection(unitDirection);
        PlanetaryTopology.ValidateFinite(baseSample.RadialGradient, nameof(baseSample));
        PlanetaryTopology.ValidateFinite(baseSample.FieldGradient, nameof(baseSample));
        if (!float.IsFinite(baseSample.RadialDisplacement) || !float.IsFinite(baseSample.FieldValue) || !float.IsFinite(baseSample.FadeTarget))
            throw new ArgumentOutOfRangeException(nameof(baseSample));

        var direction = math.normalize(unitDirection);
        var radialGradient = Tangent(direction, baseSample.RadialGradient);
        var fieldGradient = Tangent(direction, baseSample.FieldGradient);
        var p = definition.Erosion;
        var band = ErosionFrequencyBands.Select(
            p.Scale * p.CellScale,
            scale.FootprintMeters,
            p.Octaves,
            p.Lacunarity,
            p.Strength * p.Scale,
            p.Gain);

        var powers = new float3(
            Pow4(MathF.Abs(direction.x)),
            Pow4(MathF.Abs(direction.y)),
            Pow4(MathF.Abs(direction.z)));
        var powerSum = math.max(powers.x + powers.y + powers.z, 1.0e-6f);
        var weights = powers / powerSum;
        var world = direction * definition.Radius;
        var seed = definition.Seed;
        var xy = AdvancedErosionFilter.Sample(
            world.xy + new float2(713 + seed * 0.754877666f, -291 + seed * 0.569840296f),
            new float3(baseSample.FieldValue, fieldGradient.x, fieldGradient.y), baseSample.FadeTarget, p, band);
        var yz = AdvancedErosionFilter.Sample(
            world.yz + new float2(-431 + seed * 0.438289027f, 887 + seed * 0.328438163f),
            new float3(baseSample.FieldValue, fieldGradient.y, fieldGradient.z), baseSample.FadeTarget, p, band);
        var zx = AdvancedErosionFilter.Sample(
            new float2(world.z, world.x) + new float2(197 + seed * 0.219783071f, 557 + seed * 0.145898034f),
            new float3(baseSample.FieldValue, fieldGradient.z, fieldGradient.x), baseSample.FadeTarget, p, band);

        var erosionHeight = xy.Delta.x * weights.z + yz.Delta.x * weights.x + zx.Delta.x * weights.y;
        var erosionGradient = new float3(
            xy.Delta.y * weights.z + zx.Delta.z * weights.y,
            xy.Delta.z * weights.z + yz.Delta.y * weights.x,
            yz.Delta.z * weights.x + zx.Delta.y * weights.y);
        var powerGradientX = Tangent(direction, new float3(4 * direction.x * direction.x * direction.x / definition.Radius, 0, 0));
        var powerGradientY = Tangent(direction, new float3(0, 4 * direction.y * direction.y * direction.y / definition.Radius, 0));
        var powerGradientZ = Tangent(direction, new float3(0, 0, 4 * direction.z * direction.z * direction.z / definition.Radius));
        var sumGradient = powerGradientX + powerGradientY + powerGradientZ;
        var weightGradientX = (powerGradientX * powerSum - sumGradient * powers.x) / (powerSum * powerSum);
        var weightGradientY = (powerGradientY * powerSum - sumGradient * powers.y) / (powerSum * powerSum);
        var weightGradientZ = (powerGradientZ * powerSum - sumGradient * powers.z) / (powerSum * powerSum);
        erosionGradient += yz.Delta.x * weightGradientX + zx.Delta.x * weightGradientY + xy.Delta.x * weightGradientZ;
        var gradient = Tangent(direction, radialGradient + erosionGradient);
        var ridgeEvidence = xy.RidgeMap * weights.z + yz.RidgeMap * weights.x + zx.RidgeMap * weights.y;
        var ridge = math.saturate(ridgeEvidence * 0.5f + 0.5f);
        var gully = math.saturate(0.5f - ridgeEvidence * 0.5f);
        var displacement = baseSample.RadialDisplacement + erosionHeight;

        return new(
            definition.FieldVersion,
            direction,
            definition.Radius,
            displacement,
            gradient,
            PlanetaryTopology.SurfaceNormal(direction, gradient),
            math.length(gradient),
            ridge,
            gully,
            band.FinestIncludedWavelength,
            band.UnresolvedHeightBound);
    }

    public static void SampleBatch<TSource>(
        in PlanetaryFieldDefinition definition,
        ReadOnlySpan<float3> directions,
        in PlanetaryQueryScale scale,
        TSource source,
        Span<PlanetarySurfaceSample> destination)
        where TSource : IPlanetaryBaseField
    {
        if (destination.Length < directions.Length) throw new ArgumentException("Destination is shorter than directions.", nameof(destination));
        for (var i = 0; i < directions.Length; i++)
        {
            var direction = math.normalize(directions[i]);
            var baseSample = source.Sample(direction);
            destination[i] = Sample(definition, direction, baseSample, scale);
        }
    }

    public static PlanetarySurfaceSample SamplePosition<TSource>(
        in PlanetaryFieldDefinition definition,
        double3 planetLocalPosition,
        in PlanetaryQueryScale scale,
        TSource source)
        where TSource : IPlanetaryBaseField
    {
        if (!double.IsFinite(planetLocalPosition.x) || !double.IsFinite(planetLocalPosition.y) || !double.IsFinite(planetLocalPosition.z))
            throw new ArgumentOutOfRangeException(nameof(planetLocalPosition));
        var length = Math.Sqrt(planetLocalPosition.x * planetLocalPosition.x + planetLocalPosition.y * planetLocalPosition.y + planetLocalPosition.z * planetLocalPosition.z);
        if (length < 1.0e-12) throw new ArgumentOutOfRangeException(nameof(planetLocalPosition));
        var direction = new float3((float)(planetLocalPosition.x / length), (float)(planetLocalPosition.y / length), (float)(planetLocalPosition.z / length));
        return Sample(definition, direction, source.Sample(direction), scale);
    }

    private static float3 Tangent(float3 direction, float3 gradient) => gradient - direction * math.dot(gradient, direction);
    private static float Pow4(float value) { var square = value * value; return square * square; }
}

public interface IPlanetaryBaseField
{
    PlanetaryBaseFieldSample Sample(float3 unitDirection);
}
