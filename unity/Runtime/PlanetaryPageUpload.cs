using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CultMath.Unity;

[StructLayout(LayoutKind.Sequential)]
public readonly struct PlanetaryPageInputGpu
{
    public PlanetaryPageInputGpu(Vector4 directionRadius, Vector4 sampling)
    {
        DirectionRadius = directionRadius;
        Sampling = sampling;
    }

    public readonly Vector4 DirectionRadius;
    public readonly Vector4 Sampling;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct PlanetaryPageMetadataGpu
{
    public PlanetaryPageMetadataGpu(Vector4 address, Vector4 layout, Vector4 bounds, Vector4 state)
    {
        Address = address;
        Layout = layout;
        Bounds = bounds;
        State = state;
    }

    public readonly Vector4 Address;
    public readonly Vector4 Layout;
    public readonly Vector4 Bounds;
    public readonly Vector4 State;
}

public readonly record struct PlanetaryPageUpload(
    PlanetaryPageInputGpu[] Inputs,
    PlanetaryPageMetadataGpu[] Metadata,
    int OutputSampleCount,
    ulong ContentVersion,
    ulong PresentationVersion);

public static class PlanetaryPageUploadBuilder
{
    public static PlanetaryPageUpload Build(
        in PlanetaryResidencySnapshot snapshot,
        int interiorSize,
        int borderSize,
        float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var inputs = new List<PlanetaryPageInputGpu>();
        var metadata = new PlanetaryPageMetadataGpu[snapshot.Tiles.Length];
        foreach (var (resident, pageIndex) in snapshot.Tiles.Select((resident, index) => (resident, index)))
        {
            var layout = new PlanetaryPageLayout(resident.Tile, interiorSize, borderSize).Validate();
            var spacing = PlanetaryPageSampling.NominalAngularTexelSize(layout) * radius;
            var parentSpacing = resident.Tile.Level == 0
                ? 0
                : PlanetaryPageSampling.NominalAngularTexelSize(new(resident.Tile.Parent(), interiorSize, borderSize)) * radius;
            var outputOffset = inputs.Count;
            for (var y = 0; y < layout.StorageSize; y++)
            for (var x = 0; x < layout.StorageSize; x++)
            {
                var direction = PlanetaryPageSampling.Direction(layout, x, y);
                inputs.Add(new(
                    new Vector4(direction.x, direction.y, direction.z, radius),
                    new Vector4(spacing, parentSpacing, 0, 0)));
            }
            metadata[pageIndex] = new(
                new Vector4((int)resident.Tile.Face, resident.Tile.Level, resident.Tile.X, resident.Tile.Y),
                new Vector4(outputOffset, layout.StorageSize, layout.InteriorSize, layout.BorderSize),
                Vector4.zero,
                new Vector4(1, resident.Blend, PlanetaryPageSampling.NominalAngularTexelSize(layout), spacing));
        }
        return new(inputs.ToArray(), metadata, inputs.Count, snapshot.ContentVersion, snapshot.PresentationVersion);
    }
}
