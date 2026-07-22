using System.IO;
using Xunit;

using CultMath;
using GameCult.Geometry;

namespace GameCult.Geometry.Planetary.Tests;

public sealed class PlanetaryMapTileEncodingTests
{
    [Fact]
    public void BinaryTileRoundTripsIdentityValidityAndSurfaceEvidence()
    {
        var field = PlanetaryFieldDefinition.Create(5, 10, 3, AdvancedErosionParameters.Default);
        var layout = new PlanetaryMapTileLayout(new(PlanetaryProjectionKind.EqualEarth), 1, 1, 0, 5, 1);
        var key = new PlanetaryMapTileKey(field.FieldVersion, 2, 17, layout, PlanetaryQueryScale.AtFootprint(0.25f, 0.1f));
        var tile = PlanetaryMapTileBaker.Bake(field, key, new FlatField());
        using var bytes = new MemoryStream();
        PlanetaryMapTileEncoding.Write(bytes, tile, leaveOpen: true);
        bytes.Position = 0;
        var decoded = PlanetaryMapTileEncoding.Read(bytes);

        Assert.Equal(tile.Key, decoded.Key);
        Assert.Equal(tile.Validity, decoded.Validity);
        for (var i = 0; i < tile.Samples.Length; i++)
            if (tile.Validity[i]) Assert.Equal(tile.Samples[i], decoded.Samples[i]);
    }

    [Fact]
    public void DecoderRejectsForeignMagicAndVersions()
    {
        Assert.Throws<InvalidDataException>(() => PlanetaryMapTileEncoding.Read(new MemoryStream(new byte[] { 1, 2, 3, 4, 1, 0, 0, 0 })));
        var bytes = new byte[] { (byte)'C', (byte)'M', (byte)'P', (byte)'T', 99, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => PlanetaryMapTileEncoding.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void DecoderRejectsTrailingBytes()
    {
        var field = PlanetaryFieldDefinition.Create(2, 1, 0, AdvancedErosionParameters.Default);
        var layout = new PlanetaryMapTileLayout(new(PlanetaryProjectionKind.Equirectangular), 0, 0, 0, 2, 0);
        var key = new PlanetaryMapTileKey(field.FieldVersion, 1, 0, layout, PlanetaryQueryScale.AtFootprint(1));
        var tile = PlanetaryMapTileBaker.Bake(field, key, new FlatField());
        using var bytes = new MemoryStream();
        PlanetaryMapTileEncoding.Write(bytes, tile, leaveOpen: true);
        bytes.WriteByte(0xff);
        bytes.Position = 0;
        Assert.Throws<InvalidDataException>(() => PlanetaryMapTileEncoding.Read(bytes));
    }

    private readonly struct FlatField : IPlanetaryBaseField
    {
        public PlanetaryBaseFieldSample Sample(float3 direction) => new(0, float3.zero, 0, float3.zero, 0);
    }
}
