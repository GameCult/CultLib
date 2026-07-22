using System;
using System.IO;
using System.Text;
using CultMath;

// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this file,
// You can obtain one at https://mozilla.org/MPL/2.0/.

namespace GameCult.Geometry;

public static class PlanetaryMapTileEncoding
{
    public const uint FormatVersion = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CMPT");

    public static void Write(Stream stream, in PlanetarySurfaceMapTile tile, bool leaveOpen = false)
    {
        if (stream == null || !stream.CanWrite) throw new ArgumentException("A writable stream is required.", nameof(stream));
        tile.Key.Validate();
        if (tile.Samples == null || tile.Validity == null || tile.Samples.Length != tile.Validity.Length)
            throw new ArgumentException("Tile samples and validity must have equal lengths.", nameof(tile));
        var expectedCount = tile.Key.Layout.StorageSize * tile.Key.Layout.StorageSize;
        if (tile.Samples.Length != expectedCount) throw new ArgumentException("Tile payload does not match its layout.", nameof(tile));

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(tile.Key.FieldVersion);
        writer.Write(tile.Key.ProjectionVersion);
        writer.Write(tile.Key.LayerId);
        var projection = tile.Key.Layout.Projection;
        writer.Write((int)projection.Kind);
        writer.Write(projection.CenterLongitude);
        writer.Write(projection.CenterLatitude);
        writer.Write(projection.Scale);
        writer.Write(tile.Key.Layout.Level);
        writer.Write(tile.Key.Layout.X);
        writer.Write(tile.Key.Layout.Y);
        writer.Write(tile.Key.Layout.InteriorSize);
        writer.Write(tile.Key.Layout.BorderSize);
        writer.Write(tile.Key.QueryScale.FootprintMeters);
        writer.Write(tile.Key.QueryScale.MaximumUnresolvedHeight);
        writer.Write(tile.Samples.Length);
        for (var i = 0; i < tile.Samples.Length; i++)
        {
            writer.Write(tile.Validity[i]);
            if (!tile.Validity[i]) continue;
            WriteSample(writer, tile.Samples[i], tile.Key.FieldVersion);
        }
    }

    public static PlanetarySurfaceMapTile Read(Stream stream, bool leaveOpen = false)
    {
        if (stream == null || !stream.CanRead) throw new ArgumentException("A readable stream is required.", nameof(stream));
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen);
        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a CultMath planetary tile.");
        var version = reader.ReadUInt32();
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported planetary tile version {version}.");
        var fieldVersion = reader.ReadUInt64();
        var projectionVersion = reader.ReadUInt32();
        var layerId = reader.ReadUInt32();
        var kindValue = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(PlanetaryProjectionKind), kindValue)) throw new InvalidDataException("Unknown planetary projection kind.");
        var projection = new PlanetaryProjectionParameters(
            (PlanetaryProjectionKind)kindValue,
            reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()).Validate();
        var layout = new PlanetaryMapTileLayout(
            projection,
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32()).Validate();
        var scale = new PlanetaryQueryScale(reader.ReadSingle(), reader.ReadSingle()).Validate();
        var key = new PlanetaryMapTileKey(fieldVersion, projectionVersion, layerId, layout, scale).Validate();
        var count = reader.ReadInt32();
        if (count != layout.StorageSize * layout.StorageSize) throw new InvalidDataException("Planetary tile sample count does not match layout.");
        var samples = new PlanetarySurfaceSample[count];
        var validity = new bool[count];
        for (var i = 0; i < count; i++)
        {
            validity[i] = reader.ReadBoolean();
            if (validity[i]) samples[i] = ReadSample(reader, fieldVersion);
        }
        if (stream.CanSeek && stream.Position != stream.Length) throw new InvalidDataException("Planetary tile has trailing bytes.");
        return new(key, samples, validity);
    }

    private static void WriteSample(BinaryWriter writer, PlanetarySurfaceSample sample, ulong fieldVersion)
    {
        if (sample.FieldVersion != fieldVersion) throw new ArgumentException("Sample field version does not match tile identity.");
        Write(writer, sample.UnitDirection);
        writer.Write(sample.Radius);
        writer.Write(sample.RadialDisplacement);
        Write(writer, sample.TangentGradient);
        Write(writer, sample.SurfaceNormal);
        writer.Write(sample.Slope);
        writer.Write(sample.Ridge);
        writer.Write(sample.Gully);
        writer.Write(sample.FinestResolvedWavelength);
        writer.Write(sample.UnresolvedHeightBound);
    }

    private static PlanetarySurfaceSample ReadSample(BinaryReader reader, ulong fieldVersion)
    {
        var direction = ReadFloat3(reader);
        var radius = reader.ReadSingle();
        var displacement = reader.ReadSingle();
        var gradient = ReadFloat3(reader);
        var normal = ReadFloat3(reader);
        return new(
            fieldVersion, direction, radius, displacement, gradient, normal,
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle());
    }

    private static void Write(BinaryWriter writer, float3 value)
    {
        writer.Write(value.x); writer.Write(value.y); writer.Write(value.z);
    }

    private static float3 ReadFloat3(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
