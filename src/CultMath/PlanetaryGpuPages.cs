using System;

namespace CultMath;

public readonly record struct PlanetaryGpuPageInput(float4 DirectionRadius, float4 Sampling);

public readonly record struct PlanetaryGpuPageMetadata(float4 Address, float4 Layout, float4 Bounds, float4 State);

public readonly record struct PlanetaryGpuPageContent(
    PlanetaryPageLayout Layout,
    PlanetaryGpuPageInput[] Inputs,
    float SampleSpacing,
    float ParentSampleSpacing);

public static class PlanetaryGpuPageBuilder
{
    public static PlanetaryGpuPageContent BuildContent(PlanetaryPageLayout layout, float radius)
    {
        layout.Validate();
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var spacing = PlanetaryPageSampling.NominalAngularTexelSize(layout) * radius;
        var parentSpacing = layout.Tile.Level == 0
            ? 0
            : PlanetaryPageSampling.NominalAngularTexelSize(new(layout.Tile.Parent(), layout.InteriorSize, layout.BorderSize)) * radius;
        var inputs = new PlanetaryGpuPageInput[layout.StorageSize * layout.StorageSize];
        for (var y = 0; y < layout.StorageSize; y++)
        for (var x = 0; x < layout.StorageSize; x++)
        {
            var direction = PlanetaryPageSampling.Direction(layout, x, y);
            inputs[y * layout.StorageSize + x] = new(
                new float4(direction, radius),
                new float4(spacing, parentSpacing, 0, 0));
        }
        return new(layout, inputs, spacing, parentSpacing);
    }

    public static PlanetaryGpuPageMetadata Metadata(
        in PlanetaryGpuPageContent content,
        int outputOffset,
        float blend,
        float4 bounds = default)
    {
        content.Layout.Validate();
        if (content.Inputs == null || content.Inputs.Length != content.Layout.StorageSize * content.Layout.StorageSize)
            throw new ArgumentException("GPU page content does not match its layout.", nameof(content));
        if (outputOffset < 0) throw new ArgumentOutOfRangeException(nameof(outputOffset));
        if (!float.IsFinite(blend) || blend is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(blend));
        var tile = content.Layout.Tile;
        return new(
            new float4((int)tile.Face, tile.Level, tile.X, tile.Y),
            new float4(outputOffset, content.Layout.StorageSize, content.Layout.InteriorSize, content.Layout.BorderSize),
            bounds,
            new float4(1, blend, PlanetaryPageSampling.NominalAngularTexelSize(content.Layout), content.SampleSpacing));
    }
}
