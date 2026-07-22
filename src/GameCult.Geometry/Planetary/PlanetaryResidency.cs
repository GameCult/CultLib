using System;
using System.Collections.Generic;
using System.Linq;
using CultMath;

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

namespace GameCult.Geometry;

public readonly record struct PlanetaryLodParameters(
    int MaximumLevel,
    int PageInteriorSize,
    int PageBorderSize,
    float ViewportHeightPixels,
    float MinimumFootprintMeters)
{
    public PlanetaryLodParameters Validate()
    {
        if (MaximumLevel is < 0 or > PlanetaryTileAddress.MaxLevel) throw new ArgumentOutOfRangeException(nameof(MaximumLevel));
        _ = new PlanetaryPageLayout(new(PlanetaryCubeFace.PositiveX, 0, 0, 0), PageInteriorSize, PageBorderSize).Validate();
        if (!float.IsFinite(ViewportHeightPixels) || ViewportHeightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(ViewportHeightPixels));
        if (!float.IsFinite(MinimumFootprintMeters) || MinimumFootprintMeters < 0) throw new ArgumentOutOfRangeException(nameof(MinimumFootprintMeters));
        return this;
    }
}

public static class PlanetaryLodSelector
{
    public static int SelectLevel(
        in PlanetaryFieldDefinition field,
        float cameraDistanceFromCenter,
        in PlanetaryLodParameters parameters)
    {
        field.Validate(); parameters.Validate();
        if (!float.IsFinite(cameraDistanceFromCenter) || cameraDistanceFromCenter < 0) throw new ArgumentOutOfRangeException(nameof(cameraDistanceFromCenter));
        var altitude = MathF.Max(cameraDistanceFromCenter - field.Radius, 0.001f);
        var footprint = MathF.Max(altitude / parameters.ViewportHeightPixels * 2, parameters.MinimumFootprintMeters);
        var erosion = field.Erosion;
        for (var level = 0; level <= parameters.MaximumLevel; level++)
        {
            var page = new PlanetaryPageLayout(new(PlanetaryCubeFace.PositiveX, level, 0, 0), parameters.PageInteriorSize, parameters.PageBorderSize);
            var spacing = PlanetaryPageSampling.NominalAngularTexelSize(page) * field.Radius;
            var unresolved = ErosionFrequencyBands.Select(
                erosion.Scale * erosion.CellScale, spacing, erosion.Octaves,
                erosion.Lacunarity, erosion.Strength * erosion.Scale, erosion.Gain).UnresolvedHeightBound;
            if (unresolved <= footprint) return level;
        }
        return parameters.MaximumLevel;
    }

    public static PlanetaryTileAddress[] SelectAncestorChain(
        in PlanetaryFieldDefinition field,
        float3 cameraDirection,
        float cameraDistanceFromCenter,
        in PlanetaryLodParameters parameters)
    {
        PlanetaryTopology.ValidateDirection(cameraDirection);
        var level = SelectLevel(field, cameraDistanceFromCenter, parameters);
        var result = new PlanetaryTileAddress[6 + level];
        for (var face = 0; face < 6; face++) result[face] = new((PlanetaryCubeFace)face, 0, 0, 0);
        var leaf = PlanetaryTopology.TileAt(cameraDirection, level);
        for (var currentLevel = level; currentLevel >= 1; currentLevel--)
        {
            result[6 + currentLevel - 1] = leaf;
            leaf = leaf.Parent();
        }
        return result;
    }
}

public readonly record struct PlanetaryResidentTile(PlanetaryTileAddress Tile, float Blend, bool Departing);

public readonly record struct PlanetaryResidencySnapshot(
    ulong ContentVersion,
    ulong PresentationVersion,
    PlanetaryResidentTile[] Tiles);

/// <summary>Owns presentation residency only. It never owns field content.</summary>
public sealed class PlanetaryResidualResidency
{
    private readonly Dictionary<ulong, PlanetaryTileAddress> residents = new();
    private readonly Dictionary<ulong, float> arrivals = new();
    private readonly Dictionary<ulong, (float Time, float StartingBlend)> departures = new();
    private float lastTime = float.NegativeInfinity;

    public void Reset()
    {
        residents.Clear(); arrivals.Clear(); departures.Clear(); lastTime = float.NegativeInfinity;
    }

    public PlanetaryResidencySnapshot Update(ReadOnlySpan<PlanetaryTileAddress> desired, float timeSeconds, float transitionSeconds)
    {
        if (!float.IsFinite(timeSeconds)) throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        if (!float.IsFinite(transitionSeconds) || transitionSeconds < 0) throw new ArgumentOutOfRangeException(nameof(transitionSeconds));
        if (timeSeconds < lastTime) Reset();
        lastTime = timeSeconds;

        var desiredKeys = new HashSet<ulong>();
        foreach (var tile in desired)
        {
            var key = tile.StableKey;
            desiredKeys.Add(key);
            residents[key] = tile;
            if (!arrivals.ContainsKey(key)) arrivals[key] = timeSeconds;
            departures.Remove(key);
        }

        foreach (var pair in residents.ToArray())
        {
            if (pair.Value.Level == 0 || desiredKeys.Contains(pair.Key)) continue;
            if (!departures.ContainsKey(pair.Key))
            {
                var startingBlend = transitionSeconds == 0
                    ? 1
                    : Math.Clamp((timeSeconds - arrivals[pair.Key]) / transitionSeconds, 0, 1);
                departures[pair.Key] = (timeSeconds, startingBlend);
            }
            if (transitionSeconds > 0 && timeSeconds - departures[pair.Key].Time < transitionSeconds) continue;
            residents.Remove(pair.Key); arrivals.Remove(pair.Key); departures.Remove(pair.Key);
        }

        var ordered = residents
            .OrderBy(pair => pair.Value.Level)
            .ThenBy(pair => pair.Value.Face)
            .ThenBy(pair => pair.Value.X)
            .ThenBy(pair => pair.Value.Y)
            .ToArray();
        var tiles = new PlanetaryResidentTile[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var pair = ordered[i];
            var departing = departures.TryGetValue(pair.Key, out var departure);
            var blend = pair.Value.Level == 0 || transitionSeconds == 0
                ? 1
                : departing
                    ? departure.StartingBlend * Math.Clamp(1 - (timeSeconds - departure.Time) / transitionSeconds, 0, 1)
                    : Math.Clamp((timeSeconds - arrivals[pair.Key]) / transitionSeconds, 0, 1);
            tiles[i] = new(pair.Value, blend, departing);
        }
        return new(Hash(ordered.Select(pair => pair.Key)), Hash(tiles.Select(tile => tile.Tile.StableKey ^ (uint)BitConverter.SingleToInt32Bits(tile.Blend))), tiles);
    }

    private static ulong Hash(IEnumerable<ulong> values)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var value in values) hash = unchecked((hash ^ value) * prime);
        return hash == 0 ? 1 : hash;
    }
}
