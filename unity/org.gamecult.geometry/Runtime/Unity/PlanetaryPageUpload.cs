using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CultMath;
using UnityEngine;

namespace GameCult.Geometry.Unity
{

[StructLayout(LayoutKind.Sequential)] public readonly struct PlanetaryPageInputGpu { public PlanetaryPageInputGpu(Vector4 directionRadius, Vector4 sampling) { DirectionRadius = directionRadius; Sampling = sampling; } public readonly Vector4 DirectionRadius; public readonly Vector4 Sampling; }
[StructLayout(LayoutKind.Sequential)] public readonly struct PlanetaryPageMetadataGpu { public PlanetaryPageMetadataGpu(Vector4 address, Vector4 layout, Vector4 bounds, Vector4 state) { Address = address; Layout = layout; Bounds = bounds; State = state; } public readonly Vector4 Address; public readonly Vector4 Layout; public readonly Vector4 Bounds; public readonly Vector4 State; }
public readonly struct PlanetaryPageUpload { public PlanetaryPageUpload(PlanetaryPageInputGpu[] inputs, PlanetaryPageMetadataGpu[] metadata, int outputSampleCount, ulong contentVersion, ulong presentationVersion) { Inputs = inputs; Metadata = metadata; OutputSampleCount = outputSampleCount; ContentVersion = contentVersion; PresentationVersion = presentationVersion; } public PlanetaryPageInputGpu[] Inputs { get; } public PlanetaryPageMetadataGpu[] Metadata { get; } public int OutputSampleCount { get; } public ulong ContentVersion { get; } public ulong PresentationVersion { get; } }
public static class PlanetaryPageUploadBuilder
{
    public static PlanetaryPageUpload Build(in PlanetaryResidencySnapshot snapshot, int interiorSize, int borderSize, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var inputs = new List<PlanetaryPageInputGpu>(); var metadata = new PlanetaryPageMetadataGpu[snapshot.Tiles.Length];
        foreach (var (resident, pageIndex) in snapshot.Tiles.Select((resident, index) => (resident, index)))
        {
            var content = PlanetaryGpuPageBuilder.BuildContent(new PlanetaryPageLayout(resident.Tile, interiorSize, borderSize).Validate(), radius); var outputOffset = inputs.Count;
            foreach (var input in content.Inputs) inputs.Add(new(ToUnity(input.DirectionRadius), ToUnity(input.Sampling)));
            var common = PlanetaryGpuPageBuilder.Metadata(content, outputOffset, resident.Blend);
            metadata[pageIndex] = new(ToUnity(common.Address), ToUnity(common.Layout), ToUnity(common.Bounds), ToUnity(common.State));
        }
        return new(inputs.ToArray(), metadata, inputs.Count, snapshot.ContentVersion, snapshot.PresentationVersion);
    }
    private static Vector4 ToUnity(float4 value) => new(value.x, value.y, value.z, value.w);
}
}
